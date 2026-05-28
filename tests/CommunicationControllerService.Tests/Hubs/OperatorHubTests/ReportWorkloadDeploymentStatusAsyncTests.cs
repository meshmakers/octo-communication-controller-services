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
    private readonly IShutdownState _shutdownState =
        Substitute.For<IShutdownState>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly OperatorHub _hub;

    public ReportWorkloadDeploymentStatusAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _poolService, _shutdownState,
            _eventService);
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    private void GivenAdapterInRepository()
    {
        var adapter = new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
        };
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(adapter);
    }

    private void GivenApplicationInRepository()
    {
        var application = new RtApplication
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
        };
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(application);
    }

    [Test]
    public async Task Success_OnAdapter_WritesDeployedStateAndNoMessage()
    {
        GivenAdapterInRepository();

        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
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
    public async Task Success_OnApplication_RoutesToApplicationSetter()
    {
        // Regression test: previously every status report was unconditionally
        // routed to SetAdapterDeploymentStateAsync — Application status reports
        // never landed in MongoDB and the Studio UI stayed Pending forever.
        GivenApplicationInRepository();

        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            WorkloadName = "meshtest-app",
            WorkloadRtId = WorkloadRtId,
            Success = true,
        });

        await _repository.Received(1).SetApplicationDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id =>
                id.CkTypeId == SystemCommunicationCkIds.RtCkApplicationTypeId
                && id.RtId.ToString() == WorkloadRtId),
            RtDeploymentStateEnum.Deployed,
            null);
        await _repository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Failure_WritesErrorStateAndForwardsMessage()
    {
        GivenAdapterInRepository();

        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
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
    public async Task WorkloadEntityNotFound_SkipsRepositoryWrite()
    {
        // Repository returns null (default) — the entity has been deleted
        // between the operator's deploy and the status report. Nothing to
        // persist; log a warning and move on.
        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            Success = true,
        });

        await _repository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
        await _repository.DidNotReceiveWithAnyArgs().SetApplicationDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task RepositoryThrows_SwallowsException()
    {
        GivenAdapterInRepository();
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
