namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Resolves which tenants an adapter pool may lend its members to (AB#4924, concept §3).
/// </summary>
/// <remarks>
///     <para>
///         The trust direction is fixed by the tenant tree and is <b>never</b> configurable: a pool
///         lends downwards to its owner's descendants, and laterally to siblings under a shared
///         parent, but never upwards. A descendant must not be able to run work inside its ancestor.
///     </para>
///     <para>
///         🔴 <b>This does not call <c>GET {tenantId}/v1/tenants/descendants</c>.</b>
///         <c>CommunicationControllerServices</c> has no <c>Sdk.ServiceClient</c> reference
///         (deliberately — see the AB#5027 phase-2 rationale in CLAUDE.md), the endpoint is
///         <c>Authorize</c>d with the asset repository's tenant read-only policy, and a lease
///         scheduler running in the background has no caller token to forward. It does not need the
///         HTTP call: <c>GetDescendants</c> is a pure <c>ITenantContext</c> walk and the controller
///         already holds <c>ISystemContext</c>. The endpoint stays the contract for external
///         callers; it is not the mechanism here.
///     </para>
/// </remarks>
public interface ITenantLendingScopeResolver
{
    /// <summary>
    ///     Whether <paramref name="lenderTenantId" /> may lend to <paramref name="borrowerTenantId" />
    ///     under the given sharing mode and optional allow-list.
    /// </summary>
    Task<bool> MayLendAsync(string lenderTenantId, string borrowerTenantId,
        LendingScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every tenant <paramref name="lenderTenantId" /> may lend to, already intersected with the
    ///     allow-list and already stripped of the lender itself and of all its ancestors.
    /// </summary>
    Task<IReadOnlyCollection<string>> ResolveLendableTenantsAsync(string lenderTenantId,
        LendingScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Drops any cached tenant-tree walk for this lender, so the next resolve sees a tenant that
    ///     was just created, attached or deleted.
    /// </summary>
    void Invalidate(string lenderTenantId);
}

/// <summary>
///     The lending configuration of one adapter pool, lifted off the CK entity so the resolver can
///     be exercised without constructing one.
/// </summary>
/// <param name="Mode">
///     0 <c>NotShared</c>, 1 <c>Descendants</c>, 2 <c>DescendantsAndSiblings</c> — the numeric keys
///     of the <c>AdapterSharingMode</c> CK enum.
/// </param>
/// <param name="AllowedTenantIds">
///     Optional allow-list. Absent or empty means "every tenant the mode resolves to". It
///     <b>intersects</b> and can never widen — a listed tenant the mode does not resolve to is
///     ignored, never granted.
/// </param>
public readonly record struct LendingScope(int Mode, IReadOnlyCollection<string>? AllowedTenantIds)
{
    /// <summary>The pool is private to its owning tenant.</summary>
    public const int NotShared = 0;

    /// <summary>The pool lends to any descendant of the owning tenant, direct or indirect.</summary>
    public const int Descendants = 1;

    /// <summary>Additionally lends to tenants sharing the owning tenant's parent.</summary>
    public const int DescendantsAndSiblings = 2;
}
