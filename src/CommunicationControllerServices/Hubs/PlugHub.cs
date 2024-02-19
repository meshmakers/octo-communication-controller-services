using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for plugs
/// </summary>
public class PlugHub : Hub, IPlugHub
{
    private readonly IPlugService _plugService;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="plugService">The responsible plug service</param>
    public PlugHub(IPlugService plugService)
    {
        _plugService = plugService;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        var plugRtId = GetPlugRtId();

        await _plugService.SetPlugOnlineAsync(tenantId, plugRtId);

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = GetTenantId();
        var plugRtId = GetPlugRtId();

        await _plugService.SetPlugOfflineAsync(tenantId, plugRtId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public async Task<PlugConfigurationDto> RegisterPlugAsync(OctoObjectId plugRtId)
    {
        var tenantId = GetTenantId();
        
        try
        {
            var configurationDto = await _plugService.RegisterPlugAsync(tenantId, plugRtId, Context.ConnectionId);

            await _plugService.SetPlugOnlineAsync(tenantId, plugRtId);

            return configurationDto;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot register plug");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnRegisterPlugAsync(OctoObjectId plugRtId)
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }

        await _plugService.PlugUnRegisteredAsync(tenantId, plugRtId, Context.ConnectionId);
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
    
    private OctoObjectId GetPlugRtId()
    {
        var plugRtId = Context.GetHttpContext()?.GetPlugRtId();
        if (plugRtId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("PlugRtId is null");
        }

        return plugRtId.Value;
    }
}