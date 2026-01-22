using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PipelineExecutionService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    ICommunicationEventService eventService)
    : IPipelineExecutionService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task StartExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionStartDto dto)
    {
        Logger.Debug("[{TenantId}] Starting execution '{ExecutionId}' for pipeline '{PipelineRtEntityId}'",
            tenantId, dto.ExecutionId, dto.PipelineRtEntityId);

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
            }

            // Create the execution entity with a new RtId
            var execution = new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = dto.ExecutionId,
                Status = RtPipelineExecutionStatusEnum.Running,
                TriggerType = ConvertTriggerType(dto.TriggerType),
                StartedAt = dto.StartedAt,
                InputData = dto.InputData
            };

            // Create execution record and associations
            await communicationRepository.CreatePipelineExecutionAsync(tenantId, execution, dto.PipelineRtEntityId, adapterRtEntityId);

            // Set pipeline current execution
            await communicationRepository.SetPipelineCurrentExecutionAsync(tenantId, dto.PipelineRtEntityId, dto.ExecutionId);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Pipeline execution '{dto.ExecutionId}' started (trigger: {dto.TriggerType}).",
                dto.PipelineRtEntityId);

            Logger.Info("[{TenantId}] Execution '{ExecutionId}' started successfully for pipeline '{PipelineRtEntityId}'",
                tenantId, dto.ExecutionId, dto.PipelineRtEntityId);
        }
        catch (PipelineExecutionServiceException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to start execution '{ExecutionId}' for pipeline '{PipelineRtEntityId}'",
                tenantId, dto.ExecutionId, dto.PipelineRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedStartExecution(tenantId, dto.ExecutionId, e);
        }
    }

    public async Task CompleteExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto)
    {
        Logger.Debug("[{TenantId}] Completing execution '{ExecutionId}' with status '{Status}'",
            tenantId, dto.ExecutionId, dto.Status);

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
            }

            // Get the execution to find the pipeline
            var execution = await communicationRepository.GetPipelineExecutionAsync(tenantId, dto.ExecutionId);
            if (execution == null)
            {
                // Execution not found - this can happen if the start wasn't recorded (e.g., during model updates)
                // Log a warning but don't throw - the adapter pipeline continues regardless
                Logger.Warn("[{TenantId}] Execution '{ExecutionId}' not found, skipping completion update",
                    tenantId, dto.ExecutionId);
                return;
            }

            // Convert SDK status to CK Model status
            var ckStatus = ConvertExecutionStatus(dto.Status);

            // Update the execution record
            await communicationRepository.UpdatePipelineExecutionAsync(tenantId, dto.ExecutionId,
                ckStatus, dto.CompletedAt, dto.DurationMs, dto.ErrorMessage);

            // Get the pipeline from the execution's association to clear current execution
            // Note: The CkTypeId should never be null for a valid execution
            var executions = await communicationRepository.GetPipelineExecutionsAsync(tenantId,
                new RtEntityId(execution.CkTypeId!, execution.RtId), null, null, 1);

            // Clear pipeline current execution - we need to get the pipeline RtEntityId
            // For simplicity, we'll iterate through adapter's pipelines to find it
            // This could be optimized by storing the pipeline ID in the execution

            var eventMessage = ckStatus switch
            {
                RtPipelineExecutionStatusEnum.Completed =>
                    $"Pipeline execution '{dto.ExecutionId}' completed successfully in {dto.DurationMs}ms.",
                RtPipelineExecutionStatusEnum.Failed =>
                    $"Pipeline execution '{dto.ExecutionId}' failed: {dto.ErrorMessage}",
                RtPipelineExecutionStatusEnum.Cancelled =>
                    $"Pipeline execution '{dto.ExecutionId}' was cancelled.",
                _ => $"Pipeline execution '{dto.ExecutionId}' ended with status {ckStatus}."
            };

            if (ckStatus == RtPipelineExecutionStatusEnum.Failed)
            {
                await eventService.StoreErrorEventAsync(tenantId, eventMessage);
            }
            else
            {
                await eventService.StoreInformationEventAsync(tenantId, eventMessage);
            }

            Logger.Info("[{TenantId}] Execution '{ExecutionId}' completed with status '{Status}'",
                tenantId, dto.ExecutionId, ckStatus);
        }
        catch (PipelineExecutionServiceException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to complete execution '{ExecutionId}'",
                tenantId, dto.ExecutionId);
            throw PipelineExecutionServiceException.CommonFailedCompleteExecution(tenantId, dto.ExecutionId, e);
        }
    }

    public async Task MarkExecutionsAsInterruptedAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        Logger.Debug("[{TenantId}] Marking running executions as interrupted for adapter '{AdapterRtEntityId}'",
            tenantId, adapterRtEntityId);

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                // Tenant not enabled, nothing to do
                return;
            }

            var runningExecutions = await communicationRepository.GetRunningExecutionsForAdapterAsync(tenantId, adapterRtEntityId);

            foreach (var execution in runningExecutions)
            {
                if (execution.ExecutionId != null)
                {
                    await communicationRepository.UpdatePipelineExecutionAsync(tenantId, execution.ExecutionId,
                        RtPipelineExecutionStatusEnum.Interrupted, null, null, "Adapter disconnected");

                    Logger.Info("[{TenantId}] Execution '{ExecutionId}' marked as interrupted",
                        tenantId, execution.ExecutionId);
                }
            }

            if (runningExecutions.Any())
            {
                await eventService.StoreInformationEventAsync(tenantId,
                    $"{runningExecutions.Count} running execution(s) marked as interrupted due to adapter disconnect.",
                    adapterRtEntityId);
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to mark executions as interrupted for adapter '{AdapterRtEntityId}'",
                tenantId, adapterRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedMarkInterrupted(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task ReportInterruptedExecutionResultAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto)
    {
        Logger.Debug("[{TenantId}] Reporting interrupted execution result '{ExecutionId}' with status '{Status}'",
            tenantId, dto.ExecutionId, dto.Status);

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
            }

            var execution = await communicationRepository.GetPipelineExecutionAsync(tenantId, dto.ExecutionId);
            if (execution == null)
            {
                throw PipelineExecutionServiceException.ExecutionNotFound(tenantId, dto.ExecutionId);
            }

            if (execution.Status != RtPipelineExecutionStatusEnum.Interrupted)
            {
                Logger.Warn("[{TenantId}] Execution '{ExecutionId}' is not in interrupted state, current state: {Status}",
                    tenantId, dto.ExecutionId, execution.Status);
                return;
            }

            var ckStatus = ConvertExecutionStatus(dto.Status);

            await communicationRepository.UpdatePipelineExecutionAsync(tenantId, dto.ExecutionId,
                ckStatus, dto.CompletedAt, dto.DurationMs, dto.ErrorMessage);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Interrupted execution '{dto.ExecutionId}' final result reported: {ckStatus}.");

            Logger.Info("[{TenantId}] Interrupted execution '{ExecutionId}' result reported with status '{Status}'",
                tenantId, dto.ExecutionId, ckStatus);
        }
        catch (PipelineExecutionServiceException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to report interrupted execution result '{ExecutionId}'",
                tenantId, dto.ExecutionId);
            throw PipelineExecutionServiceException.CommonFailedCompleteExecution(tenantId, dto.ExecutionId, e);
        }
    }

    public async Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        if (!adapterCache.TryGetTenant(tenantId, out _))
        {
            return [];
        }

        return await communicationRepository.GetInterruptedExecutionIdsAsync(tenantId, adapterRtEntityId);
    }

    public async Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        Logger.Debug("[{TenantId}] Updating statistics for pipeline '{PipelineRtEntityId}'",
            tenantId, pipelineRtEntityId);

        try
        {
            var now = DateTime.UtcNow;

            // Get aggregates for different time periods
            var lastHour = await communicationRepository.GetExecutionAggregateAsync(tenantId, pipelineRtEntityId,
                now.AddHours(-1), now);
            var last12Hours = await communicationRepository.GetExecutionAggregateAsync(tenantId, pipelineRtEntityId,
                now.AddHours(-12), now);
            var last24Hours = await communicationRepository.GetExecutionAggregateAsync(tenantId, pipelineRtEntityId,
                now.AddHours(-24), now);
            var last30Days = await communicationRepository.GetExecutionAggregateAsync(tenantId, pipelineRtEntityId,
                now.AddDays(-30), now);

            // Get the last execution time
            var recentExecutions = await communicationRepository.GetPipelineExecutionsAsync(tenantId,
                pipelineRtEntityId, null, null, 1);
            var lastExecutionAt = recentExecutions.FirstOrDefault()?.StartedAt;

            var statistics = new RtPipelineStatistics
            {
                LastHourSuccessCount = lastHour.SuccessCount,
                LastHourFailureCount = lastHour.FailureCount,
                LastHourAvgDurationMs = (int)lastHour.AvgDurationMs,
                Last12HoursSuccessCount = last12Hours.SuccessCount,
                Last12HoursFailureCount = last12Hours.FailureCount,
                Last12HoursAvgDurationMs = (int)last12Hours.AvgDurationMs,
                Last24HoursSuccessCount = last24Hours.SuccessCount,
                Last24HoursFailureCount = last24Hours.FailureCount,
                Last24HoursAvgDurationMs = (int)last24Hours.AvgDurationMs,
                Last30DaysSuccessCount = last30Days.SuccessCount,
                Last30DaysFailureCount = last30Days.FailureCount,
                Last30DaysAvgDurationMs = (int)last30Days.AvgDurationMs,
                LastUpdatedAt = now,
                LastExecutionAt = lastExecutionAt
            };

            await communicationRepository.UpsertPipelineStatisticsAsync(tenantId, statistics, pipelineRtEntityId);

            Logger.Debug("[{TenantId}] Statistics updated for pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to update statistics for pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedUpdateStatistics(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task UpdateAllStatisticsAsync(string tenantId)
    {
        Logger.Debug("[{TenantId}] Updating statistics for all pipelines", tenantId);

        try
        {
            var pipelines = await communicationRepository.GetAllPipelinesAsync(tenantId);

            foreach (var pipeline in pipelines)
            {
                try
                {
                    // Note: CkTypeId should never be null for a valid pipeline
                    var pipelineRtEntityId = new RtEntityId(pipeline.CkTypeId!, pipeline.RtId);
                    await UpdateStatisticsAsync(tenantId, pipelineRtEntityId);
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "[{TenantId}] Failed to update statistics for pipeline '{PipelineRtId}'",
                        tenantId, pipeline.RtId);
                }
            }

            Logger.Info("[{TenantId}] Statistics updated for {Count} pipelines",
                tenantId, pipelines.Count);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to update all statistics", tenantId);
            throw;
        }
    }

    public async Task<int> CleanupOldExecutionsAsync(string tenantId, int retentionDays)
    {
        Logger.Debug("[{TenantId}] Cleaning up executions older than {RetentionDays} days",
            tenantId, retentionDays);

        try
        {
            var olderThan = DateTime.UtcNow.AddDays(-retentionDays);
            var deletedCount = await communicationRepository.DeleteOldExecutionsAsync(tenantId, olderThan);

            if (deletedCount > 0)
            {
                Logger.Info("[{TenantId}] Deleted {Count} old executions",
                    tenantId, deletedCount);

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Cleaned up {deletedCount} pipeline executions older than {retentionDays} days.");
            }

            return deletedCount;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to cleanup old executions", tenantId);
            throw PipelineExecutionServiceException.CommonFailedCleanupOldExecutions(tenantId, e);
        }
    }

    public async Task<BufferedExecutionsSyncResponse> ProcessBufferedExecutionsAsync(string tenantId, RtEntityId adapterRtEntityId,
        BufferedExecutionsSyncRequest request)
    {
        Logger.Debug("[{TenantId}] Processing {Count} buffered executions for adapter '{AdapterRtEntityId}'",
            tenantId, request.Executions.Count, adapterRtEntityId);

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
            }

            var executionIds = request.Executions.Select(e => e.ExecutionId).ToList();
            var existingIds = await communicationRepository.GetExistingExecutionIdsAsync(tenantId, executionIds);

            var newExecutions = request.Executions
                .Where(e => !existingIds.Contains(e.ExecutionId))
                .ToList();

            var syncedCount = 0;
            var lastSequenceNumber = 0;

            // Group by pipeline for bulk insert
            var groupedByPipeline = newExecutions.GroupBy(e => e.PipelineRtEntityId);

            foreach (var group in groupedByPipeline)
            {
                var pipelineRtEntityId = group.Key;
                var executionsToInsert = group.Select(dto => new RtPipelineExecution
                {
                    RtId = OctoObjectId.GenerateNewId(),
                    ExecutionId = dto.ExecutionId,
                    Status = dto.Status,
                    TriggerType = dto.TriggerType,
                    StartedAt = dto.StartedAt,
                    CompletedAt = dto.CompletedAt,
                    DurationMs = dto.DurationMs,
                    ErrorMessage = dto.ErrorMessage,
                    InputData = dto.InputData
                }).ToList();

                await communicationRepository.BulkInsertPipelineExecutionsAsync(tenantId, executionsToInsert,
                    pipelineRtEntityId, adapterRtEntityId);

                syncedCount += executionsToInsert.Count;
            }

            // Update the adapter's sync sequence number
            if (request.Executions.Any())
            {
                lastSequenceNumber = request.Executions.Max(e => e.SequenceNumber);
                await communicationRepository.UpdateAdapterSyncSequenceNumberAsync(tenantId, adapterRtEntityId, lastSequenceNumber);
            }

            Logger.Info("[{TenantId}] Synced {SyncedCount} executions, skipped {SkippedCount} for adapter '{AdapterRtEntityId}'",
                tenantId, syncedCount, request.Executions.Count - syncedCount, adapterRtEntityId);

            return new BufferedExecutionsSyncResponse
            {
                SyncedCount = syncedCount,
                SkippedCount = request.Executions.Count - syncedCount,
                LastSequenceNumber = lastSequenceNumber
            };
        }
        catch (PipelineExecutionServiceException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to process buffered executions for adapter '{AdapterRtEntityId}'",
                tenantId, adapterRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedProcessBufferedExecutions(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<int> GetLastSyncedSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        if (!adapterCache.TryGetTenant(tenantId, out _))
        {
            throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
        }

        return await communicationRepository.GetAdapterSyncSequenceNumberAsync(tenantId, adapterRtEntityId);
    }

    /// <summary>
    /// Converts SDK PipelineTriggerType to CK Model RtPipelineTriggerTypeEnum
    /// </summary>
    private static RtPipelineTriggerTypeEnum ConvertTriggerType(PipelineTriggerType triggerType)
    {
        return triggerType switch
        {
            PipelineTriggerType.Manual => RtPipelineTriggerTypeEnum.Manual,
            PipelineTriggerType.Scheduled => RtPipelineTriggerTypeEnum.Scheduled,
            PipelineTriggerType.Event => RtPipelineTriggerTypeEnum.Event,
            PipelineTriggerType.Startup => RtPipelineTriggerTypeEnum.Startup,
            _ => RtPipelineTriggerTypeEnum.Manual
        };
    }

    /// <summary>
    /// Converts SDK PipelineExecutionStatus to CK Model RtPipelineExecutionStatusEnum
    /// </summary>
    private static RtPipelineExecutionStatusEnum ConvertExecutionStatus(PipelineExecutionStatus status)
    {
        return status switch
        {
            PipelineExecutionStatus.Running => RtPipelineExecutionStatusEnum.Running,
            PipelineExecutionStatus.Completed => RtPipelineExecutionStatusEnum.Completed,
            PipelineExecutionStatus.Failed => RtPipelineExecutionStatusEnum.Failed,
            PipelineExecutionStatus.Interrupted => RtPipelineExecutionStatusEnum.Interrupted,
            PipelineExecutionStatus.Cancelled => RtPipelineExecutionStatusEnum.Cancelled,
            _ => RtPipelineExecutionStatusEnum.Failed
        };
    }
}
