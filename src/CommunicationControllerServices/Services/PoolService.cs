using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts;
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

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

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
    public async Task UnregisterPoolOperatorAsync(string tenantId, string poolName)
    {
        Logger.Info("[{TenantId}] Unregistering operator for pool '{PoolName}'",
            tenantId, poolName);

        if (_poolCache.TryGetTenant(tenantId, out var tenantDescription))
        {
            if (tenantDescription.PoolsByName.TryGetValue(poolName, out var poolDescription))
            {
                tenantDescription.RemovePool(poolDescription.PoolRtId);

                await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolDescription.PoolRtId,
                    RtDeploymentStateEnum.Pending);

                Logger.Info("[{TenantId}] Operator for pool '{PoolName}' unregistered",
                    tenantId, poolName);
            }
        }
    }

    /// <inheritdoc />
    public async Task<PoolConfigurationDto> GetPoolConfigurationAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Getting current adapters for pool '{PoolRtId}'", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            poolTenant.RemoveAdapters(poolRtId);

            var rtAdapters = await _communicationRepository.GetAdaptersAsync(tenantId, poolRtId);
            Logger.Info("[{TenantId}] '{AdapterCount}' adapters found for Pool '{PoolRtId}'", tenantId,
                rtAdapters.Count, poolRtId);
            foreach (var rtAdapter in rtAdapters)
            {
                if (string.IsNullOrWhiteSpace(rtAdapter.ImageName) || string.IsNullOrWhiteSpace(rtAdapter.ImageVersion))
                {
                    await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, rtAdapter.ToRtEntityId(),
                        RtDeploymentStateEnum.Error);
                    continue;
                }

                poolTenant.AddAdapter(new Adapter(rtAdapter.ToRtEntityId(), poolRtId,
                    CreatePoolAdapterDto(poolDescription.PoolName, rtAdapter)));
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
    
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] PreUpdate tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();

            if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
            {
                // Inform all pools that tenant is going to be updated
                await _poolHubCallbacks.PreUpdateTenantAsync(tenantId);
                // Remove all pools from cache, so we skip the possibility to communicate with them
                _poolCache.RemoveTenant(tenantId);

                foreach (var pool in poolTenant.PoolsByName.Values)
                {
                    await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, pool.PoolRtId,
                        RtCommunicationStateEnum.Unregistered);
                }
            }
        }
        catch (Exception e)
        {
            throw PoolServiceException.PreUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task PosUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] PosUpdate tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();
            
            _poolCache.AddOrUpdateTenant(tenantId);
            
            var pools = await _communicationRepository.GetPoolsAsync(tenantId);
            foreach (var pool in pools)
            {
                await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, pool.RtId,
                    RtCommunicationStateEnum.Unregistered);
            }
        }
        catch (Exception e)
        {
            throw PoolServiceException.PosUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeployAdaptersAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Deploying Adapters to pool '{PoolRtId}'", tenantId,
            poolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (!poolTenant.PoolsById.ContainsKey(poolRtId))
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
        }

        var poolConfigurationDto = await GetPoolConfigurationAsync(tenantId, poolRtId);

        // We have to find adapters that are deployed but not listed anymore in database
        var adaptersToUndeploy = poolTenant.AdaptersById.Values
            .Where(x => poolConfigurationDto.CommunicationAdapterList
                            .All(y => y.AdapterRtEntityId != x.AdapterRtEntityId));

        // Check which adapters need to be deployed or undeployed.
        var adaptersToDeploy =
            poolConfigurationDto.CommunicationAdapterList
                .Where(x => poolTenant.AdaptersById.Values
                    .Any(y => y.AdapterRtEntityId == x.AdapterRtEntityId));

        // Undeploy adapters that are not listed anymore
        foreach (var adapter in adaptersToUndeploy)
        {
            await UndeployAdapterAsync(tenantId, adapter.PoolRtId,
                adapter.AdapterRtEntityId);
        }

        // Deploy adapters that are listed newly
        foreach (var adapterDto in adaptersToDeploy)
        {
            await DeployAdapterAsync(tenantId, poolRtId, adapterDto.AdapterRtEntityId);
        }
    }

    public async Task UndeployAdaptersAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Undeploying Adapters to pool '{PoolRtId}'", tenantId,
            poolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (!poolTenant.PoolsById.ContainsKey(poolRtId))
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
        }

        foreach (var adapter in poolTenant.AdaptersById.Values)
        {
            await UndeployAdapterAsync(tenantId, adapter.PoolRtId,
                adapter.AdapterRtEntityId);
        }
    }

    /// <inheritdoc />
    public async Task DeployAdapterAsync(string tenantId, OctoObjectId poolRtId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] Deploying Adapter '{AdapterRtEntityId}' to pool '{PoolRtId}'", tenantId,
            adapterRtEntityId,
            poolRtId);
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            var rtAdapter = await _communicationRepository.GetAdapterAsync(tenantId, adapterRtEntityId);
            var adapterDto = CreatePoolAdapterDto(poolDescription.PoolName, rtAdapter);
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
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
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
    public async Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' offline", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                RtCommunicationStateEnum.Offline);
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOfflineAsync(string tenantId, string poolName)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
            if (poolDescription != null)
            {
                poolDescription.RemoveConnectionId(tenantId);
                await SetCommunicationStateOfflineAsync(tenantId, poolDescription.PoolRtId);
            }
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' online", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                RtCommunicationStateEnum.Online);
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOnlineAsync(string tenantId, string poolName, string connectionId)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
        if (poolDescription != null)
        {
            poolDescription.UpdateConnectionId(tenantId, connectionId);

            await SetCommunicationStateOnlineAsync(tenantId, poolDescription.PoolRtId);
        }
    }

    /// <inheritdoc />
    public async Task SetAdapterDeploymentStateAsync(string tenantId, string poolName, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (!poolTenant.PoolsByName.ContainsKey(poolName))
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolName);
        }

        await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
            deploymentState);
    }

    /// <inheritdoc />
    public async Task SetAdapterDeploymentStateAsync(string tenantId, string poolName,
        ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (!poolTenant.PoolsByName.ContainsKey(poolName))
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolName);
        }

        await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityIds,
            deploymentState);
    }

    private PoolCommunicationAdapterDto CreatePoolAdapterDto(string poolName,
        RtAdapter rtAdapter)
    {
        return new PoolCommunicationAdapterDto
        {
            PoolName = poolName,
            AdapterRtEntityId = rtAdapter.ToRtEntityId(),
            ImageName = rtAdapter.ImageName ?? throw PoolServiceException.ImageNameNotSet(),
            Version = rtAdapter.ImageVersion ?? throw PoolServiceException.ImageVersionNotSet(),
        };
    }
}