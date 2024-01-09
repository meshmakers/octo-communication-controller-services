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
    private readonly IPlugServiceUpdates _plugService;
    private readonly ISocketServiceUpdates _socketService;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="poolService"></param>
    /// <param name="plugService"></param>
    /// <param name="socketService"></param>
    public TenantManagementConsumer(ILogger<TenantManagementConsumer> logger, IPoolServiceUpdates poolService,
        IPlugServiceUpdates plugService, ISocketServiceUpdates socketService)
    {
        _logger = logger;
        _poolService = poolService;
        _plugService = plugService;
        _socketService = socketService;
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
        await _plugService.ReloadTenantAsync(tenantId);
        await _socketService.ReloadTenantAsync(tenantId);
    }
}