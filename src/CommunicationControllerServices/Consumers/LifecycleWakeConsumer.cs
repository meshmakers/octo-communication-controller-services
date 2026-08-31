using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
///     Consumes cron co-wake messages (AB#4918) from the durable
///     <see cref="PipelineQueueNames.LifecycleWakeQueue"/>. For every cron trigger whose
///     pipeline runs on an OnDemand workload, <c>TriggerManagementService.UpdateScheduleAsync</c>
///     registers a companion recurring send with the same cron expression that lands here —
///     the controller wakes the workload while the pipeline's own trigger message buffers
///     durably on its per-pipeline trigger queue until the adapter is up.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class LifecycleWakeConsumer(
    ILogger<LifecycleWakeConsumer> logger,
    IWorkloadLifecycleService workloadLifecycleService) : IDistributedConsumer<LifecycleWakeMessage>
{
    public async Task ConsumeAsync(IDistributedContext<LifecycleWakeMessage> context)
    {
        var message = context.Message;
        logger.LogDebug("Co-wake tick for workload '{WorkloadRtId}' (tenant '{TenantId}')",
            message.WorkloadRtId, message.TenantId);

        try
        {
            // The gate no-ops for AlwaysOn workloads and for tenants without scale-to-zero, so
            // a co-wake schedule that outlived a LifecycleMode change is harmless.
            await workloadLifecycleService.EnsureWorkloadRunningAsync(message.TenantId,
                new OctoObjectId(message.WorkloadRtId));
        }
        catch (Exception e)
        {
            // A failed co-wake must not dead-letter the queue — the trigger message itself is
            // buffered durably and the next demand signal (or the next tick) retries the wake.
            logger.LogWarning(e,
                "Co-wake of workload '{WorkloadRtId}' (tenant '{TenantId}') failed",
                message.WorkloadRtId, message.TenantId);
        }
    }
}
