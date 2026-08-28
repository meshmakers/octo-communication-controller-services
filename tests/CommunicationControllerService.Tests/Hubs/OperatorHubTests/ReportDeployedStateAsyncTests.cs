using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

/// <summary>
/// Pins the OperatorHub's reverse-sync handler contract: Cloud operators may
/// restore deployed state via <c>ReportDeployedStateAsync</c>; edge / legacy
/// operators are rejected with a typed <c>HubException</c> and an audit event
/// before any state-write fires.
/// </summary>
internal class ReportDeployedStateAsyncTests : IDisposable
{
    private const string ConnectionId = "conn-1";

    private readonly IOperatorConnectionManager _connectionManager =
        Substitute.For<IOperatorConnectionManager>();
    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly IPoolService _poolService =
        Substitute.For<IPoolService>();
    private readonly IShutdownState _shutdownState =
        Substitute.For<IShutdownState>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly IWorkloadLifecycleService _workloadLifecycleService =
        Substitute.For<IWorkloadLifecycleService>();
    private readonly OperatorHub _hub;

    public ReportDeployedStateAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _poolService, _shutdownState,
            _eventService, _workloadLifecycleService);

        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        _hub.Context = context;
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task CloudMode_DelegatesToPoolService()
    {
        // AutoManagePools=true → Cloud operator → allowed.
        _connectionManager.GetOperatorMode(ConnectionId).Returns(true);
        var reports = new List<OperatorDeployedPoolReportDto>
        {
            new() { TenantId = "tenant-a", PoolRtId = "6ad562f3ff7c40ff80275b84", PoolName = "pool-a" },
        };

        await _hub.ReportDeployedStateAsync(reports);

        await _poolService.Received(1).RestoreDeployedStateAsync(ConnectionId, reports);
        await _eventService.DidNotReceiveWithAnyArgs().StoreErrorEventAsync(
            default!, default!);
    }

    [Test]
    public async Task EdgeMode_ThrowsHubExceptionAndDoesNotDelegate()
    {
        // AutoManagePools=false → edge operator → must NOT restore Cloud state.
        // Their helm releases live on a different cluster than the controller-managed
        // Cloud pools, so reverse-syncing from an edge node would falsely revive
        // entities that don't actually exist on the central cluster.
        _connectionManager.GetOperatorMode(ConnectionId).Returns(false);
        var reports = new List<OperatorDeployedPoolReportDto>
        {
            new() { TenantId = "tenant-a", PoolRtId = "6ad562f3ff7c40ff80275b84", PoolName = "pool-a" },
        };

        var ex = await Assert.ThrowsAsync<HubException>(
            async () => await _hub.ReportDeployedStateAsync(reports));

        await Assert.That(ex!.Message).Contains("edge");
        await Assert.That(ex!.Message).Contains("AutoManagePools");
        await _poolService.DidNotReceiveWithAnyArgs().RestoreDeployedStateAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<OperatorDeployedPoolReportDto>>());
        await _eventService.Received(1).StoreErrorEventAsync(
            string.Empty, Arg.Is<string>(s => s.Contains("edge")));
    }

    [Test]
    public async Task LegacyMode_ThrowsHubExceptionAndDoesNotDelegate()
    {
        // mode==null → legacy operator that didn't declare a mode. We don't
        // know if it's central or edge, so reject conservatively. This forces
        // operator builds to be upgraded before they can use the reverse-sync.
        _connectionManager.GetOperatorMode(ConnectionId).Returns((bool?)null);
        var reports = new List<OperatorDeployedPoolReportDto>
        {
            new() { TenantId = "tenant-a", PoolRtId = "6ad562f3ff7c40ff80275b84", PoolName = "pool-a" },
        };

        var ex = await Assert.ThrowsAsync<HubException>(
            async () => await _hub.ReportDeployedStateAsync(reports));

        await Assert.That(ex!.Message).Contains("legacy");
        await _poolService.DidNotReceiveWithAnyArgs().RestoreDeployedStateAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<OperatorDeployedPoolReportDto>>());
    }

    [Test]
    public async Task CloudMode_EmptyReport_StillDelegatesAsNoOp()
    {
        // An operator that genuinely owns nothing (e.g. fresh install) should
        // still be allowed to call this — the PoolService treats an empty
        // list as a logged no-op. Pin that we don't shortcut at the hub.
        _connectionManager.GetOperatorMode(ConnectionId).Returns(true);
        var reports = new List<OperatorDeployedPoolReportDto>();

        await _hub.ReportDeployedStateAsync(reports);

        await _poolService.Received(1).RestoreDeployedStateAsync(ConnectionId, reports);
    }
}
