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
    IWorkloadLifecycleService workloadLifecycleService,
    TimeProvider timeProvider)
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

            // AB#5425. Counted per reported outcome rather than per updated row: an execution whose
            // start was never recorded (a model update swallowed it) still ran, and the adapter's
            // report is the truth about that.
            foreach (var byOutcome in updates.GroupBy(u => u.Status))
            {
                PipelineExecutionMetrics.RecordExecutionOutcomes(tenantId, byOutcome.Key, byOutcome.Count());
            }

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

            // AB#5425: the exact, immediate event stream. Counted after the record was updated,
            // so a failed write does not inflate the count.
            PipelineExecutionMetrics.RecordExecutionOutcome(tenantId, ckStatus);

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

            PipelineExecutionMetrics.RecordExecutionOutcome(tenantId, ckStatus);

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

    public Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return UpdateStatisticsAsync(tenantId, pipelineRtEntityId, timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Recomputes the statistics of one pipeline as of <paramref name="now" />. See
    /// <see cref="PipelineStatisticsFolder" /> for the window semantics (AB#5583): the persisted
    /// HourlyBuckets are folded history plus a fresh snapshot of the retained executions, and
    /// the 12h/24h/30d counters are summed from exactly that list.
    /// </summary>
    internal async Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId, DateTime now)
    {
        Logger.Debug("[{TenantId}] Updating statistics for pipeline '{PipelineRtEntityId}'",
            tenantId, pipelineRtEntityId);

        try
        {
            var windowStart30Days = PipelineStatisticsFolder.WindowStart(now, PipelineStatisticsFolder.RetentionWindowHours);
            var windowStart24Hours = PipelineStatisticsFolder.WindowStart(now, 24);
            var windowStart12Hours = PipelineStatisticsFolder.WindowStart(now, 12);
            var rollingHourStart = now.AddHours(-1);

            var existingStatistics =
                await communicationRepository.GetPipelineStatisticsAsync(tenantId, pipelineRtEntityId);
            var foldedBefore = existingStatistics?.FoldedBefore;
            var foldedHistory = PipelineStatisticsFolder
                .FoldedHistory(existingStatistics?.HourlyBuckets, foldedBefore)
                .Where(b => b.HourStartAt >= windowStart30Days)
                .ToList();

            // Retained executions are the live half. With a fold boundary, only hours at or after
            // it are snapshotted — earlier ones are folded history, and a straggler there (an
            // execution that turned terminal after its hour was folded) waits for the next fold
            // instead of being counted twice. Without a boundary (statistics written before
            // AB#5583) the buckets hold folded executions only and every retained one is live.
            var liveFrom = foldedBefore is { } boundary && boundary > windowStart30Days ? boundary : windowStart30Days;

            var liveDeltas = new Dictionary<DateTime, PipelineStatisticsFolder.BucketAccumulator>();
            var lastHour = new StatisticsAccumulator();
            DateTime? lastExecutionAt = null;
            var totalLoaded = 0;

            // Accumulate across batches to avoid loading all executions at once. Using skip/take
            // triggers the optimized MongoDB query path which applies $limit inside $lookup,
            // preventing the 16MB BSON document size limit from being exceeded.
            const int batchSize = 5000;
            var skip = 0;

            while (true)
            {
                var batch = await communicationRepository.GetPipelineExecutionsAsync(
                    tenantId, pipelineRtEntityId, windowStart30Days, now, skip, batchSize);

                if (batch.Count == 0)
                {
                    break;
                }

                // First batch contains the most recent execution (sorted descending)
                lastExecutionAt ??= batch[0].StartedAt;

                foreach (var exec in batch)
                {
                    if (exec.StartedAt >= rollingHourStart)
                    {
                        AccumulateExecution(lastHour, exec);
                    }
                }

                MergeDeltas(liveDeltas, PipelineStatisticsFolder.ToBucketDeltas(
                    batch.Where(exec => exec.StartedAt >= liveFrom)));

                totalLoaded += batch.Count;

                if (batch.Count < batchSize)
                {
                    break;
                }

                skip += batchSize;
            }

            if (totalLoaded == 0 && foldedHistory.Count == 0)
            {
                if (existingStatistics == null)
                {
                    // AB#5425: report anyway. A pipeline that has never executed must still have a
                    // series — that is what keeps "no data" meaning "the controller is silent"
                    // instead of "there is nothing to say".
                    PipelineExecutionMetrics.ObserveStatistics(tenantId, pipelineRtEntityId.RtId, null, 0, 0);

                    Logger.Debug("[{TenantId}] No executions and no existing statistics for pipeline '{PipelineRtEntityId}', skipping update",
                        tenantId, pipelineRtEntityId);
                    return;
                }

                if (IsStatisticsEmpty(existingStatistics) && (existingStatistics.HourlyBuckets?.Count ?? 0) == 0)
                {
                    PipelineExecutionMetrics.ObserveStatistics(tenantId, pipelineRtEntityId.RtId,
                        existingStatistics.LastExecutionAt, 0, 0);

                    Logger.Debug("[{TenantId}] Statistics already empty for pipeline '{PipelineRtEntityId}', skipping update",
                        tenantId, pipelineRtEntityId);
                    return;
                }

                // Statistics have non-zero values but no executions/buckets remain - reset to zero
                // Fall through to normal upsert with zero values
            }

            // One list, one source: folded history + live snapshot. Without a boundary the live
            // half is not persisted — nothing would tell the next fold which part is folded — but
            // the counters are still computed from the combined list. The fold of the same sweep
            // sets the boundary, so this legacy state lasts one sweep at most.
            var combined = PipelineStatisticsFolder.MergeBuckets(foldedHistory, liveDeltas, windowStart30Days);
            var persisted = foldedBefore != null ? combined : foldedHistory;

            var window12Hours = PipelineStatisticsFolder.SumBuckets(combined, windowStart12Hours);
            var window24Hours = PipelineStatisticsFolder.SumBuckets(combined, windowStart24Hours);
            var window30Days = PipelineStatisticsFolder.SumBuckets(combined, windowStart30Days);

            if (lastExecutionAt == null || existingStatistics?.LastExecutionAt > lastExecutionAt)
            {
                // Folded executions are gone from the live scan; never regress the marker.
                lastExecutionAt = existingStatistics?.LastExecutionAt;
            }

            var statistics = new RtPipelineStatistics
            {
                LastHourSuccessCount = lastHour.SuccessCount,
                LastHourFailureCount = lastHour.FailureCount,
                LastHourAvgDurationMs = (int)lastHour.AvgDurationMs,
                Last12HoursSuccessCount = window12Hours.SuccessCount,
                Last12HoursFailureCount = window12Hours.FailureCount,
                Last12HoursAvgDurationMs = window12Hours.AvgDurationMs,
                Last24HoursSuccessCount = window24Hours.SuccessCount,
                Last24HoursFailureCount = window24Hours.FailureCount,
                Last24HoursAvgDurationMs = window24Hours.AvgDurationMs,
                Last30DaysSuccessCount = window30Days.SuccessCount,
                Last30DaysFailureCount = window30Days.FailureCount,
                Last30DaysAvgDurationMs = window30Days.AvgDurationMs,
                LastUpdatedAt = now,
                LastExecutionAt = lastExecutionAt,
                FoldedBefore = foldedBefore,
                HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(persisted.Cast<RtRecord>().ToList())
            };

            await communicationRepository.UpsertPipelineStatisticsAsync(tenantId, statistics, pipelineRtEntityId);

            // AB#5425. The sweep is what makes the pipeline gauges unconditional: it runs for every
            // pipeline of every enabled tenant, so a healthy pipeline publishes zeros and a dead one
            // publishes a rising age, instead of both publishing nothing.
            PipelineExecutionMetrics.ObserveStatistics(tenantId, pipelineRtEntityId.RtId,
                statistics.LastExecutionAt, statistics.LastHourSuccessCount, statistics.LastHourFailureCount);

            Logger.Debug("[{TenantId}] Statistics updated for pipeline '{PipelineRtEntityId}' ({TotalExecutions} executions processed, {BucketCount} buckets)",
                tenantId, pipelineRtEntityId, totalLoaded, persisted.Count);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to update statistics for pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
            throw PipelineExecutionServiceException.CommonFailedUpdateStatistics(tenantId, pipelineRtEntityId, e);
        }
    }

    private static void MergeDeltas(Dictionary<DateTime, PipelineStatisticsFolder.BucketAccumulator> target,
        Dictionary<DateTime, PipelineStatisticsFolder.BucketAccumulator> source)
    {
        foreach (var (hour, delta) in source)
        {
            if (!target.TryGetValue(hour, out var acc))
            {
                target[hour] = delta;
                continue;
            }

            acc.SuccessCount += delta.SuccessCount;
            acc.FailureCount += delta.FailureCount;
            acc.TotalDurationMs += delta.TotalDurationMs;
            acc.DurationCount += delta.DurationCount;
        }
    }

    private static void AccumulateExecution(StatisticsAccumulator accumulator, RtPipelineExecution exec)
    {
        switch (PipelineStatisticsFolder.Classify(exec.Status))
        {
            case PipelineStatisticsFolder.ExecutionOutcome.Success:
                accumulator.SuccessCount++;
                break;
            case PipelineStatisticsFolder.ExecutionOutcome.Failure:
                accumulator.FailureCount++;
                break;
        }

        if (exec.DurationMs.HasValue)
        {
            accumulator.TotalDurationMs += exec.DurationMs.Value;
            accumulator.ExecutionWithDurationCount++;
        }
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

    /// <summary>
    ///     The rtIds of the tenant's pipelines that an ENABLED PipelineTrigger targets, or
    ///     <c>null</c> when the triggers could not be read.
    /// </summary>
    /// <remarks>
    ///     AB#5492. PipelineTrigger is the cron trigger and nothing else — the CK model defines it
    ///     as "a scheduled trigger that executes one or more pipelines based on a cron expression"
    ///     and gives it a CronExpression attribute. An HTTP-driven pipeline has no trigger entity at
    ///     all; it runs from its own FromHttpRequest node. So membership in this set is exactly the
    ///     question "does this pipeline owe an execution", which is what makes the age gauge
    ///     alertable.
    ///
    ///     The repository filters on Enabled, so a pipeline whose trigger was switched off drops out
    ///     and stops being expected to run — the same reasoning the deployment_state tag follows.
    ///
    ///     Returning null rather than an empty set on failure is deliberate: an empty set would
    ///     label every pipeline "unscheduled" and silently suppress the very alert this enables.
    ///     One read per sweep for the whole tenant, so it costs one query per sweep, not per
    ///     pipeline.
    /// </remarks>
    private async Task<IReadOnlySet<string>?> TryGetScheduledPipelineRtIdsAsync(string tenantId)
    {
        try
        {
            var triggersAndPipelines = await communicationRepository.GetTriggersAndPipelinesAsync(tenantId);
            return triggersAndPipelines.Values
                .SelectMany(pipelines => pipelines)
                .Select(pipeline => pipeline.RtId.ToString())
                .ToHashSet();
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{TenantId}] Failed to read pipeline triggers; trigger kind is reported as unknown",
                tenantId);
            return null;
        }
    }

    private static bool? IsScheduled(IReadOnlySet<string>? scheduledRtIds, OctoObjectId pipelineRtId) =>
        scheduledRtIds?.Contains(pipelineRtId.ToString());

    public async Task UpdateAllStatisticsAsync(string tenantId)
    {
        Logger.Debug("[{TenantId}] Updating statistics for all pipelines", tenantId);

        try
        {
            var pipelines = await communicationRepository.GetAllPipelinesAsync(tenantId);
            var scheduledRtIds = await TryGetScheduledPipelineRtIdsAsync(tenantId);

            foreach (var pipeline in pipelines)
            {
                try
                {
                    // Note: CkTypeId should never be null for a valid pipeline
                    var pipelineRtEntityId = new RtEntityId(pipeline.CkTypeId!, pipeline.RtId);
                    PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline,
                        IsScheduled(scheduledRtIds, pipeline.RtId));
                    await UpdateStatisticsAsync(tenantId, pipelineRtEntityId);
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "[{TenantId}] Failed to update statistics for pipeline '{PipelineRtId}'",
                        tenantId, pipeline.RtId);
                }
            }

            PipelineExecutionMetrics.RetainPipelines(tenantId,
                pipelines.Select(p => p.RtId.ToString()).ToHashSet());

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
        // One instant for the whole pass, so the fold boundary and the windows recomputed right
        // after it agree on which clock hour is current (AB#5583).
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var totalPruned = 0;

        try
        {
            var pipelines = await communicationRepository.GetAllPipelinesAsync(tenantId);
            var scheduledRtIds = await TryGetScheduledPipelineRtIdsAsync(tenantId);

            foreach (var pipeline in pipelines)
            {
                // Note: CkTypeId should never be null for a valid pipeline
                var pipelineRtEntityId = new RtEntityId(pipeline.CkTypeId!, pipeline.RtId);

                // AB#5425: the identity half of the gauge labels. Published before the fold so
                // that a pipeline whose fold or statistics update throws is still exported with a
                // readable name rather than an empty one.
                PipelineExecutionMetrics.ObservePipeline(tenantId, pipeline,
                    IsScheduled(scheduledRtIds, pipeline.RtId));

                try
                {
                    totalPruned += await FoldAndPrunePipelineAsync(tenantId, pipelineRtEntityId, now,
                        retentionHours);

                    // Refresh the windows on every pass — also when nothing was folded, so
                    // counters decay once a pipeline stops executing.
                    await UpdateStatisticsAsync(tenantId, pipelineRtEntityId, now);
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "[{TenantId}] Failed to fold executions for pipeline '{PipelineRtId}'",
                        tenantId, pipeline.RtId);
                }
            }

            // Drop pipelines that no longer exist; a deleted pipeline whose age gauge kept climbing
            // would eventually alert about something nobody can fix.
            PipelineExecutionMetrics.RetainPipelines(tenantId,
                pipelines.Select(p => p.RtId.ToString()).ToHashSet());

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

    /// <summary>
    /// Folds the terminal executions that started before the hour-aligned fold cutoff into the
    /// folded-history buckets and deletes them (AB#4370, AB#5583).
    /// </summary>
    /// <remarks>
    /// The persisted buckets at or after the previous boundary are a snapshot of retained
    /// executions, not history; the fold therefore merges into the folded history only and
    /// carries the snapshot hours that stay live. Each write stores the new boundary together
    /// with the merged history, so a crash between two batches leaves a consistent state: the
    /// executions not yet deleted are still before the boundary and are folded by the next pass.
    /// </remarks>
    private async Task<int> FoldAndPrunePipelineAsync(string tenantId, RtEntityId pipelineRtEntityId,
        DateTime now, int retentionHours)
    {
        const int batchSize = 500;
        var pruned = 0;

        var existing = await communicationRepository.GetPipelineStatisticsAsync(tenantId, pipelineRtEntityId);
        var previousBoundary = existing?.FoldedBefore;
        var cutoff = PipelineStatisticsFolder.FoldCutoff(now, retentionHours, previousBoundary);

        if (previousBoundary == null && existing?.HourlyBuckets is { Count: > 0 } legacyBuckets)
        {
            // Statistics from before AB#5583: the fold cutoff was rolling (now - retention), so the
            // newest folded bucket may hold part of an hour whose rest is still retained. Moving
            // the boundary past that hour folds the rest of it now — once — so no hour is ever
            // split between folded history and the live snapshot.
            var afterNewestFolded = legacyBuckets.Max(b => b.HourStartAt).AddHours(1);
            if (afterNewestFolded > cutoff)
            {
                cutoff = afterNewestFolded;
            }
        }

        var pruneBefore = PipelineStatisticsFolder.WindowStart(now, PipelineStatisticsFolder.RetentionWindowHours);
        var history = PipelineStatisticsFolder.FoldedHistory(existing?.HourlyBuckets, previousBoundary);
        var liveSnapshot = previousBoundary != null
            ? PipelineStatisticsFolder.SnapshotFrom(existing?.HourlyBuckets, cutoff)
            : [];
        var lastExecutionAt = existing?.LastExecutionAt;
        var written = false;

        while (true)
        {
            var batch = await communicationRepository.GetTerminalExecutionsOlderThanAsync(
                tenantId, pipelineRtEntityId, cutoff, batchSize);

            if (batch.Count == 0)
            {
                break;
            }

            history = PipelineStatisticsFolder.MergeBuckets(history,
                PipelineStatisticsFolder.ToBucketDeltas(batch), pruneBefore);

            var maxStartedAt = batch.Max(e => e.StartedAt);
            if (lastExecutionAt == null || maxStartedAt > lastExecutionAt)
            {
                lastExecutionAt = maxStartedAt;
            }

            // Fold-then-delete: persist the buckets BEFORE erasing the batch. A crash between
            // the two double-counts at most one batch on the next run instead of losing it.
            await communicationRepository.UpsertPipelineStatisticsAsync(tenantId,
                FoldedStatistics(existing, history, liveSnapshot, cutoff, lastExecutionAt), pipelineRtEntityId);
            written = true;
            await communicationRepository.DeleteExecutionsAsync(tenantId,
                batch.Select(e => e.ToRtEntityId()).ToList());

            pruned += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        if (!written && existing != null && previousBoundary != cutoff)
        {
            // Nothing to fold, but the boundary moved (or is set for the first time): the
            // snapshot hours that fell behind it have no retained executions left and are dropped.
            await communicationRepository.UpsertPipelineStatisticsAsync(tenantId,
                FoldedStatistics(existing, history, liveSnapshot, cutoff, lastExecutionAt), pipelineRtEntityId);
        }

        return pruned;
    }

    private static RtPipelineStatistics FoldedStatistics(RtPipelineStatistics? existing,
        IEnumerable<RtPipelineStatisticsHourBucketRecord> history,
        IEnumerable<RtPipelineStatisticsHourBucketRecord> liveSnapshot,
        DateTime foldedBefore, DateTime? lastExecutionAt)
    {
        var buckets = history.Where(b => b.HourStartAt < foldedBefore)
            .Concat(liveSnapshot.Where(b => b.HourStartAt >= foldedBefore))
            .OrderBy(b => b.HourStartAt)
            .Cast<RtRecord>()
            .ToList();

        return new RtPipelineStatistics
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
            LastExecutionAt = lastExecutionAt,
            FoldedBefore = foldedBefore,
            HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(buckets)
        };
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

                // AB#5425. Only the newly inserted ones are counted — duplicates were filtered out
                // above, so a replayed buffer does not double-count.
                foreach (var byOutcome in executionsToInsert.GroupBy(e => e.Status))
                {
                    PipelineExecutionMetrics.RecordExecutionOutcomes(tenantId, byOutcome.Key, byOutcome.Count());
                }

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
