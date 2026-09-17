using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
/// Pins the reverse-sync state-restore contract: a Cloud operator reporting a
/// deploymentSite / workload it owns must restore <c>DeploymentState=Deployed</c> when
/// the current state diverges, rebuild the per-connection tracking so undeploy
/// fan-out keeps working, and silently skip deploymentSites whose Environment is not
/// Cloud (defense in depth — the per-operator mode check upstream might have
/// drifted, but Edge deploymentSite state must never be revived through this path).
/// </summary>
internal class RestoreDeployedStateAsyncTests : PoolServiceTestsBase
{
    private const string OperatorConnectionId = "op-conn-1";
    private static readonly OctoObjectId WorkloadRtId = OctoObjectId.GenerateNewId();

    private RtDeploymentSite MakePool(RtEnvironmentEnum environment, RtDeploymentStateEnum state, string name = "deploymentSite-a")
    {
        return new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = name,
            Environment = environment,
            DeploymentState = state,
        };
    }

    private RtAdapter MakeAdapter(RtDeploymentStateEnum state)
    {
        return new RtAdapter
        {
            RtId = WorkloadRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "adapter-a",
            DeploymentState = state,
        };
    }

    private static IReadOnlyList<OperatorDeployedDeploymentSiteReportDto> SinglePoolReport(params string[] workloadRtIds)
        => new[]
        {
            new OperatorDeployedDeploymentSiteReportDto
            {
                TenantId = TenantId,
                DeploymentSiteRtId = DeploymentSiteRtId.ToString(),
                DeploymentSiteName = "deploymentSite-a",
                WorkloadRtIds = workloadRtIds,
            },
        };

    [Test]
    public async Task EmptyReport_IsNoOp()
    {
        // Operator on first connect after install owns nothing. Don't hit
        // the repository, don't write audit events.
        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId,
            Array.Empty<OperatorDeployedDeploymentSiteReportDto>());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().GetDeploymentSitesAsync(Arg.Any<string>());
        await CommunicationEventService.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task PoolPending_RestoresToDeployedAndTracks()
    {
        // Smoking-gun scenario: deploymentSite is currently Pending (e.g. because an
        // operator restart caused state drift), helm release exists, operator
        // reports it as deployed. Controller must lift Pending → Deployed.
        var deploymentSite = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Pending);
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.Received(1).SetDeploymentSiteDeploymentStateAsync(
            TenantId, DeploymentSiteRtId, RtDeploymentStateEnum.Deployed);
        OperatorConnectionManager.Received(1).TrackDeployedDeploymentSite(
            Arg.Is<DeployedDeploymentSiteDto>(p => p.TenantId == TenantId && p.DeploymentSiteRtId == DeploymentSiteRtId.ToString()));
        OperatorConnectionManager.Received(1).RegisterDeploymentSiteForConnection(
            OperatorConnectionId, TenantId, DeploymentSiteRtId.ToString());
        await CommunicationEventService.Received(1).StoreInformationEventAsync(
            TenantId, Arg.Is<string>(s => s.Contains("Deployed") && s.Contains("reverse-sync")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task PoolAlreadyDeployed_SkipsStateWriteButStillTracks()
    {
        // No-op for the deployment state — already correct. But the operator
        // is on a NEW connection, so per-connection tracking + deploymentSite-for-conn
        // registration MUST be rebuilt, otherwise undeploy fan-out fails.
        var deploymentSite = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetDeploymentSiteDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.Received(1).TrackDeployedDeploymentSite(Arg.Any<DeployedDeploymentSiteDto>());
        OperatorConnectionManager.Received(1).RegisterDeploymentSiteForConnection(
            OperatorConnectionId, TenantId, DeploymentSiteRtId.ToString());
        await CommunicationEventService.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task EdgePool_IsSilentlySkipped()
    {
        // Defense in depth: even if the OperatorHub's mode check is bypassed
        // somehow, a per-deploymentSite Environment guard in DeploymentSiteService must refuse to
        // revive Edge-deploymentSite state — those entities live on a different cluster
        // and the controller has no authority to flip them.
        var deploymentSite = MakePool(RtEnvironmentEnum.Edge, RtDeploymentStateEnum.Disabled);
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetDeploymentSiteDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.DidNotReceive().TrackDeployedDeploymentSite(Arg.Any<DeployedDeploymentSiteDto>());
        OperatorConnectionManager.DidNotReceive().RegisterDeploymentSiteForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UnknownPoolRtId_IsSilentlySkipped()
    {
        // Operator reports a deploymentSite the controller has no record of (entity
        // deleted while operator was offline). Skip the entry, don't blow up
        // the whole reverse-sync — other reported deploymentSites still need restoring.
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(Array.Empty<RtDeploymentSite>());

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetDeploymentSiteDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.DidNotReceive().TrackDeployedDeploymentSite(Arg.Any<DeployedDeploymentSiteDto>());
    }

    [Test]
    public async Task WorkloadPending_RestoresToDeployedAndTracks()
    {
        // Companion to the deploymentSite case — workloads inside a Cloud deploymentSite that the
        // operator reports must also be lifted Pending → Deployed.
        var deploymentSite = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });
        var adapter = MakeAdapter(RtDeploymentStateEnum.Pending);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, WorkloadRtId).Returns(adapter);

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId,
            SinglePoolReport(WorkloadRtId.ToString()));

        await CommunicationRepository.Received(1).SetAdapterDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == WorkloadRtId),
            RtDeploymentStateEnum.Deployed);
        OperatorConnectionManager.Received(1).TrackDeployedWorkload(
            Arg.Is<WorkloadUndeployedDto>(w =>
                w.TenantId == TenantId
                && w.WorkloadRtId == WorkloadRtId.ToString()
                && w.WorkloadType == WorkloadTypeDto.Adapter));
    }

    [Test]
    public async Task WorkloadAlreadyDeployed_SkipsStateWriteButStillTracks()
    {
        var deploymentSite = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });
        var adapter = MakeAdapter(RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, WorkloadRtId).Returns(adapter);

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId,
            SinglePoolReport(WorkloadRtId.ToString()));

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.Received(1).TrackDeployedWorkload(Arg.Any<WorkloadUndeployedDto>());
    }

    [Test]
    public async Task InvalidWorkloadRtId_IsSilentlySkipped()
    {
        // Malformed rtId on the wire — log + skip, continue processing rest
        // of the deploymentSite. Don't blow up the whole reverse-sync.
        var deploymentSite = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });

        await DeploymentSiteService.RestoreDeployedStateAsync(OperatorConnectionId,
            SinglePoolReport("not-a-valid-octo-object-id"));

        await CommunicationRepository.DidNotReceiveWithAnyArgs().GetWorkloadByRtIdAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>());
        OperatorConnectionManager.DidNotReceive().TrackDeployedWorkload(Arg.Any<WorkloadUndeployedDto>());
    }
}
