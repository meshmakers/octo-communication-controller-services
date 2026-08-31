namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Per-tenant runtime configuration for the on-demand adapter lifecycle (AB#4914), stored
///     under the tenant-configuration key <see cref="Constants.CommunicationLifecycleConfigurationKey"/>
///     — the same key-value store that carries the Communication enabled flag. Activation is
///     deliberately runtime configuration, not a deployment switch: it is set per tenant via
///     octo-cli / Studio and evaluated by the idle watchdog and the wake gates, so scale-to-zero
///     can be enabled for a single test tenant (and switched off again) without redeploying the
///     controller.
/// </summary>
public class CommunicationLifecycleConfiguration
{
    /// <summary>
    ///     Master switch for scale-to-zero on this tenant. Default false — even a workload with
    ///     LifecycleMode=OnDemand is never hibernated while this is off. Both levels must be on
    ///     before anything scales down (per-tenant gate here, per-workload opt-in via
    ///     LifecycleMode). Setting this to false is the emergency stop: the idle watchdog stops
    ///     hibernating immediately; already-hibernated workloads are woken on next demand or via
    ///     the wake API.
    /// </summary>
    public bool ScaleToZeroEnabled { get; set; }
}
