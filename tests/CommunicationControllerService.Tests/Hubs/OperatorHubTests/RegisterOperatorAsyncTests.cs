using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

/// <summary>
///     AB#6418 — an edge operator does not receive the tenant-crossing list of deployed Cloud sites.
/// </summary>
internal class RegisterOperatorAsyncTests : IDisposable
{
    private const string ConnectionId = "conn-1";

    private readonly IOperatorConnectionManager _connectionManager = Substitute.For<IOperatorConnectionManager>();
    private readonly OperatorHub _hub;

    public RegisterOperatorAsyncTests()
    {
        _connectionManager.GetDeployedDeploymentSites().Returns(new[]
        {
            new DeployedDeploymentSiteDto { TenantId = "meshtest", DeploymentSiteRtId = "6ad562f3ff7c40ff80275b84" },
        });

        _hub = new OperatorHub(_connectionManager, Substitute.For<ICommunicationRepository>(),
            Substitute.For<IDeploymentSiteService>(), Substitute.For<IShutdownState>(),
            Substitute.For<ICommunicationEventService>(), Substitute.For<IWorkloadLifecycleService>(),
            Substitute.For<IAdapterPoolMirrorProvisioningService>());
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
    public async Task EdgeOperator_GetsNoDeployedCloudSites()
    {
        var result = await _hub.RegisterOperatorAsync(false);

        await Assert.That(result.Count()).IsEqualTo(0);
        _connectionManager.Received(1).AddOperator(ConnectionId);
        _connectionManager.Received(1).SetOperatorMode(ConnectionId, false);
    }

    [Test]
    public async Task CentralOperator_GetsDeployedCloudSites()
    {
        var result = await _hub.RegisterOperatorAsync(true);

        await Assert.That(result.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyOperator_KeepsGettingDeployedCloudSites()
    {
        var result = await _hub.RegisterOperatorAsync();

        await Assert.That(result.Count()).IsEqualTo(1);
    }
}
