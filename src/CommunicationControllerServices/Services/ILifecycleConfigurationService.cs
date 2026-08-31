namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Reads and writes the per-tenant on-demand lifecycle configuration (AB#4914).
///     Reads are served through a short-TTL cache so the per-request wake gates do not hit the
///     tenant database on every call; writes invalidate the cache entry immediately.
/// </summary>
public interface ILifecycleConfigurationService
{
    /// <summary>
    ///     Returns the tenant's lifecycle configuration, using the short-TTL cache.
    ///     A tenant without a stored record gets the defaults (scale-to-zero off).
    /// </summary>
    Task<CommunicationLifecycleConfiguration> GetConfigurationAsync(string tenantId);

    /// <summary>
    ///     Convenience gate: true iff scale-to-zero is enabled for the tenant. Cached.
    /// </summary>
    Task<bool> IsScaleToZeroEnabledAsync(string tenantId);

    /// <summary>
    ///     Persists the tenant's lifecycle configuration and invalidates the cache entry, so the
    ///     change is effective on the next gate/watchdog read.
    /// </summary>
    Task SetConfigurationAsync(string tenantId, CommunicationLifecycleConfiguration configuration);
}
