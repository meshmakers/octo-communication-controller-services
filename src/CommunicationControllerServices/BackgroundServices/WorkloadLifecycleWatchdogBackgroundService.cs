using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Idle watchdog of the on-demand adapter lifecycle (AB#4914/AB#4918). Every
/// <c>LifecycleWatchdogIntervalMinutes</c> it sweeps all enabled tenants whose
/// <c>communicationLifecycle</c> configuration has scale-to-zero on and, per OnDemand
/// workload:
///
/// - hibernates idle workloads: <c>Running → Draining</c> + scale-0 request when the last
///   observed activity (max of <c>LastActivityAt</c> and the pipelines' statistics
///   <c>LastExecutionAt</c> — raw executions are folded after
///   <c>PipelineExecutionRetentionHours</c>, AB#4370, so <c>CompletedAt</c> is unusable)
///   is older than the workload's <c>IdleTimeoutMinutes</c>. Busy guards: running
///   executions and an in-flight wake.
/// - reconciles stale <c>Waking</c> states left behind by a controller restart (the wake
///   wait registry is in-memory): <c>Configured</c> → Running; stuck longer than twice the
///   wake budget with no active waiter → back to Hibernated.
///
/// Mirrors the structure of <c>AdapterOfflineReconciliationBackgroundService</c> (interval
/// loop, startup grace, per-tenant try/catch). Only Adapter workloads are hibernated —
/// Applications have no pipeline activity signal yet and are skipped.
/// </summary>
internal class WorkloadLifecycleWatchdogBackgroundService(
    IAdapterCache adapterCache,
    ICommunicationRepository communicationRepository,
    ILifecycleConfigurationService lifecycleConfiguration,
    IWorkloadLifecycleService workloadLifecycleService,
    ICommunicationEventService eventService,
    IOptions<CommunicationControllerOptions> options)
    : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.LifecycleWatchdogIntervalMinutes));

        Logger.Info(
            "Workload lifecycle watchdog starting with interval / startup grace of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        try
        {
            // Startup grace: adapters reconnect and config re-pushes settle after a controller
            // (re)start before any idle judgement is made.
            await Task.Delay(interval, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAllTenantsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error in workload lifecycle watchdog sweep");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepAllTenantsAsync()
    {
        foreach (var tenantId in adapterCache.GetEnabledTenantIds())
        {
            try
            {
                if (!await lifecycleConfiguration.IsScaleToZeroEnabledAsync(tenantId))
                {
                    continue;
                }

                await SweepTenantAsync(tenantId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error sweeping workload lifecycle for tenant '{TenantId}'", tenantId);
            }
        }
    }

    internal async Task SweepTenantAsync(string tenantId)
    {
        var workloads = await communicationRepository.GetWorkloadsAsync(tenantId);
        foreach (var workload in workloads)
        {
            if (workload.LifecycleMode != RtLifecycleModeEnum.OnDemand)
            {
                continue;
            }

            try
            {
                await SweepWorkloadAsync(tenantId, workload);
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "Error sweeping lifecycle of workload '{WorkloadName}' ({WorkloadRtId}) for tenant '{TenantId}'",
                    workload.Name, workload.RtId, tenantId);
            }
        }
    }

    private async Task SweepWorkloadAsync(string tenantId, RtDeployableWorkload workload)
    {
        switch (workload.LifecycleState)
        {
            case RtLifecycleStateEnum.Waking:
                await ReconcileWakingAsync(tenantId, workload);
                return;

            case RtLifecycleStateEnum.Draining:
            case RtLifecycleStateEnum.Hibernated:
                // Draining completes via the operator's scale ack; Hibernated is at rest.
                return;
        }

        // Running (or unset — pre-existing entities default to Running semantics).
        if (workload is not RtAdapter adapter)
        {
            Logger.Debug(
                "OnDemand workload '{WorkloadName}' (tenant '{TenantId}') is not an Adapter; skipping idle judgement",
                workload.Name, tenantId);
            return;
        }

        if (adapter.DeploymentState != RtDeploymentStateEnum.Deployed)
        {
            return;
        }

        if (workloadLifecycleService.HasActiveWake(tenantId, workload.RtId.ToString()))
        {
            return;
        }

        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
        var runningExecutions =
            await communicationRepository.GetRunningExecutionsForAdapterAsync(tenantId, adapterRtEntityId);
        if (runningExecutions.Count > 0)
        {
            return;
        }

        var lastActivity = await GetLastActivityAsync(tenantId, adapter, adapterRtEntityId);
        var idleTimeout = TimeSpan.FromMinutes(Math.Max(1, adapter.IdleTimeoutMinutes));
        // No observed activity at all means the adapter has been idle since before observation
        // began — exactly the fleet of long-idle adapters this feature targets.
        if (lastActivity.HasValue && DateTime.UtcNow - lastActivity.Value < idleTimeout)
        {
            return;
        }

        Logger.Info(
            "Workload '{WorkloadName}' (tenant '{TenantId}') idle since {LastActivity:u} (timeout {TimeoutMinutes} min); draining",
            adapter.Name, tenantId, lastActivity, idleTimeout.TotalMinutes);

        await communicationRepository.SetWorkloadLifecycleStateAsync(tenantId, adapter.RtId,
            RtLifecycleStateEnum.Draining, "Draining (idle timeout reached).");
        await eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{adapter.Name}' idle for more than {idleTimeout.TotalMinutes:F0} minutes; scaling to 0. (source: Lifecycle)");
        await workloadLifecycleService.RequestScaleAsync(tenantId, adapter, 0);
    }

    private async Task ReconcileWakingAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (workloadLifecycleService.HasActiveWake(tenantId, workload.RtId.ToString()))
        {
            // A gate on this pod owns the wake and its budget — nothing to reconcile.
            return;
        }

        if (workload is RtAdapter { ConfigurationState: RtConfigurationStateEnum.Configured })
        {
            // The wake completed but the transition was lost (e.g. controller restart between
            // the config ack and the state write).
            await communicationRepository.SetWorkloadLifecycleStateAsync(tenantId, workload.RtId,
                RtLifecycleStateEnum.Running, "Running (reconciled after wake).");
            return;
        }

        // No waiter, not configured: the wake was orphaned (controller restart mid-wake, or a
        // wake whose caller gave up). Give it twice the wake budget measured from the last
        // activity stamp (written when the wake started) before folding back to Hibernated.
        var staleAfter = TimeSpan.FromSeconds(Math.Max(1, options.Value.LifecycleWakeBudgetSeconds) * 2);
        var wakingSince = workload.LastActivityAt;
        if (wakingSince.HasValue && DateTime.UtcNow - wakingSince.Value < staleAfter)
        {
            return;
        }

        Logger.Warn(
            "Workload '{WorkloadName}' (tenant '{TenantId}') stuck in Waking with no active waiter; reverting to Hibernated",
            workload.Name, tenantId);
        await communicationRepository.SetWorkloadLifecycleStateAsync(tenantId, workload.RtId,
            RtLifecycleStateEnum.Hibernated, "Hibernated (stale wake reconciled).");
        await eventService.StoreErrorEventAsync(tenantId,
            $"Wake of workload '{workload.Name}' did not complete and was reverted to Hibernated. (source: Lifecycle)");
    }

    /// <summary>
    /// Latest observed demand: the workload's <c>LastActivityAt</c> combined with the
    /// <c>LastExecutionAt</c> of every pipeline's statistics entity. Statistics survive the
    /// AB#4370 execution fold, so this is stable regardless of the execution retention window.
    /// </summary>
    private async Task<DateTime?> GetLastActivityAsync(string tenantId, RtAdapter adapter,
        RtEntityId adapterRtEntityId)
    {
        DateTime? last = adapter.LastActivityAt;

        var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, adapterRtEntityId);
        foreach (var pipeline in pipelines)
        {
            var statistics = await communicationRepository.GetPipelineStatisticsAsync(tenantId,
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId));
            var lastExecution = statistics?.LastExecutionAt;
            if (lastExecution.HasValue && (!last.HasValue || lastExecution.Value > last.Value))
            {
                last = lastExecution;
            }
        }

        return last;
    }
}
