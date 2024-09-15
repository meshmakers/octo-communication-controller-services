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

    internal static Exception ExecutePipelineFailed(string tenantId, RtEntityId pipelineRtEntityId, string? errorMessage)
    {
        throw new TriggerManagementServiceException($"Failed to execute pipeline {pipelineRtEntityId} for tenant {tenantId}: {errorMessage}");
    }

    internal static Exception ExecutePipelineExecutionErrorFailed(string tenantId, RtEntityId meshPipelineRtEntityId, Exception exception)
    {
        return new TriggerManagementServiceException($"Failed to execute pipeline {meshPipelineRtEntityId} for tenant {tenantId}", exception);
    }

    internal static Exception ExecutePipelineExecutionIdNull(string tenantId, RtEntityId meshPipelineRtEntityId)
    {
        return new TriggerManagementServiceException($"Pipeline execution id is null for pipeline {meshPipelineRtEntityId} for tenant {tenantId}, but the adapter indicate that the execution start was successful");
    }
}
