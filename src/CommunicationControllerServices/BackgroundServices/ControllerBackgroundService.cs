using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
///    Background service for communication management
/// </summary>
internal class ControllerBackgroundService : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IPoolService _poolService;
    private readonly IPlugService _plugService;
    private readonly ISystemContext _systemContext;
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly IPoolCache _poolCache;
    private readonly IPlugCache _plugCache;
    private readonly ConcurrentDictionary<string, IDisposable> _updateStreams = new();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="poolService"></param>
    /// <param name="plugService"></param>
    /// <param name="systemContext"></param>
    /// <param name="distributedWithPubSubCache"></param>
    /// <param name="poolCache"></param>
    /// <param name="plugCache"></param>
    public ControllerBackgroundService(IPoolService poolService, IPlugService plugService, ISystemContext systemContext,
        IDistributedWithPubSubCache distributedWithPubSubCache, IPoolCache poolCache, IPlugCache plugCache)
    {
        _poolService = poolService;
        _plugService = plugService;
        _systemContext = systemContext;
        _distributedWithPubSubCache = distributedWithPubSubCache;
        _poolCache = poolCache;
        _plugCache = plugCache;
    }


    /// <summary>
    ///    Starts the service
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("Starting ControllerBackgroundService");

        Logger.Info("Initializing caches");
        await _poolCache.InitializeAsync();
        await _plugCache.InitializeAsync();

        Logger.Info("Starting tentants");
        await StartTenants();

        Logger.Info("Subscribing to tenant updates");
        var channel = SubscribeToTenantUpdates();

        Logger.Info("BackgroundService started");
        stoppingToken.WaitHandle.WaitOne();

        Logger.Info("BackgroundService stopping");

        Logger.Info("Unsubscribing from tenant updates");
        await channel.UnsubscribeAsync();
        channel.Dispose();

        Logger.Info("Stopping tenants and their update streams");
        foreach (var updateStream in _updateStreams.Values)
        {
            updateStream.Dispose();
        }

        Logger.Info("ControllerBackgroundService stopped");
    }

    private IChannel<string> SubscribeToTenantUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyTenantUpdate);
        channel.OnMessage(async message =>
        {
            if (!string.IsNullOrWhiteSpace(message.Message))
            {
                await ReloadTenantAsync(message.Message);
            }
        });
        return channel;
    }

    private async Task StartTenants()
    {
        var session = await _systemContext.StartSystemSessionAsync();
        session.StartTransaction();

        var pagedResult = await _systemContext.GetTenantsAsync(session);
        foreach (var tenant in pagedResult.List)
        {
            await StartTenantAsync(tenant.TenantId);
        }

        await session.CommitTransactionAsync();
    }

    private async Task StartTenantAsync(string tenantId)
    {
        Logger.Info("Subscribing to tenant '{TenantId}' for plug updates", tenantId);

        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        session.StartTransaction();

        // Subscribe to association updates Plug->PlugGroup
        var plugPlugGroupSubscription = tenantContext.Repository.SubscribeToRtAssociations<RtPlug, RtPlugGroup>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Statics.RoleIdParentChild });
        plugPlugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugPlugGroupSubscription, (_, _) => plugPlugGroupSubscription);

        // Subscribe to association updates PlugGroup->PlugMapping
        var plugGroupPlugMappingSubscription = tenantContext.Repository.SubscribeToRtAssociations<RtPlugGroup, RtPlugMapping>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Statics.RoleIdParentChild });
        plugGroupPlugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugConfigurationUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugGroupPlugMappingSubscription, (_, _) => plugGroupPlugMappingSubscription);

        // Subscribe to association updates CommunicationPool->Plug
        var communicationPoolPlugSubscription = tenantContext.Repository.SubscribeToRtAssociations<RtPlug, RtCommunicationPool>(
            new UpdateAssociationStreamFilter
                { UpdateTypes = UpdateTypes.Delete | UpdateTypes.Insert, RoleId = Statics.RoleIdParentChild });
        communicationPoolPlugSubscription.GetUpdates().Subscribe(info => HandlePoolPlugUpdateAssociations(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, communicationPoolPlugSubscription, (_, _) => communicationPoolPlugSubscription);

        // Subscribe to updates of CommunicationPool, Plug, PlugGroup and PlugMapping
        var poolSubscription =
            tenantContext.Repository.SubscribeToRtEntities<RtCommunicationPool>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        poolSubscription.GetUpdates().Subscribe(info => HandlePoolEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, poolSubscription, (_, _) => poolSubscription);

        var plugSubscription =
            tenantContext.Repository.SubscribeToRtEntities<RtPlug>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        plugSubscription.GetUpdates().Subscribe(info => HandlePlugEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugSubscription, (_, _) => plugSubscription);

        var plugGroupSubscription =
            tenantContext.Repository.SubscribeToRtEntities<RtPlugGroup>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        plugGroupSubscription.GetUpdates().Subscribe(info => HandlePlugGroupEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugGroupSubscription, (_, _) => plugGroupSubscription);

        var plugMappingSubscription =
            tenantContext.Repository.SubscribeToRtEntities<RtPlugMapping>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        plugMappingSubscription.GetUpdates().Subscribe(info => HandlePlugMappingEntityUpdates(tenantId, info).Wait());
        _updateStreams.AddOrUpdate(tenantId, plugMappingSubscription, (_, _) => plugMappingSubscription);

        await session.CommitTransactionAsync();
    }

    private async Task HandlePlugMappingEntityUpdates(string tenantId, UpdateInfo<RtPlugMapping> info)
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

    private async Task HandlePlugGroupEntityUpdates(string tenantId, UpdateInfo<RtPlugGroup> info)
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

    private async Task HandlePlugEntityUpdates(string tenantId, UpdateInfo<RtPlug> info)
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

    private async Task HandlePoolEntityUpdates(string tenantId, UpdateInfo<RtCommunicationPool> info)
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

    private async Task HandlePlugConfigurationUpdateAssociations(string tenantId, UpdateInfo<RtAssociation> info)
    {
        try
        {
            switch (info.UpdateType)
            {
                case UpdateTypes.Insert:
                    if (info.Document != null)
                    {
                        Logger.Info("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId.ToOctoObjectId(),
                            info.Document.OriginRtId.ToOctoObjectId());
                    }
                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        Logger.Info("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
                        await _poolService.UndeployAdapterAsync(tenantId, info.DocumentBeforeChange.TargetRtId.ToOctoObjectId(),
                            info.DocumentBeforeChange.OriginRtId.ToOctoObjectId());
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

    private async Task HandlePoolPlugUpdateAssociations(string tenantId, UpdateInfo<RtAssociation> info)
    {
        try
        {
            switch (info.UpdateType)
            {
                case UpdateTypes.Insert:
                    if (info.Document != null)
                    {
                        Logger.Info("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.OriginRtId);
                        await _poolService.DeployAdapterAsync(tenantId, info.Document.TargetRtId.ToOctoObjectId(),
                            info.Document.OriginRtId.ToOctoObjectId());
                    }

                    break;
                case UpdateTypes.Delete:
                    if (info.DocumentBeforeChange != null)
                    {
                        Logger.Info("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.DocumentBeforeChange.OriginRtId);
                        await _poolService.UndeployAdapterAsync(tenantId, info.DocumentBeforeChange.TargetRtId.ToOctoObjectId(),
                            info.DocumentBeforeChange.OriginRtId.ToOctoObjectId());
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

    private async Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("Reloading tenant '{TenantId}'", tenantId);
        await _poolService.ReloadTenantAsync(tenantId);
        await _plugService.ReloadTenantAsync(tenantId);
    }
}