using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Background service that periodically cleans up old pipeline execution records
/// </summary>
internal class ExecutionCleanupBackgroundService : BackgroundService
{
    private readonly IAdapterCache _adapterCache;
    private readonly IPipelineExecutionService _pipelineExecutionService;
    private readonly CommunicationControllerOptions _options;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Run cleanup once per day
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Constructor
    /// </summary>
    public ExecutionCleanupBackgroundService(
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
        Logger.Info("Execution cleanup background service starting with retention period of {RetentionDays} days and execution timeout of {TimeoutHours} hours",
            _options.PipelineExecutionRetentionDays, _options.PipelineExecutionTimeoutHours);

        try
        {
            // Wait a bit before starting to allow the service to fully initialize
            // Also offset from statistics service to avoid running simultaneously
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupOldExecutionsForAllTenantsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error cleaning up old executions");
                }

                await Task.Delay(CleanupInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested, this is expected
        }

        Logger.Info("Execution cleanup background service stopped");
    }

    private async Task CleanupOldExecutionsForAllTenantsAsync()
    {
        var tenantIds = _adapterCache.GetEnabledTenantIds();

        Logger.Info("Starting execution cleanup for {TenantCount} tenants", tenantIds.Count);

        var totalTimedOut = 0;
        var totalDeleted = 0;

        foreach (var tenantId in tenantIds)
        {
            try
            {
                // First, timeout stale running executions
                var timedOutCount = await _pipelineExecutionService.TimeoutStaleExecutionsAsync(
                    tenantId,
                    _options.PipelineExecutionTimeoutHours);

                totalTimedOut += timedOutCount;

                if (timedOutCount > 0)
                {
                    Logger.Info("Timed out {TimedOutCount} stale executions for tenant '{TenantId}'",
                        timedOutCount, tenantId);
                }
            }
            catch (PipelineExecutionServiceException ex)
            {
                Logger.Warn(ex, "Failed to timeout stale executions for tenant '{TenantId}'", tenantId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected error timing out stale executions for tenant '{TenantId}'", tenantId);
            }

            try
            {
                // Then, delete old executions based on retention policy
                var deletedCount = await _pipelineExecutionService.CleanupOldExecutionsAsync(
                    tenantId,
                    _options.PipelineExecutionRetentionDays);

                totalDeleted += deletedCount;

                if (deletedCount > 0)
                {
                    Logger.Info("Deleted {DeletedCount} old executions for tenant '{TenantId}'",
                        deletedCount, tenantId);
                }
            }
            catch (PipelineExecutionServiceException ex)
            {
                Logger.Warn(ex, "Failed to cleanup old executions for tenant '{TenantId}'", tenantId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected error cleaning up old executions for tenant '{TenantId}'", tenantId);
            }
        }

        Logger.Info("Execution cleanup completed. Total timed out: {TotalTimedOut}, Total deleted: {TotalDeleted}",
            totalTimedOut, totalDeleted);
    }
}
