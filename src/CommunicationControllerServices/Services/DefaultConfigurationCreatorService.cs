using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.ConstructionKit.Models.System.ConstructionKit.Generated.System.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class DefaultConfigurationCreatorService : DefaultConfigurationCreatorServiceBase, IConfigurationService
{
    private readonly ILogger<DefaultConfigurationCreatorService> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IPoolServiceUpdates _poolService;
    private readonly IAdapterServiceUpdates _adapterService;
    private readonly ConcurrentDictionary<string, IDisposable> _updateStreams = new();

    public DefaultConfigurationCreatorService(ILogger<DefaultConfigurationCreatorService> logger, ISystemContext systemContext,
        IPoolServiceUpdates poolService, IAdapterServiceUpdates adapterService)
        : base(logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _poolService = poolService;
        _adapterService = adapterService;
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        if (!await _systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }

        // That means that the system tenant database is existing but (currently) not valid.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await _systemContext.IsCkModelExistingAsync(SystemCkIds.ModelId))
        {
            return;
        }

        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetSystemSessionAsync();
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync<DefaultConfigurationVersion>(session,
                Constants.CommunicationControllerServiceSchemaVersionKey, null);
        if (configurationVersion == null)
        {
            await session.CommitTransactionAsync();
            return;
        }

        if (configurationVersion.Version < Constants.CommunicationControllerServiceSchemaVersionValue)
        {
            await ImportCkModelAsync(tenantId);

            await tenantContext.SetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = Constants.CommunicationControllerServiceSchemaVersionValue });
        }

        await session.CommitTransactionAsync();

        await StartTenantAsync(tenantId);

        // TODO: Implement security configuration
    }

    public async Task EnableAsync(string tenantId)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetSystemSessionAsync();
        session.StartTransaction();

        await ImportCkModelAsync(tenantId);

        await tenantContext.SetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
            new DefaultConfigurationVersion { Version = Constants.CommunicationControllerServiceSchemaVersionValue });
        
        await session.CommitTransactionAsync();
        
        await StartTenantAsync(tenantId);
    }

    public async Task DisableAsync(string tenantId)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetSystemSessionAsync();
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (configurationVersion == null)
        {
            return;
        }

        await tenantContext.DeleteConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey);

        await session.CommitTransactionAsync();

        await StopTenantAsync(tenantId);
    }


    private async Task ImportCkModelAsync(string tenantId)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);

        if (!await tenantContext.IsCkModelExistingAsync(SystemCommunicationCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemCommunicationCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId, operationResult.GetMessages());
            }
        }
    }

    private async Task StartTenantAsync(string tenantId)
    {
        _logger.LogInformation("Subscribing to tenant '{TenantId}' for adapter updates", tenantId);

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        // var session = await tenantRepository.GetSessionAsync();
        // session.StartTransaction();
        //
        // // Subscribe to association updates Plug->PlugGroup
        // var plugPlugGroupSubscription = tenantRepository.SubscribeToRtAssociations<RtPlug, RtPlugGroup>(
        //     new UpdateAssociationStreamFilter
        //         { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Constants.RoleIdParentChild });
        // plugPlugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, plugPlugGroupSubscription, (_, _) => plugPlugGroupSubscription);
        //
        // // Subscribe to association updates PlugGroup->PlugMapping
        // var plugGroupPlugMappingSubscription = tenantRepository.SubscribeToRtAssociations<RtPlugGroup, RtPlugMapping>(
        //     new UpdateAssociationStreamFilter
        //         { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Constants.RoleIdParentChild });
        // plugGroupPlugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, plugGroupPlugMappingSubscription, (_, _) => plugGroupPlugMappingSubscription);
        //
        // // Subscribe to association updates CommunicationPool->Plug
        // var communicationPoolPlugSubscription = tenantRepository.SubscribeToRtAssociations<RtPlug, RtCommunicationPool>(
        //     new UpdateAssociationStreamFilter
        //         { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Constants.RoleIdParentChild });
        // communicationPoolPlugSubscription.GetUpdates().Subscribe(info => HandlePoolPlugUpdateAssociations(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, communicationPoolPlugSubscription, (_, _) => communicationPoolPlugSubscription);
        //
        // // Subscribe to updates of CommunicationPool, Plug, PlugGroup and PlugMapping
        // var poolSubscription =
        //     tenantRepository.SubscribeToRtEntities<RtCommunicationPool>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        // poolSubscription.GetUpdates().Subscribe(info => HandlePoolEntityUpdates(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, poolSubscription, (_, _) => poolSubscription);
        //
        // var plugSubscription =
        //     tenantRepository.SubscribeToRtEntities<RtPlug>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        // plugSubscription.GetUpdates().Subscribe(info => HandlePlugEntityUpdates(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, plugSubscription, (_, _) => plugSubscription);
        //
        // var plugGroupSubscription =
        //     tenantRepository.SubscribeToRtEntities<RtPlugGroup>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        // plugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugGroupEntityUpdates(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, plugGroupSubscription, (_, _) => plugGroupSubscription);
        //
        // var plugMappingSubscription =
        //     tenantRepository.SubscribeToRtEntities<RtPlugMapping>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        // plugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugMappingEntityUpdates(tenantId, info).Wait());
        // _updateStreams.AddOrUpdate(tenantId, plugMappingSubscription, (_, _) => plugMappingSubscription);

        // await session.CommitTransactionAsync();
    }

    private Task StopTenantAsync(string tenantId)
    {
        _logger.LogInformation("Unsubscribing from tenant '{TenantId}' for adapter updates", tenantId);

        if (_updateStreams.TryRemove(tenantId, out var subscription))
        {
            subscription.Dispose();
        }

        return Task.CompletedTask;
    }

    private async Task OnHandleDataPipelineUpdateAsync(string tenantId, IUpdateInfo<RtDataPipeline> info)
    {
        try
        {
            await _adapterService.OnHandleDataPipelineUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling entity data pipeline update");
            // no further action to prevent to destroy the event stream
        }
    }

    private async Task HandleAdapterEntityUpdates(string tenantId, IUpdateInfo<RtCommunicationAdapter> info)
    {
        try
        {
            await _poolService.OnHandleAdapterUpdateAsync(tenantId, info);
            //     await _plugService.OnHandlePlugUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error handling entity adapter update");
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

    private async Task HandleAdapterConfigurationUpdateAssociations(string tenantId, IUpdateInfo<RtAssociation> info)
    {
        try
        {
            switch (info.UpdateType)
            {
                case UpdateTypes.Insert:
                    if (info.Document != null)
                    {
                        _logger.LogInformation("[{TenantId}] Adapter '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId,
                            info.Document.OriginRtId);
                    }

                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        _logger.LogInformation("[{TenantId}] Adapter '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
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
            _logger.LogError(e, "Error handling association adapter configuration update");
            // no further action to prevent to destroy the event stream
        }
    }
}