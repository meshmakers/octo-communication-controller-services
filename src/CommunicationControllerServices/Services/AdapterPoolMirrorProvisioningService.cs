using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IAdapterPoolMirrorProvisioningService" />
internal sealed class AdapterPoolMirrorProvisioningService(
    ILogger<AdapterPoolMirrorProvisioningService> logger,
    ICommunicationRepository communicationRepository,
    ITenantLendingScopeResolver lendingScopeResolver,
    ICommunicationEventService communicationEventService)
    : IAdapterPoolMirrorProvisioningService
{
    /// <summary>
    ///     The two attribute values AB#5271 orphaned on a borrowing <c>RtAdapter</c>, as the runtime
    ///     engine's attribute dictionary keys them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b><c>LentFromPoolRtId</c>, not <c>LentFromAdapterPoolRtId</c>.</b> The
    ///         <c>Pool</c> → <c>AdapterPool</c> rename happened later in the same 4.x line, so the
    ///         stored data carries the older spelling — verified in MongoDB on a tenant carried over
    ///         from 4.0.0, where the document holds <c>attributes.lentFromPoolRtId</c>. Using the
    ///         current name would find nothing and the sweep would silently never fire.
    ///     </para>
    ///     <para>
    ///         Both names are PascalCase here because that is the dictionary's own key form: the
    ///         MongoDB attribute serializer camel-cases on write and Pascal-cases every key on read,
    ///         without consulting the CK model — which is precisely why values of attributes the model
    ///         no longer declares are still readable through <c>GetAttributeValueOrDefault</c>.
    ///     </para>
    /// </remarks>
    private const string LegacyLentFromTenantIdAttribute = "LentFromTenantId";

    /// <inheritdoc cref="LegacyLentFromTenantIdAttribute" />
    private const string LegacyLentFromPoolRtIdAttribute = "LentFromPoolRtId";

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

        // The mirrors that exist after this pass, by the pair that identifies them across the tenant
        // boundary. The re-link sweep below needs the rtId of each, which only the upsert reports —
        // and deliberately only of mirrors in `desired`, so a mirror the removal sweep is about to
        // delete can never become the target of a new edge.
        var mirrorRtIds = new Dictionary<MirrorKey, OctoObjectId>();

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

                mirrorRtIds[MirrorKey.Of(pool)] = upsert.RtId;
            }
            catch (Exception e)
            {
                logger.LogError(e,
                    "[{BorrowerTenantId}] Failed to mirror adapter pool {AdapterPoolRtId} of tenant '{LenderTenantId}'",
                    borrowerTenantId, pool.AdapterPoolRtId, pool.LenderTenantId);
            }
        }

        // 🔴 Between the upsert and the removal sweep, and in that order on purpose: the mirror an
        // adapter is re-linked to has to exist already, and a mirror that is about to be removed must
        // never gain an edge.
        var relinked = await RelinkLeasedAdaptersAsync(borrowerTenantId, mirrorRtIds, cancellationToken);

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

        var result = new AdapterPoolMirrorSyncResult(1, created, removed, lendersUnreadable, relinked);

        if (!result.IsNoOp || lendersUnreadable > 0)
        {
            logger.LogInformation(
                "[{BorrowerTenantId}] Lent adapter pool mirrors reconciled: {Created} present, {Removed} removed, " +
                "{Relinked} adapter(s) re-linked, {LendersUnreadable} candidate lender(s) unreadable",
                borrowerTenantId, created, removed, relinked, lendersUnreadable);
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
    ///     Restores the <c>LentFrom</c> edge of every leased adapter that AB#5271 left without one,
    ///     from the two attribute values the model no longer declares (AB#5349).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Why this exists at all. AB#5271 replaced <c>Adapter.LentFromTenantId</c> /
    ///         <c>.LentFromAdapterPoolRtId</c> with the <c>LentFrom</c> association, and
    ///         <c>migration-meta.yaml</c> records why no CK migration can do that conversion: the
    ///         association's target is a mirror this service provisions at <b>run time</b>, so its
    ///         RtId does not exist while a migration runs. That note says the relationship is
    ///         re-established by "the mirror sync plus a re-link of the adapter" — and until AB#5349
    ///         the second half had no implementation. Measured on a local tenant carried over from
    ///         4.0.0: the mirror was correct and DEPLOYED, the adapter's <c>lentFrom</c> had
    ///         <c>totalCount = 0</c>, <c>statusMessage</c> and <c>lastDeploymentError</c> were both
    ///         null, and <c>mirrors/refresh</c> answered <c>isNoOp: true</c> — correctly, because the
    ///         backfill had no opinion about the adapter's edge. The repair was a hand-written GraphQL
    ///         mutation.
    ///     </para>
    ///     <para>
    ///         It is scriptable because the old data is still there: the attribute removal rode the
    ///         engine's post-chain schema-only bridge, which upgrades the schema and does <b>not</b>
    ///         delete attribute values.
    ///     </para>
    ///     <para>
    ///         🔴 <b>Idempotent.</b> An adapter that already has an edge is skipped here and skipped
    ///         again in the repository, which reads the current edge inside its own transaction. A
    ///         second run therefore changes nothing and reports zero.
    ///     </para>
    ///     <para>
    ///         🔴 <b>It never invents an edge.</b> Leftovers absent, blank, or naming a pair no mirror
    ///         carries ⇒ the adapter is left alone. A leased adapter pointing at a pool nobody lent it
    ///         is worse than one pointing at nothing: the latter is already a loud, named refusal at
    ///         the enqueue (AB#5329, <c>LeaseRefusalReason.BorrowerNamesNoPool</c>), while the former
    ///         would send lease requests to a lender that never agreed.
    ///     </para>
    ///     <para>
    ///         🔴 <b>The mirror is still never authoritative</b> (concept §4). Nothing here is a
    ///         lending <i>decision</i>: the mirrors this matches against were just decided, one by
    ///         one, by <c>MayLendAsync</c> against each <b>lender's own</b> pool — that is what
    ///         <paramref name="mirrorRtIds" /> holds and why it is passed in rather than re-read. All
    ///         this restores is a pointer the borrower already had, to a pool it is already entitled
    ///         to; every later lease still re-reads the lender's sharing mode and allow-list.
    ///     </para>
    ///     <para>
    ///         🔴 <b>The leftover values are not deleted.</b> They are harmless dead data — nothing
    ///         reads them but this sweep — and they are the only remaining evidence of what the
    ///         adapter borrowed. Deleting them would make this repair unrepeatable, so it could only
    ///         ever be right <i>after</i> the edge exists, and even then it would buy nothing but a
    ///         write: the next reconcile finds the edge and never looks at them again. If they are
    ///         ever to go, it should be one deliberate, reported cleanup pass over an estate where
    ///         every borrower is provably re-linked — not a side effect of a reconcile that runs on
    ///         every tenant load.
    ///     </para>
    ///     <para>
    ///         <b>Cost.</b> Zero extra reads for a tenant that borrows nothing — no mirror means no
    ///         possible match, so the sweep returns before touching the database. A tenant that does
    ///         hold mirrors pays one <c>GetWorkloadsAsync</c>, which is a single query per tenant and
    ///         the same read the lease-topology sweep already makes every 60 s; only a tenant that
    ///         also has at least one leased adapter pays a second, batched edge read. Nothing here is
    ///         per adapter unless there is an adapter to repair.
    ///     </para>
    ///     <para>
    ///         Best effort throughout, matching the surrounding passes: a failure for one adapter is
    ///         logged and the rest of the sweep continues, and a failure of either read gives up on
    ///         the re-link while leaving the mirror reconcile — and tenant startup — untouched.
    ///     </para>
    /// </remarks>
    private async Task<int> RelinkLeasedAdaptersAsync(string borrowerTenantId,
        IReadOnlyDictionary<MirrorKey, OctoObjectId> mirrorRtIds, CancellationToken cancellationToken)
    {
        if (mirrorRtIds.Count == 0)
        {
            // Nothing to match against, so nothing this sweep could do — and this is the common case
            // for the overwhelming majority of tenants, which borrow no pool at all.
            return 0;
        }

        IReadOnlyCollection<RtDeployableWorkload> workloads;
        try
        {
            workloads = await communicationRepository.GetWorkloadsAsync(borrowerTenantId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "[{BorrowerTenantId}] Could not read the tenant's workloads; leased adapters left over from the " +
                "pre-4.0.1 model were not re-linked (AB#5349)",
                borrowerTenantId);
            return 0;
        }

        var leasedAdapters = workloads
            .OfType<RtAdapter>()
            .Where(a => a.LifecycleMode == RtLifecycleModeEnum.Leased)
            .ToList();

        if (leasedAdapters.Count == 0)
        {
            return 0;
        }

        IReadOnlyDictionary<OctoObjectId, RtLentAdapterPool> existingEdges;
        try
        {
            // Batched, like the lease-topology sweep: one query for the whole set rather than one per
            // borrowing adapter.
            existingEdges = await communicationRepository.GetLentAdapterPoolsForAdaptersAsync(borrowerTenantId,
                leasedAdapters.Select(a => a.RtId).ToList());
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "[{BorrowerTenantId}] Could not resolve which leased adapters already have a LentFrom edge; none " +
                "were re-linked (AB#5349)",
                borrowerTenantId);
            return 0;
        }

        var relinked = 0;

        foreach (var adapter in leasedAdapters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existingEdges.ContainsKey(adapter.RtId))
            {
                // Already linked — the idempotent case, and the one every run after the first takes.
                continue;
            }

            // 🔴 Read through GetAttributeValueOrDefault, never a generated property: the CK model no
            // longer declares either attribute, so there is no property to read. The engine's attribute
            // dictionary is model-agnostic on both write and read — it Pascal-cases whatever keys the
            // BSON document carries — which is exactly what makes the orphaned values reachable.
            var lenderTenantId = adapter.GetAttributeValueOrDefault(LegacyLentFromTenantIdAttribute) as string;
            var lenderPoolRtId = adapter.GetAttributeValueOrDefault(LegacyLentFromPoolRtIdAttribute) as string;

            if (string.IsNullOrWhiteSpace(lenderTenantId) || string.IsNullOrWhiteSpace(lenderPoolRtId))
            {
                // No leftovers, or only half of them. Half a reference is not a partial reference but a
                // broken one (concept §7.2), so neither half is guessed at.
                continue;
            }

            if (!mirrorRtIds.TryGetValue(new MirrorKey(lenderTenantId, lenderPoolRtId), out var mirrorRtId))
            {
                // The leftovers name a pool that does not lend here — narrowed scope, deleted pool,
                // re-parented tenant, or a value that was wrong to begin with. Leaving the adapter
                // unlinked keeps AB#5329's named refusal; linking it anyway would fabricate a lending
                // relationship nobody granted.
                continue;
            }

            try
            {
                if (!await communicationRepository.TryLinkAdapterToLentAdapterPoolMirrorAsync(borrowerTenantId,
                        adapter.RtId, mirrorRtId))
                {
                    // Another run got there between the batched read and this write.
                    continue;
                }

                relinked++;

                // Once per adapter for the life of that adapter, so it is worth an Information line and
                // an entry in the tenant's own event log: the operator who sees a borrower start working
                // again should be able to find out why without reading pod logs.
                logger.LogInformation(
                    "[{BorrowerTenantId}] Re-linked leased adapter '{AdapterName}' ({AdapterRtId}) to lent adapter " +
                    "pool mirror '{MirrorRtId}' of pool '{LenderAdapterPoolRtId}' in tenant '{LenderTenantId}', from " +
                    "the attribute values AB#5271 left behind (AB#5349)",
                    borrowerTenantId, adapter.Name, adapter.RtId, mirrorRtId, lenderPoolRtId, lenderTenantId);

                await communicationEventService.StoreInformationEventAsync(borrowerTenantId,
                    $"Leased adapter '{adapter.Name}' was re-linked to the mirror of adapter pool " +
                    $"'{lenderPoolRtId}' in tenant '{lenderTenantId}' (AB#5349): the LentFrom association was " +
                    "restored from the LentFromTenantId/LentFromPoolRtId values that AB#5271 orphaned.",
                    new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // One adapter must not cost the others their repair, and must not fail tenant startup.
                logger.LogError(e,
                    "[{BorrowerTenantId}] Failed to re-link leased adapter '{AdapterName}' ({AdapterRtId}) to the " +
                    "mirror of pool '{LenderAdapterPoolRtId}' in tenant '{LenderTenantId}' (AB#5349)",
                    borrowerTenantId, adapter.Name, adapter.RtId, lenderPoolRtId, lenderTenantId);
            }
        }

        return relinked;
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
