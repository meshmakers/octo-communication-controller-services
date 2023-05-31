using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Backend.PlugControllerServices.Repository;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

/// <summary>
/// Manages plug pools for all tenants
/// </summary>
public class PlugPoolService : IPlugPoolService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IPlugRepository _plugRepository;
    private readonly Dictionary<string, TenantDescription> _tenantDescriptions = new();

    private Func<string, PlugPoolPlugDto, Task>? _addPlug;
    private Func<string, PlugPoolPlugDto, Task>? _removePlug;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="plugRepository">Plug repository</param>
    public PlugPoolService(IPlugRepository plugRepository)
    {
        _plugRepository = plugRepository;
    }

    /// <inheritdoc />
    public void RegisterHub(Func<string, PlugPoolPlugDto, Task> addPlug, Func<string, PlugPoolPlugDto, Task> removePlug)
    {
        _addPlug = addPlug;
        _removePlug = removePlug;
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> RegisterPlugPoolOperatorAsync(string tenantId, string plugPoolName)
    {
        Logger.Info("[{TenantId}] Registering operator for Plug Pool '{PlugPoolName}'",
            tenantId, plugPoolName);

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
                throw PlugPoolServiceException.CannotCreatePlugPool(tenantId, plugPoolName);
            }
        }

        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            tenantDescription = new TenantDescription(tenantId);
            _tenantDescriptions.Add(tenantId, tenantDescription);
        }

        if (tenantDescription.PoolsById.TryGetValue(rtPlugPool.RtId.ToOctoObjectId(), out var poolDescription))
        {
            Logger.Info("[{TenantId}] Plug Pool '{PlugPoolName}' already registered",
                tenantId, plugPoolName);
            return poolDescription.PlugPoolRtId;
        }

        tenantDescription.AddPool(new PoolDescription(rtPlugPool.RtId.ToOctoObjectId(), plugPoolName));

        // Update status in asset repository
        await _plugRepository.SetPlugPoolStateAsync(tenantId, rtPlugPool.RtId.ToOctoObjectId(), PlugPoolStates.Deployed);

        Logger.Info("[{TenantId}] Operator for Plug Pool '{PlugPoolName}' registered",
            tenantId, plugPoolName);
        return rtPlugPool.RtId.ToOctoObjectId();
    }

    /// <inheritdoc />
    public async Task<OctoObjectId> UnregisterPlugPoolOperatorAsync(string tenantId, string plugPoolName)
    {
        Logger.Info("[{TenantId}] Unregistering operator for Plug Pool '{PlugPoolName}'",
            tenantId, plugPoolName);
        
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsByName.TryGetValue(plugPoolName, out var poolDescription))
        {
            tenantDescription.RemovePool(plugPoolName);
            
            await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Pending);

            Logger.Info("[{TenantId}] Operator for Plug Pool '{PlugPoolName}' unregistered",
                tenantId, plugPoolName);
            return poolDescription.PlugPoolRtId;
        }
        throw PlugPoolServiceException.PlugPoolNotFound(tenantId, plugPoolName);
    }

    /// <inheritdoc />
    public async Task<PlugPoolConfigurationDto> GetCurrentPlugsAsync(string tenantId, OctoObjectId plugPoolRtId)
    {
        Logger.Info("[{TenantId}] Getting current plugs for Plug Pool '{PlugPoolRtId}'", tenantId, plugPoolRtId);

        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            var result = new PlugPoolConfigurationDto
            {
                Plugs = (await _plugRepository.GetPlugsAsync(tenantId, plugPoolRtId))
                    .Select(rtPlug => CreatePlugPoolPlugDto(plugPoolRtId, poolDescription.PoolName, rtPlug))
            };

            Logger.Info("[{TenantId}] Current plugs for Plug Pool '{PlugPoolRtId}' retrieved", tenantId, plugPoolRtId);
            return result;
        }

        throw PlugPoolServiceException.PlugPoolNotFound(tenantId, plugPoolRtId);
    }

    public async Task ReloadTenant(string tenantId)
    {
        Logger.Info("[{TenantId}] Reloading tenant", tenantId);
        
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }
        
        //tenantDescription.
        //var plugPoolList = await _plugRepository.GetPlugPoolsAsync(tenantId);
        
        //    throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async Task DeployPlugAsync(string tenantId, RtPlug rtPlug)
    {
        Logger.Info("[{TenantId}] Deploying Plug '{PlugRtId}'", tenantId, rtPlug.RtId);
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }

        var rtPlugPool = await _plugRepository.GetPlugPoolOfPlugAsync(tenantId, rtPlug.RtId.ToOctoObjectId());
        if (tenantDescription.PoolsById.ContainsKey(rtPlugPool.RtId.ToOctoObjectId()))
        {
            if (_addPlug != null)
            {
                await _addPlug.Invoke(tenantId, CreatePlugPoolPlugDto(rtPlugPool.RtId.ToOctoObjectId(), 
                    rtPlugPool.Name ?? throw PlugPoolServiceException.PlugPoolNameNotSet(),
                    rtPlug));
            }

            tenantDescription.AddPlug(new PlugDescription(rtPlugPool.RtId.ToOctoObjectId(), rtPlug.RtId.ToOctoObjectId()));

            Logger.Info("[{TenantId}] Plug '{PlugRtId}' deployed", tenantId, rtPlug.RtId);
            return;
        }

        throw PlugPoolServiceException.PlugPoolNotFound(tenantId, rtPlugPool.RtId.ToOctoObjectId());
    }

    /// <inheritdoc />
    public async Task UpdateDeploymentPlugAsync(string tenantId, RtPlug rtPlug)
    {
        Logger.Info("[{TenantId}] Updating Plug '{PlugRtId}'", tenantId, rtPlug.RtId);
        await UndeployPlugAsync(tenantId, rtPlug);
        await DeployPlugAsync(tenantId, rtPlug);

        Logger.Info("[{TenantId}] Plug '{PlugRtId}' updated", tenantId, rtPlug.RtId);
    }

    /// <inheritdoc />
    public async Task UndeployPlugAsync(string tenantId, RtPlug rtPlug)
    {
        Logger.Info("[{TenantId}] Undeploying Plug '{PlugRtId}'", tenantId, rtPlug.RtId);

        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PlugsById.TryGetValue(rtPlug.RtId.ToOctoObjectId(), out var plugDescription))
        {
            if (tenantDescription.PoolsById.TryGetValue(plugDescription.PoolRtId, out var poolDescription))
            {
                if (_removePlug != null)
                {
                    await _removePlug.Invoke(tenantId, CreatePlugPoolPlugDto(poolDescription.PlugPoolRtId,
                        poolDescription.PoolName, rtPlug));
                }

                tenantDescription.RemovePlug(rtPlug.RtId.ToOctoObjectId());

                Logger.Info("[{TenantId}] Plug '{PlugRtId}' undeployed", tenantId, rtPlug.RtId);
                return;
            }

            throw PlugPoolServiceException.PlugPoolNotFound(tenantId, plugDescription.PoolRtId);
        }

        throw PlugPoolServiceException.PlugNotFound(tenantId, rtPlug.RtId);
    }

    /// <inheritdoc />
    public async Task SetPoolOfflineAsync(string tenantId, OctoObjectId plugPoolRtId)
    {
        Logger.Info("[{TenantId}] Setting Plug pool '{PlugPoolRtId}' offline", tenantId, plugPoolRtId);

        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Offline);
        }
    }
    
    /// <inheritdoc />
    public async Task SetPoolOnlineAsync(string tenantId, OctoObjectId plugPoolRtId)
    {
        Logger.Info("[{TenantId}] Setting Plug pool '{PlugPoolRtId}' online", tenantId, plugPoolRtId);

        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            throw PlugPoolServiceException.TenantNotFound(tenantId);
        }

        if (tenantDescription.PoolsById.TryGetValue(plugPoolRtId, out var poolDescription))
        {
            await _plugRepository.SetPlugPoolStateAsync(tenantId, poolDescription.PlugPoolRtId, PlugPoolStates.Online);
        }
    }

    private PlugPoolPlugDto CreatePlugPoolPlugDto(OctoObjectId plugPoolRtId, string plugPoolName, RtPlug rtPlug)
    {
        return new PlugPoolPlugDto
        {
            PlugPoolRtId = plugPoolRtId,
            PlugPoolName = plugPoolName,
            PlugRtId = rtPlug.RtId.ToOctoObjectId(),
            ImageName = rtPlug.ImageName ?? throw PlugPoolServiceException.ImageNameNotSet(),
            Version = rtPlug.ImageVersion ?? throw PlugPoolServiceException.ImageVersionNotSet(),
        };
    }
}