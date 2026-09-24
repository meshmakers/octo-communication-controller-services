using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.
    AdapterPoolMirrorProvisioningServiceTests;

/// <summary>
///     Shared arrangement for the AB#5271 mirror sync: one borrower, one candidate lender, and the
///     mirrors the borrower currently stores.
/// </summary>
internal abstract class AdapterPoolMirrorProvisioningServiceTestsBase
{
    protected const string BorrowerTenantId = "borrower";
    protected const string LenderTenantId = "lender";
    protected const string OtherLenderTenantId = "other-lender";

    protected readonly ICommunicationRepository CommunicationRepository =
        Substitute.For<ICommunicationRepository>();

    protected readonly ITenantLendingScopeResolver LendingScopeResolver =
        Substitute.For<ITenantLendingScopeResolver>();

    protected readonly ICommunicationEventService CommunicationEventService =
        Substitute.For<ICommunicationEventService>();

    protected readonly AdapterPoolMirrorProvisioningService Service;

    /// <summary>What the borrower currently stores; mutated by the arrangements below.</summary>
    private readonly List<RtLentAdapterPool> _existingMirrors = [];

    /// <summary>The borrower's own workloads, for the AB#5349 re-link sweep.</summary>
    private readonly List<RtDeployableWorkload> _workloads = [];

    /// <summary>Which of them already have a <c>LentFrom</c> edge, and to what.</summary>
    private readonly Dictionary<OctoObjectId, RtLentAdapterPool> _existingEdges = [];

    protected AdapterPoolMirrorProvisioningServiceTestsBase()
    {
        Service = new AdapterPoolMirrorProvisioningService(
            NullLogger<AdapterPoolMirrorProvisioningService>.Instance,
            CommunicationRepository,
            LendingScopeResolver,
            CommunicationEventService);

        // AB#5349: the re-link sweep reads the tenant's workloads. Default to none, so every test
        // that says nothing about adapters behaves exactly as it did before the sweep existed.
        CommunicationRepository.GetWorkloadsAsync(Arg.Any<string>())
            .Returns(_ => (IReadOnlyCollection<RtDeployableWorkload>)_workloads.ToList());
        CommunicationRepository
            .GetLentAdapterPoolsForAdaptersAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<OctoObjectId>>())
            .Returns(_ => (IReadOnlyDictionary<OctoObjectId, RtLentAdapterPool>)
                new Dictionary<OctoObjectId, RtLentAdapterPool>(_existingEdges));

