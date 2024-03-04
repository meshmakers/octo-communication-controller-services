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

    public async Task ConsumeAsync(IDistributedContext<PosUpdateTenant> context)
    {
        _logger.LogInformation("Pre update tenant received: {Text}", context.Message.TenantId);

        if (await _configurationService.IsEnabledAsync(context.Message.TenantId))
        {
            await _adapterService.ReloadTenantAsync(context.Message.TenantId);
            await _poolService.ReloadTenantAsync(context.Message.TenantId);
        }
    }

    public async Task ConsumeAsync(IDistributedContext<PreDeleteTenant> context)
    {
        await _poolService.UnloadTenantAsync(context.Message.TenantId);
        await _adapterService.UnloadTenantAsync(context.Message.TenantId);
    }
}