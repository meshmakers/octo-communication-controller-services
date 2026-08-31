using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadLifecycleServiceTests;

internal class OnScaleStatusReportedAsyncTests
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";

    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly IOperatorConnectionManager _connectionManager =
        Substitute.For<IOperatorConnectionManager>();
    private readonly WorkloadLifecycleService _service;

    public OnScaleStatusReportedAsyncTests()
    {
        _service = new WorkloadLifecycleService(
            Substitute.For<ILogger<WorkloadLifecycleService>>(),
            _repository, _eventService, _connectionManager,
            Substitute.For<ILifecycleConfigurationService>(),
            Microsoft.Extensions.Options.Options.Create(new Meshmakers.Octo.Backend.CommunicationControllerServices.Options.CommunicationControllerOptions()));
    }

    private void GivenAdapterInLifecycleState(RtLifecycleStateEnum lifecycleState)
    {
        var adapter = new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            LifecycleState = lifecycleState,
        };
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(adapter);
    }

    private static WorkloadScaleStatusDto Status(bool success, int replicas, string? statusMessage = null) =>
        new()
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            WorkloadName = "meshtest-adapter",
            Replicas = replicas,
            Success = success,
            StatusMessage = statusMessage,
        };

    [Test]
    public async Task WorkloadEntityNotFound_SkipsAllWrites()
    {
        // Repository returns null (default) — the entity was deleted between the
        // scale request and the operator's ack. Nothing to persist.
        await _service.OnScaleStatusReportedAsync(Status(success: true, replicas: 0));

        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _eventService.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
        await _eventService.DidNotReceiveWithAnyArgs().StoreErrorEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task SuccessfulScaleToZero_Draining_TransitionsToHibernatedAndAudits()
    {
        GivenAdapterInLifecycleState(RtLifecycleStateEnum.Draining);

        await _service.OnScaleStatusReportedAsync(Status(success: true, replicas: 0));

        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Hibernated,
            Arg.Any<string?>());
        await _eventService.Received(1).StoreInformationEventAsync(
            TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task SuccessfulScaleToZero_NotDraining_IgnoresStaleAck()
    {
        // A scale-0 ack outside Draining is stale (a demand signal may already
        // have moved the workload on) — the waker owns the state, don't touch it.
        GivenAdapterInLifecycleState(RtLifecycleStateEnum.Running);

        await _service.OnScaleStatusReportedAsync(Status(success: true, replicas: 0));

        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _eventService.DidNotReceiveWithAnyArgs().StoreErrorEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task SuccessfulWakeAck_NoStateWrite()
    {
        // Waking → Running is completed by the wake gate when ConfigurationState
        // reaches Configured — the scale ack alone must not transition anything.
        GivenAdapterInLifecycleState(RtLifecycleStateEnum.Waking);

        await _service.OnScaleStatusReportedAsync(Status(success: true, replicas: 1));

        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task FailedScaleToZero_Draining_RevertsToRunningAndAuditsError()
    {
        GivenAdapterInLifecycleState(RtLifecycleStateEnum.Draining);

        await _service.OnScaleStatusReportedAsync(
            Status(success: false, replicas: 0, statusMessage: "k8s API rejected the patch"));

        await _eventService.Received(1).StoreErrorEventAsync(
            TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId,
            Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Running,
            Arg.Is<string?>(m => m != null && m.Contains("k8s API rejected the patch")));
    }

    [Test]
    public async Task FailedWake_Waking_AuditsErrorButKeepsWaking()
    {
        // The wake gate owns the budget and reverts Waking → Hibernated with a
        // typed error on timeout — a failed scale-1 ack must not preempt it.
        GivenAdapterInLifecycleState(RtLifecycleStateEnum.Waking);

        await _service.OnScaleStatusReportedAsync(
            Status(success: false, replicas: 1, statusMessage: "no Deployments found"));

        await _eventService.Received(1).StoreErrorEventAsync(
            TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task RepositoryLoadThrows_SwallowsException()
    {
        _repository.GetWorkloadByRtIdAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        // Best-effort, same contract as ReportWorkloadDeploymentStatusAsync — a
        // failed state write must not break the hub for the connection's traffic.
        await _service.OnScaleStatusReportedAsync(Status(success: true, replicas: 0));
    }

    [Test]
    public async Task StateWriteThrows_SwallowsException()
    {
        GivenAdapterInLifecycleState(RtLifecycleStateEnum.Draining);
        _repository.SetWorkloadLifecycleStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        await _service.OnScaleStatusReportedAsync(Status(success: true, replicas: 0));
    }
}
