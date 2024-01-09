using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
///    Background service for communication management
/// </summary>
internal class ControllerBackgroundService : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IPoolServiceUpdates _poolService;
    private readonly IPlugServiceUpdates _plugService;
    private readonly ISystemContext _systemContext;
    private readonly ConcurrentDictionary<string, IDisposable> _updateStreams = new();

    public ControllerBackgroundService(IPoolServiceUpdates poolService, IPlugServiceUpdates plugService,  ISystemContext systemContext)
    {
        _poolService = poolService;
        _plugService = plugService;
        _systemContext = systemContext;
    }


    /// <summary>
    ///    Starts the service
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("Starting ControllerBackgroundService");

        Logger.Info("Starting tentants");
        await StartTenants();

        Logger.Info("BackgroundService started");
        stoppingToken.WaitHandle.WaitOne();

        Logger.Info("BackgroundService stopping");

        Logger.Info("Stopping tenants and their update streams");
        foreach (var updateStream in _updateStreams.Values)
        {
            updateStream.Dispose();
        }

        Logger.Info("ControllerBackgroundService stopped");
    }

    private async Task StartTenants()
    {
        var session = await _systemContext.GetSystemSessionAsync();
        session.StartTransaction();

        var pagedResult = await _systemContext.GetChildTenantsAsync(session);
        foreach (var tenant in pagedResult.Items)
        {
            await StartTenantAsync(tenant.TenantId);
        }

        await session.CommitTransactionAsync();
    }

    private async Task StartTenantAsync(string tenantId)
    {
        Logger.Info("Subscribing to tenant '{TenantId}' for plug updates", tenantId);

        var tenantContext = await _systemContext.GetChildTenantContextAsync(tenantId);
        var tenantRepository = _systemContext.GetTenantRepository();

        var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Subscribe to association updates Plug->PlugGroup
        var plugPlugGroupSubscription = tenantRepository.SubscribeToRtAssociations<RtPlug, RtPlugGroup>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Statics.RoleIdParentChild });
        plugPlugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugPlugGroupSubscription, (_, _) => plugPlugGroupSubscription);

        // Subscribe to association updates PlugGroup->PlugMapping
        var plugGroupPlugMappingSubscription = tenantRepository.SubscribeToRtAssociations<RtPlugGroup, RtPlugMapping>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Statics.RoleIdParentChild });
        plugGroupPlugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugGroupPlugMappingSubscription, (_, _) => plugGroupPlugMappingSubscription);

        // Subscribe to association updates CommunicationPool->Plug
        var communicationPoolPlugSubscription = tenantRepository.SubscribeToRtAssociations<RtPlug, RtCommunicationPool>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Statics.RoleIdParentChild });
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

    private async Task HandlePlugMappingEntityUpdates(string tenantId, IUpdateInfo<RtPlugMapping> info)
    {
        try
        {
            await _plugService.OnHandlePlugMappingUpdateAsync(tenantId, info);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Error handling entity plug mapping update");
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
            Logger.Error(e, "Error handling entity plug group update");
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
            Logger.Error(e, "Error handling entity plug update");
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
            Logger.Error(e, "Error handling entity pool update");
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
                        Logger.Info("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId,
                            info.Document.OriginRtId);
                    }
                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        Logger.Info("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
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
            Logger.Error(e, "Error handling association plug configuration update");
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
                        Logger.Info("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId,
                            info.Document.OriginRtId);
                    }

                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        Logger.Info("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
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
            Logger.Error(e, "Error handling association pool to plug update");
            // no further action to prevent to destroy the event stream
        }
    }


}