using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

internal class OperatorConnectionManagerTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    // 24-char lowercase hex — shape of an actual OctoObjectId.
    private const string PoolRtIdX = "65d5c447b420da3fb12381bc";
    private const string PoolRtIdY = "67e10c0bfe3e19891bbfd261";
    private const string WorkloadRtId1 = "667ac108ef60ca86e830e47e";
    private const string WorkloadRtId2 = "686e0f17196fcd3c42ed8c77";

    private static OperatorConnectionManager CreateSut() =>
        new(Substitute.For<IHubContext<OperatorHub>>());

    private static DeployedPoolDto PoolDto(string tenantId, string poolRtId) =>
        new() { TenantId = tenantId, PoolRtId = poolRtId };

    [Test]
    public async Task GetDeployedPoolsForTenant_NeverNotified_ReturnsEmpty()
    {
        var sut = CreateSut();

        var pools = sut.GetDeployedPoolsForTenant(TenantA);

        await Assert.That(pools).IsNotNull();
        await Assert.That(pools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotifyPoolDeployedAsync_TracksPool()
    {
        var sut = CreateSut();

        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));

        var pools = sut.GetDeployedPoolsForTenant(TenantA);
        await Assert.That(pools.Count).IsEqualTo(1);
        await Assert.That(pools.Contains(PoolRtIdX)).IsTrue();
    }

    [Test]
    public async Task NotifyPoolDeployedAsync_DuplicatePool_TrackedOnce()
    {
        var sut = CreateSut();

        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));

        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NotifyPoolUndeployedAsync_RemovesTrackedPool()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));

        await sut.NotifyPoolUndeployedAsync(TenantA, PoolRtIdX);

        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotifyPoolUndeployedAsync_UnknownPool_NoOp()
    {
        var sut = CreateSut();

        await sut.NotifyPoolUndeployedAsync(TenantA, PoolRtIdX);

        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA).Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetDeployedPoolsForTenant_IsolatesTenants()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantB, PoolRtIdY));

        var poolsA = sut.GetDeployedPoolsForTenant(TenantA);
        var poolsB = sut.GetDeployedPoolsForTenant(TenantB);

        await Assert.That(poolsA.Contains(PoolRtIdX)).IsTrue();
        await Assert.That(poolsA.Contains(PoolRtIdY)).IsFalse();
        await Assert.That(poolsB.Contains(PoolRtIdY)).IsTrue();
        await Assert.That(poolsB.Contains(PoolRtIdX)).IsFalse();
    }

    [Test]
    public async Task GetDeployedPools_AcrossTenants_ReturnsAll()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantB, PoolRtIdY));

        var all = sut.GetDeployedPools().ToArray();

        await Assert.That(all.Length).IsEqualTo(2);
        await Assert.That(all.Any(p => p.TenantId == TenantA && p.PoolRtId == PoolRtIdX)).IsTrue();
        await Assert.That(all.Any(p => p.TenantId == TenantB && p.PoolRtId == PoolRtIdY)).IsTrue();
    }

    [Test]
    public async Task NotifyPoolUndeployedAsync_LastPoolForTenant_RemovesTenantBucket()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(PoolDto(TenantA, PoolRtIdX));

        await sut.NotifyPoolUndeployedAsync(TenantA, PoolRtIdX);

        await Assert.That(sut.GetDeployedPools()).IsEmpty();
        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA)).IsEmpty();
    }

    // ---- Workload tracking ----

    private static WorkloadDeployedDto WorkloadDeploy(string tenantId, string poolRtId,
        string workloadRtId, string workloadName, WorkloadTypeDto type = WorkloadTypeDto.Adapter) =>
        new()
        {
            TenantId = tenantId,
            PoolRtId = poolRtId,
            WorkloadRtId = workloadRtId,
            WorkloadName = workloadName,
            WorkloadType = type,
            ChartName = "test-chart",
            ChartVersion = "1.0.0",
        };

    [Test]
    public async Task NotifyWorkloadDeployedAsync_TracksWorkload()
    {
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));

        var tracked = sut.GetDeployedWorkloadsForTenant(TenantA);
        await Assert.That(tracked.Count).IsEqualTo(1);
        await Assert.That(tracked.Any(w => w.PoolRtId == PoolRtIdX && w.WorkloadRtId == WorkloadRtId1)).IsTrue();
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_PreservesWorkloadType()
    {
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX,
            WorkloadRtId1, "app-1", WorkloadTypeDto.Application));

        var tracked = sut.GetDeployedWorkloadsForTenant(TenantA);
        await Assert.That(tracked.Single().WorkloadType).IsEqualTo(WorkloadTypeDto.Application);
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_SameWorkloadTwice_TrackedOnce()
    {
        // Same workload RtId should overwrite (idempotent re-deploy).
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));

        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NotifyWorkloadUndeployedAsync_RemovesTrackedWorkload()
    {
        var sut = CreateSut();
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));

        await sut.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = TenantA,
            PoolRtId = PoolRtIdX,
            WorkloadRtId = WorkloadRtId1,
            WorkloadName = "wl-1",
            WorkloadType = WorkloadTypeDto.Adapter,
        });

        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA)).IsEmpty();
    }

    [Test]
    public async Task GetDeployedWorkloadsForTenant_IsolatesTenants()
    {
        var sut = CreateSut();
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantB, PoolRtIdY, WorkloadRtId2, "wl-2"));

        var trackedA = sut.GetDeployedWorkloadsForTenant(TenantA);
        var trackedB = sut.GetDeployedWorkloadsForTenant(TenantB);

        await Assert.That(trackedA.Any(w => w.WorkloadRtId == WorkloadRtId1)).IsTrue();
        await Assert.That(trackedA.Any(w => w.WorkloadRtId == WorkloadRtId2)).IsFalse();
        await Assert.That(trackedB.Any(w => w.WorkloadRtId == WorkloadRtId2)).IsTrue();
        await Assert.That(trackedB.Any(w => w.WorkloadRtId == WorkloadRtId1)).IsFalse();
    }

    [Test]
    public async Task NotifyWorkloadUndeployedAsync_LastWorkloadForTenant_RemovesTenantBucket()
    {
        var sut = CreateSut();
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));

        await sut.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = TenantA,
            PoolRtId = PoolRtIdX,
            WorkloadRtId = WorkloadRtId1,
            WorkloadName = "wl-1",
            WorkloadType = WorkloadTypeDto.Adapter,
        });

        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA)).IsEmpty();
    }

    [Test]
    public async Task GetDeployedWorkloadsForTenant_NoneTracked_ReturnsEmpty()
    {
        var sut = CreateSut();
        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA)).IsEmpty();
    }

    // ---- Workload routing (regression for cross-cluster broadcast bug) ----

    private const string ConnCentral = "conn-central";
    private const string ConnEdge = "conn-edge";
    private const string CloudPoolRtId = "65d5c447b420da3fb12381cc";
    private const string EdgePoolRtId = "65d5c447b420da3fb12381ee";

    private static (OperatorConnectionManager Sut, IHubContext<OperatorHub> Hub,
        ISingleClientProxy CentralProxy, ISingleClientProxy EdgeProxy) CreateRoutingSut()
    {
        var hub = Substitute.For<IHubContext<OperatorHub>>();
        var centralProxy = Substitute.For<ISingleClientProxy>();
        var edgeProxy = Substitute.For<ISingleClientProxy>();
        hub.Clients.Client(ConnCentral).Returns(centralProxy);
        hub.Clients.Client(ConnEdge).Returns(edgeProxy);
        return (new OperatorConnectionManager(hub), hub, centralProxy, edgeProxy);
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_RoutesOnlyToOperatorOwningTheTargetPool()
    {
        // Regression: deploying a workload assigned to an edge pool while a
        // central operator is also connected used to fan the event out to
        // both operators. The central operator would happily helm-install
        // the chart in its own namespace and report success, overwriting
        // the edge operator's failure on the runtime entity. Now workload
        // events must hit only the operator that registered the pool.
        var (sut, _, centralProxy, edgeProxy) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);
        sut.AddOperator(ConnEdge);
        sut.RegisterPoolForConnection(ConnCentral, TenantA, CloudPoolRtId);
        sut.RegisterPoolForConnection(ConnEdge, TenantA, EdgePoolRtId);

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, EdgePoolRtId,
            WorkloadRtId1, "modbus-pv"));

        await edgeProxy.Received(1).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadDeployedAsync),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
        await centralProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(
            default!, default!, default);
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_NoOperatorOwnsPool_DoesNotSend()
    {
        var (sut, _, centralProxy, edgeProxy) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);
        sut.AddOperator(ConnEdge);
        // Neither operator has claimed pool-orphan — must not fan out.
        sut.RegisterPoolForConnection(ConnCentral, TenantA, CloudPoolRtId);
        sut.RegisterPoolForConnection(ConnEdge, TenantA, EdgePoolRtId);

        const string orphanPoolRtId = "65d5c447b420da3fb12381ff";
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, orphanPoolRtId,
            WorkloadRtId1, "wl-1"));

        await centralProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);
        await edgeProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);
    }

    [Test]
    public async Task NotifyWorkloadUndeployedAsync_RoutesOnlyToOperatorOwningTheTargetPool()
    {
        var (sut, _, centralProxy, edgeProxy) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);
        sut.AddOperator(ConnEdge);
        sut.RegisterPoolForConnection(ConnCentral, TenantA, CloudPoolRtId);
        sut.RegisterPoolForConnection(ConnEdge, TenantA, EdgePoolRtId);

        await sut.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = TenantA,
            PoolRtId = EdgePoolRtId,
            WorkloadRtId = WorkloadRtId1,
            WorkloadName = "modbus-pv",
            WorkloadType = WorkloadTypeDto.Adapter,
        });

        await edgeProxy.Received(1).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
        await centralProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(
            default!, default!, default);
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_TracksWorkloadEvenWhenNoOperatorRegistered()
    {
        // Tracking is the source of truth for the tenant-delete cascade;
        // it must happen even when no operator currently owns the pool
        // (e.g. the operator disconnected between deploy and cascade).
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolRtIdX, WorkloadRtId1, "wl-1"));

        var tracked = sut.GetDeployedWorkloadsForTenant(TenantA);
        await Assert.That(tracked.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetOperatorMode_NeverSet_ReturnsNull()
    {
        var sut = CreateSut();

        await Assert.That(sut.GetOperatorMode("conn-1")).IsNull();
    }

    [Test]
    public async Task SetOperatorMode_True_Roundtrips()
    {
        var sut = CreateSut();

        sut.SetOperatorMode("conn-1", true);

        // Wrap bool? comparison in an expression to dodge the TUnit
        // .IsEqualTo(true)/.IsEqualTo(false) analyzer.
        await Assert.That(sut.GetOperatorMode("conn-1") == true).IsTrue();
    }

    [Test]
    public async Task SetOperatorMode_False_Roundtrips()
    {
        var sut = CreateSut();

        sut.SetOperatorMode("conn-1", false);

        await Assert.That(sut.GetOperatorMode("conn-1") == false).IsTrue();
    }

    [Test]
    public async Task SetOperatorMode_Null_ClearsPriorValue()
    {
        // Legacy operator: setting null must remove any prior entry so
        // GetOperatorMode returns null and the hub skips enforcement.
        var sut = CreateSut();
        sut.SetOperatorMode("conn-1", true);

        sut.SetOperatorMode("conn-1", null);

        await Assert.That(sut.GetOperatorMode("conn-1")).IsNull();
    }

    [Test]
    public async Task RemoveOperator_ClearsOperatorMode()
    {
        // Same connection id reused after a reconnect must not inherit the
        // previous incarnation's mode.
        var sut = CreateSut();
        sut.AddOperator("conn-1");
        sut.SetOperatorMode("conn-1", false);

        sut.RemoveOperator("conn-1");

        await Assert.That(sut.GetOperatorMode("conn-1")).IsNull();
    }

    // ---- Pending workload notifications (AB#4371) ----

    private static WorkloadUndeployedDto WorkloadUndeploy(string tenantId, string poolRtId,
        string workloadRtId, string workloadName) =>
        new()
        {
            TenantId = tenantId,
            PoolRtId = poolRtId,
            WorkloadRtId = workloadRtId,
            WorkloadName = workloadName,
            WorkloadType = WorkloadTypeDto.Adapter,
        };

    [Test]
    public async Task NotifyWorkloadUndeployedAsync_NoOwner_IsReplayedWhenThePoolRegisters()
    {
        // The prod-1 incident (AB#4371): undeploy fired while the pool was
        // orphaned used to be dropped, leaving the helm release running
        // forever. It must be queued and replayed on pool registration.
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);

        await sut.NotifyWorkloadUndeployedAsync(
            WorkloadUndeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));
        await centralProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);

        sut.RegisterPoolForConnection(ConnCentral, TenantA, CloudPoolRtId);
        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);

        await centralProxy.Received(1).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            Arg.Is<object?[]>(args =>
                args.Length == 1
                && ((WorkloadUndeployedDto)args[0]!).WorkloadRtId == WorkloadRtId1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_NoOwner_IsReplayedWhenThePoolRegisters()
    {
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);

        await sut.NotifyWorkloadDeployedAsync(
            WorkloadDeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));

        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);

        await centralProxy.Received(1).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadDeployedAsync),
            Arg.Is<object?[]>(args =>
                args.Length == 1
                && ((WorkloadDeployedDto)args[0]!).WorkloadRtId == WorkloadRtId1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PendingNotifications_UndeploySupersedesQueuedDeployOfSameWorkload()
    {
        // Deploy then undeploy while orphaned: only the undeploy may be
        // replayed — replaying the stale deploy after the undeploy would
        // resurrect the helm release.
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);

        await sut.NotifyWorkloadDeployedAsync(
            WorkloadDeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));
        await sut.NotifyWorkloadUndeployedAsync(
            WorkloadUndeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));

        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);

        await centralProxy.Received(1).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
        await centralProxy.DidNotReceive().SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadDeployedAsync),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FlushPendingWorkloadNotificationsAsync_NothingPending_NoOp()
    {
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);

        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);

        await centralProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);
    }

    [Test]
    public async Task FlushPendingWorkloadNotificationsAsync_SecondFlush_DoesNotReplayTwice()
    {
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);
        await sut.NotifyWorkloadUndeployedAsync(
            WorkloadUndeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));

        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);
        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);

        await centralProxy.Received(1).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PendingNotifications_ScopedToPool_FlushOfOtherPoolSendsNothing()
    {
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);
        await sut.NotifyWorkloadUndeployedAsync(
            WorkloadUndeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));

        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, EdgePoolRtId);

        await centralProxy.DidNotReceiveWithAnyArgs().SendCoreAsync(default!, default!, default);
    }

    [Test]
    public async Task FlushPendingWorkloadNotificationsAsync_SendFails_NotificationIsRequeued()
    {
        var (sut, _, centralProxy, _) = CreateRoutingSut();
        sut.AddOperator(ConnCentral);
        await sut.NotifyWorkloadUndeployedAsync(
            WorkloadUndeploy(TenantA, CloudPoolRtId, WorkloadRtId1, "mesh-adapter"));

        centralProxy.SendCoreAsync(default!, default!, default)
            .ReturnsForAnyArgs(
                _ => Task.FromException(new InvalidOperationException("connection gone")),
                _ => Task.CompletedTask);

        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);
        await sut.FlushPendingWorkloadNotificationsAsync(ConnCentral, TenantA, CloudPoolRtId);

        await centralProxy.Received(2).SendCoreAsync(
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }
}
