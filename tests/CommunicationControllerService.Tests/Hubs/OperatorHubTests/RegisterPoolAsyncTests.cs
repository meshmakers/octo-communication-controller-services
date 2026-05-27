using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
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
    private readonly OperatorHub _hub;

    public RegisterPoolAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _poolService, _shutdownState);

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
    public async Task ValidRtId_RegistersConnectionAndSetsOnline()
    {
        await _hub.RegisterPoolAsync(TenantId, ValidPoolRtId);

        _connectionManager.Received(1).RegisterPoolForConnection(ConnectionId, TenantId, ValidPoolRtId);
        await _poolService.Received(1).SetCommunicationStateOnlineAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == ValidPoolRtId),
            ConnectionId);
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
