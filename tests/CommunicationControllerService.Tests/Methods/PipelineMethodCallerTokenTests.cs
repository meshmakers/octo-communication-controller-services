using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.PipelineMethodTestSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     The pipeline handler does not trust the <c>Caller</c> member of a method request: the bus is not authenticated per
///     message, so anyone who can publish to the method queue can put an administrator there. It validates the forwarded
///     access token (issuer, signature, expiry, tenant, scope) with the shared validator of octo-common-services and
///     builds the pipeline caller from that token only. These tests use the real validator over a fake identity service
///     and tokens signed with a throw-away key. Every refusal must leave the pipeline unstarted.
/// </summary>
internal class PipelineMethodCallerTokenTests : IDisposable
{
    // The forged identity: an administrator who is not the owner of any token used below.
    private static readonly CkMethodCaller ForgedAdministrator = new()
    {
        SubjectId = "forged-admin", TenantId = TenantId, Name = "Eve Forger", Roles = ["AdminPanelManagement"]
    };

    private readonly CallerTokenKit _kit = new();
    private readonly IPipelineMethodCatalog _catalog = Substitute.For<IPipelineMethodCatalog>();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly ITriggerManagementService _trigger = Substitute.For<ITriggerManagementService>();
    private readonly PipelineMethodInvoker _invoker;
    private readonly Guid _executionId = Guid.NewGuid();
    private ExecutePipelineCaller? _startedWith;

    public PipelineMethodCallerTokenTests()
    {
        var options = Options.Create(new PipelineMethodOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(5), MaxPollInterval = TimeSpan.FromMilliseconds(20)
        });
        var services = new ServiceCollection().AddSingleton(_trigger).BuildServiceProvider();
        var logger = LoggerFactory.Create(b => b.AddProvider(_kit.Logs)).CreateLogger<PipelineMethodInvoker>();
        _invoker = new PipelineMethodInvoker(_catalog, _repository, services,
            new PipelineMethodHandlerInstance(options, TimeProvider.System), options, TimeProvider.System,
            _kit.Validator, logger);

