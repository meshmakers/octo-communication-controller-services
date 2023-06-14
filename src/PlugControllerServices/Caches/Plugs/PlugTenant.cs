using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs.Descriptions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs;

public class PlugTenant
{
    private readonly IPlugCachePublish _plugCachePublish;
    public string TenantId { get; }
    
    private readonly ConcurrentDictionary<string, Plug> _plugsByConnectId;
    private readonly ConcurrentDictionary<OctoObjectId, Plug> _plugsById;

    public IReadOnlyDictionary<string, Plug> PlugsByConnectionId { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, Plug> PlugsById { get; private set; }

    public PlugTenant(IPlugCachePublish plugCachePublish, string tenantId)
    {
        _plugCachePublish = plugCachePublish;

        TenantId = tenantId;
        _plugsByConnectId = new ConcurrentDictionary<string, Plug>();
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>();
        PlugsByConnectionId = new ReadOnlyDictionary<string, Plug>(_plugsByConnectId);
        PlugsById = new ReadOnlyDictionary<OctoObjectId, Plug>(_plugsById);
    }
    
    public PlugTenant(IPlugCachePublish plugCachePublish, string tenantId, IList<PlugDescription> plugDescriptions)
    {
        _plugCachePublish = plugCachePublish;
        
        TenantId = tenantId;
        _plugsByConnectId = new ConcurrentDictionary<string, Plug>(
            plugDescriptions.ToDictionary(p => p.ConnectionId, p => new Plug(plugCachePublish, p)));
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>(
            plugDescriptions.ToDictionary(p => p.PlugRtId, p => new Plug(plugCachePublish, p)));
        PlugsByConnectionId = new ReadOnlyDictionary<string, Plug>(_plugsByConnectId);
        PlugsById = new ReadOnlyDictionary<OctoObjectId, Plug>(_plugsById);
    }

    public Plug AddPlug(OctoObjectId plugRtId, string connectionId, PlugConfigurationDto configuration)
    {
        var plug = new Plug(_plugCachePublish, plugRtId, connectionId, configuration);
        _plugsByConnectId.AddOrUpdate(connectionId, _ => plug,
            (_, _) => plug);
        _plugsById.AddOrUpdate(plugRtId, _ => plug,
            (_, _) => plug);
        
        _plugCachePublish.PublishConfiguration();

        return plug;
    }

    public void RemovePlug(OctoObjectId plugRtId)
    {
        if (_plugsById.TryRemove(plugRtId, out var plug))
        {
            if (_plugsByConnectId.TryRemove(plug.ConnectionId, out _))
            {
                _plugCachePublish.PublishConfiguration();
            }
        }
    }

    public PlugTenantDescription GetTenantDescription()
    {
        return new PlugTenantDescription
        {
            TenantId = TenantId,
            Plugs = PlugsById.Values.Select(p => p.GetPlugDescription())
        };
    }
}