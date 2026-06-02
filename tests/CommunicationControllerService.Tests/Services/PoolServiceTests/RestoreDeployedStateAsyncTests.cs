using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

/// <summary>
/// Pins the reverse-sync state-restore contract: a Cloud operator reporting a
/// pool / workload it owns must restore <c>DeploymentState=Deployed</c> when
/// the current state diverges, rebuild the per-connection tracking so undeploy
/// fan-out keeps working, and silently skip pools whose Environment is not
/// Cloud (defense in depth — the per-operator mode check upstream might have
/// drifted, but Edge pool state must never be revived through this path).
/// </summary>
internal class RestoreDeployedStateAsyncTests : PoolServiceTestsBase
{
    private const string OperatorConnectionId = "op-conn-1";
    private static readonly OctoObjectId WorkloadRtId = OctoObjectId.GenerateNewId();

    private RtPool MakePool(RtEnvironmentEnum environment, RtDeploymentStateEnum state, string name = "pool-a")
    {
        return new RtPool
        {
            RtId = PoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
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

    private static IReadOnlyList<OperatorDeployedPoolReportDto> SinglePoolReport(params string[] workloadRtIds)
        => new[]
        {
            new OperatorDeployedPoolReportDto
            {
                TenantId = TenantId,
                PoolRtId = PoolRtId.ToString(),
                PoolName = "pool-a",
                WorkloadRtIds = workloadRtIds,
            },
        };

    [Test]
    public async Task EmptyReport_IsNoOp()
    {
        // Operator on first connect after install owns nothing. Don't hit
        // the repository, don't write audit events.
        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId,
            Array.Empty<OperatorDeployedPoolReportDto>());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().GetPoolsAsync(Arg.Any<string>());
        await CommunicationEventService.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task PoolPending_RestoresToDeployedAndTracks()
    {
        // Smoking-gun scenario: pool is currently Pending (e.g. because an
        // operator restart caused state drift), helm release exists, operator
        // reports it as deployed. Controller must lift Pending → Deployed.
        var pool = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Pending);
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { pool });

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.Received(1).SetPoolDeploymentStateAsync(
            TenantId, PoolRtId, RtDeploymentStateEnum.Deployed);
        OperatorConnectionManager.Received(1).TrackDeployedPool(
            Arg.Is<DeployedPoolDto>(p => p.TenantId == TenantId && p.PoolRtId == PoolRtId.ToString()));
        OperatorConnectionManager.Received(1).RegisterPoolForConnection(
            OperatorConnectionId, TenantId, PoolRtId.ToString());
        await CommunicationEventService.Received(1).StoreInformationEventAsync(
            TenantId, Arg.Is<string>(s => s.Contains("Deployed") && s.Contains("reverse-sync")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task PoolAlreadyDeployed_SkipsStateWriteButStillTracks()
    {
        // No-op for the deployment state — already correct. But the operator
        // is on a NEW connection, so per-connection tracking + pool-for-conn
        // registration MUST be rebuilt, otherwise undeploy fan-out fails.
        var pool = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { pool });

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPoolDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.Received(1).TrackDeployedPool(Arg.Any<DeployedPoolDto>());
        OperatorConnectionManager.Received(1).RegisterPoolForConnection(
            OperatorConnectionId, TenantId, PoolRtId.ToString());
        await CommunicationEventService.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task EdgePool_IsSilentlySkipped()
    {
        // Defense in depth: even if the OperatorHub's mode check is bypassed
        // somehow, a per-pool Environment guard in PoolService must refuse to
        // revive Edge-pool state — those entities live on a different cluster
        // and the controller has no authority to flip them.
        var pool = MakePool(RtEnvironmentEnum.Edge, RtDeploymentStateEnum.Disabled);
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { pool });

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPoolDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.DidNotReceive().TrackDeployedPool(Arg.Any<DeployedPoolDto>());
        OperatorConnectionManager.DidNotReceive().RegisterPoolForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UnknownPoolRtId_IsSilentlySkipped()
    {
        // Operator reports a pool the controller has no record of (entity
        // deleted while operator was offline). Skip the entry, don't blow up
        // the whole reverse-sync — other reported pools still need restoring.
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(Array.Empty<RtPool>());

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId, SinglePoolReport());

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetPoolDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.DidNotReceive().TrackDeployedPool(Arg.Any<DeployedPoolDto>());
    }

    [Test]
    public async Task WorkloadPending_RestoresToDeployedAndTracks()
    {
        // Companion to the pool case — workloads inside a Cloud pool that the
        // operator reports must also be lifted Pending → Deployed.
        var pool = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { pool });
        var adapter = MakeAdapter(RtDeploymentStateEnum.Pending);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, WorkloadRtId).Returns(adapter);

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId,
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
        var pool = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { pool });
        var adapter = MakeAdapter(RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, WorkloadRtId).Returns(adapter);

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId,
            SinglePoolReport(WorkloadRtId.ToString()));

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>());
        OperatorConnectionManager.Received(1).TrackDeployedWorkload(Arg.Any<WorkloadUndeployedDto>());
    }

    [Test]
    public async Task InvalidWorkloadRtId_IsSilentlySkipped()
    {
        // Malformed rtId on the wire — log + skip, continue processing rest
        // of the pool. Don't blow up the whole reverse-sync.
        var pool = MakePool(RtEnvironmentEnum.Cloud, RtDeploymentStateEnum.Deployed);
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { pool });

        await PoolService.RestoreDeployedStateAsync(OperatorConnectionId,
            SinglePoolReport("not-a-valid-octo-object-id"));

        await CommunicationRepository.DidNotReceiveWithAnyArgs().GetWorkloadByRtIdAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>());
        OperatorConnectionManager.DidNotReceive().TrackDeployedWorkload(Arg.Any<WorkloadUndeployedDto>());
    }
}
