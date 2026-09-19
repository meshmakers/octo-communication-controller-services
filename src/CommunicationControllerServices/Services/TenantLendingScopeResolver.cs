using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ITenantLendingScopeResolver" />
internal sealed class TenantLendingScopeResolver(ISystemContext systemContext) : ITenantLendingScopeResolver
{
    /// <summary>
    ///     How long a resolved lending scope is reused. The walk opens an admin session against
    ///     every descendant tenant, so it must not run per work item; leases are granted at a rate
    ///     measured in seconds, and a tenant added to the tree becoming lendable up to half a minute
    ///     later is not a property anybody depends on. Mirrors the
    ///     <see cref="ILifecycleConfigurationService" /> cache window.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>child → parent for the whole tree; see <see cref="ResolveParentMapAsync" />.</summary>
    private volatile ParentMapEntry? _parentMap;

    /// <inheritdoc />
    public async Task<bool> MayLendAsync(string lenderTenantId, string borrowerTenantId,
        LendingScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lenderTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(borrowerTenantId);

        var lendable = await ResolveLendableTenantsAsync(lenderTenantId, scope, cancellationToken)
            .ConfigureAwait(false);
        return lendable.Contains(borrowerTenantId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> ResolveLendableTenantsAsync(string lenderTenantId,
        LendingScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lenderTenantId);

        if (scope.Mode == LendingScope.NotShared)
        {
            // No walk at all. The overwhelming majority of pools are NotShared and must not pay
            // for a tenant-tree traversal to learn it.
            return EmptySet();
        }

        var cacheKey = $"{lenderTenantId}|{scope.Mode}";
        if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return Intersect(cached.Tenants, scope.AllowedTenantIds);
        }

        var resolved = await WalkAsync(lenderTenantId, scope.Mode, cancellationToken).ConfigureAwait(false);
        _cache[cacheKey] = new CacheEntry(resolved, DateTimeOffset.UtcNow.Add(CacheDuration));

        // The allow-list is applied AFTER caching, never before: two pools in the same tenant with
        // the same mode share the walk, and each narrows the shared result by its own list.
        return Intersect(resolved, scope.AllowedTenantIds);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> ResolveCandidateLenderTenantsAsync(
        string borrowerTenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(borrowerTenantId);
        cancellationToken.ThrowIfCancellationRequested();

        // Ancestors first: the ordinary case is a pool in the parent, and ResolveAncestorsAsync
        // already walks ParentTenantId upwards with its own cycle guard.
        var candidates = new HashSet<string>(
            await ResolveAncestorsAsync(borrowerTenantId).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        foreach (var sibling in await ResolveSiblingsAsync(borrowerTenantId).ConfigureAwait(false))
        {
            candidates.Add(sibling);
        }

        // The borrower can never be its own lender. ResolveAncestorsAsync cannot return it unless
        // the registry has a cycle, and ResolveSiblingsAsync already filters it out, but a
        // candidate set that decides what a tenant is shown should not depend on that.
        candidates.Remove(borrowerTenantId);
        return candidates;
    }

    /// <inheritdoc />
    public void Invalidate(string lenderTenantId)
    {
        foreach (var key in _cache.Keys.Where(k =>
                     k.StartsWith($"{lenderTenantId}|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    private async Task<HashSet<string>> WalkAsync(string lenderTenantId, int mode,
        CancellationToken cancellationToken)
    {
        var lenderContext = await systemContext.TryFindTenantContextAsync(lenderTenantId).ConfigureAwait(false);
        if (lenderContext is null)
        {
            // The lender itself cannot be resolved. Deny rather than guess: an unresolvable lender
            // is exactly the "parent tenant deleted while lending" case in concept §6, and a
            // borrower must fall back to a blocking reason, never to a wider scope.
            Logger.Warn("Lending scope requested for unresolvable tenant '{LenderTenantId}'; denying all lending",
                lenderTenantId);
            return [];
        }

        // ---- descendants -------------------------------------------------------------------
        // The walk itself is GetDescendants() from octo-asset-repo-services, copied verbatim
        // including both of its guards: `visited` seeded with the root (so a registry cycle
        // degrades to a partial listing rather than an endless walk, and a tenant reachable by two
        // paths is listed once), and "a child whose context cannot be resolved is listed but its
        // subtree is not walked" (mid-creation / mid-delete tenants).
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lenderTenantId };
        var queue = new Queue<ITenantContext>();
        queue.Enqueue(lenderContext);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = queue.Dequeue();

            using var session = await context.GetAdminSessionAsync().ConfigureAwait(false);
            session.StartTransaction();
            var children = await context.GetDirectChildTenantsAsync(session).ConfigureAwait(false);
            await session.CommitTransactionAsync().ConfigureAwait(false);

            foreach (var child in children.Items)
            {
                if (!visited.Add(child.TenantId))
                {
                    continue;
                }

                result.Add(child.TenantId);

                var childContext = await context.TryGetChildTenantContextAsync(child.TenantId)
                    .ConfigureAwait(false);
                if (childContext != null)
                {
                    queue.Enqueue(childContext);
                }
            }
        }

        // ---- siblings ----------------------------------------------------------------------
        if (mode == LendingScope.DescendantsAndSiblings)
        {
            foreach (var sibling in await ResolveSiblingsAsync(lenderTenantId).ConfigureAwait(false))
            {
                result.Add(sibling);
            }
        }

        // ---- the upward invariant, ENFORCED ------------------------------------------------
        //
        // 🔴 The cycle guard above is not sufficient for an authorization decision, and copying
        // GetDescendants verbatim is therefore not enough on its own.
        //
        // `visited` stops the walk from looping forever, but it does not stop an ANCESTOR from
        // appearing in the result. In a registry where parent P lists child C and a corrupted (or
        // deliberately crafted) record also lists P as a child of C, the walk starting at C adds P
        // to the result — P was never visited, so nothing rejects it. GetDescendants can tolerate
        // that because it only produces a listing; here the same set decides whether a tenant may
        // execute work inside another, so a cycle would become a privilege escalation upwards.
        //
        // Subtracting the lender's own ancestor chain closes it. The chain is computed by walking
        // ParentTenantId upwards, which is independent of the child records the cycle corrupted.
        var ancestors = await ResolveAncestorsAsync(lenderTenantId).ConfigureAwait(false);
        var removed = result.RemoveWhere(t => ancestors.Contains(t) ||
                                              string.Equals(t, lenderTenantId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Logger.Warn(
                "Lending scope for '{LenderTenantId}' contained {Count} ancestor or self entries and " +
                "they were removed. This means the tenant registry contains a cycle — lending never " +
                "flows upwards (AB#4924, concept §3).",
                lenderTenantId, removed);
        }

        return result;
    }

    /// <summary>
    ///     Tenants sharing the lender's parent, excluding the lender itself.
    /// </summary>
    /// <remarks>
    ///     A tenant with no parent (a root, or a record written before <c>ParentTenantId</c> existed)
    ///     has no siblings by this definition. That is deliberate: treating "every other root" as a
    ///     sibling set would make every unparented tenant lend to every other unparented tenant,
    ///     which is precisely the trust boundary the tenant tree exists to draw.
    /// </remarks>
    private async Task<IReadOnlyCollection<string>> ResolveSiblingsAsync(string lenderTenantId)
    {
        var parentId = await ResolveParentAsync(lenderTenantId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(parentId))
        {
            return [];
        }

        var parentContext = await systemContext.TryFindTenantContextAsync(parentId).ConfigureAwait(false);
        if (parentContext is null)
        {
            Logger.Warn("Parent tenant '{ParentTenantId}' of '{LenderTenantId}' cannot be resolved; no siblings",
                parentId, lenderTenantId);
            return [];
        }

        using var session = await parentContext.GetAdminSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        var children = await parentContext.GetDirectChildTenantsAsync(session).ConfigureAwait(false);
        await session.CommitTransactionAsync().ConfigureAwait(false);

        return children.Items
            .Select(c => c.TenantId)
            .Where(t => !string.Equals(t, lenderTenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    ///     Every ancestor of the tenant, walking <c>ParentTenantId</c> upwards.
    /// </summary>
    /// <remarks>
    ///     Carries its own cycle guard: a registry in which A is B's parent and B is A's parent
    ///     would otherwise loop here too. It terminates and returns what it found.
    /// </remarks>
    private async Task<HashSet<string>> ResolveAncestorsAsync(string tenantId)
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = tenantId;

        while (true)
        {
            var parentId = await ResolveParentAsync(current).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(parentId) || !ancestors.Add(parentId))
            {
                return ancestors;
            }

            current = parentId;
        }
    }

    /// <summary>
    ///     The tenant's parent, or null when it has none.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>This cannot be answered from <c>GetAllTenantsAsync</c> on the system context, and
    ///     doing so was a silent bug (AB#5271).</b> A tenant's registry entry lives in its
    ///     <b>parent's</b> database, so that call returns only the system tenant's own direct
    ///     children: a grandchild is not in it at all, the lookup missed, and every tenant below the
    ///     first level was reported as having no parent. Nothing failed loudly — ancestors and
    ///     siblings simply came back empty, so a borrower two levels down was shown no lent pools
    ///     and the upward-escalation guard in <see cref="WalkAsync" /> silently had nothing to
    ///     subtract. The unit tests did not catch it because they substitute the registry.
    ///
    ///     <para>
    ///     The answer therefore has to come from the same mechanism the descendant walk uses:
    ///     <c>GetDirectChildTenantsAsync</c>, downwards from the roots, recording child → parent on
    ///     the way. See <see cref="ResolveParentMapAsync" />.
    ///     </para>
    /// </remarks>
    private async Task<string?> ResolveParentAsync(string tenantId)
    {
        var parents = await ResolveParentMapAsync().ConfigureAwait(false);
        return parents.GetValueOrDefault(tenantId);
    }

    /// <summary>
    ///     child → parent for the whole tenant tree, built by walking it downwards and cached for
    ///     <see cref="CacheDuration" />.
    /// </summary>
    /// <remarks>
    ///     One traversal serves every ancestor and sibling question, which is what makes it
    ///     affordable: <see cref="ResolveAncestorsAsync" /> would otherwise re-walk the tree once
    ///     per level. Same cycle guard as the descendant walk — a tenant already seen is not
    ///     enqueued again, so a corrupted registry degrades to a partial map instead of looping.
    ///     A child whose context cannot be resolved is still recorded (its parent is known) but its
    ///     own subtree is not walked, exactly as in <see cref="WalkAsync" />.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> ResolveParentMapAsync()
    {
        if (_parentMap is { IsExpired: false } cached)
        {
            return cached.Parents;
        }

        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using (var session = await systemContext.GetAdminSessionAsync().ConfigureAwait(false))
            {
                session.StartTransaction();
                var roots = await systemContext.GetAllTenantsAsync(session).ConfigureAwait(false);
                await session.CommitTransactionAsync().ConfigureAwait(false);

                var queue = new Queue<string>();
                foreach (var root in roots.Items)
                {
                    // A root's own ParentTenantId is whatever the system registry recorded — it may
                    // legitimately be null. Only the edges discovered below are added to the map.
                    if (visited.Add(root.TenantId))
                    {
                        queue.Enqueue(root.TenantId);
                    }
                }

                while (queue.Count > 0)
                {
                    var tenantId = queue.Dequeue();
                    var context = await systemContext.TryFindTenantContextAsync(tenantId).ConfigureAwait(false);
                    if (context is null)
                    {
                        continue;
                    }

                    using var childSession = await context.GetAdminSessionAsync().ConfigureAwait(false);
                    childSession.StartTransaction();
                    var children = await context.GetDirectChildTenantsAsync(childSession).ConfigureAwait(false);
                    await childSession.CommitTransactionAsync().ConfigureAwait(false);

                    foreach (var child in children.Items)
                    {
                        parents[child.TenantId] = tenantId;
                        if (visited.Add(child.TenantId))
                        {
                            queue.Enqueue(child.TenantId);
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            // A partial map is still better than none: the caller degrades to "no ancestors", which
            // is the conservative direction for every consumer — a narrower lending scope and a
            // borrower shown fewer pools, never more.
            Logger.Warn(e, "Could not build the tenant parent map; ancestor and sibling resolution will be partial");
        }

        _parentMap = new ParentMapEntry(parents, DateTimeOffset.UtcNow.Add(CacheDuration));
        return parents;
    }

    private static IReadOnlyCollection<string> Intersect(IReadOnlyCollection<string> resolved,
        IReadOnlyCollection<string>? allowedTenantIds)
    {
        if (allowedTenantIds is null || allowedTenantIds.Count == 0)
        {
            return resolved;
        }

        var allowed = new HashSet<string>(allowedTenantIds, StringComparer.OrdinalIgnoreCase);
        return resolved.Where(allowed.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<string> EmptySet() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry(HashSet<string> Tenants, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    /// <summary>
    ///     A class rather than a struct: the field holding it is <c>volatile</c>, which C# does not
    ///     allow for a value type, and a reference swap is what makes the refresh atomic.
    /// </summary>
    private sealed record ParentMapEntry(IReadOnlyDictionary<string, string> Parents, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}
