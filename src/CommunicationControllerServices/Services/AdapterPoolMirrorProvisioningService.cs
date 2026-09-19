using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IAdapterPoolMirrorProvisioningService" />
internal sealed class AdapterPoolMirrorProvisioningService(
    ILogger<AdapterPoolMirrorProvisioningService> logger,
    ICommunicationRepository communicationRepository,
    ITenantLendingScopeResolver lendingScopeResolver)
    : IAdapterPoolMirrorProvisioningService
{
    /// <inheritdoc />
    public async Task<AdapterPoolMirrorSyncResult> ProvisionForBorrowerAsync(string borrowerTenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(borrowerTenantId))
        {
            throw new ArgumentException("Borrower tenant id is required.", nameof(borrowerTenantId));
        }

        IReadOnlyCollection<RtLentAdapterPool> existingMirrors;
        try
        {
            existingMirrors = await communicationRepository.GetLentAdapterPoolMirrorsAsync(borrowerTenantId);
        }
        catch (Exception e)
        {
            // Without the existing set there is no safe action left: upserting would still work, but
            // removing would not, and a run that only ever adds turns a narrowed lending scope into
            // a mirror that never goes away.
            logger.LogWarning(e,
                "[{BorrowerTenantId}] Could not read the existing lent adapter pool mirrors; skipping this tenant",
                borrowerTenantId);
            return AdapterPoolMirrorSyncResult.Nothing;
        }

        var candidates = await ResolveLendersToWalkAsync(borrowerTenantId, existingMirrors, cancellationToken);

        var desired = new Dictionary<MirrorKey, LendableAdapterPool>();
        var lendersUnreadable = 0;

        foreach (var lenderTenantId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyCollection<LendableAdapterPool> pools;
            try
            {
                pools = await communicationRepository.GetAdapterPoolsForMirroringAsync(lenderTenantId);
            }
            catch (Exception e)
            {
                // One broken lender must not cost the borrower its mirrors of every other lender —
                // the same stance the cross-tenant number-claim scan takes in SignalChannelService.
                logger.LogWarning(e,
                    "[{BorrowerTenantId}] Could not read the adapter pools of candidate lender '{LenderTenantId}'",
                    borrowerTenantId, lenderTenantId);
                lendersUnreadable++;

                // 🔴 Keep whatever this lender's mirrors already say. Treating an unreadable lender
                // as "lends nothing" would delete the borrower's mirrors — and with the association
                // leading, that breaks every adapter borrowing from it. An unreadable tenant is
                // unknown, never empty.
                CarryForwardMirrorsOf(lenderTenantId, existingMirrors, desired);
                continue;
            }

            foreach (var pool in pools)
            {
                // 🔴 The pool's OWN sharing mode and allow-list decide, evaluated against the tenant
                // tree. Never the mirror's mirrored SharingMode: that value lives in the borrower's
                // own database, where the borrower can edit it.
                if (!await lendingScopeResolver.MayLendAsync(lenderTenantId, borrowerTenantId, pool.Scope,
                        cancellationToken))
                {
                    continue;
                }

                desired[MirrorKey.Of(pool)] = pool;
            }
        }

        var created = 0;
        var removed = 0;

        foreach (var pool in desired.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var upsert = await communicationRepository.UpsertLentAdapterPoolMirrorAsync(borrowerTenantId, pool);
                if (upsert.Wrote)
                {
                    created++;
                }
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "[{BorrowerTenantId}] Failed to mirror adapter pool {AdapterPoolRtId} of tenant '{LenderTenantId}'",
                    borrowerTenantId, pool.AdapterPoolRtId, pool.LenderTenantId);
            }
        }

        foreach (var mirror in existingMirrors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = MirrorKey.Of(mirror);

            // A mirror whose Lender record says nothing usable cannot be matched against anything —
            // and it cannot serve an adapter either, since LentFromReference refuses it. Removing it
            // is what lets the next run write a correct one.
            if (key is not null && desired.ContainsKey(key.Value))
            {
                continue;
            }

            try
            {
                await communicationRepository.RemoveLentAdapterPoolMirrorAsync(borrowerTenantId, mirror.RtId);
                removed++;
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "[{BorrowerTenantId}] Failed to remove the stale lent adapter pool mirror '{MirrorRtId}'",
                    borrowerTenantId, mirror.RtId);
            }
        }

        var result = new AdapterPoolMirrorSyncResult(1, created, removed, lendersUnreadable);

        if (!result.IsNoOp || lendersUnreadable > 0)
        {
            logger.LogInformation(
                "[{BorrowerTenantId}] Lent adapter pool mirrors reconciled: {Created} present, {Removed} removed, " +
                "{LendersUnreadable} candidate lender(s) unreadable",
                borrowerTenantId, created, removed, lendersUnreadable);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<AdapterPoolMirrorSyncResult> ProvisionForLenderAsync(string lenderTenantId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lenderTenantId))
        {
            throw new ArgumentException("Lender tenant id is required.", nameof(lenderTenantId));
        }

        IReadOnlyCollection<LendableAdapterPool> pools;
        try
        {
            pools = await communicationRepository.GetAdapterPoolsForMirroringAsync(lenderTenantId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "[{LenderTenantId}] Could not read the tenant's adapter pools; no borrower was reconciled",
                lenderTenantId);
            return AdapterPoolMirrorSyncResult.Nothing;
        }

        // The union over every pool, not per pool: a tenant borrowing from two pools of the same
        // lender must be reconciled once, and the per-borrower pass reconciles all of its lenders
        // anyway.
        var borrowers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in pools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var borrower in await lendingScopeResolver.ResolveLendableTenantsAsync(lenderTenantId,
                         pool.Scope, cancellationToken))
            {
                borrowers.Add(borrower);
            }
        }

        var result = AdapterPoolMirrorSyncResult.Nothing;
        foreach (var borrowerTenantId in borrowers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = result.Add(await ProvisionForBorrowerAsync(borrowerTenantId, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "[{LenderTenantId}] Failed to reconcile borrower '{BorrowerTenantId}' after a pool change",
                    lenderTenantId, borrowerTenantId);
            }
        }

        return result;
    }

    /// <summary>
    ///     The lenders one borrower-side run has to look at: its candidates from the tenant tree,
    ///     plus every lender its stored mirrors already name.
    /// </summary>
    /// <remarks>
    ///     🔴 The second half is what makes <b>revocation</b> work. When a lender drops out of the
    ///     candidate set — the borrower was re-parented, or the tenant tree changed — the tree walk
    ///     stops returning it, so a run driven by candidates alone would never visit its mirrors
    ///     again and they would sit there forever, still naming a pool the borrower may no longer
    ///     use. Walking the stored mirrors as well guarantees every one of them is re-decided at
    ///     least once per run, and a lender that genuinely lends nothing here simply contributes no
    ///     desired entries, so its mirrors fall into the removal pass.
    /// </remarks>
    private async Task<IReadOnlyCollection<string>> ResolveLendersToWalkAsync(string borrowerTenantId,
        IReadOnlyCollection<RtLentAdapterPool> existingMirrors, CancellationToken cancellationToken)
    {
        var lenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var candidate in await lendingScopeResolver.ResolveCandidateLenderTenantsAsync(borrowerTenantId,
                         cancellationToken))
            {
                lenders.Add(candidate);
            }
        }
        catch (Exception e)
        {
            // Same rule as an unreadable lender: unknown, not empty. Without the candidate set the
            // run can still re-decide the stored mirrors, which is strictly better than assuming the
            // borrower has no lenders and deleting all of them.
            logger.LogWarning(e,
                "[{BorrowerTenantId}] Could not resolve the candidate lender tenants; reconciling only the lenders " +
                "the existing mirrors name",
                borrowerTenantId);
        }

        foreach (var mirror in existingMirrors)
        {
            var lender = mirror.Lender?.LenderTenantId;
            if (!string.IsNullOrWhiteSpace(lender))
            {
                lenders.Add(lender!);
            }
        }

        return lenders;
    }

    private static void CarryForwardMirrorsOf(string lenderTenantId,
        IReadOnlyCollection<RtLentAdapterPool> existingMirrors, Dictionary<MirrorKey, LendableAdapterPool> desired)
    {
        foreach (var mirror in existingMirrors)
        {
            var key = MirrorKey.Of(mirror);
            if (key is null ||
                !string.Equals(key.Value.LenderTenantId, lenderTenantId, StringComparison.OrdinalIgnoreCase) ||
                desired.ContainsKey(key.Value))
            {
                continue;
            }

            // Re-upsert it from what it already holds rather than skipping it: the removal pass
            // keys off `desired`, so an entry has to be there for the mirror to survive. The values
            // are the mirror's own, so this writes nothing new.
            desired[key.Value] = new LendableAdapterPool(
                key.Value.LenderTenantId,
                key.Value.AdapterPoolRtId,
                mirror.Name,
                mirror.Description,
                new LendingScope((int)mirror.SharingMode, null),
                mirror.MinReplicas ?? 0,
                mirror.MaxReplicas ?? 0,
                (int)mirror.DeploymentState);
        }
    }

    /// <summary>
    ///     Identity of a mirror: the lending tenant and the pool inside it. Case-insensitive on the
    ///     tenant id, matching every other tenant comparison in this service.
    /// </summary>
    private readonly record struct MirrorKey(string LenderTenantId, string AdapterPoolRtId)
    {
        public static MirrorKey Of(LendableAdapterPool pool)
        {
            return new MirrorKey(pool.LenderTenantId, pool.AdapterPoolRtId);
        }

        public static MirrorKey? Of(RtLentAdapterPool mirror)
        {
            var lentFrom = LentFromReference.FromMirror(mirror);
            return lentFrom is null ? null : new MirrorKey(lentFrom.LenderTenantId, lentFrom.AdapterPoolRtId);
        }

        public bool Equals(MirrorKey other)
        {
            return string.Equals(LenderTenantId, other.LenderTenantId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(AdapterPoolRtId, other.AdapterPoolRtId, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(LenderTenantId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(AdapterPoolRtId));
        }
    }
}
