using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Methods;

/// <summary>
///     AB#8595: the Adapter, DeployableWorkload and AdapterPool methods of System.Communication 4.17.0, served by the
///     controller's handlers through the real method host of octo-common-services. The definitions come from the CK cache of
///     a real MongoDB tenant that imported the System.Communication this service embeds; the caller token is validated by the
///     real validator over a fake identity service (throw-away signing key). Every method is called once through REST
///     (the controller action) and once as a method on the same substituted services: the service call and the answer must
///     be the same. A caller without the declared role and a token of another tenant are refused before any service is called.
/// </summary>
[Collection("CommunicationController")]
public sealed class CommunicationMethodHostTests(CommunicationControllerFixture fixture) : IAsyncLifetime
{
    private const string Authority = "https://identity.test/";
    private const string KeyId = "comm-method-key";
    private const string Subject = "comm-method-user";
    private const string CommunicationManagement = "CommunicationManagement";
    private const string UserManagement = "UserManagement";

    private static readonly JsonSerializerOptions RestJson = new(JsonSerializerDefaults.Web);

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly CapturingLogs _logs = new();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();
    private readonly IWorkloadLifecycleService _lifecycle = Substitute.For<IWorkloadLifecycleService>();

    private readonly IPipelineServiceAccountProvisioningService _provisioning =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly ICommunicationEventService _events = Substitute.For<ICommunicationEventService>();

    private readonly IAdapterPoolMirrorProvisioningService _mirrors =
        Substitute.For<IAdapterPoolMirrorProvisioningService>();

    private readonly ILeaseService _leases = Substitute.For<ILeaseService>();
    private readonly ILeaseSchedulerService _scheduler = Substitute.For<ILeaseSchedulerService>();
    private ServiceProvider? _provider;

    private string TenantId => fixture.TestTenantId;

    private ICkMethodHandlerInvoker Invoker => _provider!.GetRequiredService<ICkMethodHandlerInvoker>();

