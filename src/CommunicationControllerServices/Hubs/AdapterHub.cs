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
public class AdapterHub : Hub, IAdapterHub
{
    private readonly IAdapterService _adapterService;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="adapterService">The responsible adapter service</param>
    public AdapterHub(IAdapterService adapterService)
    {
        _adapterService = adapterService;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        var adapterRtId = GetAdapterRtId();

        await _adapterService.SetAdapterOnlineAsync(tenantId, adapterRtId);

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = GetTenantId();
        var adapterRtId = GetAdapterRtId();

        await _adapterService.SetAdapterOfflineAsync(tenantId, adapterRtId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public async Task<AdapterConfigurationDto> RegisterAdapterAsync(OctoObjectId adapterRtId)
    {
        var tenantId = GetTenantId();
        
        try
        {
            var configurationDto = await _adapterService.RegisterAdapterAsync(tenantId, adapterRtId, Context.ConnectionId);

            await _adapterService.SetAdapterOnlineAsync(tenantId, adapterRtId);

            return configurationDto;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot register adapter");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnRegisterAdapterAsync(OctoObjectId adapterRtId)
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }

        await _adapterService.AdapterUnRegisteredAsync(tenantId, adapterRtId, Context.ConnectionId);
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
    
    private OctoObjectId GetAdapterRtId()
    {
        var adapterRtId = Context.GetHttpContext()?.GetAdapterRtId();
        if (adapterRtId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("AdapterRtId is null");
        }

        return adapterRtId.Value;
    }
}