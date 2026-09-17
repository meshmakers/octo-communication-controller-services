namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
///     How <c>/adapterPoolHub</c> treats a connection that does not satisfy
///     <see cref="Constants.TenantCommunicationApiReadWritePolicy" /> for the tenant that owns the
///     pool it claims to belong to.
/// </summary>
internal enum AdapterPoolHubAuthorizationMode
{
    /// <summary>
    ///     Connection outcomes identical to a hub with no gate at all, but every connection an
    ///     enforcing run would refuse is logged as a warning naming the caller and the two tenants.
    ///     <b>The default</b>, for the same reason the other two gates default to it — see the
    ///     remarks on <see cref="AdapterPoolHubAuthorizationOptions" />.
    /// </summary>
    LogOnly = 0,

    /// <summary>
    ///     A connection that fails the check is refused with a <c>HubException</c>, and a member that
    ///     registers for a pool tenant other than its own token's is refused the registration.
    /// </summary>
    Enforce = 1
}

/// <summary>
///     Staged authorization of the adapter <b>pool</b> management channel at <c>/adapterPoolHub</c>
///     (AB#4924, concept §8 Q4).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why a third gate and not one of the two that exist.</b> <c>/operatorHub</c>'s gate
///         applies <c>SystemCommunicationApiPolicy</c> — system-level authority over every tenant.
///         That was considered for pool members and rejected: a member runs pipelines, it does not
///         administer the estate, and handing the whole pool fleet system authority to save a file is
///         the wrong trade. <c>/{tenantId}/adapterHub</c>'s gate is the right <i>policy</i> but binds
///         the connection to a <b>route</b> tenant, and this route deliberately has none — a pool
///         member belongs to no tenant. So: the adapter gate's policy, bound to the tenant in the
///         token rather than in the path.
///     </para>
///     <para>
///         <b>What the binding is, concretely.</b> The filter resolves the connection's principal and
///         remembers its <c>tenant_id</c> claim on the connection. <c>AdapterPoolHub</c> then refuses
///         a <c>RegisterPoolMemberAsync</c> whose declared <c>AdapterPoolTenantId</c> is a different tenant.
///         Two halves in two places on purpose: the filter cannot see the declared pool (it runs at
///         connect, before any hub method), and the hub cannot re-derive the principal as cheaply or
///         as reliably as the filter, which authenticates the bearer scheme explicitly when the
///         default scheme left the connection anonymous.
///     </para>
///     <para>
///         🔴 <b>Authorization for a borrower never comes from this connection.</b> It comes from the
///         lease, as the borrower's own <c>PipelineServiceAccount</c> credential (concept §8 Q6), and
///         it expires with the lease. That is what keeps a pool member from being a standing
///         cross-tenant credential — which is precisely what a <c>SystemCommunicationApiPolicy</c>
///         connection would have been.
///     </para>
///     <para>
///         It ships in <see cref="AdapterPoolHubAuthorizationMode.LogOnly" /> like its two siblings,
///         for the same reason and with the same operator contract: set
///         <c>OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE=Enforce</c> per environment, no release needed.
///         Unlike the adapter fleet there is no legacy consumer to protect here — nothing connects to
///         this hub yet — but the staging shape is kept identical so an operator has one mental model
///         for all three, and so an environment can arm the three independently.
///     </para>
///     <para>
///         There is no third "off" value: <see cref="AdapterPoolHubAuthorizationMode.LogOnly" />
///         already changes no outcome, so a silent mode would only buy the ability to hide the
///         inventory.
///     </para>
/// </remarks>
internal class AdapterPoolHubAuthorizationOptions
{
    /// <summary>
    ///     Configuration section, i.e. <c>OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE</c>.
    /// </summary>
    public const string SectionName = "AdapterPoolHubAuthorization";

    /// <summary>
    ///     Enforcement mode. Defaults to <see cref="AdapterPoolHubAuthorizationMode.LogOnly" />, which
    ///     is also the enum's zero value, so an unbound section keeps today's behaviour.
    /// </summary>
    public AdapterPoolHubAuthorizationMode Mode { get; set; } = AdapterPoolHubAuthorizationMode.LogOnly;
}
