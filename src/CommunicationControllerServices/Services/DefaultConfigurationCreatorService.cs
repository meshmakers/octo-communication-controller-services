using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class DefaultConfigurationCreatorService : IConfigurationService
{
    private readonly ILogger<DefaultConfigurationCreatorService> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IPoolServiceUpdates _poolService;
    private readonly IPlugServiceUpdates _plugService;
    private readonly ConcurrentDictionary<string, IDisposable> _updateStreams = new();

    public DefaultConfigurationCreatorService(ILogger<DefaultConfigurationCreatorService> logger, ISystemContext systemContext, 
        IPoolServiceUpdates poolService, IPlugServiceUpdates plugService)
    {
        _logger = logger;
        _systemContext = systemContext;
        _poolService = poolService;
        _plugService = plugService;
    }
    
    public async Task SetupAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        if (!await _systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }
        
        using var session = await _systemContext.GetSystemSessionAsync();
        session.StartTransaction();
        
        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await _systemContext.GetConfigurationAsync<DefaultConfigurationVersion>(session, Constants.CommunicationControllerServiceSchemaVersionKey, null);
        if (configurationVersion == null)
        {
            return;
        }
        
        if (configurationVersion.Version < Constants.CommunicationControllerServiceSchemaVersionValue)
        {
            await ImportCkModelAsync(tenantId);
            
            await _systemContext.SetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = Constants.CommunicationControllerServiceSchemaVersionValue });
        }
        
        await session.CommitTransactionAsync();

        await StartTenantAsync(tenantId);

        // TODO: Implement security configuration
    }

    public async Task TakeDownAsync(string tenantId)
    {
        using var session = await _systemContext.GetSystemSessionAsync();
        session.StartTransaction();
        
        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await _systemContext.GetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (configurationVersion == null)
        {
            return;
        }
        
        await _systemContext.SetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
            new DefaultConfigurationVersion { Version = -1 });
        
        await session.CommitTransactionAsync();
        
        await StopTenantAsync(tenantId);

    }



    private async Task ImportCkModelAsync(string tenantId)
    {
        ITenantContext tenantContext = _systemContext;
        if (tenantId != _systemContext.TenantId)
        {
            tenantContext = await _systemContext.GetChildTenantContextAsync(tenantId);
        }
        
        using var session = await tenantContext.GetSystemSessionAsync();
        session.StartTransaction();
        
        if (!await tenantContext.IsCkModelExistingAsync(session, SystemCommunicationCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(session, SystemCommunicationCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId, operationResult.GetMessages());
            }
        }
        
        await session.CommitTransactionAsync();
    }
    
    private async Task StartTenantAsync(string tenantId)
    {
        _logger.LogInformation("Subscribing to tenant '{TenantId}' for plug updates", tenantId);

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        
        // Subscribe to association updates Plug->PlugGroup
        var plugPlugGroupSubscription = tenantRepository.SubscribeToRtAssociations<RtPlug, RtPlugGroup>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Constants.RoleIdParentChild });
        plugPlugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugPlugGroupSubscription, (_, _) => plugPlugGroupSubscription);

        // Subscribe to association updates PlugGroup->PlugMapping
        var plugGroupPlugMappingSubscription = tenantRepository.SubscribeToRtAssociations<RtPlugGroup, RtPlugMapping>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Constants.RoleIdParentChild });
        plugGroupPlugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugGroupPlugMappingSubscription, (_, _) => plugGroupPlugMappingSubscription);

        // Subscribe to association updates CommunicationPool->Plug
        var communicationPoolPlugSubscription = tenantRepository.SubscribeToRtAssociations<RtPlug, RtCommunicationPool>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Constants.RoleIdParentChild });
        communicationPoolPlugSubscription.GetUpdates().Subscribe(info => HandlePoolPlugUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, communicationPoolPlugSubscription, (_, _) => communicationPoolPlugSubscription);

        // Subscribe to updates of CommunicationPool, Plug, PlugGroup and PlugMapping
        var poolSubscription =
            tenantRepository.SubscribeToRtEntities<RtCommunicationPool>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        poolSubscription.GetUpdates().Subscribe(info => HandlePoolEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, poolSubscription, (_, _) => poolSubscription);

        var plugSubscription =
            tenantRepository.SubscribeToRtEntities<RtPlug>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        plugSubscription.GetUpdates().Subscribe(info => HandlePlugEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugSubscription, (_, _) => plugSubscription);

        var plugGroupSubscription =
            tenantRepository.SubscribeToRtEntities<RtPlugGroup>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        plugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugGroupEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugGroupSubscription, (_, _) => plugGroupSubscription);

        var plugMappingSubscription =
            tenantRepository.SubscribeToRtEntities<RtPlugMapping>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        plugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugMappingEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugMappingSubscription, (_, _) => plugMappingSubscription);

        await session.CommitTransactionAsync();
    }
    
    private Task StopTenantAsync(string tenantId)
    {
        _logger.LogInformation("Unsubscribing from tenant '{TenantId}' for plug updates", tenantId);

        if (_updateStreams.TryRemove(tenantId, out var subscription))
        {
            subscription.Dispose();
        }

        return Task.CompletedTask;
    }

    private async Task HandlePlugMappingEntityUpdates(string tenantId, IUpdateInfo<RtPlugMapping> info)
    {
        try
        {
            await _plugService.OnHandlePlugMappingUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling entity plug mapping update");
            // no further action to prevent to destroy the event stream
        }
    }

    private async Task HandlePlugGroupEntityUpdates(string tenantId, IUpdateInfo<RtPlugGroup> info)
    {
        try
        {
            await _plugService.OnHandlePlugGroupUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling entity plug group update");
            // no further action to prevent to destroy the event stream
        }
    }

    private async Task HandlePlugEntityUpdates(string tenantId, IUpdateInfo<RtPlug> info)
    {
        try
        {
            await _poolService.OnHandlePlugUpdateAsync(tenantId, info);
       //     await _plugService.OnHandlePlugUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling entity plug update");
            // no further action to prevent to destroy the event stream
        }
    }

    private async Task HandlePoolEntityUpdates(string tenantId, IUpdateInfo<RtCommunicationPool> info)
    {
        try
        {
            await _poolService.OnHandlePoolUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling entity pool update");
            // no further action to prevent to destroy the event stream
        }
    }

    private async Task HandlePlugConfigurationUpdateAssociations(string tenantId, IUpdateInfo<RtAssociation> info)
    {
        try
        {
            switch (info.UpdateType)
            {
                case UpdateTypes.Insert:
                    if (info.Document != null)
                    {
                        _logger.LogInformation("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId,
                            info.Document.OriginRtId);
                    }
                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        _logger.LogInformation("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
                        await _poolService.UndeployAdapterAsync(tenantId, info.DocumentBeforeChange.TargetRtId,
                            info.DocumentBeforeChange.OriginRtId);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling association plug configuration update");
            // no further action to prevent to destroy the event stream
        }
    }

    private async Task HandlePoolPlugUpdateAssociations(string tenantId, IUpdateInfo<RtAssociation> info)
    {
        try
        {
            switch (info.UpdateType)
            {
                case UpdateTypes.Insert:
                    if (info.Document != null)
                    {
                        _logger.LogInformation("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId,
                            info.Document.OriginRtId);
                    }

                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        _logger.LogInformation("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
                        await _poolService.UndeployAdapterAsync(tenantId, info.DocumentBeforeChange.TargetRtId,
                            info.DocumentBeforeChange.OriginRtId);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling association pool to plug update");
            // no further action to prevent to destroy the event stream
        }
    }
}