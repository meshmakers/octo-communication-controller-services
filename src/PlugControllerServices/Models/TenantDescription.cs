using System.Collections.ObjectModel;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

internal class TenantDescription
{
    public string TenantId { get; }
    private readonly Dictionary<OctoObjectId, PoolDescription> _poolsById = new();
    private readonly Dictionary<string, PoolDescription> _poolsByName = new();
    private readonly Dictionary<OctoObjectId, PlugDescription> _plugsById = new();

    public IReadOnlyDictionary<string, PoolDescription> PoolsByName { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, PoolDescription> PoolsById { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, PlugDescription> PlugsById { get; private set; }

    public TenantDescription(string tenantId)
    {
        TenantId = tenantId;
        PoolsByName = new ReadOnlyDictionary<string, PoolDescription>(_poolsByName);
        PoolsById = new ReadOnlyDictionary<OctoObjectId, PoolDescription>(_poolsById);
        
        PlugsById = new ReadOnlyDictionary<OctoObjectId, PlugDescription>(_plugsById);
    }

    public void AddPool(PoolDescription poolDescription)
    {
        _poolsById.Add(poolDescription.PlugPoolRtId, poolDescription);
        _poolsByName.Add(poolDescription.PoolName, poolDescription);
    }

    public void AddPlug(PlugDescription plugDescription)
    {
        _plugsById.Add(plugDescription.PlugRtId, plugDescription);
    }

    public void Clear()
    {
        _poolsById.Clear();
        _poolsByName.Clear();
    }

    public void RemovePlug(OctoObjectId plugRtId)
    {
        _plugsById.Remove(plugRtId);
    }

    public void RemovePool(string plugPoolName)
    {
        if (_poolsByName.TryGetValue(plugPoolName, out var poolDescription))
        {
            _poolsById.Remove(poolDescription.PlugPoolRtId);
            _poolsByName.Remove(plugPoolName);
        }
    }
}