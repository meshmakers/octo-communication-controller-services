using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

internal class ReportWorkloadDeploymentProgressAsyncTests : IDisposable
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";
    private const string ProgressMessage =
        "Pod meshtest-xxx container 'adapter' waiting: ImagePullBackOff";

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

    public ReportWorkloadDeploymentProgressAsyncTests()
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
    public async Task Adapter_WritesPendingWithMessage()
    {
        GivenAdapterInRepository();

        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = TenantId,
            WorkloadName = "meshtest-adapter",
            WorkloadRtId = WorkloadRtId,
            Message = ProgressMessage,
        });

        await _repository.Received(1).SetAdapterDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id =>
                id.CkTypeId == SystemCommunicationCkIds.RtCkAdapterTypeId
                && id.RtId.ToString() == WorkloadRtId),
            RtDeploymentStateEnum.Pending,
            ProgressMessage);
    }

    [Test]
    public async Task Application_RoutesToApplicationSetter()
    {
        GivenApplicationInRepository();

        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = TenantId,
            WorkloadName = "meshtest-app",
            WorkloadRtId = WorkloadRtId,
            Message = ProgressMessage,
        });

        await _repository.Received(1).SetApplicationDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id =>
                id.CkTypeId == SystemCommunicationCkIds.RtCkApplicationTypeId
                && id.RtId.ToString() == WorkloadRtId),
            RtDeploymentStateEnum.Pending,
            ProgressMessage);
        await _repository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task NeverWritesNonPendingState()
    {
        // Defense in depth: progress reports must NEVER write Deployed or
        // Error — those states belong to the terminal status report path.
        // If a future refactor changes the controller-side state, the
        // progress watcher would inadvertently flip the UI to a final
        // state mid-helm-install.
        GivenAdapterInRepository();

        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            Message = ProgressMessage,
        });

        await _repository.DidNotReceive().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), RtDeploymentStateEnum.Deployed, Arg.Any<string?>());
        await _repository.DidNotReceive().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), RtDeploymentStateEnum.Error, Arg.Any<string?>());
    }

    [Test]
    public async Task MissingTenantOrRtId_SkipsRepositoryCall()
    {
        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = string.Empty,
            WorkloadRtId = WorkloadRtId,
            Message = ProgressMessage,
        });

        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = TenantId,
            WorkloadRtId = string.Empty,
            Message = ProgressMessage,
        });

        await _repository.DidNotReceiveWithAnyArgs().SetAdapterDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
        await _repository.DidNotReceiveWithAnyArgs().SetApplicationDeploymentStateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task WorkloadEntityNotFound_SkipsRepositoryWrite()
    {
        // Repository returns null (default) — entity was deleted between
        // the operator's deploy and the progress report (rare but possible
        // under tenant-delete cascade). Skip silently and rely on the
        // controller-side tracking to converge.
        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            Message = ProgressMessage,
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

        // Progress writes are best-effort; a failure must not crash the hub
        // for the rest of this connection's traffic.
        await _hub.ReportWorkloadDeploymentProgressAsync(new WorkloadDeploymentProgressDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            Message = ProgressMessage,
        });
    }
}