    public async ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The definitions come from the test tenant (the service validates against the system tenant).
            ["Methods:ValidationTenantIds:0"] = TenantId,
            ["Methods:Token:Authority"] = Authority
        }).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment);
        services.AddSingleton(fixture.GetService<ICkCacheService>());
        services.AddSingleton(fixture.GetService<IOptions<OctoSystemConfiguration>>());
        services.Configure<CommunicationControllerServices.Options.CommunicationControllerOptions>(o =>
            o.AuthorityUrl = Authority);
        services.AddSingleton(_repository).AddSingleton(_adapterService).AddSingleton(_lifecycle)
            .AddSingleton(_provisioning).AddSingleton(_events).AddSingleton(_mirrors).AddSingleton(_leases)
            .AddSingleton(_scheduler);

        services.AddCommunicationCkMethodHost(configuration).Should().BeTrue();
        services.AddSingleton<IHttpClientFactory>(new FakeIdentity(this));
        // The test has no broker: drop the bus side of the host (queues, announcement, heartbeat).
        foreach (var descriptor in services.Where(d => d.ImplementationType?.Name is "CkMethodHandlerStartupService"
                     or "CkMethodHandlerHeartbeatService").ToList())
        {
            services.Remove(descriptor);
        }

        _provider = services.BuildServiceProvider();

        // The startup validation of the SDK (order 15) finds the definitions and arms the declared authorization.
        foreach (var initializer in _provider.GetServices<IAsyncInitializationService>()
                     .Where(i => i.GetType().Name == "CkMethodHandlerValidationService"))
        {
            await initializer.InitializeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider != null)
        {
            await _provider.DisposeAsync();
        }

        _rsa.Dispose();
    }

    // ------------------------------------------------------------------ registration

    [Fact]
    public void EveryMethodOfTheModel_HasExactlyOneHandler()
    {
        Invoker.MethodIds.Should().BeEquivalentTo(CommunicationMethodHost.Handlers.Select(h => h.MethodId));
        CommunicationMethodHost.Handlers.Select(h => h.MethodId).Should().OnlyHaveUniqueItems();

        var cache = fixture.GetService<ICkCacheService>();
        var declared = new[] { "Adapter", "DeployableWorkload", "AdapterPool" }
            .SelectMany(type => cache.GetRtCkType(TenantId, new RtCkId<CkTypeId>($"System.Communication/{type}"))
                .DefinedMethods.Select(m => $"System.Communication/{type}.{m.MethodId}"))
            .ToList();
        declared.Should().BeEquivalentTo(CommunicationMethodHost.Handlers.Select(h => h.MethodId),
            "a method declared handler: service without a handler would answer METHOD_HANDLER_NOT_AVAILABLE forever");
    }

    // ------------------------------------------------------------------ authorization (every method)

    public static TheoryData<string> AllMethods()
    {
        var data = new TheoryData<string>();
        foreach (var handler in CommunicationMethodHost.Handlers)
        {
            data.Add(handler.MethodId);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task ACallerWithoutCommunicationManagement_IsRefused_AndNoServiceIsCalled(string methodId)
    {
        var response = await InvokeAsync(methodId, OctoObjectId.GenerateNewId().ToString(), ValidParameters(methodId),
            Token(roles: ["Development", UserManagement]));

        response.Success.Should().BeFalse();
        response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        NoServiceWasCalled();
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task ATokenOfAnotherTenant_IsRefused_AndNoServiceIsCalled(string methodId)
    {
        var response = await InvokeAsync(methodId, OctoObjectId.GenerateNewId().ToString(), ValidParameters(methodId),
            Token(tenant: "another-tenant"));

        response.Success.Should().BeFalse();
        response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        response.Error.Parameters[CkMethodErrorParameters.Reason].Should().Be("tenant-not-allowed");
        NoServiceWasCalled();
    }

    [Theory]
    [MemberData(nameof(AllMethods))]
    public async Task ACallWithoutToken_IsRefused(string methodId)
    {
        var response = await InvokeAsync(methodId, OctoObjectId.GenerateNewId().ToString(), ValidParameters(methodId),
            token: null);

        response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
        NoServiceWasCalled();
    }

    // ------------------------------------------------------------------ REST parity, method by method

    [Fact]
    public async Task DeployUpdate_CallsTheSameServiceAsRest()
    {
        var adapter = ArrangeAdapter();

        var rest = await AdapterController().DeployUpdate(adapter.ToRtEntityId().ToString());
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterDeployUpdateMethodId, adapter.RtId.ToString());

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue();
        await _adapterService.Received(2).DeployAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId());
    }

    [Fact]
    public async Task DeployUpdate_AFailedPush_IsTheDeclaredErrorWithTheMessageRestShows()
    {
        var adapter = ArrangeAdapter();
        var failure = AdapterServiceException.AdapterNotLoaded(TenantId, adapter.ToRtEntityId());
        _adapterService.DeployAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId()).Throws(failure);

        var rest = await AdapterController().DeployUpdate(adapter.ToRtEntityId().ToString());
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterDeployUpdateMethodId, adapter.RtId.ToString());

        var restMessage = ((ErrorResponse)((BadRequestObjectResult)rest).Value!).ErrorMessage;
        method.Error!.Code.Should().Be("ADAPTER_CONFIGURATION_DEPLOY_FAILED");
        method.Error.Parameters["message"].Should().Be(restMessage);
    }

    [Fact]
    public async Task DeployUpdate_AnIdThatNamesNoAdapter_IsTargetNotFound()
    {
        _repository.GetAdaptersAsync(TenantId).Returns([]);

        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterDeployUpdateMethodId,
            OctoObjectId.GenerateNewId().ToString());

        method.Error!.Code.Should().Be(CkMethodErrorCodes.MethodTargetNotFound);
        await _adapterService.DidNotReceiveWithAnyArgs().DeployAdapterConfigurationAsync(default!, default!);
    }

    [Fact]
    public async Task Wake_CallsTheSameServiceAsRest_AndMapsAFailure()
    {
        var workloadRtId = OctoObjectId.GenerateNewId();

        var rest = await AdapterController().Wake(workloadRtId.ToString(), _lifecycle);
        var method = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadWakeMethodId,
            workloadRtId.ToString(), ckTypeId: "System.Communication/Application");

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue();
        await _lifecycle.Received(2).EnsureWorkloadRunningAsync(TenantId, workloadRtId);

        var failure = WorkloadLifecycleServiceException.WakeTimedOut(TenantId, workloadRtId, "sleepy",
            TimeSpan.FromSeconds(60));
        _lifecycle.EnsureWorkloadRunningAsync(TenantId, workloadRtId).Throws(failure);
        var failedRest = await AdapterController().Wake(workloadRtId.ToString(), _lifecycle);
        var failedMethod = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadWakeMethodId,
            workloadRtId.ToString());

        failedMethod.Error!.Code.Should().Be("WORKLOAD_WAKE_FAILED");
        failedMethod.Error.Parameters["message"].Should()
            .Be(((ErrorResponse)((BadRequestObjectResult)failedRest).Value!).ErrorMessage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileServiceAccount_AnswersWhatRestAnswers_RolesOnlyWithUserManagement(bool userManagement)
    {
        var adapter = ArrangeAdapter();
        var reconciled = new ServiceAccountReconcileResult(PipelineServiceAccountProvisioningOutcome.Repaired,
            "octo-pipeline-sa-x", "pipeline-service-account-x", RoleChangesSkipped: !userManagement);
        _provisioning.ReconcileAdapterAsync(TenantId, adapter, Arg.Any<ServiceAccountReconcileContext>())
            .Returns(reconciled);
        string[] roles = userManagement ? [CommunicationManagement, UserManagement] : [CommunicationManagement];

        var rest = await AdapterController(roles).ReconcileServiceAccount(adapter.RtId.ToString(), _provisioning,
            _events);
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterReconcileServiceAccountMethodId,
            adapter.RtId.ToString(), token: Token(roles: roles));

        method.Success.Should().BeTrue();
        SameMembers(((OkObjectResult)rest).Value!, method.ResultJson!);
        var contexts = _provisioning.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPipelineServiceAccountProvisioningService.ReconcileAdapterAsync))
            .Select(c => (ServiceAccountReconcileContext)c.GetArguments()[2]!).ToList();
        contexts.Should().HaveCount(2);
        contexts.Should().OnlyContain(c => c.MaterializeRoles == userManagement);
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("source: User via CK method")));
    }

    [Fact]
    public async Task RotateServiceAccountSecret_AnswersWhatRestAnswers_WithoutASecret()
    {
        var adapter = ArrangeAdapter();
        _provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .Returns(new PipelineServiceAccountRotationResult("octo-pipeline-sa-y", "pipeline-service-account-y",
                WasCreated: false));

        var rest = await AdapterController().RotateServiceAccountSecret(adapter.RtId.ToString(), _provisioning,
            _events);
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterRotateServiceAccountSecretMethodId,
            adapter.RtId.ToString());

        method.Success.Should().BeTrue();
        SameMembers(((OkObjectResult)rest).Value!, method.ResultJson!);
        using var json = JsonDocument.Parse(method.ResultJson!);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .NotContain(n => n.Contains("secret", StringComparison.OrdinalIgnoreCase));
        await _provisioning.Received(2).RotateAdapterSecretAsync(TenantId, adapter);
    }

    [Fact]
    public async Task RotateServiceAccountSecret_AFailure_IsTheDeclaredError_AndTheOldSecretStays()
    {
        var adapter = ArrangeAdapter();
        _provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .Throws(new InvalidOperationException("identity bus timed out"));

        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterRotateServiceAccountSecretMethodId,
            adapter.RtId.ToString());

        method.Error!.Code.Should().Be("SERVICE_ACCOUNT_ROTATION_FAILED");
        method.Error.Parameters.Values.Should().NotContain(v => v.Contains("identity bus timed out"),
            "free text of an unknown exception stays in the log");
        await _events.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("The previous secret remains in effect.")));
    }

    [Fact]
    public async Task RefreshAndPublishMirrors_AnswerWhatRestAnswers_ForTheCallsTenant()
    {
        var borrower = new AdapterPoolMirrorSyncResult(1, 2, 1, 0, 1);
        var lender = new AdapterPoolMirrorSyncResult(3, 0, 0, 1);
        _mirrors.ProvisionForBorrowerAsync(TenantId, Arg.Any<CancellationToken>()).Returns(borrower);
        _mirrors.ProvisionForLenderAsync(TenantId, Arg.Any<CancellationToken>()).Returns(lender);
        var controller = AdapterPoolController();

        var restRefresh = await controller.RefreshMirrorsAsync();
        var restPublish = await controller.PublishMirrorsAsync();
        var refresh = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolRefreshMirrorsMethodId, null);
        var publish = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolPublishMirrorsMethodId, null);

        SameMembers(((OkObjectResult)restRefresh).Value!, refresh.ResultJson!);
        SameMembers(((OkObjectResult)restPublish).Value!, publish.ResultJson!);
        await _mirrors.Received(2).ProvisionForBorrowerAsync(TenantId, Arg.Any<CancellationToken>());
        await _mirrors.Received(2).ProvisionForLenderAsync(TenantId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GrantLease_AnswersWhatRestAnswers(bool granted)
    {
        var poolRtId = OctoObjectId.GenerateNewId();
        var adapterRtId = OctoObjectId.GenerateNewId();
        _leases.GrantLeaseAsync(TenantId, poolRtId, Arg.Any<LeaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(granted
                ? new LeaseGrantResult(true, "lease-1", "member-1", null)
                : LeaseGrantResult.Refused(LeaseRefusalReason.BorrowerAdapterUnknown, "borrower adapter unknown"));

        var rest = await AdapterPoolController().GrantLeaseAsync(poolRtId,
            new GrantAdapterPoolLeaseRequestDto("borrower", adapterRtId.ToString(), "exec-1", 120));
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolGrantLeaseMethodId, poolRtId.ToString(),
            new { borrowerTenantId = "borrower", adapterRtId = adapterRtId.ToString(), executionId = "exec-1", ttlSeconds = 120 });

        method.Success.Should().BeTrue();
        SameMembers(((OkObjectResult)rest).Value!, method.ResultJson!);
        var requests = _leases.ReceivedCalls().Select(c => (LeaseRequest)c.GetArguments()[2]!).ToList();
        requests.Should().HaveCount(2);
        requests[1].Should().Be(requests[0]);
        requests[0].Should().Be(new LeaseRequest("borrower", adapterRtId, "exec-1", TimeSpan.FromSeconds(120)));
    }

    [Theory]
    [InlineData("not-an-object-id", 60, "adapterRtId")]
    [InlineData(null, 0, "ttlSeconds")]
    [InlineData(null, 3601, "ttlSeconds")]
    public async Task GrantLease_InvalidParameters_AreValidationErrors_AndNoLeaseIsAsked(string? adapterRtId, int ttl,
        string field)
    {
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolGrantLeaseMethodId,
            OctoObjectId.GenerateNewId().ToString(),
            new { borrowerTenantId = "borrower", adapterRtId = adapterRtId ?? OctoObjectId.GenerateNewId().ToString(), ttlSeconds = ttl });

        method.Error!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        method.Error.Parameters[CkMethodErrorParameters.Field].Should().Be(field);
        _leases.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(QueueCancellationResult.Cancelled, null, typeof(NoContentResult))]
    [InlineData(QueueCancellationResult.AlreadyLeased, "EXECUTION_ALREADY_LEASED", typeof(ConflictObjectResult))]
    [InlineData(QueueCancellationResult.NotFound, "QUEUED_EXECUTION_NOT_FOUND", typeof(NotFoundObjectResult))]
    public async Task CancelQueuedExecution_MapsEveryOutcomeLikeRest(QueueCancellationResult outcome, string? code,
        Type restResult)
    {
        var poolRtId = OctoObjectId.GenerateNewId();
        _scheduler.CancelQueuedExecutionAsync(TenantId, poolRtId, "exec-9", Arg.Any<CancellationToken>())
            .Returns(outcome);

        var rest = await AdapterPoolController().CancelQueuedExecutionAsync(poolRtId, "exec-9");
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolCancelQueuedExecutionMethodId,
            poolRtId.ToString(), new { executionId = "exec-9" });

        rest.Should().BeOfType(restResult);
        if (code == null)
        {
            method.Success.Should().BeTrue();
        }
        else
        {
            method.Error!.Code.Should().Be(code);
        }

        await _scheduler.Received(2).CancelQueuedExecutionAsync(TenantId, poolRtId, "exec-9",
            Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ helpers

    private RtAdapter ArrangeAdapter()
    {
        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "mesh-adapter"
        };
        _repository.GetAdaptersAsync(TenantId).Returns([adapter]);
        return adapter;
    }

    private void NoServiceWasCalled()
    {
        foreach (var service in new object[]
                     { _repository, _adapterService, _lifecycle, _provisioning, _events, _mirrors, _leases, _scheduler })
        {
            service.ReceivedCalls().Should().BeEmpty($"{service.GetType().Name} must not be called for a refused caller");
        }
    }

    private static object ValidParameters(string methodId) => methodId switch
    {
        SystemCommunicationCkIds.AdapterPoolGrantLeaseMethodId => new
            { borrowerTenantId = "borrower", adapterRtId = OctoObjectId.GenerateNewId().ToString() },
        SystemCommunicationCkIds.AdapterPoolCancelQueuedExecutionMethodId => new { executionId = "exec-1" },
        _ => new { }
    };

    private Task<CkMethodResponse> InvokeAsync(string methodId, string? targetRtId, object? parameters = null,
        string? token = "<valid>", string? ckTypeId = null)
    {
        var declaringType = methodId[..methodId.LastIndexOf('.')];
        return Invoker.InvokeAsync(new CkMethodRequest(TenantId)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = methodId,
            CkTypeId = ckTypeId ?? declaringType,
            TargetRtId = targetRtId,
            ParametersJson = JsonSerializer.Serialize(parameters ?? new { }),
            CallerAccessToken = token == "<valid>" ? Token() : token,
            DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The REST answer and the method result carry the same members with the same values (camelCase JSON).</summary>
    private static void SameMembers(object restValue, string methodResultJson)
    {
        using var rest = JsonDocument.Parse(JsonSerializer.Serialize(restValue, restValue.GetType(), RestJson));
        using var method = JsonDocument.Parse(methodResultJson);
        foreach (var member in rest.RootElement.EnumerateObject())
        {
            method.RootElement.TryGetProperty(member.Name, out var value).Should()
                .BeTrue($"the method result must carry '{member.Name}' like the REST answer");
            value.GetRawText().Should().Be(member.Value.GetRawText(), member.Name);
        }
    }

    private AdapterController AdapterController(string[]? roles = null)
    {
        var controller = new AdapterController(NullLogger<AdapterController>.Instance, _repository, _adapterService,
            Substitute.For<ITenantLendingScopeResolver>());
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextOf(roles) };
        return controller;
    }

    private AdapterPoolController AdapterPoolController()
    {
        var controller = new AdapterPoolController(Substitute.For<IAdapterPoolConnectionManager>(), _scheduler, _leases,
            _mirrors, _repository, Substitute.For<ITenantLendingScopeResolver>(),
            NullLogger<AdapterPoolController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextOf(null) };
        return controller;
    }

    private DefaultHttpContext HttpContextOf(string[]? roles)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["tenantId"] = TenantId;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            (roles ?? [CommunicationManagement]).Select(r => new Claim("role", r)), "Bearer", "name", "role"));
        return context;
    }

    private string Token(string[]? roles = null, string tenant = "")
    {
        var payload = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            ["client_id"] = "studio",
            ["role"] = roles ?? [CommunicationManagement],
            ["tenant_id"] = string.IsNullOrEmpty(tenant) ? TenantId : tenant,
            ["scope"] = new[] { "octo_api", "openid" }
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Claims = payload,
            NotBefore = DateTime.UtcNow.AddMinutes(-5),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256)
        });
    }

    private string KeysJson()
    {
        var p = _rsa.ExportParameters(false);
        return "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + KeyId + "\",\"n\":\"" +
               Base64UrlEncoder.Encode(p.Modulus) + "\",\"e\":\"" + Base64UrlEncoder.Encode(p.Exponent) + "\"}]}";
    }

    /// <summary>Discovery document and JWKS of the fake identity service.</summary>
    private sealed class FakeIdentity(CommunicationMethodHostTests owner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(owner), disposeHandler: true);

        private sealed class Handler(CommunicationMethodHostTests owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var json = path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal)
                    ? $"{{\"issuer\":\"{Authority}\",\"jwks_uri\":\"{Authority}.well-known/openid-configuration/jwks\"}}"
                    : path.EndsWith("/jwks", StringComparison.Ordinal)
                        ? owner.KeysJson()
                        : null;
                return Task.FromResult(json == null
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                    : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    });
            }
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string All => string.Join("\n", _entries);

        public ILogger CreateLogger(string categoryName) => new Sink(_entries);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"[{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
        }
    }
}
