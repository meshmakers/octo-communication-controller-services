using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.BackgroundServices;

/// <summary>
///    Background service for plug pool management
/// </summary>
public class PlugControllerBackgroundService : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IPlugPoolService _plugPoolService;
    private readonly ISystemContext _systemContext;
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, IUpdateStream<RtPlug>> _updateStreams = new();

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="plugPoolService"></param>
    /// <param name="systemContext"></param>
    /// <param name="distributedWithPubSubCache"></param>
    public PlugControllerBackgroundService(IPlugPoolService plugPoolService, ISystemContext systemContext,
        IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _plugPoolService = plugPoolService;
        _systemContext = systemContext;
        _distributedWithPubSubCache = distributedWithPubSubCache;
    }


    /// <summary>
    ///    Starts the service
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("Starting PlugControllerBackgroundService");
        await StartTenants();

        Logger.Info("Subscribing to tenant updates");
        var channel = SubscribeToTenantUpdates();

        Logger.Info("PlugControllerBackgroundService started");
        stoppingToken.WaitHandle.WaitOne();
        
        Logger.Info("PlugControllerBackgroundService stopping");

        Logger.Info("Unsubscribing from tenant updates");
        await channel.UnsubscribeAsync();
        channel.Dispose();
        
        Logger.Info("Stopping tenants");
        foreach (var updateStream in _updateStreams.Values)
        {
            updateStream.Dispose();
        }
        
        Logger.Info("PlugControllerBackgroundService stopped");
    }

    private IChannel<string> SubscribeToTenantUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyTenantUpdate);
        channel.OnMessage(message =>
        {
            if (!string.IsNullOrWhiteSpace(message.Message))
            {
                ReloadTenant(message.Message);
            }

            return Task.CompletedTask;
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
        
        var subscribeToRtEntities = tenantContext.Repository.SubscribeToRtEntities<RtPlug>(new UpdateStreamFilter { UpdateTypes = UpdateTypes.All });
        subscribeToRtEntities.GetUpdates().Subscribe(info =>
        {
            switch (info.UpdateType)
            {
                case UpdateTypes.Insert:
                    Logger.Info("[{TenantId}] Plug '{RtId}' inserted", tenantId, info.Document.RtId);
                    _plugPoolService.DeployPlugAsync(tenantId, info.Document);
                    break;
                case UpdateTypes.Update:
                case UpdateTypes.Replace:
                    Logger.Info("[{TenantId}] Plug '{RtId}'  replaced", tenantId, info.Document.RtId);
                    _plugPoolService.UpdateDeploymentPlugAsync(tenantId, info.Document);
                    break;
                case UpdateTypes.Delete:
                    Logger.Info("[{TenantId}] Plug '{RtId}'  deleted", tenantId, info.Document.RtId);
                    _plugPoolService.UndeployPlugAsync(tenantId, info.Document);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        });
        _updateStreams.AddOrUpdate(tenantId, subscribeToRtEntities, (_, _) => subscribeToRtEntities);
    }
    
    private void ReloadTenant(string tenantId)
    {
        Logger.Info("Reloading tenant '{TenantId}'", tenantId);
        _plugPoolService.ReloadTenant(tenantId);
    }
}