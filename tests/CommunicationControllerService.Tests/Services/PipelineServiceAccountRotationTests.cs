using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5032 — the deliberate secret rotation of an adapter's pipeline service account.
///
/// <para>
/// The AB#5027 convergence paths are built to <b>never</b> rotate: they re-send the plaintext they
/// already hold, because a service restart that invalidated every adapter's credential would be a
/// self-inflicted outage. That makes a leaked or aged secret unretireable — this path closes that,
/// and the whole risk of it is that the two halves (identity hash, tenant configuration) must never
/// end up apart.
/// </para>
/// </summary>
internal class PipelineServiceAccountRotationTests
{
    private const string TenantId = "tenantId";
    private const string AuthorityUrl = "https://identity.example.com";
    private const string PublicUrl = "https://communication.example.com";
    private const string ExistingSecret = "the-previous-plaintext-secret";

    private readonly ICommunicationRepository _communicationRepository =
        Substitute.For<ICommunicationRepository>();

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();

    private readonly PipelineServiceAccountProvisioningService _sut;

    public PipelineServiceAccountRotationTests()
    {
        ArrangeIdentityResponse(CreateIdentityDataResult.Success);

        var services = new ServiceCollection();
        services.AddSingleton(_commandClient);

        _sut = new PipelineServiceAccountProvisioningService(
            _communicationRepository,
            new PipelineServiceAccountResolver(_communicationRepository),
            services.BuildServiceProvider(),
            _eventService,
            Options.Create(new CommunicationControllerOptions
            {
                AuthorityUrl = AuthorityUrl,
                PublicUrl = PublicUrl
            }));
    }

