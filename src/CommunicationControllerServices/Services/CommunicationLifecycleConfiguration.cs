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

    /// <summary>
    ///     Master switch for adapter-pool leasing on this tenant (AB#4924 §14). Default false.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>Pulled forward from increment 9 deliberately.</b> With increment 7 merged, a
    ///         tenant that owns an <c>AdapterPool</c> and a <c>Leased</c> adapter starts being
    ///         scheduled the moment the controller rolls out, and there was no way to stop it short of
    ///         a redeploy. A kill switch that arrives after the thing it switches off is not a kill
    ///         switch.
    ///     </para>
    ///     <para>
    ///         <b>One flag, two meanings, both required.</b> On a <b>lending</b> tenant it reads "this
    ///         tenant's deploymentSites hand their members out"; on a <b>borrowing</b> tenant it reads "this
    ///         tenant's <c>Leased</c> adapters get scheduled". A lease needs both ends on, because
    ///         lending is the lender's capability and borrowing is the borrower's and neither tenant
    ///         can assert the other's. That is what the plan's §14 asks for in one line.
    ///     </para>
    ///     <para>
    ///         🔴 <b>Switching it off HOLDS the queue.</b> Work already <c>Queued</c> stays
    ///         <c>Queued</c> and stays visible; nothing new is enqueued and nothing is granted.
    ///         Draining would mean "off" still runs the next hour of work — the exact surprise the
    ///         switch exists to prevent. Cancelling would destroy work the operator never asked to
    ///         lose, and a queue entry is the borrower's, not the operator's, to discard. Holding is
    ///         the only one of the three that is reversible: switching back on resumes the queue in its
    ///         original order, and <c>DELETE {tenantId}/v1/adapterPool/{id}/queue/{executionId}</c> is
    ///         the explicit way to throw it away.
    ///     </para>
    /// </remarks>
    public bool LeasingEnabled { get; set; }
}
