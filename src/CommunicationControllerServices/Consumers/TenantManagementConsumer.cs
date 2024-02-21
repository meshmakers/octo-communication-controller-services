using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
///    Updates jobs for a tenant
/// </summary>
internal class TenantManagementConsumer : IDistributedConsumer<PosUpdateTenant>,
    IDistributedConsumer<PreDeleteTenant>
{
    private readonly ILogger<TenantManagementConsumer> _logger;
    private readonly IPoolServiceUpdates _poolService;
    private readonly IAdapterServiceUpdates _adapterService;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="poolService"></param>
    /// <param name="adapterService"></param>
    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger, IPoolServiceUpdates poolService,
        IAdapterServiceUpdates adapterService)
    {
        _logger = logger;
        _poolService = poolService;
        _adapterService = adapterService;
    }

    public async Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: {Text}", context.Message.TenantId);

        await ReloadTenantAsync(context.Message.TenantId);
    }

    public async Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        await ReloadTenantAsync(context.Message.TenantId);
    }
    
    private async Task ReloadTenantAsync(string tenantId)
    {
        _logger.LogInformation("Reloading tenant '{TenantId}'", tenantId);
        await _poolService.ReloadTenantAsync(tenantId);
        await _adapterService.ReloadTenantAsync(tenantId);
    }
}