        // The repository re-checks the edge inside its own transaction and answers false when one is
        // already there; the substitute stands in for "the gap was filled".
        CommunicationRepository
            .TryLinkAdapterToLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<OctoObjectId>())
            .Returns(call =>
            {
                var adapterRtId = call.ArgAt<OctoObjectId>(1);
                var mirrorRtId = call.ArgAt<OctoObjectId>(2);
                if (_existingEdges.ContainsKey(adapterRtId))
                {
                    return false;
                }

                // Recording the edge is what makes a SECOND run of the same arrangement idempotent,
                // which is one of the properties under test rather than a convenience.
                _existingEdges[adapterRtId] = _existingMirrors.FirstOrDefault(m => m.RtId == mirrorRtId)
                                              ?? RtEntityCreator.CreateLentAdapterPool("linked", "linked",
                                                  mirrorRtId.ToString());
                return true;
            });

        CommunicationRepository.GetLentAdapterPoolMirrorsAsync(BorrowerTenantId)
            .Returns(_ => (IReadOnlyCollection<RtLentAdapterPool>)_existingMirrors.ToList());

        // Default: an upsert reports that it wrote. The "already correct, nothing written" case is
        // the repository's own decision (it compares the stored mirror against the pool), so a
        // substitute has to state which of the two it is standing in for — and the counters this
        // service returns are read by the Studio's Refresh, so getting it wrong here would hide
        // exactly the regression that made a no-op report three provisioned pools.
        //
        // AB#5349: the rtId it reports is what the re-link sweep matches an adapter's leftovers
        // against, so it has to be STABLE per (lender, pool) and has to be the rtId of the mirror the
        // test arranged — a fresh id per call would make every re-link target an entity no assertion
        // can name.
        CommunicationRepository
            .UpsertLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<LendableAdapterPool>())
            .Returns(call =>
            {
                var pool = call.ArgAt<LendableAdapterPool>(1);
                var mirror = _existingMirrors.FirstOrDefault(m =>
                    string.Equals(m.Lender?.LenderTenantId, pool.LenderTenantId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(m.Lender?.LenderAdapterPoolRtId, pool.AdapterPoolRtId,
                        StringComparison.OrdinalIgnoreCase));
                if (mirror is null)
                {
                    // The pool lends here but the borrower had no mirror yet: the upsert creates one,
                    // so from this point on the test's stored set holds it too.
                    mirror = ArrangeExistingMirror(pool.LenderTenantId, pool.AdapterPoolRtId);
                }

                return LentAdapterPoolMirrorUpsert.Created(mirror.RtId);
            });

        // Default: nothing lends anything anywhere. Each test declares only what it needs.
        ArrangeCandidateLenders();
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(Arg.Any<string>())
            .Returns([]);
        LendingScopeResolver
            .MayLendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LendingScope>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
    }

    protected void ArrangeCandidateLenders(params string[] lenderTenantIds)
    {
        LendingScopeResolver.ResolveCandidateLenderTenantsAsync(BorrowerTenantId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)lenderTenantIds.ToList());
    }

    /// <summary>
    ///     Declares one pool in <paramref name="lenderTenantId" /> and whether it lends to the
    ///     borrower.
    /// </summary>
    protected LendableAdapterPool ArrangePool(string lenderTenantId, string adapterPoolRtId, bool lends,
        string name = "a-pool", int sharingMode = LendingScope.Descendants)
    {
        var pool = new LendableAdapterPool(lenderTenantId, adapterPoolRtId, name, null,
            new LendingScope(sharingMode, null), 1, 3, 0);

        var existing = CommunicationRepository.GetAdapterPoolsForMirroringAsync(lenderTenantId)
            .Result.ToList();
        existing.Add(pool);
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(lenderTenantId)
            .Returns((IReadOnlyCollection<LendableAdapterPool>)existing);

        LendingScopeResolver
            .MayLendAsync(lenderTenantId, BorrowerTenantId, pool.Scope, Arg.Any<CancellationToken>())
            .Returns(lends);

        return pool;
    }

    /// <summary>Declares a mirror the borrower already stores.</summary>
    protected RtLentAdapterPool ArrangeExistingMirror(string lenderTenantId, string adapterPoolRtId)
    {
        var mirror = RtEntityCreator.CreateLentAdapterPool(lenderTenantId, adapterPoolRtId);
        _existingMirrors.Add(mirror);
        return mirror;
    }

    /// <summary>Declares a stored mirror whose <c>Lender</c> record says nothing usable.</summary>
    protected RtLentAdapterPool ArrangeBrokenExistingMirror()
    {
        var mirror = RtEntityCreator.CreateLentAdapterPool(LenderTenantId, "irrelevant");
        // The controller writes the record as one unit, so this shape only arises from a hand edit
        // in the borrower's own database — which is exactly why it has to be handled.
        mirror.Lender = new RtLenderReferenceRecord
        {
            LenderTenantId = string.Empty,
            LenderAdapterPoolRtId = string.Empty
        };
        _existingMirrors.Add(mirror);
        return mirror;
    }

    /// <summary>
    ///     A leased adapter of the borrower carrying the two attribute values AB#5271 orphaned
    ///     (AB#5349), and no <c>LentFrom</c> edge.
    /// </summary>
    /// <remarks>
    ///     🔴 The leftovers are written with <c>SetAttributeRawValue</c> under the keys the engine's
    ///     attribute dictionary uses, <b>not</b> through a generated property: the CK model no longer
    ///     declares either attribute, so there is no property. And the pool key is
    ///     <c>LentFromPoolRtId</c>, not <c>LentFromAdapterPoolRtId</c> — the stored data predates the
    ///     <c>Pool</c> → <c>AdapterPool</c> rename, verified in MongoDB on a tenant carried over from
    ///     4.0.0.
    /// </remarks>
    protected RtAdapter ArrangeLeasedAdapter(string? leftoverLenderTenantId, string? leftoverPoolRtId,
        RtLifecycleModeEnum lifecycleMode = RtLifecycleModeEnum.Leased, RtLentAdapterPool? existingEdgeTo = null,
        string? name = null)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = name ?? $"borrower-{adapter.RtId}";
        adapter.LifecycleMode = lifecycleMode;

        if (leftoverLenderTenantId is not null)
        {
            adapter.SetAttributeRawValue("LentFromTenantId", leftoverLenderTenantId);
        }

        if (leftoverPoolRtId is not null)
        {
            adapter.SetAttributeRawValue("LentFromPoolRtId", leftoverPoolRtId);
        }

        _workloads.Add(adapter);

        if (existingEdgeTo is not null)
        {
            _existingEdges[adapter.RtId] = existingEdgeTo;
        }

        return adapter;
    }

    /// <summary>Whether the adapter ended the run with a <c>LentFrom</c> edge, and to which mirror.</summary>
    protected OctoObjectId? EdgeOf(RtAdapter adapter)
    {
        return _existingEdges.TryGetValue(adapter.RtId, out var mirror) ? mirror.RtId : null;
    }

    protected static OctoObjectId NewPoolRtId()
    {
        return OctoObjectId.GenerateNewId();
    }
}
