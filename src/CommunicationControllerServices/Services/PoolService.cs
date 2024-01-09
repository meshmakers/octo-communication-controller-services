using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using NLog;
using Plug = Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools.Plug;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PoolService : IPoolServiceUpdates
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPoolCache _poolCache;
    private readonly IPoolHubCallbacks _poolHubCallbacks;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="communicationRepository">Plug repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="poolHubCallbacks">Callbacks to inform client of configuration changes</param>
    public PoolService(ICommunicationRepository communicationRepository, IPoolCache poolCache, IPoolHubCallbacks poolHubCallbacks)
    {
        _communicationRepository = communicationRepository;
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

            poolDescription.UpdateConnectionId(tenantId, connectionId);
        }
        else
        {
            var poolList = await _communicationRepository.GetPoolByNameAsync(tenantId, poolName);
            var rtPool = poolList.FirstOrDefault();
            if (rtPool == null)
            {
                Logger.Info("[{TenantId}] Creating pool '{PoolName}'",
                    tenantId, poolName);
                await _communicationRepository.CreatePoolAsync(tenantId, poolName);

                poolList = await _communicationRepository.GetPoolByNameAsync(tenantId, poolName);
                rtPool = poolList.FirstOrDefault();

                if (rtPool == null)
                {
                    throw PoolServiceException.CannotCreatePool(tenantId, poolName);
                }
            }

            poolDescription = poolTenant.AddPool(poolName, rtPool.RtId, connectionId);
        }

        // Update status in asset repository
        await _communicationRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, RtPoolStateEnum.Deployed);

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

            await _communicationRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, RtPoolStateEnum.Pending);

            Logger.Info("[{TenantId}] Operator for pool '{PoolName}' unregistered",
                tenantId, poolName);
            return poolDescription.PoolRtId;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolName);
    }

    /// <inheritdoc />
    public async Task<PoolConfigurationDto> GetCurrentAdapterAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Getting current adapters for pool '{PoolRtId}'", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            poolTenant.RemovePlugs(poolRtId);

            var rtPlugs = await _communicationRepository.GetPlugsAsync(tenantId, poolRtId);
            Logger.Info("[{TenantId}] '{PlugCount}' adapters found for Pool '{PoolRtId}'", tenantId, rtPlugs.Count, poolRtId);
            foreach (var rtPlug in rtPlugs)
            {
                poolTenant.AddPlug(new Plug(rtPlug.RtId, poolRtId,
                    CreatePoolAdapterDto(poolRtId, poolDescription.PoolName, rtPlug)));
            }

            var result = new PoolConfigurationDto(
                poolTenant.PlugsById.Values.Where(p => p.PoolRtId == poolRtId).Select(p => p.AdapterDto)
            );

            Logger.Info("[{TenantId}] Current adapters for Pool '{PoolRtId}' retrieved (Adapter count: {AdapterCount})",
                tenantId, poolRtId, result.CommunicationAdapterList.Count());
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

        // First, undeploy all communication adapters
        foreach (var keyValuePair in poolTenant.PlugsById)
        {
            await UndeployAdapterAsync(tenantId, keyValuePair.Value.PoolRtId, keyValuePair.Value.PlugRtId);
        }

        poolTenant.Clear();

        // Second, check if tenant exists in asset repository and reload plugs
        try
        {
            Logger.Info("[{TenantId}] Checking tenant and reloading plugs", tenantId);

            if (await _communicationRepository.IsTenantExistingAsync(tenantId))
            {
                // First, register pools
                foreach (var pool in poolTenant.PoolsByName.Values.ToArray())
                {
                    poolTenant.RemovePool(pool.PoolRtId);
                    var poolRtId = await RegisterPoolOperatorAsync(tenantId, pool.PoolName, pool.ConnectionId);

                    // Second, register communicationAdapter
                    var poolConfigurationDto = await GetCurrentAdapterAsync(tenantId, poolRtId);
                    foreach (var adapterDto in poolConfigurationDto.CommunicationAdapterList)
                    {
                        await DeployAdapterAsync(tenantId, poolRtId, adapterDto.AdapterRtId);
                    }
                }
            }
            else
            {
                // It seems that the tenant has been deleted.
                // TODO: What happens with pools of a tenant that has been deleted? Maybe a zombie state? Disconnect them in operator?
                _poolCache.RemoveTenant(tenantId);
            }
        }
        catch (Exception e)
        {
            throw PoolServiceException.TenantReloadFailed(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task DeployAdapterAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] Deploying Plug '{PlugRtId}' to pool '{PoolRtId}'", tenantId, plugRtId, poolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            var rtPlug = await _communicationRepository.GetPlugAsync(tenantId, plugRtId);
            var adapterDto = CreatePoolAdapterDto(poolRtId,
                poolDescription.PoolName,
                rtPlug);
            await _poolHubCallbacks.DeployCommunicationAdapterAsync(tenantId, adapterDto);

            poolTenant.AddPlug(new Plug(plugRtId, poolRtId, adapterDto));

            Logger.Info("[{TenantId}] Plug '{PlugRtId}' deployed", tenantId, rtPlug.RtId);
            return;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
    }

    /// <inheritdoc />
    public async Task UndeployAdapterAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] Undeploying Plug '{PlugRtId}' from pool '{PoolRtId}'", tenantId, plugRtId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PlugsById.TryGetValue(plugRtId, out var plugDescription))
        {
            await _poolHubCallbacks.UndeployCommunicationAdapterAsync(tenantId, plugDescription.AdapterDto);

            poolTenant.RemovePlug(plugRtId);

            Logger.Info("[{TenantId}] Plug '{PlugRtId}' undeployed", tenantId, plugRtId);
            return;
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
            await _communicationRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, RtPoolStateEnum.Offline);
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
            await _communicationRepository.SetPoolStateAsync(tenantId, poolDescription.PoolRtId, RtPoolStateEnum.Online);
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
            poolDescription.UpdateConnectionId(tenantId, connectionId);

            await SetPoolOnlineAsync(tenantId, poolDescription.PoolRtId);
        }
    }
    
    public Task OnHandlePoolUpdateAsync(string tenantId, IUpdateInfo<RtCommunicationPool> info)
    {
        // TODO: Implement updates of pool entity.
        return Task.CompletedTask;
    }

    public async Task OnHandlePlugUpdateAsync(string tenantId, IUpdateInfo<RtPlug> info)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant) || poolTenant == null)
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        switch (info.UpdateType)
        {
            case UpdateTypes.Update:
            case UpdateTypes.Replace:
                if (info.Document != null && poolTenant.PlugsById.TryGetValue(info.Document.RtId, out var plug))
                {
                    if (info.UpdateFields.Contains("attributes." + nameof(RtPlug.ImageName).ToCamelCase()) ||
                        info.UpdateFields.Contains("attributes." + nameof(RtPlug.ImageVersion).ToCamelCase()))
                    {
                        await UndeployAdapterAsync(tenantId, plug.PoolRtId, plug.PlugRtId);
                        await DeployAdapterAsync(tenantId, plug.PoolRtId, plug.PlugRtId);
                    }
                }

                break;
            default:
                // By default we do nothing
                break;
        }
    }

    private PoolCommunicationAdapterDto CreatePoolAdapterDto(OctoObjectId poolRtId, string poolName, RtPlug rtPlug)
    {
        return new PoolCommunicationAdapterDto
        {
            PoolRtId = poolRtId,
            PoolName = poolName,
            AdapterRtId = rtPlug.RtId,
            AdapterCkTypeId = rtPlug.CkTypeId,
            ImageName = rtPlug.ImageName ?? throw PoolServiceException.ImageNameNotSet(),
            Version = rtPlug.ImageVersion ?? throw PoolServiceException.ImageVersionNotSet(),
        };
    }


}