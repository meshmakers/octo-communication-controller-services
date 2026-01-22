using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Background service that periodically updates pipeline statistics for all tenants
/// </summary>
internal class PipelineStatisticsBackgroundService : BackgroundService
{
    private readonly IAdapterCache _adapterCache;
    private readonly IPipelineExecutionService _pipelineExecutionService;
    private readonly CommunicationControllerOptions _options;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Constructor
    /// </summary>
    public PipelineStatisticsBackgroundService(
        IAdapterCache adapterCache,
        IPipelineExecutionService pipelineExecutionService,
        IOptions<CommunicationControllerOptions> options)
    {
        _adapterCache = adapterCache;
        _pipelineExecutionService = pipelineExecutionService;
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("Pipeline statistics background service starting");

        try
        {
            // Wait a bit before starting to allow the service to fully initialize
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await UpdateStatisticsForAllTenantsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error updating pipeline statistics");
                }

                await Task.Delay(TimeSpan.FromMinutes(_options.StatisticsUpdateIntervalMinutes), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested, this is expected
        }

        Logger.Info("Pipeline statistics background service stopped");
    }

    private async Task UpdateStatisticsForAllTenantsAsync()
    {
        var tenantIds = _adapterCache.GetEnabledTenantIds();

        Logger.Debug("Updating pipeline statistics for {TenantCount} tenants", tenantIds.Count);

        foreach (var tenantId in tenantIds)
        {
            try
            {
                await _pipelineExecutionService.UpdateAllStatisticsAsync(tenantId);
                Logger.Debug("Updated pipeline statistics for tenant '{TenantId}'", tenantId);
            }
            catch (PipelineExecutionServiceException ex)
            {
                Logger.Warn(ex, "Failed to update pipeline statistics for tenant '{TenantId}'", tenantId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected error updating pipeline statistics for tenant '{TenantId}'", tenantId);
            }
        }
    }
}
