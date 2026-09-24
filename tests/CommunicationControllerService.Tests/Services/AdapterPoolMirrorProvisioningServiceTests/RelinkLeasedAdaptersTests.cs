using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.
    AdapterPoolMirrorProvisioningServiceTests;

/// <summary>
///     The decision rules of the AB#5349 re-link: which leased adapter gets its <c>LentFrom</c> edge
///     restored from the attribute values AB#5271 orphaned, and — far more importantly — which one
///     must be left exactly as it is.
/// </summary>
/// <remarks>
///     <para>
///         The measurement behind these rules: on a tenant carried over from CK 4.0.0 the mirror was
///         correct and DEPLOYED, the borrowing adapter's <c>lentFrom</c> had <c>totalCount = 0</c>, and
///         <c>mirrors/refresh</c> answered <c>isNoOp: true</c> — correctly, because the mirror sweep
///         had no opinion about the adapter's edge. <c>migration-meta.yaml</c> says the relationship is
///         re-established by "the mirror sync plus a re-link of the adapter"; the second half is what
///         this suite pins.
///     </para>
///     <para>
///         🔴 The one thing a mock cannot show is the crux — that the orphaned values are readable at
///         all once the CK model stops declaring them. That is
///         <c>LentAdapterPoolRelinkTests</c> in the integration suite, against real MongoDB.
///     </para>
/// </remarks>
internal sealed class RelinkLeasedAdaptersTests : AdapterPoolMirrorProvisioningServiceTestsBase
{
    [Test]
    public async Task ALeasedAdapterWithLeftoversMatchingAMirror_IsRelinked()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, poolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(1);
        await Assert.That(EdgeOf(adapter)).IsEqualTo(mirror.RtId);
        await CommunicationRepository.Received(1)
            .TryLinkAdapterToLentAdapterPoolMirrorAsync(BorrowerTenantId, adapter.RtId, mirror.RtId);
    }

    /// <summary>
    ///     🔴 A repair that reports nothing is how this gap stayed invisible: the refresh endpoint
    ///     answered <c>isNoOp: true</c> while a borrower sat unlinked. A run that re-links something
    ///     is not a no-op, even when every mirror was already correct.
    /// </summary>
    [Test]
    public async Task ARunThatRelinks_IsNotANoOp()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        ArrangeLeasedAdapter(LenderTenantId, poolRtId);

        CommunicationRepository
            .UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId, Arg.Any<LendableAdapterPool>())
            .Returns(_ => LentAdapterPoolMirrorUpsert.Unchanged(mirror.RtId));

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsCreatedOrUpdated).IsEqualTo(0);
        await Assert.That(result.MirrorsRemoved).IsEqualTo(0);
        await Assert.That(result.AdaptersRelinked).IsEqualTo(1);
        await Assert.That(result.IsNoOp).IsFalse();
    }

    /// <summary>
    ///     The re-link is a one-time repair per adapter, and it has to be visible in the tenant's own
    ///     event log rather than only in the pod log: the operator who sees a borrower start working
    ///     again should be able to find out why.
    /// </summary>
    [Test]
    public async Task ARelink_IsWrittenToTheTenantsEventLog()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        ArrangeLeasedAdapter(LenderTenantId, poolRtId, name: "AB4924 Leased Adapter");

        await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await CommunicationEventService.Received(1).StoreInformationEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains("AB4924 Leased Adapter") && m.Contains(poolRtId)),
            Arg.Any<RtEntityId?>());
    }

    /// <summary>
    ///     🔴 Idempotent. An adapter that already has an edge is skipped, so the repository is never
    ///     even asked — re-pointing a borrower is an operator decision, never a reconcile's.
    /// </summary>
    [Test]
    public async Task AnAdapterThatAlreadyHasAnEdge_IsLeftAlone()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        ArrangeLeasedAdapter(LenderTenantId, poolRtId, existingEdgeTo: mirror);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive().TryLinkAdapterToLentAdapterPoolMirrorAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<OctoObjectId>());
    }

    /// <summary>
    ///     🔴 An adapter whose edge points somewhere else is also left alone. The sweep fills a gap; it
    ///     does not re-decide a pointer somebody set deliberately, and the leftovers are by definition
    ///     the older statement of the two.
    /// </summary>
    [Test]
    public async Task AnAdapterPointingAtADifferentMirror_IsNotRepointed()
    {
        var poolRtId = NewPoolRtId().ToString();
        var otherPoolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangePool(LenderTenantId, otherPoolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var otherMirror = ArrangeExistingMirror(LenderTenantId, otherPoolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, poolRtId, existingEdgeTo: otherMirror);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsEqualTo(otherMirror.RtId);
    }

    /// <summary>
    ///     Running the same reconcile twice must change nothing the second time — the property the
    ///     startup re-check, the deploy fan-out and the manual refresh all depend on.
    /// </summary>
    [Test]
    public async Task RunningTwice_RelinksOnceAndThenReportsNothing()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, poolRtId);

        var first = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);
        var second = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(first.AdaptersRelinked).IsEqualTo(1);
        await Assert.That(second.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsEqualTo(mirror.RtId);
        await CommunicationRepository.Received(1).TryLinkAdapterToLentAdapterPoolMirrorAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<OctoObjectId>());
    }

    /// <summary>
    ///     🔴 No leftovers, no edge. An adapter configured as <c>Leased</c> by hand and never given a
    ///     pool has nothing to restore, and guessing one would be inventing a lending relationship.
    /// </summary>
    [Test]
    public async Task ALeasedAdapterWithNoLeftovers_IsLeftAlone()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(null, null);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
    }

    /// <summary>
    ///     Half a reference is not a partial reference but a broken one (concept §7.2), so neither half
    ///     is guessed at — not even when exactly one mirror exists and it would be the obvious match.
    /// </summary>
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task ALeasedAdapterWithOnlyHalfTheLeftovers_IsLeftAlone(bool hasTenantId, bool hasPoolRtId)
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(hasTenantId ? LenderTenantId : null, hasPoolRtId ? poolRtId : null);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
    }

    /// <summary>
    ///     Blank is not a value. A whitespace leftover reads as "configured" to a null check and as
    ///     nothing at all to a lender.
    /// </summary>
    [Test]
    public async Task ALeasedAdapterWithBlankLeftovers_IsLeftAlone()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter("   ", poolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
    }

    /// <summary>
    ///     🔴 The rule the whole feature stands on: leftovers that match no mirror change nothing.
    /// </summary>
    /// <remarks>
    ///     A leased adapter pointing at a pool nobody lent it is worse than one pointing at nothing —
    ///     the latter is already a loud, named refusal at the enqueue (AB#5329,
    ///     <c>LeaseRefusalReason.BorrowerNamesNoPool</c>), while the former would send lease requests to
    ///     a lender that never agreed. This is also what makes the sweep incapable of a lending
    ///     decision: it can only ever point at a mirror that <c>MayLendAsync</c> has just approved
    ///     against the lender's own pool.
    /// </remarks>
    [Test]
    public async Task LeftoversThatMatchNoMirror_ChangeNothing()
    {
        var lentPoolRtId = NewPoolRtId().ToString();
        var neverLentPoolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, lentPoolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, lentPoolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, neverLentPoolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
        await CommunicationRepository.DidNotReceive().TryLinkAdapterToLentAdapterPoolMirrorAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<OctoObjectId>());
    }

    /// <summary>
    ///     The same pool RtId in the wrong tenant is not a match either. Both halves of the pair
    ///     identify a pool across the tenant boundary, and an RtId is only unique inside one database.
    /// </summary>
    [Test]
    public async Task LeftoversNamingTheRightPoolInTheWrongTenant_ChangeNothing()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(OtherLenderTenantId, poolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
    }

    /// <summary>
    ///     🔴 A mirror the removal sweep is about to delete must never gain an edge. The sweep matches
    ///     only against mirrors that are in the desired set, which is why it runs between the upsert
    ///     and the removal rather than after both.
    /// </summary>
    [Test]
    public async Task LeftoversNamingAPoolWhoseMirrorIsAboutToBeRemoved_ChangeNothing()
    {
        var revokedPoolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        // The pool still exists in the lender but no longer lends here.
        ArrangePool(LenderTenantId, revokedPoolRtId, lends: false);
        var doomedMirror = ArrangeExistingMirror(LenderTenantId, revokedPoolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, revokedPoolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsRemoved).IsEqualTo(1);
        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
        await CommunicationRepository.Received(1)
            .RemoveLentAdapterPoolMirrorAsync(BorrowerTenantId, doomedMirror.RtId);
    }

    /// <summary>
    ///     With several mirrors in play, each adapter is linked to the one its own leftovers name —
    ///     including across two different lenders, where only the pair distinguishes them.
    /// </summary>
    [Test]
    public async Task WithSeveralMirrors_EachAdapterIsLinkedToTheOneItsLeftoversName()
    {
        var firstPoolRtId = NewPoolRtId().ToString();
        var secondPoolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId, OtherLenderTenantId);
        ArrangePool(LenderTenantId, firstPoolRtId, lends: true);
        ArrangePool(OtherLenderTenantId, secondPoolRtId, lends: true);
        var firstMirror = ArrangeExistingMirror(LenderTenantId, firstPoolRtId);
        var secondMirror = ArrangeExistingMirror(OtherLenderTenantId, secondPoolRtId);

        var firstAdapter = ArrangeLeasedAdapter(LenderTenantId, firstPoolRtId);
        var secondAdapter = ArrangeLeasedAdapter(OtherLenderTenantId, secondPoolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(2);
        await Assert.That(EdgeOf(firstAdapter)).IsEqualTo(firstMirror.RtId);
        await Assert.That(EdgeOf(secondAdapter)).IsEqualTo(secondMirror.RtId);
    }

    /// <summary>
    ///     🔴 Only a <c>Leased</c> adapter is touched. An <c>AlwaysOn</c> or <c>OnDemand</c> adapter that
    ///     still carries the leftovers was once a borrower and is not one now; giving it an edge would
    ///     re-declare it as one behind the operator's back.
    /// </summary>
    [Test]
    [Arguments(RtLifecycleModeEnum.AlwaysOn)]
    [Arguments(RtLifecycleModeEnum.OnDemand)]
    [Arguments(RtLifecycleModeEnum.Auto)]
    public async Task AnAdapterThatIsNotLeased_IsLeftAlone(RtLifecycleModeEnum lifecycleMode)
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, poolRtId, lifecycleMode);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await Assert.That(EdgeOf(adapter)).IsNull();
    }

    /// <summary>
    ///     🔴 The leftover values are never deleted. They are the only remaining evidence of what the
    ///     adapter borrowed, and nothing but this sweep reads them.
    /// </summary>
    [Test]
    public async Task ARelink_DoesNotDeleteTheLeftoverValues()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        var adapter = ArrangeLeasedAdapter(LenderTenantId, poolRtId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(1);
        await Assert.That(adapter.GetAttributeValueOrDefault("LentFromTenantId")).IsEqualTo(LenderTenantId);
        await Assert.That(adapter.GetAttributeValueOrDefault("LentFromPoolRtId")).IsEqualTo(poolRtId);
    }

    /// <summary>
    ///     Best effort: one adapter that cannot be linked must not cost the others their repair, and
    ///     must not fail the reconcile — which hangs off tenant startup.
    /// </summary>
    [Test]
    public async Task OneAdapterThatFailsToLink_DoesNotStopTheOthers()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        var failing = ArrangeLeasedAdapter(LenderTenantId, poolRtId);
        var healthy = ArrangeLeasedAdapter(LenderTenantId, poolRtId);

        CommunicationRepository
            .TryLinkAdapterToLentAdapterPoolMirrorAsync(BorrowerTenantId, failing.RtId, Arg.Any<OctoObjectId>())
            .ThrowsAsync(new InvalidOperationException("edge write failed"));

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(1);
        await Assert.That(EdgeOf(healthy)).IsEqualTo(mirror.RtId);
    }

    /// <summary>
    ///     An unreadable workload set gives up on the re-link only. The mirrors were already
    ///     reconciled at that point and must stay reconciled, and tenant startup must still complete.
    /// </summary>
    [Test]
    public async Task WorkloadsThatCannotBeRead_DoNotFailTheReconcile()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);

        CommunicationRepository.GetWorkloadsAsync(BorrowerTenantId)
            .ThrowsAsync(new InvalidOperationException("tenant mid-update"));

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsCreatedOrUpdated).IsEqualTo(1);
        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
    }

    /// <summary>
    ///     🔴 The cheap case, and it is the overwhelming majority of tenants: no mirror means no
    ///     possible match, so the sweep must return before it reads anything. The reconcile runs on
    ///     every tenant load, so a workload query for every tenant on the estate would be a cost this
    ///     repair has no right to.
    /// </summary>
    [Test]
    public async Task ATenantThatBorrowsNothing_PaysNoExtraRead()
    {
        ArrangeCandidateLenders(LenderTenantId);
        // A leased adapter with leftovers is present, and still nothing is read: without a mirror there
        // is nothing it could be matched against.
        ArrangeLeasedAdapter(LenderTenantId, NewPoolRtId().ToString());

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive().GetWorkloadsAsync(Arg.Any<string>());
        await CommunicationRepository.DidNotReceive().GetLentAdapterPoolsForAdaptersAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<OctoObjectId>>());
    }

    /// <summary>
    ///     A tenant that holds mirrors but has no leased adapter pays the one workload read and stops
    ///     there — the batched edge read is only worth making when there is a borrower to resolve.
    /// </summary>
    [Test]
    public async Task ATenantWithMirrorsButNoLeasedAdapter_DoesNotResolveEdges()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);
        ArrangeLeasedAdapter(LenderTenantId, poolRtId, RtLifecycleModeEnum.AlwaysOn);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.AdaptersRelinked).IsEqualTo(0);
        await CommunicationRepository.Received(1).GetWorkloadsAsync(BorrowerTenantId);
        await CommunicationRepository.DidNotReceive().GetLentAdapterPoolsForAdaptersAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<OctoObjectId>>());
    }
}
