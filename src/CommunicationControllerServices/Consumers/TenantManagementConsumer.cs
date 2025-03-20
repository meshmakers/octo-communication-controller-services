using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
///    Updates jobs for a tenant
/// </summary>
internal class TenantManagementConsumer : IDistributedConsumer<PreUpdateTenant>, IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ILogger<TenantManagementConsumer> _logger;
    private readonly IPoolService _poolService;
    private readonly IAdapterService _adapterService;
    private readonly IConfigurationService _configurationService;
    private readonly ConcurrentDictionary<Guid, bool> _receivedPreUpdateTenant = new();

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="poolService"></param>
    /// <param name="adapterService"></param>
    /// <param name="configurationService"></param>
    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger, IPoolService poolService,
        IAdapterService adapterService, IConfigurationService configurationService)
    {
        _logger = logger;
        _poolService = poolService;
        _adapterService = adapterService;
        _configurationService = configurationService;
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

            // We check if already a pos update tenant message was received for this correlation id
            if (_receivedPreUpdateTenant.TryGetValue(context.Message.CorrelationId, out bool receivedPreUpdateTenant))
            {
                if (!receivedPreUpdateTenant)
                {
                    _logger.LogInformation("Pos update tenant message was received before pos update tenant message");
                    await ExecutePreTenantUpdate(context.Message.TenantId);
                    await ExecutePosTenantUpdate(context.Message.TenantId);
                    _receivedPreUpdateTenant.Remove(context.Message.CorrelationId, out _);
                    return;
                }
            }

            _receivedPreUpdateTenant.AddOrUpdate(context.Message.CorrelationId, true, (_, oldValue) => oldValue);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pre update tenant failed: {TenantId}", context.Message.TenantId);
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

            // We check if already a pre update tenant message was received for this correlation id
            if (_receivedPreUpdateTenant.TryGetValue(context.Message.CorrelationId, out bool receivedPreUpdateTenant))
            {
                if (receivedPreUpdateTenant)
                {
                    _logger.LogInformation("Pre update tenant message was received before pos update tenant message");
                    await ExecutePosTenantUpdate(context.Message.TenantId);
                    _receivedPreUpdateTenant.Remove(context.Message.CorrelationId, out _);
                    return;
                }
            }

            _receivedPreUpdateTenant.AddOrUpdate(context.Message.CorrelationId, false, (_, oldValue) => oldValue);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pos update tenant failed: {TenantId}", context.Message.TenantId);
        }
        finally
        {
            _logger.LogInformation("Pos update tenant finished: {TenantId}", context.Message.TenantId);
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

            await ExecutePreTenantUpdate(context.Message.TenantId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Pre delete tenant failed: {TenantId}", context.Message.TenantId);
        }
        finally
        {
            _logger.LogInformation("Pre delete tenant finished: {TenantId}", context.Message.TenantId);
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