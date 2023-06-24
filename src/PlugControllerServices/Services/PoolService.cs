using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.PlugControllerServices.Repository;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;
using Plug = Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools.Plug;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

internal class PoolService : IPoolService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IPlugRepository _plugRepository;
    private readonly IPoolCache _poolCache;
    private readonly IPoolHubCallbacks _poolHubCallbacks;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="plugRepository">Plug repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="poolHubCallbacks">Callbacks to inform client of configuration changes</param>
    public PoolService(IPlugRepository plugRepository, IPoolCache poolCache, IPoolHubCallbacks poolHubCallbacks)
    {
        _plugRepository = plugRepository;
        _poolCache = poolCache;
        _poolHubCallbacks = poolHubCallbacks;
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> RegisterPlugPoolOperatorAsync(string tenantId, string plugPoolName, string connectionId)
    {
        Logger.Info("[{TenantId}] Registering operator for Plug Pool '{PlugPoolName}'",
            tenantId, plugPoolName);
        
        var poolTenant = _poolCache.AddOrUpdateTenant(tenantId);

        if (poolTenant.PoolsByName.TryGetValue(plugPoolName, out var poolDescription))
        {
            Logger.Info("[{TenantId}] Plug Pool '{PlugPoolName}' already registered",
                tenantId, plugPoolName);

            poolDescription.UpdateConnectionId(connectionId);
        }
        else
        {
            var plugPoolList = await _plugRepository.GetPlugPoolByNameAsync(tenantId, plugPoolName);
            var rtPlugPool = plugPoolList.FirstOrDefault();
            if (rtPlugPool == null)
            {
                Logger.Info("[{TenantId}] Creating Plug Pool '{PlugPoolName}'",
                    tenantId, plugPoolName);
                await _plugRepository.CreatePlugPoolAsync(tenantId, plugPoolName);

                plugPoolList = await _plugRepository.GetPlugPoolByNameAsync(tenantId, plugPoolName);
                rtPlugPool = plugPoolList.FirstOrDefault();

                if (rtPlugPool == null)
                {
                    throw PoolServiceException.CannotCreatePlugPool(tenantId, plugPoolName);
                }
            }

            poolDescription = poolTenant.AddPool(plugPoolName, rtPlugPool.RtId.ToOctoObjectId(), connectionId);
        }

        // Update status in asset repository
        await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Deployed);

        Logger.Info("[{TenantId}] Operator for Plug Pool '{PlugPoolName}' registered",
            tenantId, plugPoolName);
        return poolDescription.PlugPoolRtId;
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> UnregisterPlugPoolOperatorAsync(string tenantId, string plugPoolName)
    {
        Logger.Info("[{TenantId}] Unregistering operator for Plug Pool '{PlugPoolName}'",
            tenantId, plugPoolName);

        if (!_poolCache.TryGetTenant(tenantId, out var tenantDescription) || tenantDescription == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsByName.TryGetValue(plugPoolName, out var poolDescription))
        {
            tenantDescription.RemovePool(poolDescription.PlugPoolRtId);

            await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Pending);

            Logger.Info("[{TenantId}] Operator for Plug Pool '{PlugPoolName}' unregistered",
                tenantId, plugPoolName);
            return poolDescription.PlugPoolRtId;
        }

        throw PoolServiceException.PlugPoolNotFound(tenantId, plugPoolName);
    }

    /// <inheritdoc />
    public async Task<PlugPoolConfigurationDto> GetCurrentPlugsAsync(string tenantId, OctoObjectId plugPoolRtId)
    {
        Logger.Info("[{TenantId}] Getting current plugs for Plug Pool '{PlugPoolRtId}'", tenantId, plugPoolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            poolTenant.RemovePlugs(plugPoolRtId);
            
            var rtPlugs = await _plugRepository.GetPlugsAsync(tenantId, plugPoolRtId);
            foreach (var rtPlug in rtPlugs)
            {
                poolTenant.AddPlug(new Plug(rtPlug.RtId.ToOctoObjectId(), plugPoolRtId));
            }
            
            var result = new PlugPoolConfigurationDto
            {
                Plugs = rtPlugs
                    .Select(rtPlug => CreatePlugPoolPlugDto(plugPoolRtId, poolDescription.PoolName, rtPlug))
            };
            
            Logger.Info("[{TenantId}] Current plugs for Plug Pool '{PlugPoolRtId}' retrieved", tenantId, plugPoolRtId);
            return result;
        }

        throw PoolServiceException.PlugPoolNotFound(tenantId, plugPoolRtId);
    }

    public async Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reloading tenant", tenantId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        foreach (var keyValuePair in poolTenant.PlugsById)
        {
            await UndeployPlugAsync(tenantId, keyValuePair.Value.PoolRtId, keyValuePair.Value.PlugRtId);
        }

        poolTenant.Clear();
    }

    /// <inheritdoc />
    public async Task DeployPlugAsync(string tenantId, OctoObjectId plugPoolRtId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] Deploying Plug '{PlugRtId}' to pool '{PoolRtId}'", tenantId, plugRtId, plugPoolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            var rtPlug = await _plugRepository.GetPlugAsync(tenantId, plugRtId);
            await _poolHubCallbacks.DeployPlugAsync(tenantId, CreatePlugPoolPlugDto(plugPoolRtId,
                poolDescription.PoolName,
                rtPlug));

            poolTenant.AddPlug(new Plug(plugRtId, plugPoolRtId));

            Logger.Info("[{TenantId}] Plug '{PlugRtId}' deployed", tenantId, rtPlug.RtId);
            return;
        }

        throw PoolServiceException.PlugPoolNotFound(tenantId, plugPoolRtId);
    }

    /// <inheritdoc />
    public async Task UndeployPlugAsync(string tenantId, OctoObjectId plugPoolRtId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] Undeploying Plug '{PlugRtId}' from pool '{PoolRtId}'", tenantId, plugRtId, plugPoolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PlugsById.TryGetValue(plugRtId, out var plugDescription))
        {
            if (poolTenant.PoolsById.TryGetValue(plugDescription.PoolRtId, out var poolDescription))
            {
                var rtPlug = await _plugRepository.GetPlugAsync(tenantId, plugRtId);
                await _poolHubCallbacks.UndeployPlugAsync(tenantId, CreatePlugPoolPlugDto(poolDescription.PlugPoolRtId,
                    poolDescription.PoolName, rtPlug));

                poolTenant.RemovePlug(rtPlug.RtId.ToOctoObjectId());

                Logger.Info("[{TenantId}] Plug '{PlugRtId}' undeployed", tenantId, rtPlug.RtId);
                return;
            }

            throw PoolServiceException.PlugPoolNotFound(tenantId, plugDescription.PoolRtId);
        }

        throw PoolServiceException.PlugNotFound(tenantId, plugRtId);
    }

    /// <inheritdoc />
    public async Task SetPoolOfflineAsync(string tenantId, OctoObjectId plugPoolRtId)
    {
        Logger.Info("[{TenantId}] Setting Plug pool '{PlugPoolRtId}' offline", tenantId, plugPoolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Offline);
        }
    }

    /// <inheritdoc />
    public async Task SetPoolOfflineAsync(string tenantId, string poolName)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
            if (poolDescription != null)
            {
                await SetPoolOfflineAsync(tenantId, poolDescription.PlugPoolRtId);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetPoolOnlineAsync(string tenantId, OctoObjectId plugPoolRtId)
    {
        Logger.Info("[{TenantId}] Setting Plug pool '{PlugPoolRtId}' online", tenantId, plugPoolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Online);
        }
    }

    /// <inheritdoc />
    public async Task SetPoolOnlineAsync(string tenantId, string poolName, string connectionId)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
        if (poolDescription != null)
        {
            poolDescription.UpdateConnectionId(connectionId);
            
            await SetPoolOnlineAsync(tenantId, poolDescription.PlugPoolRtId);
        }
    }

    public Task OnHandlePoolUpdateAsync(string tenantId, UpdateInfo<RtPlugPool> info)
    {
        // TODO: Implement updates of pool entity.
        return Task.CompletedTask;
    }

    public async Task OnHandlePlugUpdateAsync(string tenantId, UpdateInfo<RtPlug> info)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        switch (info.UpdateType)
        {
            case UpdateTypes.Update:
            case UpdateTypes.Replace:
                if (info.Document != null && poolTenant.PlugsById.TryGetValue(info.Document.RtId.ToOctoObjectId(), out var plug))
                {
                    if (info.UpdateFields.Contains("attributes." + nameof(RtPlug.ImageName).ToCamelCase()) ||
                        info.UpdateFields.Contains("attributes." + nameof(RtPlug.ImageVersion).ToCamelCase()))
                    {
                        await UndeployPlugAsync(tenantId, plug.PoolRtId, plug.PlugRtId);
                        await DeployPlugAsync(tenantId, plug.PoolRtId, plug.PlugRtId);
                    }
                }
                break;
            default:
                // By default we do nothing
                break;
        }

    }

    private PlugPoolPlugDto CreatePlugPoolPlugDto(OctoObjectId plugPoolRtId, string plugPoolName, RtPlug rtPlug)
    {
        return new PlugPoolPlugDto
        {
            PlugPoolRtId = plugPoolRtId,
            PlugPoolName = plugPoolName,
            PlugRtId = rtPlug.RtId.ToOctoObjectId(),
            ImageName = rtPlug.ImageName ?? throw PoolServiceException.ImageNameNotSet(),
            Version = rtPlug.ImageVersion ?? throw PoolServiceException.ImageVersionNotSet(),
        };
    }
}