        _catalog.IsTenantEnabled(TenantId).Returns(true);
        _catalog.Find(TenantId, MethodId).Returns(Method());
        var pipeline = RtEntityCreator.CreatePipeline();
        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns(pipeline);
        _repository.GetPipelineExecutionAsync(TenantId, _executionId.ToString()).Returns(_ =>
        {
            var execution = RtEntityCreator.CreatePipelineExecution(_executionId.ToString(),
                RtPipelineExecutionStatusEnum.Completed);
            execution.OutputData = "{\"value\": 1}";
            return Task.FromResult<RtPipelineExecution?>(execution);
        });
        _trigger.StartExecutePipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Do<ExecutePipelineCaller?>(c => _startedWith = c), Arg.Any<string?>(),
                Arg.Any<RtPipelineTriggerTypeEnum>())
            .Returns(_ => Task.FromResult(new PipelineExecutionDataDto { Id = _executionId, DateTime = DateTime.UtcNow }));
    }

    public void Dispose() => _kit.Dispose();

    private CkMethodRequest Forged(string? token)
    {
        return Request(token: token) with { Caller = ForgedAdministrator };
    }

    private async Task AssertRefusedAsync(string? token, string reason)
    {
        var response = await _invoker.InvokeAsync(Forged(token));

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodForbidden);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo(reason);
        await Assert.That(response.Error.Parameters["methodId"]).IsEqualTo(MethodId);
        // nothing ran: not started, and not even looked up
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default, default, default, default,
            default, default);
        await _repository.DidNotReceiveWithAnyArgs().GetPipelineByWellKnownNameAsync(default!, default!);
        _catalog.DidNotReceiveWithAnyArgs().Find(default!, default!);
    }

    [Test]
    public Task ForgedCallerWithoutAToken_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(null, CkMethodTokenReasons.TokenMissing);

    [Test]
    public Task ForgedCallerWithAnEmptyToken_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync("  ", CkMethodTokenReasons.TokenMissing);

    [Test]
    public Task TokenOfAnotherTenant_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.Create(tenant: "othertenant"), CkMethodTokenReasons.TenantNotAllowed);

    [Test]
    public Task TokenWithoutATenant_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.Create(tenant: null), CkMethodTokenReasons.TenantNotAllowed);

    [Test]
    public Task ExpiredToken_IsForbiddenWithReasonTokenExpired_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.Create(expires: DateTime.UtcNow.AddHours(-1)), CkMethodTokenReasons.TokenExpired);

    [Test]
    public Task TokenOfAForeignIssuer_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.Create(issuer: "https://evil.test/"), CkMethodTokenReasons.TokenInvalid);

    [Test]
    public Task TokenWithAForeignSignature_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.CreateWithForeignKey(), CkMethodTokenReasons.TokenInvalid);

    [Test]
    public Task UnsignedToken_AlgNone_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(CallerTokenKit.Unsigned(), CkMethodTokenReasons.TokenInvalid);

    [Test]
    public Task TokenWithoutTheOctoApiScope_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.Create(scope: false), CkMethodTokenReasons.TokenInvalid);

    [Test]
    public Task ServiceTokenWithoutAClientId_IsForbidden_AndNoPipelineStarts() =>
        AssertRefusedAsync(_kit.Create(c => c.Remove("client_id"), user: false), CkMethodTokenReasons.TokenInvalid);

    [Test]
    public async Task ValidTokenWithAForgedCaller_RunsAsTheTokenSubject_AndNeverAsTheForgedAdministrator()
    {
        var response = await _invoker.InvokeAsync(Forged(_kit.Create()));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(_startedWith!.SubjectId).IsEqualTo(CallerTokenKit.TokenSubject);
        await Assert.That(_startedWith.Roles).Contains("Reporting");
        await Assert.That(_startedWith.Roles).DoesNotContain("AdminPanelManagement");
        await Assert.That(_startedWith.Name).IsNull();
        await Assert.That(_startedWith.TenantId).IsEqualTo(TenantId);
        await Assert.That(_startedWith.TrustLevel).IsEqualTo(2);
    }

    [Test]
    public async Task ValidTokenWithoutAnyCallerMember_StillRunsAsTheTokenSubject()
    {
        var request = Request(token: _kit.Create()) with { Caller = null };

        var response = await _invoker.InvokeAsync(request);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(_startedWith!.SubjectId).IsEqualTo(CallerTokenKit.TokenSubject);
        await Assert.That(_startedWith.TrustLevel).IsEqualTo(2);
    }

    [Test]
    public async Task ServiceToken_RunsWithTheClientIdAsSubject()
    {
        var response = await _invoker.InvokeAsync(Forged(_kit.Create(user: false)));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(_startedWith!.SubjectId).IsEqualTo(CallerTokenKit.ServiceClientId);
        await Assert.That(_startedWith.Roles).IsEmpty();
        await Assert.That(_startedWith.TenantId).IsEqualTo(TenantId);
        await Assert.That(_startedWith.TrustLevel).IsEqualTo(2);
    }

    [Test]
    public async Task TheForwardedTokenReachesThePipelineUnchanged()
    {
        var token = _kit.Create();
        string? forwarded = null;
        _trigger.StartExecutePipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<ExecutePipelineCaller?>(), Arg.Do<string?>(t => forwarded = t),
                Arg.Any<RtPipelineTriggerTypeEnum>())
            .Returns(_ => Task.FromResult(new PipelineExecutionDataDto { Id = _executionId, DateTime = DateTime.UtcNow }));

        await _invoker.InvokeAsync(Forged(token));

        await Assert.That(forwarded).IsEqualTo(token);
    }

    [Test]
    public async Task NoTokenClaimOrKeyIsLogged_OnTheSuccessAndTheFailurePaths()
    {
        var valid = _kit.Create();
        var refused = new[]
        {
            null, _kit.Create(tenant: "othertenant"), _kit.Create(expires: DateTime.UtcNow.AddHours(-1)),
            _kit.Create(issuer: "https://evil.test/"), _kit.CreateWithForeignKey(), CallerTokenKit.Unsigned(),
            _kit.Create(scope: false)
        };

        await _invoker.InvokeAsync(Forged(valid));
        foreach (var token in refused)
        {
            await _invoker.InvokeAsync(Forged(token));
        }

        var logs = _kit.Logs.All;
        await Assert.That(logs).IsNotEmpty();
        foreach (var token in refused.Append(valid).Where(t => t is not null))
        {
            await Assert.That(logs).DoesNotContain(token!);
            // the payload and the signature segments on their own
            foreach (var segment in token!.Split('.').Skip(1).Where(s => s.Length > 0))
            {
                await Assert.That(logs).DoesNotContain(segment);
            }
        }

        await Assert.That(logs).DoesNotContain(CallerTokenKit.PayloadMarker);
        await Assert.That(logs).DoesNotContain(CallerTokenKit.TokenSubject);
        await Assert.That(logs).DoesNotContain(CallerTokenKit.TokenClient);
        await Assert.That(logs).DoesNotContain(CallerTokenKit.ServiceClientId);
        await Assert.That(logs).DoesNotContain(CallerTokenKit.KeyId);
        await Assert.That(logs).DoesNotContain("Eve Forger");
        await Assert.That(logs).DoesNotContain("forged-admin");
    }

    [Test]
    public async Task WhenTheSigningKeysCannotBeLoaded_TheAnswerIsHandlerFailed_NotForbidden_AndNothingStarts()
    {
        // an identity service nobody can reach: no keys, so the token cannot be judged
        using var offline = new OfflineKit();
        var invoker = offline.CreateInvoker(_catalog, _repository, _trigger);

        var response = await invoker.InvokeAsync(Forged(_kit.Create()));

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await _trigger.DidNotReceiveWithAnyArgs().StartExecutePipelineAsync(default!, default, default, default, default,
            default, default);
    }

    /// <summary>A validator whose identity service always answers 503.</summary>
    private sealed class OfflineKit : IDisposable
    {
        private readonly ServiceProvider _provider;

        public OfflineKit()
        {
            var environment = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
            environment.EnvironmentName.Returns(Microsoft.Extensions.Hosting.Environments.Production);
            _provider = new ServiceCollection()
                .AddLogging()
                .AddSingleton(environment)
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
                .AddCkMethodCallerTokenValidation(o => o.Authority = CallerTokenKit.Authority)
                .AddSingleton<IHttpClientFactory>(new Unavailable())
                .BuildServiceProvider();
        }

        public PipelineMethodInvoker CreateInvoker(IPipelineMethodCatalog catalog, ICommunicationRepository repository,
            ITriggerManagementService trigger)
        {
            var options = Options.Create(new PipelineMethodOptions());
            return new PipelineMethodInvoker(catalog, repository,
                new ServiceCollection().AddSingleton(trigger).BuildServiceProvider(),
                new PipelineMethodHandlerInstance(options, TimeProvider.System), options, TimeProvider.System,
                _provider.GetRequiredService<ICkMethodCallerTokenValidator>(),
                new CapturingLogger<PipelineMethodInvoker>());
        }

        public void Dispose() => _provider.Dispose();

        private sealed class Unavailable : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new Down(), disposeHandler: true);
        }

        private sealed class Down : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
