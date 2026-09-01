using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5027 — the proof that phase 1 and phase 2 fit together, and the reason they must ship in the
/// same release.
///
/// <para>
/// Phase 1 refuses to deploy a pipeline whose adapter has no service account. Nothing on the
/// platform created one, so on its own phase 1 would refuse <b>every</b> pipeline deploy in
/// <b>every</b> tenant. This test drives the real guard, the real resolver and the real provisioning
/// service over one substituted repository: deploy is refused before provisioning, and goes through
/// afterwards — with no change to the guard in between.
/// </para>
/// </summary>
internal class DeployPipelineAfterProvisioningTests : AdapterServiceTestsBase
{
    private const string AuthorityUrl = "https://identity.example.com";

    private const string PipelineDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private PipelineServiceAccountProvisioningService CreateProvisioningService()
    {
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult>
                { Response = CreateIdentityDataResult.Success });

        var services = new ServiceCollection();
        services.AddSingleton(_commandClient);

        return new PipelineServiceAccountProvisioningService(
            CommunicationRepository,
            // The real resolver, over the same repository the guard reads through.
            new PipelineServiceAccountResolver(CommunicationRepository),
            services.BuildServiceProvider(),
            CommunicationEventService,
            Options.Create(new CommunicationControllerOptions { AuthorityUrl = AuthorityUrl }));
    }

    private (RtAdapter Adapter, RtPipeline Pipeline) ArrangeUnprovisionedTenant()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "mesh-adapter";
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(PipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(), null, []));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));
        CommunicationRepository.GetAdaptersAsync(TenantId).Returns([rtAdapter]);

        // The unprovisioned starting point — the state every tenant is in the moment phase 1 ships.
        CommunicationRepository
            .GetServiceAccountForAdapterAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .Returns((RtServiceAccountConfiguration?)null);
        CommunicationRepository
            .GetServiceAccountByWellKnownNameAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns((RtServiceAccountConfiguration?)null);

        // Persisting the account makes the adapter edge resolvable, exactly as the repository does.
        CommunicationRepository
            .SavePipelineServiceAccountAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>())
            .Returns(callInfo =>
            {
                var saved = callInfo.ArgAt<RtServiceAccountConfiguration>(2);
                CommunicationRepository
                    .GetServiceAccountForAdapterAsync(TenantId, rtAdapter.RtId)
                    .Returns(saved);
                return Task.CompletedTask;
            });

        return (rtAdapter, rtPipeline);
    }

    [Test]
    public async Task DeployPipeline_BeforeProvisioning_IsRefused_AfterProvisioning_Succeeds()
    {
        var (adapter, pipeline) = ArrangeUnprovisionedTenant();

        // Phase 1 alone: every pipeline deploy in the tenant is refused.
        var refusal = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));
        await Assert.That(refusal!).IsTypeOf<AdapterServiceException>();

        // Phase 2 backfill over the very same tenant.
        var report = await CreateProvisioningService().EnsureTenantProvisionedAsync(TenantId);
        await Assert.That(report.Provisioned).IsEqualTo(1);

        // Same guard, same call — now it passes and the configuration reaches the adapter.
        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task ProvisionedAccount_IsProjectedIntoThePipelineConfiguration()
    {
        var (adapter, pipeline) = ArrangeUnprovisionedTenant();

        await CreateProvisioningService().EnsureTenantProvisionedAsync(TenantId);
        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        var configuration = (AdapterConfigurationDto)AdapterHubCallbacks.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAdapterHubCallbacks.AdapterConfigurationUpdatedAsync))
            .GetArguments()[1]!;

        // The adapter reads a ServiceAccountConfiguration by its RtWellKnownName, so the projected
        // entry must carry the deterministic name the provisioning built — this is the wire-level
        // hand-off between phase 2 and the mesh adapter's ServiceAccountTokenService.
        var projected = configuration.Pipelines.Single().Configurations.Single();
        await Assert.That(projected.ConfigurationName)
            .IsEqualTo(PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId));
    }
}
