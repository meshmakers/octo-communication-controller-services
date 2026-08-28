using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Default implementation of <see cref="ILifecycleConfigurationService"/> over the tenant
///     configuration key-value store (AB#4914). Singleton; the cache is per controller pod.
///     The TTL is a trade-off between gate latency (every ExecutePipeline / config push on an
///     OnDemand tenant reads the gate) and how fast the emergency stop propagates — 30 seconds
///     keeps both acceptable. The idle watchdog's 5-minute loop tolerates the same staleness.
/// </summary>
internal class LifecycleConfigurationService(
    ILogger<LifecycleConfigurationService> logger,
    ISystemContext systemContext) : ILifecycleConfigurationService
{
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<CommunicationLifecycleConfiguration> GetConfigurationAsync(string tenantId)
    {
        if (_cache.TryGetValue(tenantId, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
        {
            return entry.Configuration;
        }

        var configuration = await LoadAsync(tenantId);
        _cache[tenantId] = new CacheEntry(configuration, DateTime.UtcNow.Add(CacheTtl));
        return configuration;
    }

    public async Task<bool> IsScaleToZeroEnabledAsync(string tenantId)
    {
        return (await GetConfigurationAsync(tenantId)).ScaleToZeroEnabled;
    }

    public async Task SetConfigurationAsync(string tenantId, CommunicationLifecycleConfiguration configuration)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();
        await tenantContext.SetConfigurationAsync(session, Constants.CommunicationLifecycleConfigurationKey,
            configuration);
        await session.CommitTransactionAsync();

        // Effective immediately on this pod; other pods converge within the TTL.
        _cache.TryRemove(tenantId, out _);

        logger.LogInformation(
            "Lifecycle configuration for tenant '{TenantId}' updated: ScaleToZeroEnabled={ScaleToZeroEnabled}",
            tenantId, configuration.ScaleToZeroEnabled);
    }

    private async Task<CommunicationLifecycleConfiguration> LoadAsync(string tenantId)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();
        var configuration = await tenantContext.GetConfigurationAsync<CommunicationLifecycleConfiguration>(
            session, Constants.CommunicationLifecycleConfigurationKey, null);
        return configuration ?? new CommunicationLifecycleConfiguration();
    }

    private sealed record CacheEntry(CommunicationLifecycleConfiguration Configuration, DateTime ExpiresAt);
}