    private void ArrangeIdentityResponse(CreateIdentityDataResult result)
    {
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult> { Response = result });
    }

    private RtServiceAccountConfiguration ArrangeProvisionedAdapter(RtAdapter adapter,
        string secret = ExistingSecret)
    {
        var serviceAccount = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId),
            ClientId = PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId),
            ClientSecret = secret,
            IssuerUri = AuthorityUrl,
            TenantId = TenantId
        };

        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(serviceAccount);
        _communicationRepository
            .GetServiceAccountByWellKnownNameAsync(TenantId, serviceAccount.RtWellKnownName!)
            .Returns(serviceAccount);
        return serviceAccount;
    }

    /// <summary>Every plaintext that was sent to the identity service, in order.</summary>
    private List<string?> SentSecrets()
    {
        return _commandClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name ==
                        nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
            .Select(c => ((CreateIdentityDataCommandRequest)c.GetArguments()[0]!).Clients!.Single().ClientSecret)
            .ToList();
    }

    private List<RtServiceAccountConfiguration> SavedConfigurations()
    {
        return _communicationRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name ==
                        nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .Select(c => (RtServiceAccountConfiguration)c.GetArguments()[2]!)
            .ToList();
    }

    // ------------------------------------------------------------------ both sides renewed

    [Test]
    public async Task Rotate_RenewsBothSidesWithTheSameNewSecret()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var existing = ArrangeProvisionedAdapter(adapter);

        var result = await _sut.RotateAdapterSecretAsync(TenantId, adapter);

        var sentSecret = SentSecrets().Single();
        var saved = SavedConfigurations().Single();

        using var _ = Assert.Multiple();
        // The point of the whole feature: a NEW secret, and the identical one on both sides.
        await Assert.That(sentSecret).IsNotNull();
        await Assert.That(sentSecret).IsNotEqualTo(ExistingSecret);
        await Assert.That(saved.ClientSecret).IsEqualTo(sentSecret);

        // Everything else about the entity stays put — same rtId (an update, not a second entity),
        // same well-known name (the key the mesh adapter resolves by), same client.
        await Assert.That(saved.RtId).IsEqualTo(existing.RtId);
        await Assert.That(saved.RtWellKnownName).IsEqualTo(existing.RtWellKnownName);
        await Assert.That(saved.ClientId).IsEqualTo(existing.ClientId);
        // AB#5111: every service-account write converges IssuerUri onto the portable token.
        await Assert.That(saved.IssuerUri)
            .IsEqualTo(PipelineServiceAccountProvisioningService.IssuerUriToken);
        await Assert.That(saved.TenantId).IsEqualTo(TenantId);

        await Assert.That(result.WasCreated).IsFalse();
        await Assert.That(result.RequiresPipelineRedeploy).IsTrue();
        await Assert.That(result.ClientId).IsEqualTo(existing.ClientId);
        await Assert.That(result.WellKnownName).IsEqualTo(existing.RtWellKnownName);
    }

    [Test]
    public async Task Rotate_UpdatesTheExistingEntityInsteadOfInsertingASecondOne()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeProvisionedAdapter(adapter);

        await _sut.RotateAdapterSecretAsync(TenantId, adapter);

        var isNewEntity = (bool)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .GetArguments()[3]!;

        await Assert.That(isNewEntity).IsFalse();
    }

    [Test]
    public async Task Rotate_IdentityIsWrittenBeforeTheConfiguration()
    {
        // Ordering IS the consistency argument: identity first means a failure there changes
        // nothing at all, rather than leaving a configuration whose secret no client accepts.
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeProvisionedAdapter(adapter);

        var identityWritten = false;
        var identityWrittenFirst = false;
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(_ =>
            {
                identityWritten = true;
                return new EnumCommandResponse<CreateIdentityDataResult>
                    { Response = CreateIdentityDataResult.Success };
            });
        _communicationRepository
            .SavePipelineServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(_ =>
            {
                identityWrittenFirst = identityWritten;
                return Task.CompletedTask;
            });

        await _sut.RotateAdapterSecretAsync(TenantId, adapter);

        await Assert.That(identityWrittenFirst).IsTrue();
    }

    // ------------------------------------------------------------------ repeatability

    [Test]
    public async Task Rotate_Twice_LeavesBothSidesConsistentAgain()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var existing = ArrangeProvisionedAdapter(adapter);

        await _sut.RotateAdapterSecretAsync(TenantId, adapter);
        var firstSecret = SavedConfigurations().Single().ClientSecret;

        // The repository is a stub, so mirror the write the first rotation just made — otherwise
        // the second call would still read the pre-rotation plaintext.
        existing.ClientSecret = firstSecret;

        await _sut.RotateAdapterSecretAsync(TenantId, adapter);

        var sentSecrets = SentSecrets();
        var savedConfigurations = SavedConfigurations();

        using var _ = Assert.Multiple();
        await Assert.That(sentSecrets.Count).IsEqualTo(2);
        await Assert.That(savedConfigurations.Count).IsEqualTo(2);
        // Each pass is internally consistent …
        await Assert.That(savedConfigurations[0].ClientSecret).IsEqualTo(sentSecrets[0]);
        await Assert.That(savedConfigurations[1].ClientSecret).IsEqualTo(sentSecrets[1]);
        // … and the second pass really rotated rather than re-sending the first secret.
        await Assert.That(sentSecrets[1]).IsNotEqualTo(sentSecrets[0]);
        // Still one entity, not a new one per rotation.
        await Assert.That(savedConfigurations[1].RtId).IsEqualTo(existing.RtId);
    }

    // ------------------------------------------------------------------ failure on either side

    [Test]
    public async Task Rotate_IdentityRefusal_LeavesTheConfigurationUntouched()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeProvisionedAdapter(adapter);
        ArrangeIdentityResponse(CreateIdentityDataResult.FailedTenantHasNoIdentityCk);

        await Assert.That(async () => await _sut.RotateAdapterSecretAsync(TenantId, adapter))
            .Throws<InvalidOperationException>();

        // Nothing written: both sides still carry the old secret, which is a consistent state.
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            default!, default, default!, default);
    }

    [Test]
    public async Task Rotate_ConfigurationWriteFails_RestoresThePreviousSecretAtTheIdentityService()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeProvisionedAdapter(adapter);
        _communicationRepository
            .SavePipelineServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("mongo down")));

        await Assert.That(async () => await _sut.RotateAdapterSecretAsync(TenantId, adapter))
            .Throws<InvalidOperationException>();

        var sentSecrets = SentSecrets();

        using var _ = Assert.Multiple();
        // The compensation: the second identity write puts the plaintext the configuration still
        // holds back on the client, so the adapters running on it keep working. No half state.
        await Assert.That(sentSecrets.Count).IsEqualTo(2);
        await Assert.That(sentSecrets[0]).IsNotEqualTo(ExistingSecret);
        await Assert.That(sentSecrets[1]).IsEqualTo(ExistingSecret);
    }

    [Test]
    public async Task Rotate_ConfigurationWriteFails_AndRollbackFails_StillReportsTheOriginalFailure()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeProvisionedAdapter(adapter);
        _communicationRepository
            .SavePipelineServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("mongo down")));

        var identityCalls = 0;
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(_ =>
            {
                identityCalls++;
                return identityCalls == 1
                    ? new EnumCommandResponse<CreateIdentityDataResult>
                        { Response = CreateIdentityDataResult.Success }
                    : throw new TimeoutException("identity gone");
            });

        // The caller must learn that the rotation failed — never a swallowed rollback error that
        // makes a failed rotation look like a successful one.
        var exception = await Assert
            .That(async () => await _sut.RotateAdapterSecretAsync(TenantId, adapter))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("mongo down");
    }

    // ------------------------------------------------------------------ degenerate case

    [Test]
    public async Task Rotate_AdapterWithoutServiceAccount_ProvisionsOneAndNeedsNoRedeploy()
    {
        // Nothing was running under an old credential, so this is a first provisioning that just
        // happens to have been triggered through the rotation verb.
        var adapter = RtEntityCreator.CreateAdapter();

        var result = await _sut.RotateAdapterSecretAsync(TenantId, adapter);

        var saved = SavedConfigurations().Single();

        using var _ = Assert.Multiple();
        await Assert.That(result.WasCreated).IsTrue();
        await Assert.That(result.RequiresPipelineRedeploy).IsFalse();
        await Assert.That(saved.ClientSecret).IsEqualTo(SentSecrets().Single());
        await Assert.That(saved.ClientId)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId));
    }

    // ------------------------------------------------------------------ secret hygiene

    [Test]
    [NotInParallel(nameof(PipelineServiceAccountRotationTests))]
    public async Task Rotate_NeverWritesEitherSecretToAnyLogTarget()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        ArrangeProvisionedAdapter(adapter);

        var memoryTarget = new NLog.Targets.MemoryTarget("rotation-secret-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            await _sut.RotateAdapterSecretAsync(TenantId, adapter);

            var newSecret = SavedConfigurations().Single().ClientSecret!;

            using var _ = Assert.Multiple();
            // Neither the new secret nor the retired one — a retired secret is still secret
            // material, and the rotation path is the one place that holds both at once.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(newSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(newSecret[..8], StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ExistingSecret, StringComparison.Ordinal)))
                .IsFalse();
            // The probe only means something if the run logged at all.
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    [NotInParallel(nameof(PipelineServiceAccountRotationTests))]
    public async Task Rotate_FailureOnBothSides_StillWritesNoSecretToAnyLogTarget()
    {
        // The loudest code path — an error log plus a rollback error log — is also the one most
        // likely to interpolate state into a message.
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        ArrangeProvisionedAdapter(adapter);
        _communicationRepository
            .SavePipelineServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("mongo down")));

        var identityCalls = 0;
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(_ =>
            {
                identityCalls++;
                return identityCalls == 1
                    ? new EnumCommandResponse<CreateIdentityDataResult>
                        { Response = CreateIdentityDataResult.Success }
                    : throw new TimeoutException("identity gone");
            });

        var memoryTarget = new NLog.Targets.MemoryTarget("rotation-failure-secret-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            await Assert.That(async () => await _sut.RotateAdapterSecretAsync(TenantId, adapter))
                .Throws<InvalidOperationException>();

            var newSecret = SentSecrets()[0]!;

            using var _ = Assert.Multiple();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(newSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ExistingSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }
}
