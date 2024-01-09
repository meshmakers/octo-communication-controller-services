using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;

internal class PlugTenant
{
    private readonly IPlugCachePublish _plugCachePublish;
    public string TenantId { get; }
    
    private readonly ConcurrentDictionary<string, Plug> _plugsByConnectId;
    private readonly ConcurrentDictionary<OctoObjectId, Plug> _plugsById;

    public IReadOnlyDictionary<string, Plug> PlugsByConnectionId { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, Plug> PlugsById { get; }

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

        var plugs = plugDescriptions.Select(p => new Plug(plugCachePublish, p)).ToArray();
        _plugsByConnectId = new ConcurrentDictionary<string, Plug>(
            plugs.ToDictionary(p => p.ConnectionId, p => p));
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>(
            plugs.ToDictionary(p => p.PlugRtId, p => p));
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
        
        _plugCachePublish.PublishConfiguration(TenantId);

        return plug;
    }

    public void RemovePlug(OctoObjectId plugRtId)
    {
        if (_plugsById.TryRemove(plugRtId, out var plug))
        {
            if (_plugsByConnectId.TryRemove(plug.ConnectionId, out _))
            {
                _plugCachePublish.PublishConfiguration(TenantId);
            }
        }
    }

    public IEnumerable<PlugDescription> GetPlugDescriptions()
    {
        return PlugsById.Values.Select(p => p.GetPlugDescription()).ToArray();
    }
}