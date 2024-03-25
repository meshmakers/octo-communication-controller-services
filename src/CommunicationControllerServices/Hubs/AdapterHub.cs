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
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="adapterService">The responsible adapter service</param>
    /// <param name="pipelineDebugService">The responsible pipeline debug service</param>
    public AdapterHub(IAdapterService adapterService, IPipelineDebugService pipelineDebugService)
    {
        _adapterService = adapterService;
        _pipelineDebugService = pipelineDebugService;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        var adapterRtEntityId = GetAdapterRtEntityId();

        try
        {
            await _adapterService.SetAdapterOnlineAsync(tenantId, adapterRtEntityId, Context.ConnectionId);

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
            await _adapterService.SetAdapterOfflineAsync(tenantId, adapterRtEntityId);

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
            throw;
        }
    }

    public async Task SendDebugDataAsync(RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId, string debugData)
    {
        var tenantId = GetTenantId();

        try
        {
            await _pipelineDebugService.CacheDebugInfo(tenantId, adapterRtEntityId, pipelineRtEntityId, debugData);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot cache debug data");
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