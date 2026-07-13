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

    // Retention cleanup (deleting old records) runs at most once per day.
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(24);

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
        Logger.Info(
            "Execution cleanup background service starting with fold retention of {RetentionHours} hours, " +
            "orphan retention of {RetentionDays} days, stuck grace period of {GraceMinutes} minutes and " +
            "stuck-check interval of {IntervalMinutes} minutes",
            _options.PipelineExecutionRetentionHours, _options.PipelineExecutionRetentionDays,
            _options.PipelineExecutionStuckGraceMinutes, _options.PipelineExecutionStuckCheckIntervalMinutes);

        var stuckCheckInterval = TimeSpan.FromMinutes(Math.Max(1, _options.PipelineExecutionStuckCheckIntervalMinutes));
        // Force a retention sweep on the first loop iteration.
        var lastRetentionRun = DateTime.UtcNow - RetentionInterval;

        try
        {
            // Wait a bit before starting to allow the service to fully initialize
            // Also offset from statistics service to avoid running simultaneously
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // The connection-aware stuck reaper runs every iteration (minutes-scale) so an
                // execution orphaned by an adapter restart becomes observable quickly.
                try
                {
                    await FailStuckExecutionsForAllTenantsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error failing stuck executions");
                }

                // Fold terminal executions older than the retention window into the hourly
                // statistics buckets, delete them, and refresh the sliding-window counters
                // (AB#4370). Runs every iteration — the fold is incremental and cheap.
                try
                {
                    await FoldAndPruneExecutionsForAllTenantsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error folding executions into statistics");
                }

                // The daily sweep remains as a safety net for orphaned executions whose pipeline
                // no longer exists — those are never reached by the per-pipeline fold.
                if (DateTime.UtcNow - lastRetentionRun >= RetentionInterval)
                {
                    try
                    {
                        await CleanupOldExecutionsForAllTenantsAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error cleaning up old executions");
                    }

                    lastRetentionRun = DateTime.UtcNow;
                }

                await Task.Delay(stuckCheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested, this is expected
        }

        Logger.Info("Execution cleanup background service stopped");
    }

    private async Task FailStuckExecutionsForAllTenantsAsync()
    {
        var tenantIds = _adapterCache.GetEnabledTenantIds();
        var totalFailed = 0;

        foreach (var tenantId in tenantIds)
        {
            try
            {
                var failedCount = await _pipelineExecutionService.FailStuckExecutionsAsync(
                    tenantId,
                    _options.PipelineExecutionStuckGraceMinutes);

                totalFailed += failedCount;
            }
            catch (PipelineExecutionServiceException ex)
            {
                Logger.Warn(ex, "Failed to fail stuck executions for tenant '{TenantId}'", tenantId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected error failing stuck executions for tenant '{TenantId}'", tenantId);
            }
        }

        if (totalFailed > 0)
        {
            Logger.Info("Stuck-execution reaper failed {TotalFailed} orphaned executions across {TenantCount} tenants",
                totalFailed, tenantIds.Count);
        }
    }

    private async Task FoldAndPruneExecutionsForAllTenantsAsync()
    {
        var tenantIds = _adapterCache.GetEnabledTenantIds();
        var totalPruned = 0;

        foreach (var tenantId in tenantIds)
        {
            try
            {
                totalPruned += await _pipelineExecutionService.FoldAndPruneExecutionsAsync(
                    tenantId, _options.PipelineExecutionRetentionHours);
            }
            catch (PipelineExecutionServiceException ex)
            {
                Logger.Warn(ex, "Failed to fold executions for tenant '{TenantId}'", tenantId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected error folding executions for tenant '{TenantId}'", tenantId);
            }
        }

        if (totalPruned > 0)
        {
            Logger.Info("Execution fold pruned {TotalPruned} executions across {TenantCount} tenants",
                totalPruned, tenantIds.Count);
        }
    }

    private async Task CleanupOldExecutionsForAllTenantsAsync()
    {
        var tenantIds = _adapterCache.GetEnabledTenantIds();

        Logger.Info("Starting execution retention cleanup for {TenantCount} tenants", tenantIds.Count);

        var totalDeleted = 0;

        foreach (var tenantId in tenantIds)
        {
            try
            {
                // Delete old executions based on retention policy
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

        Logger.Info("Execution retention cleanup completed. Total deleted: {TotalDeleted}", totalDeleted);
    }
}
