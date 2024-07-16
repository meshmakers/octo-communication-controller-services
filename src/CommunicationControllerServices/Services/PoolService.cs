using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PoolService : IPoolService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPoolCache _poolCache;
    private readonly IPoolHubCallbacks _poolHubCallbacks;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="communicationRepository">Communication repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="poolHubCallbacks">Callbacks to inform client of configuration changes</param>
    public PoolService(ICommunicationRepository communicationRepository, IPoolCache poolCache,
        IPoolHubCallbacks poolHubCallbacks)
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
        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolDescription.PoolRtId,
            RtDeploymentStateEnum.Deployed);

        Logger.Info("[{TenantId}] Operator for pool '{PoolName}' registered",
            tenantId, poolName);
        return poolDescription.PoolRtId;
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> UnregisterPoolOperatorAsync(string tenantId, string poolName)
    {
        Logger.Info("[{TenantId}] Unregistering operator for pool '{PoolName}'",
            tenantId, poolName);

        if (!_poolCache.TryGetTenant(tenantId, out var tenantDescription))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsByName.TryGetValue(poolName, out var poolDescription))
        {
            tenantDescription.RemovePool(poolDescription.PoolRtId);

            await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolDescription.PoolRtId,
                RtDeploymentStateEnum.Pending);

            Logger.Info("[{TenantId}] Operator for pool '{PoolName}' unregistered",
                tenantId, poolName);
            return poolDescription.PoolRtId;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolName);
    }

    /// <inheritdoc />
    public async Task<PoolConfigurationDto> GetPoolConfigurationAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Getting current adapters for pool '{PoolRtId}'", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            poolTenant.RemoveAdapters(poolRtId);

            var rtAdapters = await _communicationRepository.GetAdaptersAsync(tenantId, poolRtId);
            Logger.Info("[{TenantId}] '{AdapterCount}' adapters found for Pool '{PoolRtId}'", tenantId,
                rtAdapters.Count, poolRtId);
            foreach (var rtAdapter in rtAdapters)
            {
                poolTenant.AddAdapter(new Adapter(rtAdapter.ToRtEntityId(), poolRtId,
                    CreatePoolAdapterDto(poolRtId, poolDescription.PoolName, rtAdapter)));
            }

            var result = new PoolConfigurationDto(
                poolTenant.AdaptersById.Values.Where(p => p.PoolRtId == poolRtId).Select(p => p.AdapterDto)
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

        try
        {
            if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
            {
                var pools = await _communicationRepository.GetPoolsAsync(tenantId);
                foreach (var pool in pools)
                {
                    if (poolTenant.PoolsById.TryGetValue(pool.RtId, out var poolCache))
                    {
                        if (!string.IsNullOrWhiteSpace(poolCache.ConnectionId))
                        {
                            await SetPoolOfflineAsync(tenantId, pool.RtId);
                        }
                        else
                        {
                            await SetPoolOnlineAsync(tenantId, pool.RtId);

                            var poolConfigurationDto = await GetPoolConfigurationAsync(tenantId, pool.RtId);

                            // Check which adapters need to be deployed or undeployed.
                            var adaptersToUndeploy = poolTenant.AdaptersById.Values.Where(
                                x => x.PoolRtId == pool.RtId
                                     && poolConfigurationDto.CommunicationAdapterList
                                         .All(y => y.PoolRtId != pool.RtId));
                            var adaptersToDeploy =
                                poolConfigurationDto.CommunicationAdapterList
                                    .Where(x => poolTenant.AdaptersById.Values
                                        .All(y => y.AdapterRtEntityId != x.AdapterRtEntityId));

                            // Undeploy adapters that are not listed any more
                            foreach (var adapter in adaptersToUndeploy)
                            {
                                await UndeployAdapterAsync(tenantId, adapter.PoolRtId,
                                    adapter.AdapterRtEntityId);
                            }

                            // Deploy adapters that are listed newly
                            foreach (var adapterDto in adaptersToDeploy)
                            {
                                await DeployAdapterAsync(tenantId, adapterDto.PoolRtId, adapterDto.AdapterRtEntityId);
                            }
                        }

                        continue;
                    }

                    await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, pool.RtId,
                        RtCommunicationStateEnum.Unregistered);
                }
            }
            else
            {
                _poolCache.AddOrUpdateTenant(tenantId);
            }
        }
        catch (Exception e)
        {
            throw PoolServiceException.TenantReloadFailed(tenantId, e);
        }
    }

    /// <summary>
    /// Unloads an entire tenant if a tenant gets deleted
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    public async Task UnloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Unloading tenant", tenantId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            Logger.Info("[{TenantId}] Tenant not loaded, skipping further unload checks", tenantId);
            return;
        }

        // First, undeploy all communication adapters
        foreach (var keyValuePair in poolTenant.AdaptersById)
        {
            await UndeployAdapterAsync(tenantId, keyValuePair.Value.PoolRtId, keyValuePair.Value.AdapterRtEntityId);
        }

        poolTenant.Clear();
        _poolCache.RemoveTenant(tenantId);
    }

    /// <inheritdoc />
    public async Task DeployAdapterAsync(string tenantId, OctoObjectId poolRtId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] Deploying Adapter '{AdapterRtEntityId}' to pool '{PoolRtId}'", tenantId,
            adapterRtEntityId,
            poolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            var rtAdapter = await _communicationRepository.GetAdapterAsync(tenantId, adapterRtEntityId);
            var adapterDto = CreatePoolAdapterDto(poolRtId,
                poolDescription.PoolName,
                rtAdapter);
            await _poolHubCallbacks.DeployCommunicationAdapterAsync(tenantId, adapterDto);

            poolTenant.AddAdapter(new Adapter(adapterRtEntityId, poolRtId, adapterDto));

            Logger.Info("[{TenantId}] Adapter '{AdapterRtEntityId}' deployed", tenantId, adapterRtEntityId);
            return;
        }

        throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
    }

    /// <inheritdoc />
    public async Task UndeployAdapterAsync(string tenantId, OctoObjectId poolRtId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] Undeploying Adapter '{AdapterRtEntityId}' from pool '{PoolRtId}'", tenantId,
            adapterRtEntityId,
            poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.AdaptersById.TryGetValue(adapterRtEntityId, out var adapterDescription))
        {
            await _poolHubCallbacks.UndeployCommunicationAdapterAsync(tenantId, adapterDescription.AdapterDto);

            poolTenant.RemoveAdapter(adapterRtEntityId);

            Logger.Info("[{TenantId}] Adapter '{AdapterRtEntityId}' undeployed", tenantId, adapterRtEntityId);
            return;
        }

        throw PoolServiceException.AdapterNotFound(tenantId, adapterRtEntityId);
    }

    /// <inheritdoc />
    public async Task SetPoolOfflineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' offline", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                RtCommunicationStateEnum.Offline);
        }
    }

    /// <inheritdoc />
    public async Task SetPoolOfflineAsync(string tenantId, string poolName)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
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

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                RtCommunicationStateEnum.Online);
        }
    }

    /// <inheritdoc />
    public async Task SetPoolOnlineAsync(string tenantId, string poolName, string connectionId)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
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

    public Task OnHandlePoolUpdateAsync(string tenantId, IUpdateInfo<RtPool> info)
    {
        // TODO: Implement updates of pool entity.
        return Task.CompletedTask;
    }

    public async Task OnHandleAdapterUpdateAsync(string tenantId, IUpdateInfo<RtAdapter> info)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFound(tenantId);
        }

        switch (info.UpdateType)
        {
            case UpdateTypes.Update:
            case UpdateTypes.Replace:
                if (info.Document != null &&
                    poolTenant.AdaptersById.TryGetValue(info.Document.ToRtEntityId(), out var adapter))
                {
                    if (info.UpdateFields.Contains("attributes." +
                                                   nameof(RtAdapter.ImageName).ToCamelCase()) ||
                        info.UpdateFields.Contains("attributes." +
                                                   nameof(RtAdapter.ImageVersion).ToCamelCase()))
                    {
                        await UndeployAdapterAsync(tenantId, adapter.PoolRtId, adapter.AdapterRtEntityId);
                        await DeployAdapterAsync(tenantId, adapter.PoolRtId, adapter.AdapterRtEntityId);
                    }
                }

                break;
            default:
                // By default we do nothing
                break;
        }
    }

    private PoolCommunicationAdapterDto CreatePoolAdapterDto(OctoObjectId poolRtId, string poolName,
        RtAdapter rtAdapter)
    {
        return new PoolCommunicationAdapterDto
        {
            PoolRtId = poolRtId,
            PoolName = poolName,
            AdapterRtEntityId = rtAdapter.ToRtEntityId(),
            ImageName = rtAdapter.ImageName ?? throw PoolServiceException.ImageNameNotSet(),
            Version = rtAdapter.ImageVersion ?? throw PoolServiceException.ImageVersionNotSet(),
        };
    }
}