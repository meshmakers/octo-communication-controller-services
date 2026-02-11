using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PipelineExecutionServiceException : Exception
{
    private PipelineExecutionServiceException()
    {
    }

    private PipelineExecutionServiceException(string message) : base(message)
    {
    }

    private PipelineExecutionServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception ExecutionNotFound(string tenantId, string executionId)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Execution '{executionId}' not found");
    }

    internal static Exception ExecutionAlreadyStarted(string tenantId, string executionId)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Execution '{executionId}' has already been started");
    }

    internal static Exception ExecutionNotRunning(string tenantId, string executionId)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Execution '{executionId}' is not in running state");
    }

    internal static Exception TenantNotEnabled(string tenantId)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Tenant not enabled");
    }

    internal static Exception CommonFailedStartExecution(string tenantId, string executionId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to start execution '{executionId}'", exception);
    }

    internal static Exception CommonFailedCompleteExecution(string tenantId, string executionId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to complete execution '{executionId}'", exception);
    }

    internal static Exception CommonFailedMarkInterrupted(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to mark executions as interrupted for adapter '{adapterRtEntityId}'", exception);
    }

    internal static Exception CommonFailedUpdateStatistics(string tenantId, RtEntityId pipelineRtEntityId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to update statistics for pipeline '{pipelineRtEntityId}'", exception);
    }

    internal static Exception CommonFailedCleanupOldExecutions(string tenantId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to cleanup old executions", exception);
    }

    internal static Exception CommonFailedProcessBufferedExecutions(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to process buffered executions for adapter '{adapterRtEntityId}'", exception);
    }

    internal static Exception CommonFailedTimeoutStaleExecutions(string tenantId, Exception exception)
    {
        return new PipelineExecutionServiceException($"[{tenantId}] Failed to timeout stale executions", exception);
    }
}
