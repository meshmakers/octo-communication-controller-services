using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for adapter
/// </summary>
internal class AdapterHub : Hub, IAdapterHub
{
    private readonly IAdapterService _adapterService;
    private readonly IPipelineDebugService _pipelineDebugService;
    private readonly ICommunicationEventService _eventService;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="adapterService">The responsible adapter service</param>
    /// <param name="pipelineDebugService">The responsible pipeline debug service</param>
    /// <param name="eventService">Service for storing system events</param>
    public AdapterHub(IAdapterService adapterService, IPipelineDebugService pipelineDebugService,
        ICommunicationEventService eventService)
    {
        _adapterService = adapterService;
        _pipelineDebugService = pipelineDebugService;
        _eventService = eventService;
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
            await _adapterService.SetAdapterCommunicationStateOfflineAsync(tenantId, adapterRtEntityId);

            await base.OnDisconnectedAsync(exception);
        }
        catch (AdapterServiceException e)
        {
            Logger.Error(e, e.Message);
            throw;
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