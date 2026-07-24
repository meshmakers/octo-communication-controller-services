using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
///    Handles tenant lifecycle events for communication management.
///    Tenant lifecycle no longer triggers operator-side actions — those are
///    driven by pool deploy / undeploy events instead. This consumer keeps
///    pre/post-update bookkeeping for adapters and pools so that disabling a
///    tenant for communication tears down its pool/adapter caches cleanly.
/// </summary>
internal class TenantManagementConsumer : IDistributedConsumer<PreUpdateTenant>, IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PreDeleteTenant>, IDistributedConsumer<PosCreateTenant>
{
    private readonly ILogger<TenantManagementConsumer> _logger;
    private readonly IPoolService _poolService;
    private readonly IAdapterService _adapterService;
    private readonly IConfigurationService _configurationService;
    private readonly ICommunicationEventService _eventService;

    // Static on purpose (AB#4456): the consumer is registered scoped (AddBroadcastEventConsumer →
    // AddScoped), so MassTransit creates a NEW instance per message. An instance field can never
    // pair Pre with Pos — the pair state must live at process scope or the paired branch
    // (adapter restart relay + CK cache flush) silently never runs.
    private static readonly ConcurrentDictionary<Guid, bool> _receivedPreUpdateTenant = new();

    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger, IPoolService poolService,
        IAdapterService adapterService, IConfigurationService configurationService,
        ICommunicationEventService eventService)
    {
        _logger = logger;
        _poolService = poolService;
        _adapterService = adapterService;
        _configurationService = configurationService;
        _eventService = eventService;
    }


    public async Task ConsumeAsync(IDistributedContext<PreUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: {TenantId}", context.Message.TenantId);
        try
        {
            if (context.Message.Timestamp < Constants.StartTime)
            {
                _logger.LogInformation("Ignoring old message");
                return;
            }

            // Pair Pre and Pos by correlation id. Whichever message arrives second
            // triggers the actual tenant update — Pre first, then Pos — regardless of
            // delivery order. This guarantees adapters are notified (Pre) before the
            // cache is reinitialised (Pos), even when the broker delivers in normal order.
            if (_receivedPreUpdateTenant.TryRemove(context.Message.CorrelationId, out _))
            {
                await NotifyCkModelChangedAsync(context.Message.TenantId);
                await ExecutePreTenantUpdate(context.Message.TenantId);
                await ExecutePosTenantUpdate(context.Message.TenantId);
            }
            else
            {
                _receivedPreUpdateTenant.TryAdd(context.Message.CorrelationId, true);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pre update tenant failed: {TenantId}", context.Message.TenantId);
            await _eventService.StoreErrorEventAsync(context.Message.TenantId,
                $"Pre-update tenant failed: {e.Message}");
        }
        finally
        {
            _logger.LogInformation("Pre update tenant finished: {TenantId}", context.Message.TenantId);
        }
    }


    public async Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _logger.LogInformation("Pos update tenant received: {TenantId}", context.Message.TenantId);
        try
        {
            if (context.Message.Timestamp < Constants.StartTime)
            {
                _logger.LogInformation("Ignoring old message");
                return;
            }

            if (_receivedPreUpdateTenant.TryRemove(context.Message.CorrelationId, out _))
            {
                await NotifyCkModelChangedAsync(context.Message.TenantId);
                await ExecutePreTenantUpdate(context.Message.TenantId);
                await ExecutePosTenantUpdate(context.Message.TenantId);
            }
            else
            {
                _receivedPreUpdateTenant.TryAdd(context.Message.CorrelationId, false);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pos update tenant failed: {TenantId}", context.Message.TenantId);
            await _eventService.StoreErrorEventAsync(context.Message.TenantId,
                $"Post-update tenant failed: {e.Message}");
        }
        finally
        {
            _logger.LogInformation("Pos update tenant finished: {TenantId}", context.Message.TenantId);
        }
    }


    public async Task ConsumeAsync(IDistributedContext<PosCreateTenant> context)
    {
        _logger.LogInformation("Pos create tenant received: {TenantId}", context.Message.TenantId);
        try
        {
            if (context.Message.Timestamp < Constants.StartTime)
            {
                _logger.LogInformation("Ignoring old message");
                return;
            }

            // Tenant creation no longer triggers any operator-side action.
            // Pool deploy events do.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pos create tenant failed: {TenantId}", context.Message.TenantId);
            await _eventService.StoreErrorEventAsync(context.Message.TenantId,
                $"Post-create tenant failed: {e.Message}");
        }
        finally
        {
            _logger.LogInformation("Pos create tenant finished: {TenantId}", context.Message.TenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _logger.LogInformation("Pre delete tenant received: {TenantId}", context.Message.TenantId);
        try
        {
            if (context.Message.Timestamp < Constants.StartTime)
            {
                _logger.LogInformation("Ignoring old message");
                return;
            }

            // Tell the central Communication Operator to clean up every Cloud
            // pool of this tenant before the rest of the tenant data goes
            // away. Edge pools are managed externally and stay untouched.
            await _poolService.UndeployAllCloudPoolsAsync(context.Message.TenantId);

            await ExecutePreTenantUpdate(context.Message.TenantId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pre delete tenant failed: {TenantId}", context.Message.TenantId);
            await _eventService.StoreErrorEventAsync(context.Message.TenantId,
                $"Pre-delete tenant failed: {e.Message}");
        }
        finally
        {
            _logger.LogInformation("Pre delete tenant finished: {TenantId}", context.Message.TenantId);
        }
    }

    /// <summary>
    ///     Tells every connected adapter of the tenant to invalidate its in-process CK model cache
    ///     (AB#4456). Runs when the Pre/Pos pair completes — i.e. after the tenant update (CK model
    ///     import, cache clear) has finished. Deliberately NOT gated on
    ///     <see cref="IConfigurationService.IsEnabledAsync" /> and independent of the adapter cache:
    ///     a connected adapter must drop its stale CK cache even when the enabled-gated restart
    ///     relay below does not fire. Failures are logged but never block the restart relay.
    /// </summary>
    private async Task NotifyCkModelChangedAsync(string tenantId)
    {
        try
        {
            await _adapterService.CkModelChangedAsync(tenantId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "CK model change notification failed: {TenantId}", tenantId);
            await _eventService.StoreErrorEventAsync(tenantId,
                $"CK model change notification to adapters failed: {e.Message}");
        }
    }

    private async Task ExecutePreTenantUpdate(string tenantId)
    {
        if (await _configurationService.IsEnabledAsync(tenantId))
        {
            await _adapterService.PreUpdateTenantAsync(tenantId);
            await _poolService.PreUpdateTenantAsync(tenantId);
        }
    }

    private async Task ExecutePosTenantUpdate(string tenantId)
    {
        if (await _configurationService.IsEnabledAsync(tenantId))
        {
            await _adapterService.PosUpdateTenantAsync(tenantId);
            await _poolService.PosUpdateTenantAsync(tenantId);
        }
    }
}
