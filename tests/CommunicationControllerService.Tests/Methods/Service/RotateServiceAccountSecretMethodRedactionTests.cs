using System.Text.Json;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.Service;

/// <summary>
///     AB#8595 — <c>Adapter.RotateServiceAccountSecret</c> over the REAL provisioning service (AB#5032): the new secret is
///     generated, sent to the identity service and written into the <c>ServiceAccountConfiguration</c> — and appears in no
///     method result, no log line and no event, on success and on failure (redaction test like AB#6914).
/// </summary>
internal class RotateServiceAccountSecretMethodRedactionTests
{
    private const string TenantId = "acme";
    private const string ExistingSecret = "the-previous-plaintext-secret";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private readonly ICommunicationEventService _events = Substitute.For<ICommunicationEventService>();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly AdapterRotateServiceAccountSecretHandler _handler;
    private readonly RtAdapter _adapter = RtEntityCreator.CreateAdapter();

    public RotateServiceAccountSecretMethodRedactionTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_commandClient);
        var provisioning = new PipelineServiceAccountProvisioningService(
            _repository,
            new PipelineServiceAccountResolver(_repository),
            services.BuildServiceProvider(),
            _events,
            Options.Create(new CommunicationControllerOptions
            {
                AuthorityUrl = "https://identity.example.com", PublicUrl = "https://communication.example.com"
            }),
            TestSecretEncryption.Create().EncryptionService);
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs)); // lives as long as the test instance
        _handler = new AdapterRotateServiceAccountSecretHandler(_repository, provisioning, _events,
            loggerFactory.CreateLogger<AdapterRotateServiceAccountSecretHandler>());

        _adapter.Name = "mesh-adapter";
        _repository.GetAdaptersAsync(TenantId).Returns([_adapter]);
        var serviceAccount = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(_adapter.RtId),
            ClientId = PipelineServiceAccountProvisioningService.BuildClientId(_adapter.RtId),
            ClientSecret = SecretTestValues.Stored(ExistingSecret)
        };
        _repository.GetServiceAccountForAdapterAsync(TenantId, _adapter.RtId).Returns(serviceAccount);
        _repository.GetServiceAccountByWellKnownNameAsync(TenantId, serviceAccount.RtWellKnownName!)
            .Returns(serviceAccount);
    }

    private void ArrangeIdentity(CreateIdentityDataResult result) =>
        _commandClient.GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult> { Response = result });

    private List<string> SentSecrets() => _commandClient.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
        .Select(c => ((CreateIdentityDataCommandRequest)c.GetArguments()[0]!).Clients!.Single().ClientSecret)
        .OfType<string>()
        .ToList();

    private string EventTexts() => string.Join("\n", _events.ReceivedCalls()
        .SelectMany(c => c.GetArguments()).Select(a => a?.ToString() ?? string.Empty));

    private Task<CkMethodOutcome<AdapterRotateServiceAccountSecretResult>> CallAsync() =>
        _handler.HandleAsync(new CkMethodContext<AdapterRotateServiceAccountSecretParameters>(
            new CkMethodRequest(TenantId)
            {
                InvocationId = Guid.NewGuid(),
                MethodId = SystemCommunicationCkIds.AdapterRotateServiceAccountSecretMethodId,
                CkTypeId = "System.Communication/Adapter",
                TargetRtId = _adapter.RtId.ToString(),
                DeadlineUtc = DateTime.UtcNow.AddSeconds(60)
            }, new AdapterRotateServiceAccountSecretParameters()), CancellationToken.None);

    [Test]
    public async Task Success_TheNewSecretIsInNoResultLogOrEvent()
    {
        ArrangeIdentity(CreateIdentityDataResult.Success);

        var outcome = await CallAsync();

        var secret = SentSecrets().Single();
        var resultJson = JsonSerializer.Serialize(outcome.Value!.Value, CkMethodJson.Options);

        using var _ = Assert.Multiple();
        await Assert.That(outcome.IsSuccess).IsTrue();
        await Assert.That(secret).IsNotEqualTo(ExistingSecret);
        await Assert.That(outcome.Value.Value!.RequiresPipelineRedeploy).IsTrue();
        await Assert.That(resultJson).DoesNotContain(secret);
        await Assert.That(outcome.Value.ToString()).DoesNotContain(secret);
        await Assert.That(_logs.All).DoesNotContain(secret);
        await Assert.That(EventTexts()).DoesNotContain(secret);
        await Assert.That(EventTexts()).Contains("source: User via CK method");
    }

    [Test]
    public async Task Failure_TheGeneratedSecretIsInNoErrorLogOrEvent_AndTheAnswerIsTheDeclaredError()
    {
        ArrangeIdentity(CreateIdentityDataResult.Success);
        _repository.SavePipelineServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("mongo down")));

        var outcome = await CallAsync();

        var sent = SentSecrets();

        using var _ = Assert.Multiple();
        await Assert.That(outcome.Failure?.Code).IsEqualTo(CommunicationMethodErrors.ServiceAccountRotationFailed);
        await Assert.That(outcome.Failure!.Parameters.Count).IsEqualTo(0);
        await Assert.That(sent.Count).IsGreaterThanOrEqualTo(1);
        foreach (var secret in sent.Where(s => s != ExistingSecret))
        {
            await Assert.That(_logs.All).DoesNotContain(secret);
            await Assert.That(EventTexts()).DoesNotContain(secret);
        }

        await Assert.That(EventTexts()).Contains("The previous secret remains in effect.");
    }
}
