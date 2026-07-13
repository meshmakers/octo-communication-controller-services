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
    /// Folds terminal executions older than the retention window into the hourly statistics
    /// buckets, physically deletes them, and refreshes the sliding-window counters for every
    /// pipeline of the tenant (AB#4370). Running executions are never touched.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="retentionHours">Number of hours a terminal execution is retained</param>
    /// <returns>Number of folded and deleted executions</returns>
    Task<int> FoldAndPruneExecutionsAsync(string tenantId, int retentionHours);

    /// <summary>
    /// Cleans up old executions based on retention policy. Safety net behind the fold: catches
    /// orphaned executions whose pipeline no longer exists (they are erased without folding).
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
    /// Connection-aware reaper: fails executions stuck in a non-terminal state past the grace
    /// period. All <c>Interrupted</c> executions and <c>Running</c> executions whose owning adapter
    /// is offline are failed; <c>Running</c> executions on a live adapter are left untouched so
    /// long-running pipelines are never killed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="graceMinutes">Executions must be older than this many minutes to be eligible</param>
    /// <returns>Number of executions failed</returns>
    Task<int> FailStuckExecutionsAsync(string tenantId, int graceMinutes);

    /// <summary>
    /// Fails all non-terminal executions of a freshly (re)started adapter that predate the given
    /// process start time. Called on fresh adapter startup to resolve executions orphaned by the
    /// previous process (which lost its in-memory task on restart).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">The (re)started adapter</param>
    /// <param name="beforeUtc">The adapter process start time</param>
    /// <returns>Number of executions failed</returns>
    Task<int> FailOrphanedExecutionsForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId, DateTime beforeUtc);

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
