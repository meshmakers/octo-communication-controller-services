using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
///     Consumes cron ticks of pipelines on <c>Leased</c> adapters (AB#5278) from the durable
///     <see cref="PipelineQueueNames.LeaseTriggerQueue"/>. <c>TriggerManagementService.UpdateScheduleAsync</c>
///     registers the recurring send here — instead of on the adapter's own trigger queue, which a
///     leased adapter has no process to consume — and every tick becomes a queued work item through
///     the one path that creates leases: <see cref="ITriggerManagementService.StartExecutePipelineAsync"/>.
///     That path re-resolves the adapter's mode itself, so a tick that outlives a switch back to a
///     dedicated mode runs the pipeline the way that mode requires instead of being lost.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal class LeaseTriggerConsumer(
    ILogger<LeaseTriggerConsumer> logger,
    ICommunicationRepository communicationRepository,
    ITriggerManagementService triggerManagementService) : IDistributedConsumer<LeaseTriggerMessage>
{
    /// <summary>
    ///     How many queued work items of the adapter are inspected for a tick already waiting.
    ///     Bounded because the queue read is a repository round-trip per tick; a queue deeper than
    ///     this is a pool that cannot keep up, and one more entry no longer changes that picture.
    /// </summary>
    internal const int CoalesceReadLimit = 200;

    public async Task ConsumeAsync(IDistributedContext<LeaseTriggerMessage> context)
    {
        var message = context.Message;
        var pipelineRtId = new OctoObjectId(message.PipelineRtId);
        logger.LogDebug("[{TenantId}] Lease trigger tick for pipeline '{PipelineRtId}' (trigger '{TriggerRtId}')",
            message.TenantId, message.PipelineRtId, message.TriggerRtId);

        try
        {
            if (await IsTickAlreadyQueuedAsync(message.TenantId, pipelineRtId))
            {
                // A cron that fires faster than the pool serves it must not pile up one work item
                // per tick: the dedicated path buffers such ticks on the adapter's durable queue
                // and drains them in a burst, which is not a behaviour worth reproducing on a
                // shared pool. One waiting run of this pipeline is the whole cron's intent.
                logger.LogInformation(
                    "[{TenantId}] Lease trigger tick for pipeline '{PipelineRtId}' coalesced: a run of it is " +
                    "still waiting for a lease",
                    message.TenantId, message.PipelineRtId);
                return;
            }

            // No caller and no input: a cron tick is the system acting on the author's schedule,
            // exactly what the adapter-side FromPipelineTriggerEvent path carries — nothing.
            await triggerManagementService.StartExecutePipelineAsync(message.TenantId, pipelineRtId, null);
        }
        catch (Exception e)
        {
            // A failed tick must not dead-letter the queue: the refusal (leasing disabled, pipeline
            // gone, no lender) has been logged and stored as a tenant event by the execute path,
            // and the next tick is the retry a cron already implies.
            logger.LogWarning(e,
                "[{TenantId}] Lease trigger tick for pipeline '{PipelineRtId}' (trigger '{TriggerRtId}') failed",
                message.TenantId, message.PipelineRtId, message.TriggerRtId);
        }
    }

    private async Task<bool> IsTickAlreadyQueuedAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        var adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId));
        if (adapter is null || adapter.LifecycleMode != RtLifecycleModeEnum.Leased)
        {
            // Not leased (any more): nothing is queued for it, the execute path decides what to do.
            return false;
        }

        var queued = await communicationRepository.GetQueuedExecutionsForAdapterAsync(tenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId), CoalesceReadLimit);
        return queued.Any(q => q.PipelineRtId == pipelineRtId);
    }
}
