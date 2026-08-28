using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Default implementation of <see cref="IWorkloadLifecycleService"/>. Singleton.
///     The wake-wait registry is deliberately separate from <c>AdapterService._pendingDeployments</c>
///     (a single-TCS-per-adapter slot whose last-writer-wins semantics would drop concurrent
///     wake waiters).
/// </summary>
internal class WorkloadLifecycleService(
    ILogger<WorkloadLifecycleService> logger,
    ICommunicationRepository communicationRepository,
    ICommunicationEventService communicationEventService,
    IOperatorConnectionManager operatorConnectionManager,
    ILifecycleConfigurationService lifecycleConfiguration,
    IOptions<CommunicationControllerOptions> options) : IWorkloadLifecycleService
{
    // One shared TCS per waking workload; every concurrent wake caller awaits the same task.
    // Completed by NotifyWorkloadConfiguredAsync when the woken adapter acks its config push.
    private readonly ConcurrentDictionary<(string TenantId, string WorkloadRtId), TaskCompletionSource<bool>>
        _wakeWaiters = new();

    public bool HasActiveWake(string tenantId, string workloadRtId)
    {
        return _wakeWaiters.ContainsKey((tenantId, workloadRtId));
    }

    public async Task EnsureWorkloadRunningForPipelineAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        // Cheap cached gate first — on a tenant without scale-to-zero the execute path must not
        // pay a repository lookup.
        if (!await lifecycleConfiguration.IsScaleToZeroEnabledAsync(tenantId))
        {
            return;
        }

        var adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId));
        if (adapter == null)
        {
            // Let the caller's own path produce its established "pipeline has no adapter" error.
            return;
        }

        await EnsureWorkloadRunningAsync(tenantId, adapter);
    }

    public async Task EnsureWorkloadRunningAsync(string tenantId, OctoObjectId workloadRtId)
    {
        if (!await lifecycleConfiguration.IsScaleToZeroEnabledAsync(tenantId))
        {
            return;
        }

        var workload = await communicationRepository.GetWorkloadByRtIdAsync(tenantId, workloadRtId);
        if (workload == null)
        {
            return;
        }

        await EnsureWorkloadRunningAsync(tenantId, workload);
    }

    public async Task EnsureWorkloadRunningAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (workload.LifecycleMode != RtLifecycleModeEnum.OnDemand)
        {
            return;
        }

        if (!await lifecycleConfiguration.IsScaleToZeroEnabledAsync(tenantId))
        {
            return;
        }

        switch (workload.LifecycleState)
        {
            case RtLifecycleStateEnum.Hibernated:
            case RtLifecycleStateEnum.Draining:
                // Draining: the scale-0 request may already be in flight — requesting scale-1 now
                // wins either way (SIGTERM-to-restart is safe: trigger queues are durable and the
                // execute path is gated behind this very method).
                await communicationRepository.SetWorkloadLifecycleStateAsync(tenantId, workload.RtId,
                    RtLifecycleStateEnum.Waking, "Waking (demand signal).");
                await StampActivityAsync(tenantId, workload.RtId);
                await communicationEventService.StoreInformationEventAsync(tenantId,
                    $"Waking workload '{workload.Name}' (demand signal). (source: Lifecycle)");
                await RequestScaleAsync(tenantId, workload, 1);
                await WaitForConfiguredAsync(tenantId, workload);
                return;

            case RtLifecycleStateEnum.Waking:
                // Another caller started the wake — join its wait.
                await WaitForConfiguredAsync(tenantId, workload);
                return;

            default:
                // Running — just record the demand for the idle watchdog.
                await StampActivityAsync(tenantId, workload.RtId);
                return;
        }
    }

    public async Task NotifyWorkloadConfiguredAsync(string tenantId, OctoObjectId workloadRtId)
    {
        // Release waiters first so gated callers proceed immediately; the state write below is
        // bookkeeping and must not delay them.
        if (_wakeWaiters.TryRemove((tenantId, workloadRtId.ToString()), out var tcs))
        {
            tcs.TrySetResult(true);
        }

        try
        {
            var workload = await communicationRepository.GetWorkloadByRtIdAsync(tenantId, workloadRtId);
            if (workload is not { LifecycleMode: RtLifecycleModeEnum.OnDemand })
            {
                return;
            }

            if (workload.LifecycleState is RtLifecycleStateEnum.Waking or RtLifecycleStateEnum.Draining
                or RtLifecycleStateEnum.Hibernated)
            {
                await communicationRepository.SetWorkloadLifecycleStateAsync(tenantId, workloadRtId,
                    RtLifecycleStateEnum.Running, "Running.");
            }

            await StampActivityAsync(tenantId, workloadRtId);
        }
        catch (Exception e)
        {
            // Best-effort bookkeeping on the configuration-ack path — never break the ack.
            logger.LogWarning(e,
                "Failed to update lifecycle state after configuration ack for workload '{WorkloadRtId}' (tenant '{TenantId}')",
                workloadRtId, tenantId);
        }
    }

    private async Task WaitForConfiguredAsync(string tenantId, RtDeployableWorkload workload)
    {
        var key = (tenantId, workload.RtId.ToString());
        var tcs = _wakeWaiters.GetOrAdd(key,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

        var budget = TimeSpan.FromSeconds(Math.Max(1, options.Value.LifecycleWakeBudgetSeconds));
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(budget));
        if (completed == tcs.Task)
        {
            // Propagates a fail-fast set by OnScaleStatusReportedAsync (failed scale-1).
            await tcs.Task;
            return;
        }

        // Budget exhausted. Revert to Hibernated (typed error to the caller); the deployment
        // deliberately stays scaled up for diagnosis — the idle watchdog re-hibernates it after
        // the idle timeout if it never comes up.
        _wakeWaiters.TryRemove(key, out _);
        try
        {
            await communicationRepository.SetWorkloadLifecycleStateAsync(tenantId, workload.RtId,
                RtLifecycleStateEnum.Hibernated,
                $"Wake timed out after {budget.TotalSeconds:F0}s (workload never reached Configured).");
            await communicationEventService.StoreErrorEventAsync(tenantId,
                $"Waking workload '{workload.Name}' timed out after {budget.TotalSeconds:F0}s. (source: Lifecycle)");
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Failed to record wake timeout for workload '{WorkloadRtId}' (tenant '{TenantId}')",
                workload.RtId, tenantId);
        }

        throw WorkloadLifecycleServiceException.WakeTimedOut(tenantId, workload.RtId, workload.Name, budget);
    }

    private async Task StampActivityAsync(string tenantId, OctoObjectId workloadRtId)
    {
        try
        {
            await communicationRepository.SetWorkloadLastActivityAsync(tenantId, workloadRtId, DateTime.UtcNow);
        }
        catch (Exception e)
        {
            // The stamp is watchdog input only — never fail the demand path over it.
            logger.LogDebug(e,
                "Failed to stamp last activity for workload '{WorkloadRtId}' (tenant '{TenantId}')",
                workloadRtId, tenantId);
        }
    }

    public async Task RequestScaleAsync(string tenantId, RtDeployableWorkload workload, int replicas)
    {
        var pool = await communicationRepository.GetPoolForWorkloadAsync(tenantId, workload.RtId);
        if (pool == null)
        {
            throw PoolServiceException.WorkloadNotInPool(tenantId, workload.RtId);
        }

        await operatorConnectionManager.NotifyWorkloadScaleAsync(new ScaleWorkloadDto
        {
            TenantId = tenantId,
            PoolRtId = pool.RtId.ToString(),
            WorkloadRtId = workload.RtId.ToString(),
            WorkloadName = workload.Name ?? string.Empty,
            WorkloadType = workload is RtApplication ? WorkloadTypeDto.Application : WorkloadTypeDto.Adapter,
            Replicas = replicas,
        });
    }

    public async Task OnScaleStatusReportedAsync(WorkloadScaleStatusDto status)
    {
        try
        {
            var workloadRtId = new OctoObjectId(status.WorkloadRtId);
            var workload = await communicationRepository.GetWorkloadByRtIdAsync(status.TenantId, workloadRtId);
            if (workload == null)
            {
                logger.LogWarning(
                    "Workload '{WorkloadRtId}' (tenant '{TenantId}') reported scale status but no entity exists; skipping",
                    status.WorkloadRtId, status.TenantId);
                return;
            }

            if (status.Success)
            {
                if (status.Replicas == 0)
                {
                    if (workload.LifecycleState == RtLifecycleStateEnum.Draining)
                    {
                        await communicationRepository.SetWorkloadLifecycleStateAsync(status.TenantId, workloadRtId,
                            RtLifecycleStateEnum.Hibernated, "Hibernated (scaled to 0 after idle timeout).");
                        await communicationEventService.StoreInformationEventAsync(status.TenantId,
                            $"Workload '{status.WorkloadName}' hibernated (scaled to 0 replicas). (source: Lifecycle)");
                    }
                    else
                    {
                        // A scale-0 ack outside Draining is stale (e.g. a demand signal already
                        // moved the workload to Waking while the ack was in flight). The waker
                        // has issued / will issue its own scale-1 — don't overwrite its state.
                        logger.LogInformation(
                            "Ignoring scale-to-0 ack for workload '{WorkloadName}' (tenant '{TenantId}') in lifecycle state {State}",
                            status.WorkloadName, status.TenantId, workload.LifecycleState);
                    }
                }
                else
                {
                    // Wake ack: the pod is starting. Waking → Running is completed by the wake
                    // gate when ConfigurationState reaches Configured (AB#4594: Online is not
                    // enough), so nothing to transition here.
                    logger.LogInformation(
                        "Scale-to-{Replicas} acknowledged for workload '{WorkloadName}' (tenant '{TenantId}'); waiting for Configured",
                        status.Replicas, status.WorkloadName, status.TenantId);
                }

                return;
            }

            await communicationEventService.StoreErrorEventAsync(status.TenantId,
                $"Scaling workload '{status.WorkloadName}' to {status.Replicas} replicas failed: {status.StatusMessage} (source: Lifecycle)");

            if (status.Replicas == 0 && workload.LifecycleState == RtLifecycleStateEnum.Draining)
            {
                // The pod is still running — revert so the workload is not treated as
                // hibernated. The watchdog retries after the next idle window.
                await communicationRepository.SetWorkloadLifecycleStateAsync(status.TenantId, workloadRtId,
                    RtLifecycleStateEnum.Running, $"Scale to 0 failed: {status.StatusMessage}");
            }
            // A failed wake (replicas >= 1) keeps LifecycleState=Waking (the wake gate owns
            // the budget and reverts Waking → Hibernated on timeout), but active waiters are
            // failed fast so callers don't burn the full budget on a scale the operator
            // already reported as failed.
            if (status.Replicas >= 1 &&
                _wakeWaiters.TryRemove((status.TenantId, status.WorkloadRtId), out var waiter))
            {
                waiter.TrySetException(WorkloadLifecycleServiceException.WakeScaleFailed(status.TenantId,
                    status.WorkloadName, status.StatusMessage));
            }
        }
        catch (Exception e)
        {
            // Best-effort, mirrors ReportWorkloadDeploymentStatusAsync: a failed state write
            // must not crash the hub for the rest of the connection's traffic.
            logger.LogError(e,
                "Failed to process scale status for workload '{WorkloadName}' (tenant '{TenantId}')",
                status.WorkloadName, status.TenantId);
        }
    }
}
