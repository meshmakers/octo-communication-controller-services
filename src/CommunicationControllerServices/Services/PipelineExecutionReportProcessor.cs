using System.Diagnostics;
using System.Threading.Channels;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Queue interface for enqueuing pipeline execution reports from hub methods
/// without blocking the SignalR connection.
/// </summary>
internal interface IPipelineExecutionReportQueue
{
    /// <summary>
    /// Enqueues an execution start report for background processing.
    /// </summary>
    void EnqueueStart(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionStartDto dto);

    /// <summary>
    /// Enqueues an execution completion report for background processing.
    /// </summary>
    void EnqueueComplete(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto);

    /// <summary>
    /// Enqueues an interrupted execution result report for background processing.
    /// </summary>
    void EnqueueInterruptedResult(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto);
}

/// <summary>
/// Background processor that reads execution reports from a Channel and processes them in batches.
/// Drains all available items from the channel and groups them for efficient bulk MongoDB writes,
/// preventing queue backlogs when adapters produce execution reports faster than individual writes can handle.
/// </summary>
internal class PipelineExecutionReportProcessor : BackgroundService, IPipelineExecutionReportQueue
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const int MaxBatchSize = 100;

    private readonly Channel<ExecutionReportItem> _channel = Channel.CreateUnbounded<ExecutionReportItem>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly IPipelineExecutionService _pipelineExecutionService;
    private readonly ICommunicationEventService _eventService;

    public PipelineExecutionReportProcessor(
        IPipelineExecutionService pipelineExecutionService,
        ICommunicationEventService eventService)
    {
        _pipelineExecutionService = pipelineExecutionService;
        _eventService = eventService;
    }

    public void EnqueueStart(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionStartDto dto)
    {
        if (!_channel.Writer.TryWrite(new StartExecutionReport(tenantId, adapterRtEntityId, dto)))
        {
            Logger.Warn("[{TenantId}] Failed to enqueue execution start report for '{ExecutionId}'",
                tenantId, dto.ExecutionId);
        }
    }

    public void EnqueueComplete(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto)
    {
        if (!_channel.Writer.TryWrite(new CompleteExecutionReport(tenantId, adapterRtEntityId, dto)))
        {
            Logger.Warn("[{TenantId}] Failed to enqueue execution complete report for '{ExecutionId}'",
                tenantId, dto.ExecutionId);
        }
    }

    public void EnqueueInterruptedResult(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto dto)
    {
        if (!_channel.Writer.TryWrite(new InterruptedExecutionReport(tenantId, adapterRtEntityId, dto)))
        {
            Logger.Warn("[{TenantId}] Failed to enqueue interrupted execution report for '{ExecutionId}'",
                tenantId, dto.ExecutionId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("Pipeline execution report processor started (batch mode, max batch size: {MaxBatchSize})",
            MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one item
                if (!await _channel.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                // Drain all available items up to max batch size
                var batch = new List<ExecutionReportItem>();
                while (batch.Count < MaxBatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    await ProcessBatchAsync(batch);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Unexpected error in execution report processor loop");
            }
        }

        Logger.Info("Pipeline execution report processor stopped");
    }

    private async Task ProcessBatchAsync(List<ExecutionReportItem> batch)
    {
        var sw = Stopwatch.StartNew();
        var startReports = new List<StartExecutionReport>();
        var completeReports = new List<CompleteExecutionReport>();
        var interruptedReports = new List<InterruptedExecutionReport>();

        foreach (var item in batch)
        {
            switch (item)
            {
                case StartExecutionReport start:
                    startReports.Add(start);
                    break;
                case CompleteExecutionReport complete:
                    completeReports.Add(complete);
                    break;
                case InterruptedExecutionReport interrupted:
                    interruptedReports.Add(interrupted);
                    break;
            }
        }

        // Process starts in bulk (grouped by tenant + adapter)
        if (startReports.Count > 0)
        {
            await ProcessStartBatchAsync(startReports);
        }

        // Process completions in bulk (grouped by tenant)
        if (completeReports.Count > 0)
        {
            await ProcessCompleteBatchAsync(completeReports);
        }

        // Process interrupted reports individually (rare)
        foreach (var interrupted in interruptedReports)
        {
            await ProcessSingleItemAsync(interrupted);
        }

        sw.Stop();
        if (batch.Count > 1)
        {
            Logger.Debug("Processed batch of {Count} reports ({Starts} starts, {Completes} completes, {Interrupted} interrupted) in {ElapsedMs}ms",
                batch.Count, startReports.Count, completeReports.Count, interruptedReports.Count, sw.ElapsedMilliseconds);
        }
    }

    private async Task ProcessStartBatchAsync(List<StartExecutionReport> startReports)
    {
        // Group by (tenantId, adapterRtEntityId) for bulk insert
        var groups = startReports.GroupBy(s => (s.TenantId, s.AdapterRtEntityId));

        foreach (var group in groups)
        {
            try
            {
                var dtos = group.Select(g => g.Dto).ToList();
                await _pipelineExecutionService.BatchStartExecutionsAsync(
                    group.Key.TenantId, group.Key.AdapterRtEntityId, dtos);
            }
            catch (Exception e)
            {
                Logger.Error(e, "[{TenantId}] Failed to batch process {Count} start reports, falling back to individual processing",
                    group.Key.TenantId, group.Count());

                // Fallback: process individually
                foreach (var report in group)
                {
                    await ProcessSingleItemAsync(report);
                }
            }
        }
    }

    private async Task ProcessCompleteBatchAsync(List<CompleteExecutionReport> completeReports)
    {
        // Group by tenantId for bulk update
        var groups = completeReports.GroupBy(c => c.TenantId);

        foreach (var group in groups)
        {
            try
            {
                var dtos = group.Select(g => g.Dto).ToList();
                await _pipelineExecutionService.BatchCompleteExecutionsAsync(group.Key, dtos);
            }
            catch (Exception e)
            {
                Logger.Error(e, "[{TenantId}] Failed to batch process {Count} complete reports, falling back to individual processing",
                    group.Key, group.Count());

                // Fallback: process individually
                foreach (var report in group)
                {
                    await ProcessSingleItemAsync(report);
                }
            }
        }
    }

    private async Task ProcessSingleItemAsync(ExecutionReportItem item)
    {
        try
        {
            switch (item)
            {
                case StartExecutionReport start:
                    await _pipelineExecutionService.StartExecutionAsync(
                        start.TenantId, start.AdapterRtEntityId, start.Dto);
                    break;

                case CompleteExecutionReport complete:
                    await _pipelineExecutionService.CompleteExecutionAsync(
                        complete.TenantId, complete.AdapterRtEntityId, complete.Dto);
                    break;

                case InterruptedExecutionReport interrupted:
                    await _pipelineExecutionService.ReportInterruptedExecutionResultAsync(
                        interrupted.TenantId, interrupted.AdapterRtEntityId, interrupted.Dto);
                    break;
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Failed to process execution report", item.TenantId);
            try
            {
                await _eventService.StoreErrorEventAsync(item.TenantId,
                    $"Failed to process execution report: {e.Message}");
            }
            catch (Exception eventEx)
            {
                Logger.Error(eventEx, "[{TenantId}] Failed to store error event for execution report failure",
                    item.TenantId);
            }
        }
    }

    private abstract record ExecutionReportItem(string TenantId, RtEntityId AdapterRtEntityId);

    private record StartExecutionReport(
        string TenantId, RtEntityId AdapterRtEntityId, PipelineExecutionStartDto Dto)
        : ExecutionReportItem(TenantId, AdapterRtEntityId);

    private record CompleteExecutionReport(
        string TenantId, RtEntityId AdapterRtEntityId, PipelineExecutionEndDto Dto)
        : ExecutionReportItem(TenantId, AdapterRtEntityId);

    private record InterruptedExecutionReport(
        string TenantId, RtEntityId AdapterRtEntityId, PipelineExecutionEndDto Dto)
        : ExecutionReportItem(TenantId, AdapterRtEntityId);
}
