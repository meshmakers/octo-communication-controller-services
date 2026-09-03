using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5027 phase 2 — provisioning of the identity a pipeline executes as.
///
/// <para>
/// These tests carry the load of the phase: phase 1's deploy guard refuses every pipeline whose
/// adapter has no service account, and before this service nothing on the platform created one, so
/// "does the provisioning actually produce something the guard accepts, exactly once" is the
/// question that decides whether the two phases can ship together.
/// </para>
/// </summary>
internal class PipelineServiceAccountProvisioningServiceTests
{
    private const string TenantId = "tenantId";
    private const string AuthorityUrl = "https://identity.example.com";
    private const string PublicUrl = "https://communication.example.com";

    private readonly ICommunicationRepository _communicationRepository =
        Substitute.For<ICommunicationRepository>();

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();

    private readonly PipelineServiceAccountProvisioningService _sut;

    public PipelineServiceAccountProvisioningServiceTests()
    {
        ArrangeIdentityResponse(CreateIdentityDataResult.Success);

        // The command client is scoped in production (MassTransit IRequestClient), so the service
        // resolves it per call — a real container keeps that path under test instead of bypassing it.
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

    /// <summary>The steady state: entity present, complete, linked and pointing at this instance.</summary>
    private RtServiceAccountConfiguration ArrangeProvisionedAdapter(RtAdapter adapter, string secret = "existing-secret")
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

    private CreateIdentityDataCommandRequest CapturedIdentityRequest()
    {
        var calls = _commandClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
            .ToList();
        return (CreateIdentityDataCommandRequest)calls.Single().GetArguments()[0]!;
    }

    // ---------------------------------------------------------------- secret generation

    [Test]
    public async Task GenerateSecret_IsUrlSafeAndCarriesFullEntropy()
    {
        var secret = PipelineServiceAccountProvisioningService.GenerateSecret();

        using var _ = Assert.Multiple();
        // 48 bytes base64url-encode to 64 characters with no padding left to strip.
        await Assert.That(secret.Length).IsEqualTo(64);
        await Assert.That(secret.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')).IsTrue();
    }

    [Test]
    public async Task GenerateSecret_IsRandom()
    {
        var secrets = Enumerable.Range(0, 200)
            .Select(_ => PipelineServiceAccountProvisioningService.GenerateSecret())
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(secrets.Count).IsEqualTo(200);
    }

    // ---------------------------------------------------------------- first provisioning

    [Test]
    public async Task Provision_FreshAdapter_CreatesClientWithBothGrantTypesAndTheApiScope()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        var client = CapturedIdentityRequest().Clients!.Single();

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Provisioned);
        await Assert.That(client.ClientId)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId));
        await Assert.That(client.AllowedGrantTypes).Contains("client_credentials");
        // Without the delegation URN Duende rejects an on-behalf-of request before the validator
        // runs, so AB#5031 would need every provisioned tenant touched again.
        await Assert.That(client.AllowedGrantTypes).Contains(Constants.OnBehalfOfGrantType);
        await Assert.That(client.AllowedScopes).IsEquivalentTo(new[] { CommonConstants.OctoApiFullAccess });
        await Assert.That(client.RequireClientSecret).IsTrue();
        await Assert.That(client.ClientSecret).IsNotNull();
        await Assert.That(client.AllowOfflineAccess).IsFalse();
    }

    [Test]
    public async Task Provision_FreshAdapter_AssignsTheCommunicationManagementRole()
    {
        var adapter = RtEntityCreator.CreateAdapter();

        await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        var client = CapturedIdentityRequest().Clients!.Single();
        // The controller's own policies gate on the octo_api SCOPE, not on a role — but the AB#5031
        // delegated token is the INTERSECTION of service-account and user roles, so a role the
        // service account does not hold can never reach a delegated token.
        await Assert.That(client.AssignedRoleNames)
            .IsEquivalentTo(new[] { CommonConstants.CommunicationManagementRole });
    }

    [Test]
    public async Task Provision_FreshAdapter_WritesTheConfigurationEntityAndLinksIt()
    {
        var adapter = RtEntityCreator.CreateAdapter();

        await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        var call = _communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync));
        var arguments = call.GetArguments();
        var savedAdapter = (RtEntityId)arguments[1]!;
        var saved = (RtServiceAccountConfiguration)arguments[2]!;
        var isNewEntity = (bool)arguments[3]!;

        var clientSecret = CapturedIdentityRequest().Clients!.Single().ClientSecret;

        using var _ = Assert.Multiple();
        await Assert.That(isNewEntity).IsTrue();
        await Assert.That(savedAdapter).IsEqualTo(adapter.ToRtEntityId());
        await Assert.That(saved.RtWellKnownName)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId));
        await Assert.That(saved.ClientId)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId));
        // AB#5111: the portable deploy-time token, not this instance's concrete authority URL — it
        // is resolved when the configuration is projected to the adapter.
        await Assert.That(saved.IssuerUri)
            .IsEqualTo(PipelineServiceAccountProvisioningService.IssuerUriToken);
        // The delegation grant needs acr_values=tenant:{tenantId}; without it the adapter cannot
        // even resolve a tenant at the token endpoint.
        await Assert.That(saved.TenantId).IsEqualTo(TenantId);
        // The tenant-side entity must hold the PLAINTEXT — the identity side stores only the hash,
        // so the adapter could never authenticate from it.
        await Assert.That(saved.ClientSecret).IsEqualTo(clientSecret);
        // AB#5111: a fresh account is declarative from birth — the declaration defaults are
        // persisted so Studio shows (and an operator can edit) what will be materialised.
        await Assert.That(saved.AssignedRoleNames!.ToArray())
            .IsEquivalentTo(new[] { CommonConstants.CommunicationManagementRole });
        await Assert.That(saved.AllowDelegation ?? false).IsTrue();
    }

    // ---------------------------------------------------------------- idempotency

    [Test]
    public async Task Provision_SecondRun_DoesNotRotateTheSecretAndWritesNothing()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var existing = ArrangeProvisionedAdapter(adapter);

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned);
        // No entity write at all — a working configuration stays untouched.
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
        // The identity command is still sent (it is what converges grants, scope and roles for a
        // client created before this code) but carries the SAME plaintext, which hashes to the same
        // value identity-side — so nothing rotates and no duplicate client is created.
        await Assert.That(CapturedIdentityRequest().Clients!.Single().ClientSecret)
            .IsEqualTo(existing.ClientSecret);
    }

    [Test]
    public async Task Provision_EntityExistsButIsNotLinked_RelinksWithoutRotatingTheSecret()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var orphaned = ArrangeProvisionedAdapter(adapter);
        // The edge is gone — a half-applied earlier run, or an operator who unlinked it.
        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId)
            .Returns((RtServiceAccountConfiguration?)null);

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        var saved = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .GetArguments()[2]!;

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Repaired);
        // Adopted, not duplicated: same entity id, same secret.
        await Assert.That(saved.RtId).IsEqualTo(orphaned.RtId);
        await Assert.That(saved.ClientSecret).IsEqualTo(orphaned.ClientSecret);
    }

    [Test]
    public async Task Provision_EntityWithoutSecret_IssuesAFreshOne()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var incomplete = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId),
            ClientId = PipelineServiceAccountProvisioningService.BuildClientId(adapter.RtId),
            IssuerUri = AuthorityUrl,
            TenantId = TenantId
            // ClientSecret deliberately never written — e.g. a blueprint-seeded placeholder entity.
        };
        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(incomplete);
        _communicationRepository
            .GetServiceAccountByWellKnownNameAsync(TenantId, incomplete.RtWellKnownName!)
            .Returns(incomplete);

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        var call = _communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync));
        var saved = (RtServiceAccountConfiguration)call.GetArguments()[2]!;
        var isNewEntity = (bool)call.GetArguments()[3]!;

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Repaired);
        // Updated in place — a second credential entity next to the first would be worse than useless.
        await Assert.That(isNewEntity).IsFalse();
        await Assert.That(saved.RtId).IsEqualTo(incomplete.RtId);
        await Assert.That(saved.ClientSecret).IsNotNull();
        await Assert.That(saved.ClientSecret!.Length).IsEqualTo(64);
    }

    [Test]
    public async Task Provision_IssuerMovedToAnotherCluster_ConvergesTheEntity()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var stale = ArrangeProvisionedAdapter(adapter);
        stale.IssuerUri = "https://identity.old-cluster.example.com";

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        var saved = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
            .GetArguments()[2]!;

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Repaired);
        // AB#5111: converged onto the portable token, so the next cluster move is a no-op.
        await Assert.That(saved.IssuerUri)
            .IsEqualTo(PipelineServiceAccountProvisioningService.IssuerUriToken);
        // Moving cluster must not invalidate the credential the adapter already holds.
        await Assert.That(saved.ClientSecret).IsEqualTo(stale.ClientSecret);
    }

    // ---------------------------------------------------------------- secret hygiene

    [Test]
    [NotInParallel(nameof(PipelineServiceAccountProvisioningServiceTests))]
    public async Task Provision_NeverWritesTheSecretToAnyLogTarget()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";

        var memoryTarget = new NLog.Targets.MemoryTarget("provisioning-secret-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

            var saved = (RtServiceAccountConfiguration)_communicationRepository.ReceivedCalls()
                .Single(c => c.GetMethodInfo().Name ==
                             nameof(ICommunicationRepository.SavePipelineServiceAccountAsync))
                .GetArguments()[2]!;
            var secret = saved.ClientSecret!;

            using var _ = Assert.Multiple();
            // Not verbatim, and not truncated either — a prefix is still secret material.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(secret, StringComparison.Ordinal))).IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l =>
                l.Contains(secret[..8], StringComparison.Ordinal))).IsFalse();
            // The probe is only meaningful if the run actually logged something.
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    public async Task DistClientDto_ToString_RedactsTheSecret()
    {
        // The compiler-generated record ToString() prints every property, so a single
        // logger.LogDebug("{Dto}", dto) anywhere in the platform would leak a live client secret.
        var dto = new DistClientDto("client", "name", "https://example.com")
        {
            ClientSecret = "super-secret-value"
        };

        using var _ = Assert.Multiple();
        await Assert.That(dto.ToString()).DoesNotContain("super-secret-value");
        await Assert.That(dto.ToString()).Contains("client");
    }

    // ---------------------------------------------------------------- tenant sweep / backfill

    [Test]
    public async Task EnsureTenantProvisioned_BacksFillsAdaptersWithoutAnAccountAndLeavesProvisionedOnesAlone()
    {
        var fresh = RtEntityCreator.CreateAdapter();
        var alreadyProvisioned = RtEntityCreator.CreateAdapter();
        ArrangeProvisionedAdapter(alreadyProvisioned);
        _communicationRepository.GetAdaptersAsync(TenantId).Returns([fresh, alreadyProvisioned]);

        var report = await _sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.Provisioned).IsEqualTo(1);
        await Assert.That(report.AlreadyProvisioned).IsEqualTo(1);
        await Assert.That(report.HasFailures).IsFalse();
        // Exactly one entity write: the already-provisioned adapter is untouched.
        await _communicationRepository.Received(1).SavePipelineServiceAccountAsync(
            TenantId, fresh.ToRtEntityId(), Arg.Any<RtServiceAccountConfiguration>(), true);
    }

    [Test]
    public async Task EnsureTenantProvisioned_OneAdapterFails_TheOthersAreStillProvisioned()
    {
        var broken = RtEntityCreator.CreateAdapter();
        broken.Name = "broken-adapter";
        var healthy = RtEntityCreator.CreateAdapter();
        _communicationRepository.GetAdaptersAsync(TenantId).Returns([broken, healthy]);
        _communicationRepository
            .SavePipelineServiceAccountAsync(TenantId, broken.ToRtEntityId(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(Task.FromException(new InvalidOperationException("write failed")));

        var report = await _sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.Provisioned).IsEqualTo(1);
        await Assert.That(report.Failures.Count).IsEqualTo(1);
        await Assert.That(report.Failures[0]).Contains("broken-adapter");
        // Loud and persistent: the refusal an operator later sees on a pipeline deploy must have a
        // visible cause in the tenant's own event log, not only in a pod log.
        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("broken-adapter") && m.Contains("AB#5027")));
        await _communicationRepository.Received(1).SavePipelineServiceAccountAsync(
            TenantId, healthy.ToRtEntityId(), Arg.Any<RtServiceAccountConfiguration>(), true);
    }

    [Test]
    public async Task EnsureTenantProvisioned_AdapterLookupFails_ReportsInsteadOfThrowing()
    {
        _communicationRepository.GetAdaptersAsync(TenantId)
            .Returns(Task.FromException<IReadOnlyCollection<RtAdapter>>(
                new InvalidOperationException("ck cache unloaded")));

        var report = await _sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.HasFailures).IsTrue();
        await Assert.That(report.Provisioned).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureTenantProvisioned_NoAdapters_IsANoOp()
    {
        _communicationRepository.GetAdaptersAsync(TenantId).Returns([]);

        var report = await _sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.HasChanges).IsFalse();
        await Assert.That(report.HasFailures).IsFalse();
        await _commandClient.DidNotReceiveWithAnyArgs()
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    // ---------------------------------------------------------------- identity failures

    [Test]
    public async Task Provision_IdentityHasNoIdentityCk_FailsWithoutWritingTheEntity()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeIdentityResponse(CreateIdentityDataResult.FailedTenantHasNoIdentityCk);

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Failed);
        // No entity: a configuration whose client does not exist would let the guard pass while
        // every token request fails — worse than being refused at deploy time with a clear message.
        await _communicationRepository.DidNotReceiveWithAnyArgs().SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
        await _eventService.ReceivedWithAnyArgs(1).StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Provision_IdentityRoleSeedPending_StillWritesTheEntity()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeIdentityResponse(CreateIdentityDataResult.SuccessIdentityDataSeedPending);

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        // The client exists; only the role assignment was skipped identity-side. Pipelines must be
        // deployable — the next sweep re-sends and the roles converge then.
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Provisioned);
        await _communicationRepository.ReceivedWithAnyArgs(1).SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
    }
}
