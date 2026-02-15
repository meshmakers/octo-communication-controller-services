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
/// Background processor that reads execution reports from a Channel and processes them sequentially.
/// This decouples the heavy MongoDB writes from SignalR hub method processing, preventing execution
/// reports from blocking high-priority messages like deployment results on the same connection.
/// </summary>
internal class PipelineExecutionReportProcessor : BackgroundService, IPipelineExecutionReportQueue
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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
        Logger.Info("Pipeline execution report processor started");

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
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

        Logger.Info("Pipeline execution report processor stopped");
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
