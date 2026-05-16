using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

internal class ReportWorkloadDeploymentStatusAsyncTests : IDisposable
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";

    private readonly IOperatorConnectionManager _connectionManager =
        Substitute.For<IOperatorConnectionManager>();
    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly IPoolService _poolService =
        Substitute.For<IPoolService>();
    private readonly OperatorHub _hub;

    public ReportWorkloadDeploymentStatusAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _poolService);
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task Success_WritesDeployedStateAndNoMessage()
    {
        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            PoolName = "cloud",
            WorkloadName = "meshtest-adapter",
            WorkloadRtId = WorkloadRtId,
            Success = true,
        });

        await _repository.Received(1).SetAdapterDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id =>
                id.CkTypeId == SystemCommunicationCkIds.RtCkAdapterTypeId
                && id.RtId.ToString() == WorkloadRtId),
            RtDeploymentStateEnum.Deployed,
            null);
    }

    [Test]
    public async Task Failure_WritesErrorStateAndForwardsMessage()
    {
        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            PoolName = "cloud",
            WorkloadName = "meshtest-adapter",
            WorkloadRtId = WorkloadRtId,
            Success = false,
            StatusMessage = "helm: secrets.databaseUser does not exist",
        });

        await _repository.Received(1).SetAdapterDeploymentStateAsync(
            TenantId,
            Arg.Any<RtEntityId>(),
            RtDeploymentStateEnum.Error,
            "helm: secrets.databaseUser does not exist");
    }

    [Test]
    public async Task MissingTenantOrRtId_SkipsRepositoryCall()
    {
        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = string.Empty,
            WorkloadRtId = WorkloadRtId,
            Success = true,
        });

        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            WorkloadRtId = string.Empty,
            Success = true,
        });

        await _repository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task RepositoryThrows_SwallowsException()
    {
        _repository
            .SetAdapterDeploymentStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        // Must not propagate — the hub stays up for other workloads even
        // when one status write fails.
        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            Success = true,
        });
    }
}
