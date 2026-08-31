using System.Collections.Frozen;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PipelineExecutionService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    ICommunicationEventService eventService,
    IWorkloadLifecycleService workloadLifecycleService)
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

    public async Task BatchStartExecutionsAsync(string tenantId, RtEntityId adapterRtEntityId,
        IReadOnlyList<PipelineExecutionStartDto> dtos)
    {
        if (dtos.Count == 0)
        {
            return;
        }

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
            }

            // Group by pipeline for bulk insert
            var groupedByPipeline = dtos.GroupBy(d => d.PipelineRtEntityId);

            foreach (var group in groupedByPipeline)
            {
                var pipelineRtEntityId = group.Key;
                var executions = group.Select(dto => new RtPipelineExecution
                {
                    RtId = OctoObjectId.GenerateNewId(),
                    ExecutionId = dto.ExecutionId,
                    Status = RtPipelineExecutionStatusEnum.Running,
                    TriggerType = ConvertTriggerType(dto.TriggerType),
                    StartedAt = dto.StartedAt,
                    InputData = dto.InputData
                }).ToList();

                await communicationRepository.BulkInsertPipelineExecutionsAsync(
                    tenantId, executions, pipelineRtEntityId, adapterRtEntityId);
            }

            Logger.Info("[{TenantId}] Batch started {Count} executions for adapter '{AdapterRtEntityId}'",
                tenantId, dtos.Count, adapterRtEntityId);
        }
        catch (PipelineExecutionServiceException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to batch start {Count} executions",
                tenantId, dtos.Count);
            throw;
        }
    }

    public async Task BatchCompleteExecutionsAsync(string tenantId, IReadOnlyList<PipelineExecutionEndDto> dtos)
    {
        if (dtos.Count == 0)
        {
            return;
        }

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw PipelineExecutionServiceException.TenantNotEnabled(tenantId);
            }

            var updates = dtos.Select(dto => new PipelineExecutionUpdate
            {
                ExecutionId = dto.ExecutionId,
                Status = ConvertExecutionStatus(dto.Status),
                CompletedAt = dto.CompletedAt,
                DurationMs = dto.DurationMs,
                ErrorMessage = dto.ErrorMessage,
                OutputData = dto.OutputData
            }).ToList();

            var updatedCount = await communicationRepository.BulkUpdatePipelineExecutionsAsync(tenantId, updates);

            // Log summary for failures
            var failedCount = dtos.Count(d => d.Status == PipelineExecutionStatus.Failed);
            if (failedCount > 0)
            {
                var failedMessages = dtos
                    .Where(d => d.Status == PipelineExecutionStatus.Failed)
                    .Select(d => d.ErrorMessage)
                    .Where(m => m != null)
                    .ToFrozenSet();

                foreach (var message in failedMessages)
                {
                    await eventService.StoreErrorEventAsync(tenantId,
                        $"Pipeline execution failed: {message}");
                }
            }

            Logger.Info("[{TenantId}] Batch completed {UpdatedCount}/{TotalCount} executions",
                tenantId, updatedCount, dtos.Count);
        }
        catch (PipelineExecutionServiceException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to batch complete {Count} executions",
                tenantId, dtos.Count);
            throw;
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
                ckStatus, dto.CompletedAt, dto.DurationMs, dto.ErrorMessage, dto.OutputData);

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
                // AB#4919: the idle watchdog only drains a workload with no running executions, so
                // finding any here means a hibernation cut work short — the drain guarantee did not
                // hold. That is worth a warning rather than the routine information event, because
                // unlike an ordinary disconnect nobody pulled a plug: the platform did it.
                if (await workloadLifecycleService.IsIntentionallyDownAsync(tenantId, adapterRtEntityId.RtId))
                {
                    await eventService.StoreWarningEventAsync(tenantId,
                        $"{runningExecutions.Count} running execution(s) were interrupted while the adapter was " +
                        "being hibernated. Hibernation is supposed to wait for running executions; " +
                        "please report this with the execution ids.",
                        adapterRtEntityId);
                }
                else
                {
                    await eventService.StoreInformationEventAsync(tenantId,
                        $"{runningExecutions.Count} running execution(s) marked as interrupted due to adapter disconnect.",
                        adapterRtEntityId);
                }
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
                ckStatus, dto.CompletedAt, dto.DurationMs, dto.ErrorMessage, dto.OutputData);

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
            var from30Days = now.AddDays(-30);
            var from24Hours = now.AddHours(-24);
            var from12Hours = now.AddHours(-12);
            var from1Hour = now.AddHours(-1);

            // Sliding windows are computed from two disjoint sources (AB#4370): the hourly
            // buckets (folded + pruned executions) and a live scan over the executions still
            // retained (roughly the last retention hour plus anything non-terminal). The two
            // never overlap because an execution is deleted in the same pass that folds it.
            var existingStatistics =
                await communicationRepository.GetPipelineStatisticsAsync(tenantId, pipelineRtEntityId);
            var buckets = (existingStatistics?.HourlyBuckets ?? Enumerable.Empty<RtPipelineStatisticsHourBucketRecord>())
                .Where(b => b.HourStartAt >= from30Days)
                .ToList();

            // Accumulate statistics across batches to avoid loading all executions at once.
            // Using skip/take triggers the optimized MongoDB query path which applies $limit
            // inside $lookup, preventing the 16MB BSON document size limit from being exceeded.
            var lastHour = new StatisticsAccumulator();
            var last12Hours = new StatisticsAccumulator();
            var last24Hours = new StatisticsAccumulator();
            var last30Days = new StatisticsAccumulator();
            DateTime? lastExecutionAt = null;
            var totalLoaded = 0;

            const int batchSize = 5000;
            var skip = 0;

            while (true)
            {
                var batch = await communicationRepository.GetPipelineExecutionsAsync(
                    tenantId, pipelineRtEntityId, from30Days, now, skip, batchSize);

                if (batch.Count == 0)
                {
                    break;
                }

                // First batch contains the most recent execution (sorted descending)
                lastExecutionAt ??= batch[0].StartedAt;

                foreach (var exec in batch)
                {
                    AccumulateExecution(last30Days, exec);

                    if (exec.StartedAt >= from24Hours)
                    {
                        AccumulateExecution(last24Hours, exec);
                    }

                    if (exec.StartedAt >= from12Hours)
                    {
                        AccumulateExecution(last12Hours, exec);
                    }

                    if (exec.StartedAt >= from1Hour)
                    {
                        AccumulateExecution(lastHour, exec);
                    }
                }

                totalLoaded += batch.Count;

                if (batch.Count < batchSize)
                {
                    break;
                }

                skip += batchSize;
            }

            if (totalLoaded == 0 && buckets.Count == 0)
            {
                if (existingStatistics == null)
                {
                    Logger.Debug("[{TenantId}] No executions and no existing statistics for pipeline '{PipelineRtEntityId}', skipping update",
                        tenantId, pipelineRtEntityId);
                    return;
                }

                if (IsStatisticsEmpty(existingStatistics))
                {
                    Logger.Debug("[{TenantId}] Statistics already empty for pipeline '{PipelineRtEntityId}', skipping update",
                        tenantId, pipelineRtEntityId);
                    return;
                }

                // Statistics have non-zero values but no executions/buckets remain - reset to zero
                // Fall through to normal upsert with zero values
            }

            var bucket1Hour = PipelineStatisticsFolder.SumBuckets(buckets, from1Hour);
            var bucket12Hours = PipelineStatisticsFolder.SumBuckets(buckets, from12Hours);
            var bucket24Hours = PipelineStatisticsFolder.SumBuckets(buckets, from24Hours);
            var bucket30Days = PipelineStatisticsFolder.SumBuckets(buckets, from30Days);

            if (lastExecutionAt == null || existingStatistics?.LastExecutionAt > lastExecutionAt)
            {
                // Folded executions are gone from the live scan; never regress the marker.
                lastExecutionAt = existingStatistics?.LastExecutionAt;
            }

            var statistics = new RtPipelineStatistics
            {
                LastHourSuccessCount = lastHour.SuccessCount + bucket1Hour.SuccessCount,
                LastHourFailureCount = lastHour.FailureCount + bucket1Hour.FailureCount,
                LastHourAvgDurationMs = CombinedAvgDurationMs(lastHour, bucket1Hour),
                Last12HoursSuccessCount = last12Hours.SuccessCount + bucket12Hours.SuccessCount,
                Last12HoursFailureCount = last12Hours.FailureCount + bucket12Hours.FailureCount,
                Last12HoursAvgDurationMs = CombinedAvgDurationMs(last12Hours, bucket12Hours),
                Last24HoursSuccessCount = last24Hours.SuccessCount + bucket24Hours.SuccessCount,
                Last24HoursFailureCount = last24Hours.FailureCount + bucket24Hours.FailureCount,
                Last24HoursAvgDurationMs = CombinedAvgDurationMs(last24Hours, bucket24Hours),
                Last30DaysSuccessCount = last30Days.SuccessCount + bucket30Days.SuccessCount,
                Last30DaysFailureCount = last30Days.FailureCount + bucket30Days.FailureCount,
                Last30DaysAvgDurationMs = CombinedAvgDurationMs(last30Days, bucket30Days),
                LastUpdatedAt = now,
                LastExecutionAt = lastExecutionAt,
                HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(buckets.Cast<RtRecord>().ToList())
            };

            await communicationRepository.UpsertPipelineStatisticsAsync(tenantId, statistics, pipelineRtEntityId);

            Logger.Debug("[{TenantId}] Statistics updated for pipeline '{PipelineRtEntityId}' ({TotalExecutions} executions processed, {BucketCount} buckets)",
                tenantId, pipelineRtEntityId, totalLoaded, buckets.Count);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to update statistics for pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedUpdateStatistics(tenantId, pipelineRtEntityId, e);
        }
    }

    private static int CombinedAvgDurationMs(StatisticsAccumulator live, PipelineStatisticsFolder.WindowTotals buckets)
    {
        var durationCount = live.ExecutionWithDurationCount + buckets.DurationCount;
        if (durationCount == 0)
        {
            return 0;
        }

        return (int)((live.TotalDurationMs + buckets.TotalDurationMs) / durationCount);
    }

    private static void AccumulateExecution(StatisticsAccumulator accumulator, RtPipelineExecution exec)
    {
        if (exec.Status == RtPipelineExecutionStatusEnum.Completed)
        {
            accumulator.SuccessCount++;
        }
        else if (exec.Status == RtPipelineExecutionStatusEnum.Failed)
        {
            accumulator.FailureCount++;
        }

        if (exec.DurationMs.HasValue)
        {
            accumulator.TotalDurationMs += exec.DurationMs.Value;
            accumulator.ExecutionWithDurationCount++;
        }
    }

    /// <summary>
    /// Computes aggregate statistics from a list of executions starting from a given cutoff time
    /// </summary>
    private static ExecutionAggregateResult ComputeAggregate(IReadOnlyList<RtPipelineExecution> executions, DateTime from)
    {
        var filtered = executions.Where(e => e.StartedAt >= from).ToList();

        var successCount = filtered.Count(e => e.Status == RtPipelineExecutionStatusEnum.Completed);
        var failureCount = filtered.Count(e => e.Status == RtPipelineExecutionStatusEnum.Failed);
        var executionsWithDuration = filtered.Where(e => e.DurationMs.HasValue).ToList();
        var totalDurationMs = executionsWithDuration.Sum(e => (long)e.DurationMs!.Value);

        return new ExecutionAggregateResult(successCount, failureCount, totalDurationMs, executionsWithDuration.Count);
    }

    /// <summary>
    /// Mutable accumulator for computing statistics across batches
    /// </summary>
    private class StatisticsAccumulator
    {
        public int SuccessCount;
        public int FailureCount;
        public long TotalDurationMs;
        public int ExecutionWithDurationCount;
        public long AvgDurationMs => ExecutionWithDurationCount > 0 ? TotalDurationMs / ExecutionWithDurationCount : 0;
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

    public async Task<int> FoldAndPruneExecutionsAsync(string tenantId, int retentionHours)
    {
        var olderThan = DateTime.UtcNow.AddHours(-Math.Max(1, retentionHours));
        var totalPruned = 0;

        try
        {
            var pipelines = await communicationRepository.GetAllPipelinesAsync(tenantId);

            foreach (var pipeline in pipelines)
            {
                // Note: CkTypeId should never be null for a valid pipeline
                var pipelineRtEntityId = new RtEntityId(pipeline.CkTypeId!, pipeline.RtId);

                try
                {
                    totalPruned += await FoldAndPrunePipelineAsync(tenantId, pipelineRtEntityId, olderThan);

                    // Refresh the sliding windows on every pass — also when nothing was folded,
                    // so counters decay once a pipeline stops executing.
                    await UpdateStatisticsAsync(tenantId, pipelineRtEntityId);
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "[{TenantId}] Failed to fold executions for pipeline '{PipelineRtId}'",
                        tenantId, pipeline.RtId);
                }
            }

            if (totalPruned > 0)
            {
                Logger.Info("[{TenantId}] Folded and pruned {Count} executions into statistics buckets",
                    tenantId, totalPruned);
            }

            return totalPruned;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to fold and prune executions", tenantId);
            throw PipelineExecutionServiceException.CommonFailedCleanupOldExecutions(tenantId, e);
        }
    }

    private async Task<int> FoldAndPrunePipelineAsync(string tenantId, RtEntityId pipelineRtEntityId,
        DateTime olderThan)
    {
        const int batchSize = 500;
        var pruned = 0;

        while (true)
        {
            var batch = await communicationRepository.GetTerminalExecutionsOlderThanAsync(
                tenantId, pipelineRtEntityId, olderThan, batchSize);

            if (batch.Count == 0)
            {
                break;
            }

            var deltas = PipelineStatisticsFolder.ToBucketDeltas(batch);

            var existing = await communicationRepository.GetPipelineStatisticsAsync(tenantId, pipelineRtEntityId);
            var merged = PipelineStatisticsFolder.MergeBuckets(existing?.HourlyBuckets, deltas,
                DateTime.UtcNow.AddDays(-30));

            var maxStartedAt = batch.Max(e => e.StartedAt);
            var updated = new RtPipelineStatistics
            {
                // Carry the current window values — they are recomputed right after the drain,
                // but the update must not zero them in between.
                LastHourSuccessCount = existing?.LastHourSuccessCount ?? 0,
                LastHourFailureCount = existing?.LastHourFailureCount ?? 0,
                LastHourAvgDurationMs = existing?.LastHourAvgDurationMs ?? 0,
                Last12HoursSuccessCount = existing?.Last12HoursSuccessCount ?? 0,
                Last12HoursFailureCount = existing?.Last12HoursFailureCount ?? 0,
                Last12HoursAvgDurationMs = existing?.Last12HoursAvgDurationMs ?? 0,
                Last24HoursSuccessCount = existing?.Last24HoursSuccessCount ?? 0,
                Last24HoursFailureCount = existing?.Last24HoursFailureCount ?? 0,
                Last24HoursAvgDurationMs = existing?.Last24HoursAvgDurationMs ?? 0,
                Last30DaysSuccessCount = existing?.Last30DaysSuccessCount ?? 0,
                Last30DaysFailureCount = existing?.Last30DaysFailureCount ?? 0,
                Last30DaysAvgDurationMs = existing?.Last30DaysAvgDurationMs ?? 0,
                LastUpdatedAt = existing?.LastUpdatedAt,
                LastExecutionAt = existing?.LastExecutionAt > maxStartedAt
                    ? existing.LastExecutionAt
                    : maxStartedAt,
                HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(merged.Cast<RtRecord>().ToList())
            };

            // Fold-then-delete: persist the buckets BEFORE erasing the batch. A crash between
            // the two double-counts at most one batch on the next run instead of losing it.
            await communicationRepository.UpsertPipelineStatisticsAsync(tenantId, updated, pipelineRtEntityId);
            await communicationRepository.DeleteExecutionsAsync(tenantId,
                batch.Select(e => e.ToRtEntityId()).ToList());

            pruned += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return pruned;
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

    public async Task<int> TimeoutStaleExecutionsAsync(string tenantId, int timeoutHours)
    {
        Logger.Debug("[{TenantId}] Timing out stale executions older than {TimeoutHours} hours",
            tenantId, timeoutHours);

        try
        {
            var olderThan = DateTime.UtcNow.AddHours(-timeoutHours);
            var timedOutCount = await communicationRepository.TimeoutStaleExecutionsAsync(tenantId, olderThan);

            if (timedOutCount > 0)
            {
                Logger.Info("[{TenantId}] Timed out {Count} stale executions",
                    tenantId, timedOutCount);

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Timed out {timedOutCount} stale pipeline executions running longer than {timeoutHours} hours.");
            }

            return timedOutCount;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to timeout stale executions", tenantId);
            throw PipelineExecutionServiceException.CommonFailedTimeoutStaleExecutions(tenantId, e);
        }
    }

    public async Task<int> FailStuckExecutionsAsync(string tenantId, int graceMinutes)
    {
        Logger.Debug("[{TenantId}] Failing stuck executions older than {GraceMinutes} minutes", tenantId, graceMinutes);

        try
        {
            var graceCutoff = DateTime.UtcNow.AddMinutes(-graceMinutes);
            var failedCount = await communicationRepository.FailStuckExecutionsAsync(tenantId, graceCutoff);

            if (failedCount > 0)
            {
                Logger.Info("[{TenantId}] Failed {Count} stuck executions (interrupted or running on offline adapter)",
                    tenantId, failedCount);

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Failed {failedCount} stuck pipeline execution(s) orphaned by adapter restart/disconnect.");
            }

            return failedCount;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to fail stuck executions", tenantId);
            throw PipelineExecutionServiceException.CommonFailedTimeoutStaleExecutions(tenantId, e);
        }
    }

    public async Task<int> FailOrphanedExecutionsForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        DateTime beforeUtc)
    {
        Logger.Debug("[{TenantId}] Failing orphaned executions for adapter '{AdapterRtEntityId}' started before {BeforeUtc}",
            tenantId, adapterRtEntityId, beforeUtc);

        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                // Tenant not enabled, nothing to do
                return 0;
            }

            var failedCount =
                await communicationRepository.FailOrphanedExecutionsForAdapterAsync(tenantId, adapterRtEntityId, beforeUtc);

            if (failedCount > 0)
            {
                Logger.Info("[{TenantId}] Failed {Count} orphaned execution(s) for restarted adapter '{AdapterRtEntityId}'",
                    tenantId, failedCount, adapterRtEntityId);

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Failed {failedCount} orphaned pipeline execution(s) after adapter restart.", adapterRtEntityId);
            }

            return failedCount;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to fail orphaned executions for adapter '{AdapterRtEntityId}'",
                tenantId, adapterRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedTimeoutStaleExecutions(tenantId, e);
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
                    InputData = dto.InputData,
                    OutputData = dto.OutputData
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

    public async Task<DataFlowStatusDto> GetDataFlowStatusAsync(string tenantId, OctoObjectId dataFlowRtId)
    {
        Logger.Debug("[{TenantId}] Getting data flow status for '{DataFlowRtId}'", tenantId, dataFlowRtId);

        try
        {
            // Get all child pipelines of the data flow
            var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, dataFlowRtId);

            // Fetch execution and statistics data for all pipelines in parallel
            var pipelineStatusTasks = pipelines.Select(async pipeline =>
            {
                var pipelineRtEntityId = new RtEntityId(pipeline.CkTypeId!, pipeline.RtId);

                var recentExecutionsTask = communicationRepository.GetPipelineExecutionsAsync(
                    tenantId, pipelineRtEntityId, null, null, 1);
                var statisticsTask = communicationRepository.GetPipelineStatisticsAsync(
                    tenantId, pipelineRtEntityId);

                await Task.WhenAll(recentExecutionsTask, statisticsTask);

                var recentExecutions = await recentExecutionsTask;
                var statistics = await statisticsTask;
                var pipelineState = DeterminePipelineState(recentExecutions);

                var statisticsSummary = statistics != null
                    ? new PipelineStatisticsSummaryDto
                    {
                        LastHourSuccessCount = statistics.LastHourSuccessCount,
                        LastHourFailureCount = statistics.LastHourFailureCount,
                        LastHourAvgDurationMs = statistics.LastHourAvgDurationMs
                    }
                    : null;

                return new PipelineStatusDto
                {
                    PipelineRtEntityId = pipelineRtEntityId,
                    PipelineType = pipeline.CkTypeId?.ToString() ?? "Unknown",
                    State = pipelineState,
                    LastExecutionAt = statistics?.LastExecutionAt,
                    Statistics = statisticsSummary
                };
            });

            var pipelineStatuses = (await Task.WhenAll(pipelineStatusTasks)).ToList();

            var aggregatedState = AggregateDataFlowState(pipelineStatuses);

            Logger.Debug("[{TenantId}] Data flow '{DataFlowRtId}' status: {State} ({PipelineCount} pipelines)",
                tenantId, dataFlowRtId, aggregatedState, pipelineStatuses.Count);

            return new DataFlowStatusDto
            {
                DataFlowRtId = dataFlowRtId,
                State = aggregatedState,
                Pipelines = pipelineStatuses
            };
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to get data flow status for '{DataFlowRtId}'",
                tenantId, dataFlowRtId);
            throw PipelineExecutionServiceException.CommonFailedGetDataFlowStatus(tenantId, dataFlowRtId, e);
        }
    }

    /// <summary>
    /// Determines the execution state of a single pipeline based on its most recent execution
    /// </summary>
    internal static PipelineExecutionState DeterminePipelineState(IReadOnlyList<RtPipelineExecution> recentExecutions)
    {
        if (recentExecutions.Count == 0)
        {
            return PipelineExecutionState.Idle;
        }

        var latest = recentExecutions[0];
        return latest.Status switch
        {
            RtPipelineExecutionStatusEnum.Running => PipelineExecutionState.Running,
            RtPipelineExecutionStatusEnum.Completed => PipelineExecutionState.Completed,
            RtPipelineExecutionStatusEnum.Failed => PipelineExecutionState.Failed,
            RtPipelineExecutionStatusEnum.Interrupted => PipelineExecutionState.Failed,
            RtPipelineExecutionStatusEnum.Cancelled => PipelineExecutionState.Idle,
            _ => PipelineExecutionState.Idle
        };
    }

    /// <summary>
    /// Aggregates individual pipeline states into an overall data flow state
    /// </summary>
    internal static DataFlowExecutionState AggregateDataFlowState(IReadOnlyList<PipelineStatusDto> pipelineStatuses)
    {
        if (pipelineStatuses.Count == 0)
        {
            return DataFlowExecutionState.Idle;
        }

        // If any pipeline is running, the data flow is running
        if (pipelineStatuses.Any(p => p.State == PipelineExecutionState.Running))
        {
            return DataFlowExecutionState.Running;
        }

        // If any pipeline failed (and none running), the data flow is failed
        if (pipelineStatuses.Any(p => p.State == PipelineExecutionState.Failed))
        {
            return DataFlowExecutionState.Failed;
        }

        // If any pipeline completed (and none running/failed), the data flow is completed
        if (pipelineStatuses.Any(p => p.State == PipelineExecutionState.Completed))
        {
            return DataFlowExecutionState.Completed;
        }

        // All pipelines are idle
        return DataFlowExecutionState.Idle;
    }

    private static bool IsStatisticsEmpty(RtPipelineStatistics statistics)
    {
        return statistics.LastExecutionAt == null &&
               statistics.LastHourSuccessCount == 0 &&
               statistics.LastHourFailureCount == 0 &&
               statistics.Last12HoursSuccessCount == 0 &&
               statistics.Last12HoursFailureCount == 0 &&
               statistics.Last24HoursSuccessCount == 0 &&
               statistics.Last24HoursFailureCount == 0 &&
               statistics.Last30DaysSuccessCount == 0 &&
               statistics.Last30DaysFailureCount == 0;
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
