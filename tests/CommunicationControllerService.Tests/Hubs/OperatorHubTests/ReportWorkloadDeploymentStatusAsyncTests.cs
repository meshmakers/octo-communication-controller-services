using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
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
    private readonly IDeploymentSiteService _deploymentSiteService =
        Substitute.For<IDeploymentSiteService>();
    private readonly IShutdownState _shutdownState =
        Substitute.For<IShutdownState>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly IWorkloadLifecycleService _workloadLifecycleService =
        Substitute.For<IWorkloadLifecycleService>();
    private readonly OperatorHub _hub;

    public ReportWorkloadDeploymentStatusAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _deploymentSiteService, _shutdownState,
            _eventService, _workloadLifecycleService);
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

    private void GivenAdapterPoolInRepository()
    {
        var pool = new RtAdapterPool
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
        };
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(pool);
    }

    /// <summary>
    ///     🔴 AB#4924 — an adapter pool is the THIRD DeployableWorkload and this switch only knew
    ///     two.
    /// </summary>
    /// <remarks>
    ///     The cost was invisible from every other test: the helm release rolled out, the pod came
    ///     up, the member registered on the pool hub — and the entity sat at <c>Pending</c> for
    ///     ever, because the operator's success report fell into the default arm and logged
    ///     "unsupported type 'RtAdapterPool'; skipping status persist". Studio therefore showed a
    ///     fully deployed pool as Pending with no error anywhere. The deploy PATH had the same hole
    ///     and was fixed in increment 5; nothing covered what the OPERATOR reports back, which is
    ///     the only thing that ever writes Deployed. Observed on a local kind cluster.
    /// </remarks>
    [Test]
    public async Task Success_OnAdapterPool_WritesDeployedState()
    {
        GivenAdapterPoolInRepository();

        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            WorkloadName = "Adapter Pool",
            WorkloadRtId = WorkloadRtId,
            Success = true
        });

        await _repository.Received(1).SetAdapterPoolDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId.ToString() == WorkloadRtId),
            RtDeploymentStateEnum.Deployed, null);
        await _repository.DidNotReceive().SetAdapterDeploymentStateAsync(Arg.Any<string>(),
            Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task Failure_OnAdapterPool_WritesErrorStateAndMessage()
    {
        GivenAdapterPoolInRepository();

        await _hub.ReportWorkloadDeploymentStatusAsync(new WorkloadDeploymentStatusDto
        {
            TenantId = TenantId,
            WorkloadName = "Adapter Pool",
            WorkloadRtId = WorkloadRtId,
            Success = false,
            StatusMessage = "helm failed"
        });

        await _repository.Received(1).SetAdapterPoolDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId.ToString() == WorkloadRtId),
            RtDeploymentStateEnum.Error, "helm failed");
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
