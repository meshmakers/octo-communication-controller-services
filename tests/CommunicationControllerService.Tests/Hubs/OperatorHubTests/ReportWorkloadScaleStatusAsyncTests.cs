using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.OperatorHubTests;

internal class ReportWorkloadScaleStatusAsyncTests : IDisposable
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
    private readonly IWorkloadLifecycleService _workloadLifecycleService =
        Substitute.For<IWorkloadLifecycleService>();
    private readonly OperatorHub _hub;

    public ReportWorkloadScaleStatusAsyncTests()
    {
        _hub = new OperatorHub(_connectionManager, _repository, _poolService, _shutdownState,
            _eventService, _workloadLifecycleService);
    }

    public void Dispose()
    {
        _hub.Dispose();
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task ValidReport_DelegatesToLifecycleService()
    {
        var status = new WorkloadScaleStatusDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            WorkloadName = "meshtest-adapter",
            Replicas = 0,
            Success = true,
        };

        await _hub.ReportWorkloadScaleStatusAsync(status);

        // The hub only validates and forwards — the lifecycle service owns the
        // state machine, so the DTO must arrive unmodified.
        await _workloadLifecycleService.Received(1).OnScaleStatusReportedAsync(status);
    }

    [Test]
    public async Task MissingTenantOrRtId_SkipsDelegateCall()
    {
        await _hub.ReportWorkloadScaleStatusAsync(new WorkloadScaleStatusDto
        {
            TenantId = string.Empty,
            WorkloadRtId = WorkloadRtId,
            Replicas = 0,
            Success = true,
        });

        await _hub.ReportWorkloadScaleStatusAsync(new WorkloadScaleStatusDto
        {
            TenantId = TenantId,
            WorkloadRtId = " ",
            Replicas = 0,
            Success = true,
        });

        await _workloadLifecycleService.DidNotReceiveWithAnyArgs().OnScaleStatusReportedAsync(
            Arg.Any<WorkloadScaleStatusDto>());
    }
}
