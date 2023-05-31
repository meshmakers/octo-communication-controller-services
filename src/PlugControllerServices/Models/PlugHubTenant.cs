using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class PlugHubTenant
{
    private readonly IPlugHubContextPublish _plugHubContextPublish;
    public string TenantId { get; }
    
    private readonly ConcurrentDictionary<string, PlugHubPool> _poolsByConnectId;
    private readonly ConcurrentDictionary<OctoObjectId, PlugHubPool> _poolsById;

    public IReadOnlyDictionary<string, PlugHubPool> PoolsByConnectionId { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, PlugHubPool> PoolsById { get; private set; }

    public PlugHubTenant(IPlugHubContextPublish plugHubContextPublish, string tenantId)
    {
        _plugHubContextPublish = plugHubContextPublish;

        TenantId = tenantId;
        _poolsByConnectId = new ConcurrentDictionary<string, PlugHubPool>();
        _poolsById = new ConcurrentDictionary<OctoObjectId, PlugHubPool>();
        PoolsByConnectionId = new ReadOnlyDictionary<string, PlugHubPool>(_poolsByConnectId);
        PoolsById = new ReadOnlyDictionary<OctoObjectId, PlugHubPool>(_poolsById);
    }
    
    public PlugHubTenant(IPlugHubContextPublish plugHubContextPublish, string tenantId, IList<PlugHubPoolDescription> plugHubPools)
    {
        _plugHubContextPublish = plugHubContextPublish;
        
        TenantId = tenantId;
        _poolsByConnectId = new ConcurrentDictionary<string, PlugHubPool>(
            plugHubPools.ToDictionary(p => p.ConnectionId, p => new PlugHubPool(p)));
        _poolsById = new ConcurrentDictionary<OctoObjectId, PlugHubPool>(
            plugHubPools.ToDictionary(p => p.PlugPoolRtId, p => new PlugHubPool(p)));
        PoolsByConnectionId = new ReadOnlyDictionary<string, PlugHubPool>(_poolsByConnectId);
        PoolsById = new ReadOnlyDictionary<OctoObjectId, PlugHubPool>(_poolsById);
    }

    public PlugHubPool AddPool(string plugPoolName, OctoObjectId plugPoolRtId, string connectionId)
    {
        var plugHubPool = new PlugHubPool(plugPoolRtId, plugPoolName, connectionId);
        _poolsByConnectId.AddOrUpdate(connectionId, _ => plugHubPool,
            (_, _) => plugHubPool);
        _poolsById.AddOrUpdate(plugPoolRtId, _ => plugHubPool,
            (_, _) => plugHubPool);
        
        _plugHubContextPublish.PublishConfiguration();

        return plugHubPool;
    }

    public void RemovePool(OctoObjectId plugPoolRtId)
    {
        if (_poolsById.TryRemove(plugPoolRtId, out var plugHubPool))
        {
            if (_poolsByConnectId.TryRemove(plugHubPool.ConnectionId, out _))
            {
                _plugHubContextPublish.PublishConfiguration();
            }
        }
        
    }

    public PlugHubTenantDescription GetTenantDescription()
    {
        return new PlugHubTenantDescription
        {
            TenantId = TenantId,
            Pools = PoolsById.Values.Select(p => p.GetPoolDescription())
        };
    }
}