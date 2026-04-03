using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Service for managing pipeline execution metrics
/// </summary>
public interface IPipelineExecutionService
{
    /// <summary>
    /// Reports the start of a pipeline execution
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter executing the pipeline</param>
    /// <param name="dto">Execution start details</param>
    Task StartExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionStartDto dto);

    /// <summary>
    /// Reports the completion of a pipeline execution
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter that executed the pipeline</param>
    /// <param name="dto">Execution end details</param>
    Task CompleteExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto);

    /// <summary>
    /// Reports the start of multiple pipeline executions in a single batch operation
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter executing the pipelines</param>
    /// <param name="dtos">Execution start details grouped by pipeline</param>
    Task BatchStartExecutionsAsync(string tenantId, RtEntityId adapterRtEntityId, IReadOnlyList<PipelineExecutionStartDto> dtos);

    /// <summary>
    /// Reports the completion of multiple pipeline executions in a single batch operation
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dtos">Execution end details</param>
    Task BatchCompleteExecutionsAsync(string tenantId, IReadOnlyList<PipelineExecutionEndDto> dtos);

    /// <summary>
    /// Marks all running executions for an adapter as interrupted (called on disconnect)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter that disconnected</param>
    Task MarkExecutionsAsInterruptedAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Reports the final result of an interrupted execution (called after reconnect)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter reporting the result</param>
    /// <param name="dto">Final execution result</param>
    Task ReportInterruptedExecutionResultAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto);

    /// <summary>
    /// Gets interrupted execution IDs for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>List of interrupted execution IDs</returns>
    Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Updates statistics for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Updates statistics for all pipelines in a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    Task UpdateAllStatisticsAsync(string tenantId);

    /// <summary>
    /// Cleans up old executions based on retention policy
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="retentionDays">Number of days to retain executions</param>
    /// <returns>Number of deleted executions</returns>
    Task<int> CleanupOldExecutionsAsync(string tenantId, int retentionDays);

    /// <summary>
    /// Marks stale running executions as failed after timeout
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="timeoutHours">Number of hours after which running executions are considered stale</param>
    /// <returns>Number of timed out executions</returns>
    Task<int> TimeoutStaleExecutionsAsync(string tenantId, int timeoutHours);

    /// <summary>
    /// Processes buffered executions from an adapter (for offline sync)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <param name="request">Sync request with buffered executions</param>
    /// <returns>Sync response with results</returns>
    Task<BufferedExecutionsSyncResponse> ProcessBufferedExecutionsAsync(string tenantId, RtEntityId adapterRtEntityId,
        BufferedExecutionsSyncRequest request);

    /// <summary>
    /// Gets the last synced sequence number for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>Last synced sequence number</returns>
    Task<int> GetLastSyncedSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets the aggregated execution status of a data flow by querying all child pipeline executions
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Runtime identifier of the data flow</param>
    /// <returns>Aggregated data flow status with per-pipeline details</returns>
    Task<DataFlowStatusDto> GetDataFlowStatusAsync(string tenantId, OctoObjectId dataFlowRtId);
}
