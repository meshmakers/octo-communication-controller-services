using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NLog;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for adapter
/// </summary>
internal class AdapterHub : Hub, IAdapterHub
{
    private readonly IAdapterService _adapterService;
    private readonly IPipelineDebugService _pipelineDebugService;
    private readonly ICommunicationEventService _eventService;
    private readonly IPipelineExecutionService _pipelineExecutionService;
    private readonly IPipelineExecutionReportQueue _executionReportQueue;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="adapterService">The responsible adapter service</param>
    /// <param name="pipelineDebugService">The responsible pipeline debug service</param>
    /// <param name="eventService">Service for storing system events</param>
    /// <param name="pipelineExecutionService">Service for managing pipeline execution metrics</param>
    /// <param name="executionReportQueue">Queue for background processing of execution reports</param>
    public AdapterHub(IAdapterService adapterService, IPipelineDebugService pipelineDebugService,
        ICommunicationEventService eventService, IPipelineExecutionService pipelineExecutionService,
        IPipelineExecutionReportQueue executionReportQueue)
    {
        _adapterService = adapterService;
        _pipelineDebugService = pipelineDebugService;
        _eventService = eventService;
        _pipelineExecutionService = pipelineExecutionService;
        _executionReportQueue = executionReportQueue;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        try
        {
            await _adapterService.SetAdapterCommunicationStateOnlineAsync(tenantId, adapterRtEntityId, Context.ConnectionId);

            await base.OnConnectedAsync();
        }
        catch (AdapterServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        try
        {
            // Mark any running executions as interrupted before going offline
            await _pipelineExecutionService.MarkExecutionsAsInterruptedAsync(tenantId, adapterRtEntityId);

            await _adapterService.SetAdapterCommunicationStateOfflineAsync(tenantId, adapterRtEntityId, Context.ConnectionId);

            await base.OnDisconnectedAsync(exception);
        }
        catch (AdapterServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
        catch (PipelineExecutionServiceException e)
        {
            Logger.Error(e, e.Message);
            // Continue with disconnect even if execution marking fails
        }
    }

    /// <inheritdoc />
    public async Task<AdapterConfigurationDto> RegisterAdapterAsync(RtEntityId adapterRtEntityId)
    {
        var tenantId = GetTenantId();

        try
        {
            var configurationDto =
                await _adapterService.RegisterAdapterAsync(tenantId, adapterRtEntityId, Context.ConnectionId);
            return configurationDto;
        }
        catch (AdapterServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot register adapter");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to register adapter: {e.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnRegisterAdapterAsync(RtEntityId adapterRtEntityId)
    {
        var tenantId = GetTenantId();

        try
        {
            await _adapterService.UnregisterAsync(tenantId, adapterRtEntityId, Context.ConnectionId);
        }
        catch (AdapterServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot unregister adapter");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to unregister adapter: {e.Message}");
            throw;
        }
    }

    public async Task SendDebugDataAsync(RtEntityId pipelineRtEntityId, Guid pipelineExecutionId, DebugPointDto debugPoint)
    {
        var tenantId = GetTenantId();

        try
        {
            Logger.Info("[{TenantId}] Received debug data for pipeline '{PipelineRtEntityId}', execution '{ExecutionId}', node '{NodeId}'",
                tenantId, pipelineRtEntityId, pipelineExecutionId, debugPoint.NodeId);

            await _pipelineDebugService.CacheDebugPointAsync(tenantId, pipelineRtEntityId, pipelineExecutionId, debugPoint);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot cache debug data");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to cache debug data for pipeline '{pipelineRtEntityId}': {e.Message}");
            throw;
        }
    }


    public async Task SendDeploymentUpdateResultAsync(RtEntityId adapterRtEntityId, DeploymentResult deploymentResult)
    {
        var tenantId = GetTenantId();

        try
        {
            await _adapterService.UpdateConfigurationStateAsync(tenantId, adapterRtEntityId, deploymentResult);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot update deployment result");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to update deployment result for adapter '{adapterRtEntityId}': {e.Message}");
            throw;
        }
    }

    /// <summary>
    /// Reports the start of a pipeline execution from the adapter.
    /// Enqueues the report for background processing to avoid blocking the SignalR connection.
    /// </summary>
    /// <param name="startDto">The execution start details</param>
    public Task ReportExecutionStartAsync(PipelineExecutionStartDto startDto)
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        _executionReportQueue.EnqueueStart(tenantId, adapterRtEntityId, startDto);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reports the end of a pipeline execution from the adapter.
    /// Enqueues the report for background processing to avoid blocking the SignalR connection.
    /// </summary>
    /// <param name="endDto">The execution end details</param>
    public Task ReportExecutionEndAsync(PipelineExecutionEndDto endDto)
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        _executionReportQueue.EnqueueComplete(tenantId, adapterRtEntityId, endDto);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reports the final result of a previously interrupted execution after adapter reconnect.
    /// Enqueues the report for background processing to avoid blocking the SignalR connection.
    /// </summary>
    /// <param name="endDto">The final execution result</param>
    public Task ReportInterruptedExecutionResultAsync(PipelineExecutionEndDto endDto)
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        _executionReportQueue.EnqueueInterruptedResult(tenantId, adapterRtEntityId, endDto);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the list of execution IDs that were marked as interrupted for this adapter
    /// </summary>
    /// <returns>List of interrupted execution IDs</returns>
    public async Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync()
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        try
        {
            return await _pipelineExecutionService.GetInterruptedExecutionIdsAsync(tenantId, adapterRtEntityId);
        }
        catch (PipelineExecutionServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot get interrupted execution IDs");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to get interrupted execution IDs for adapter '{adapterRtEntityId}': {e.Message}");
            throw;
        }
    }

    /// <summary>
    /// Synchronizes buffered executions from an adapter that was offline
    /// </summary>
    /// <param name="request">The sync request containing buffered executions</param>
    /// <returns>Sync response with processing results</returns>
    public async Task<BufferedExecutionsSyncResponse> SyncBufferedExecutionsAsync(BufferedExecutionsSyncRequest request)
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        try
        {
            return await _pipelineExecutionService.ProcessBufferedExecutionsAsync(tenantId, adapterRtEntityId, request);
        }
        catch (PipelineExecutionServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot sync buffered executions");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to sync buffered executions for adapter '{adapterRtEntityId}': {e.Message}");
            throw;
        }
    }

    /// <summary>
    /// Gets the last synced sequence number for this adapter
    /// </summary>
    /// <returns>The last synced sequence number, or 0 if never synced</returns>
    public async Task<long> GetLastSyncedSequenceNumberAsync()
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        try
        {
            return await _pipelineExecutionService.GetLastSyncedSequenceNumberAsync(tenantId, adapterRtEntityId);
        }
        catch (PipelineExecutionServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot get last synced sequence number");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to get last synced sequence number for adapter '{adapterRtEntityId}': {e.Message}");
            throw;
        }
    }

    private string GetTenantId()
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }

        return tenantId.NormalizeString();
    }

    private RtEntityId GetAdapterRtEntityId()
    {
        var adapterRtId = Context.GetHttpContext()?.GetAdapterRtEntityId();
        if (adapterRtId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("AdapterRtId is null");
        }

        return adapterRtId.Value;
    }
}