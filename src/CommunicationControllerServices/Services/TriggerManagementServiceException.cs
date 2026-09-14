using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Exception thrown by the trigger management service
/// </summary>
public class TriggerManagementServiceException : Exception
{
    private TriggerManagementServiceException()
    {
    }

    private TriggerManagementServiceException(string message) : base(message)
    {
    }

    private TriggerManagementServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception UpdateScheduleFailed(string tenantId, Exception exception)
    {
        return new TriggerManagementServiceException($"Failed to update schedule for tenant {tenantId}", exception);
    }

    internal static Exception RemoveScheduleFailed(string tenantId, Exception exception)
    {
        return new TriggerManagementServiceException($"Failed to remove schedule for tenant {tenantId}", exception);
    }

    internal static Exception ExecutePipelineFailed(string tenantId, OctoObjectId dataFlowRtId, string? errorMessage)
    {
        throw new TriggerManagementServiceException($"Failed to execute data flow {dataFlowRtId} for tenant {tenantId}: {errorMessage}");
    }

    internal static Exception ExecutePipelineExecutionErrorFailed(string tenantId, OctoObjectId dataFlowRtId, Exception exception)
    {
        return new TriggerManagementServiceException($"Failed to execute data flow {dataFlowRtId} for tenant {tenantId}", exception);
    }

    internal static Exception ExecutePipelineExecutionIdNull(string tenantId, OctoObjectId dataFlowRtId)
    {
        return new TriggerManagementServiceException($"Pipeline execution id is null for data flow {dataFlowRtId} for tenant {tenantId}, but the adapter indicate that the execution start was successful");
    }

    /// <summary>
    ///     AB#4924 §14 — the pipeline runs on a <c>Leased</c> adapter and adapter-pool leasing is
    ///     switched off for the tenant.
    /// </summary>
    /// <remarks>
    ///     🔴 Refused here, loudly, rather than falling through to the manual-adapter path. That path
    ///     publishes to a per-pipeline queue that nothing is listening on and reports failure only
    ///     after the 30 s MassTransit request timeout, with a message about an adapter — which is not
    ///     what happened. A named refusal is both faster and true.
    /// </remarks>
    internal static Exception LeasingDisabled(string tenantId, OctoObjectId pipelineRtId, string adapterName)
    {
        return new TriggerManagementServiceException(
            $"Pipeline {pipelineRtId} of tenant {tenantId} is executed by the leased adapter '{adapterName}', but " +
            "adapter pool leasing is disabled for this tenant. Nothing was queued. Enable it with octo-cli " +
            "SetCommunicationLifecycle -le true.");
    }
}
