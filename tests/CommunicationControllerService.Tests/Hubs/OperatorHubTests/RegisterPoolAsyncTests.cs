using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

internal class RegisterPoolAsyncTests : IDisposable
{
    private const string TenantId = "meshtest";
    private const string ConnectionId = "conn-1";
    private const string ValidPoolRtId = "6ad562f3ff7c40ff80275b84";

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

    public RegisterPoolAsyncTests()
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

    private void GivenPoolWithEnvironment(RtEnvironmentEnum environment, string name = "test-pool")
    {
        var pool = new RtPool
        {
            RtId = new OctoObjectId(ValidPoolRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = name,
            Environment = environment,
        };
        _repository.GetPoolsAsync(TenantId).Returns(new[] { pool });
    }

    [Test]
    public async Task LegacyMode_RegistersConnectionAndSetsOnline_AuditedAsInfo()
    {
        // Operator did not call RegisterOperatorAsync(...) with a mode (legacy build).
        // Enforcement is skipped; the registration is recorded as an information event
        // so the audit trail still shows that a mode-less operator claimed the pool.
        _connectionManager.GetOperatorMode(ConnectionId).Returns((bool?)null);

        await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId);

        _connectionManager.Received(1).RegisterPoolForConnection(ConnectionId, TenantId, ValidPoolRtId);
        await _poolService.Received(1).SetCommunicationStateOnlineAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == ValidPoolRtId),
            ConnectionId);
        await _eventService.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(s => s.Contains("Legacy operator") && s.Contains(ValidPoolRtId)),
            Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>());
        await _repository.DidNotReceiveWithAnyArgs().GetPoolsAsync(Arg.Any<string>());
    }

    [Test]
    public async Task CentralMode_CloudPool_RegistersAndSetsOnline()
    {
        _connectionManager.GetOperatorMode(ConnectionId).Returns(true);
        GivenPoolWithEnvironment(RtEnvironmentEnum.Cloud);

        await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId);

        _connectionManager.Received(1).RegisterPoolForConnection(ConnectionId, TenantId, ValidPoolRtId);
        await _poolService.Received(1).SetCommunicationStateOnlineAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == ValidPoolRtId),
            ConnectionId);
        await _eventService.DidNotReceiveWithAnyArgs().StoreErrorEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>());
    }

    [Test]
    public async Task EdgeMode_EdgePool_RegistersAndSetsOnline()
    {
        _connectionManager.GetOperatorMode(ConnectionId).Returns(false);
        GivenPoolWithEnvironment(RtEnvironmentEnum.Edge);

        await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId);

        _connectionManager.Received(1).RegisterPoolForConnection(ConnectionId, TenantId, ValidPoolRtId);
        await _poolService.Received(1).SetCommunicationStateOnlineAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == ValidPoolRtId),
            ConnectionId);
        await _eventService.DidNotReceiveWithAnyArgs().StoreErrorEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>());
    }

    [Test]
    public async Task EdgeMode_CloudPool_RejectsAndAudits()
    {
        // This is the regression that the operator-side reconnect bug exposed:
        // an edge operator must not be able to claim a Cloud pool, otherwise
        // workload-deploy events get routed to the edge K3s alongside the
        // central cluster.
        _connectionManager.GetOperatorMode(ConnectionId).Returns(false);
        GivenPoolWithEnvironment(RtEnvironmentEnum.Cloud, name: "the-cloud-pool");

        await Assert.That(async () => await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId))
            .Throws<HubException>();

        _connectionManager.DidNotReceiveWithAnyArgs().RegisterPoolForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _poolService.DidNotReceiveWithAnyArgs().SetCommunicationStateOnlineAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string>());
        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(s => s.Contains("the-cloud-pool") && s.Contains("Cloud") && s.Contains("edge")),
            Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>());
    }

    [Test]
    public async Task CentralMode_EdgePool_RejectsAndAudits()
    {
        _connectionManager.GetOperatorMode(ConnectionId).Returns(true);
        GivenPoolWithEnvironment(RtEnvironmentEnum.Edge, name: "the-edge-pool");

        await Assert.That(async () => await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId))
            .Throws<HubException>();

        _connectionManager.DidNotReceiveWithAnyArgs().RegisterPoolForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _poolService.DidNotReceiveWithAnyArgs().SetCommunicationStateOnlineAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string>());
        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(s => s.Contains("the-edge-pool") && s.Contains("Edge") && s.Contains("central")),
            Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>());
    }

    [Test]
    public async Task ModeSet_PoolNotFound_RejectsAndAudits()
    {
        _connectionManager.GetOperatorMode(ConnectionId).Returns(false);
        _repository.GetPoolsAsync(TenantId).Returns(Array.Empty<RtPool>());

        await Assert.That(async () => await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId))
            .Throws<HubException>();

        _connectionManager.DidNotReceiveWithAnyArgs().RegisterPoolForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(s => s.Contains("no such pool")),
            Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId?>());
    }

    [Test]
    public async Task EmptyRtId_ThrowsHubExceptionAndSkipsConnectionManager()
    {
        // Regression: an empty poolRtId used to surface as FormatException
        // from `new OctoObjectId(poolRtId)`, which SignalR wrapped in a
        // generic HubException. The operator-side log named no field, the
        // CR stayed Unregistered forever. The hub now rejects up-front
        // with a typed message that points at the offending field.
        await Assert.That(async () => await _hub.RegisterPoolAsync(TenantId, string.Empty))
            .Throws<HubException>();

        _connectionManager.DidNotReceiveWithAnyArgs().RegisterPoolForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _poolService.DidNotReceiveWithAnyArgs().SetCommunicationStateOnlineAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string>());
    }

    [Test]
    public async Task MalformedRtId_ThrowsHubExceptionAndSkipsConnectionManager()
    {
        await Assert.That(async () => await _hub.RegisterPoolAsync(TenantId, "not-an-objectid"))
            .Throws<HubException>();

        _connectionManager.DidNotReceiveWithAnyArgs().RegisterPoolForConnection(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await _poolService.DidNotReceiveWithAnyArgs().SetCommunicationStateOnlineAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string>());
    }

    [Test]
    public async Task ShortHexRtId_ThrowsHubException()
    {
        // 23 hex chars — close to the right shape but still invalid.
        await Assert.That(async () => await _hub.RegisterPoolAsync(TenantId, "6ad562f3ff7c40ff80275b8"))
            .Throws<HubException>();
    }
}
