using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;
using Plug = Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools.Plug;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

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
    public async Task<OctoObjectId> RegisterPoolOperatorAsync(string tenantId, string poolName, string connectionId)
    {
        Logger.Info("[{TenantId}] Registering operator for pool '{PoolName}'",
            tenantId, poolName);
        
        var poolTenant = _poolCache.AddOrUpdateTenant(tenantId);

        if (poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription))
        {
            Logger.Info("[{TenantId}] Pool '{PoolName}' already registered",
                tenantId, poolName);

            poolDescription.UpdateConnectionId(connectionId);
        }
        else
        {
            var poolList = await _plugRepository.GetPoolByNameAsync(tenantId, poolName);
            var rtPool = poolList.FirstOrDefault();
            if (rtPool == null)
            {
                Logger.Info("[{TenantId}] Creating pool '{PoolName}'",
                    tenantId, poolName);
                await _plugRepository.CreatePoolAsync(tenantId, poolName);

                poolList = await _plugRepository.GetPoolByNameAsync(tenantId, poolName);
                rtPool = poolList.FirstOrDefault();

                if (rtPool == null)
                {
                    throw PoolServiceException.CannotCreatePool(tenantId, poolName);
                }
            }

            poolDescription = poolTenant.AddPool(poolName, rtPool.RtId.ToOctoObjectId(), connectionId);
        }

        // Update status in asset repository
        await _plugRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, PoolStates.Deployed);

        Logger.Info("[{TenantId}] Operator for pool '{PoolName}' registered",
            tenantId, poolName);
        return poolDescription.PoolRtId;
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> UnregisterPoolOperatorAsync(string tenantId, string poolName)
    {
        Logger.Info("[{TenantId}] Unregistering operator for pool '{PoolName}'",
            tenantId, poolName);

        if (!_poolCache.TryGetTenant(tenantId, out var tenantDescription) || tenantDescription == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsByName.TryGetValue(poolName, out var poolDescription))
        {
            tenantDescription.RemovePool(poolDescription.PoolRtId);

            await _plugRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, PoolStates.Pending);

            Logger.Info("[{TenantId}] Operator for pool '{PoolName}' unregistered",
                tenantId, poolName);
            return poolDescription.PoolRtId;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolName);
    }

    /// <inheritdoc />
    public async Task<PoolConfigurationDto> GetCurrentPlugsAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Getting current plugs for pool '{PoolRtId}'", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            poolTenant.RemovePlugs(poolRtId);
            
            var rtPlugs = await _plugRepository.GetPlugsAsync(tenantId, poolRtId);
            foreach (var rtPlug in rtPlugs)
            {
                poolTenant.AddPlug(new Plug(rtPlug.RtId.ToOctoObjectId(), poolRtId));
            }
            
            var result = new PoolConfigurationDto
            {
                Plugs = rtPlugs
                    .Select(rtPlug => CreatePoolPlugDto(poolRtId, poolDescription.PoolName, rtPlug))
            };
            
            Logger.Info("[{TenantId}] Current plugs for Pool '{PoolRtId}' retrieved", tenantId, poolRtId);
            return result;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
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
    public async Task DeployPlugAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] Deploying Plug '{PlugRtId}' to pool '{PoolRtId}'", tenantId, plugRtId, poolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            var rtPlug = await _plugRepository.GetPlugAsync(tenantId, plugRtId);
            await _poolHubCallbacks.DeployPlugAsync(tenantId, CreatePoolPlugDto(poolRtId,
                poolDescription.PoolName,
                rtPlug));

            poolTenant.AddPlug(new Plug(plugRtId, poolRtId));

            Logger.Info("[{TenantId}] Plug '{PlugRtId}' deployed", tenantId, rtPlug.RtId);
            return;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
    }

    /// <inheritdoc />
    public async Task UndeployPlugAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] Undeploying Plug '{PlugRtId}' from pool '{PoolRtId}'", tenantId, plugRtId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PlugsById.TryGetValue(plugRtId, out var plugDescription))
        {
            if (poolTenant.PoolsById.TryGetValue(plugDescription.PoolRtId, out var poolDescription))
            {
                var rtPlug = await _plugRepository.GetPlugAsync(tenantId, plugRtId);
                await _poolHubCallbacks.UndeployPlugAsync(tenantId, CreatePoolPlugDto(poolDescription.PoolRtId,
                    poolDescription.PoolName, rtPlug));

                poolTenant.RemovePlug(rtPlug.RtId.ToOctoObjectId());

                Logger.Info("[{TenantId}] Plug '{PlugRtId}' undeployed", tenantId, rtPlug.RtId);
                return;
            }

            throw PoolServiceException.PoolNotFound(tenantId, plugDescription.PoolRtId);
        }

        throw PoolServiceException.PlugNotFound(tenantId, plugRtId);
    }

    /// <inheritdoc />
    public async Task SetPoolOfflineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' offline", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _plugRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, PoolStates.Offline);
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
                await SetPoolOfflineAsync(tenantId, poolDescription.PoolRtId);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetPoolOnlineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' online", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _plugRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, PoolStates.Online);
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
            
            await SetPoolOnlineAsync(tenantId, poolDescription.PoolRtId);
        }
    }

    public Task OnHandlePoolUpdateAsync(string tenantId, UpdateInfo<RtCommunicationPool> info)
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

    private PoolPlugDto CreatePoolPlugDto(OctoObjectId poolRtId, string poolName, RtPlug rtPlug)
    {
        return new PoolPlugDto
        {
            PoolRtId = poolRtId,
            PoolName = poolName,
            PlugRtId = rtPlug.RtId.ToOctoObjectId(),
            ImageName = rtPlug.ImageName ?? throw PoolServiceException.ImageNameNotSet(),
            Version = rtPlug.ImageVersion ?? throw PoolServiceException.ImageVersionNotSet(),
        };
    }
}