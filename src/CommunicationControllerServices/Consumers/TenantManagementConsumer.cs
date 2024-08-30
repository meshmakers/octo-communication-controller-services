using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

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
        
        if (await _configurationService.IsEnabledAsync(context.Message.TenantId))
        {
            await _adapterService.PreUpdateTenantAsync(context.Message.TenantId);
            await _poolService.PreUpdateTenantAsync(context.Message.TenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _logger.LogInformation("Pos update tenant received: {TenantId}", context.Message.TenantId);

        if (await _configurationService.IsEnabledAsync(context.Message.TenantId))
        {
            await _adapterService.PosUpdateTenantAsync(context.Message.TenantId);
            await _poolService.PosUpdateTenantAsync(context.Message.TenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        _logger.LogInformation("Pre delete tenant received: {TenantId}", context.Message.TenantId);
        
        await _poolService.PreUpdateTenantAsync(context.Message.TenantId);
        await _adapterService.PreUpdateTenantAsync(context.Message.TenantId);
    }


}