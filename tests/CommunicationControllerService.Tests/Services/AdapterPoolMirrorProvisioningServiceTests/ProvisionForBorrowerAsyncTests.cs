using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.
    AdapterPoolMirrorProvisioningServiceTests;

/// <summary>
///     The borrower-side reconciliation (AB#5271): which mirrors exist after a run, and — more
///     importantly — which ones the run must NOT delete.
/// </summary>
internal sealed class ProvisionForBorrowerAsyncTests : AdapterPoolMirrorProvisioningServiceTestsBase
{
    [Test]
    public async Task APoolThatLendsHere_IsMirrored()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsCreatedOrUpdated).IsEqualTo(1);
        await Assert.That(result.MirrorsRemoved).IsEqualTo(0);
        await CommunicationRepository.Received(1).UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId,
            Arg.Is<LendableAdapterPool>(p => p.AdapterPoolRtId == poolRtId));
    }

    /// <summary>
    ///     The lender's own scope decides. A pool that exists in a candidate lender but does not lend
    ///     here must not become visible to the borrower — this is the whole authority rule of the
    ///     feature, and a mirror is the borrower-visible half of it.
    /// </summary>
    [Test]
    public async Task APoolOfACandidateLenderThatDoesNotLendHere_IsNotMirrored()
    {
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, NewPoolRtId().ToString(), lends: false);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsCreatedOrUpdated).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .UpsertLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<LendableAdapterPool>());
    }

    [Test]
    public async Task AMirrorOfAPoolThatNoLongerLendsHere_IsRemoved()
    {
        var poolRtId = NewPoolRtId().ToString();
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: false);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsRemoved).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .RemoveLentAdapterPoolMirrorAsync(BorrowerTenantId, mirror.RtId);
    }

    [Test]
    public async Task AMirrorOfAPoolThatIsGone_IsRemoved()
    {
        var mirror = ArrangeExistingMirror(LenderTenantId, NewPoolRtId().ToString());
        ArrangeCandidateLenders(LenderTenantId);
        // The lender still exists and still answers — it simply has no pools any more.

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsRemoved).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .RemoveLentAdapterPoolMirrorAsync(BorrowerTenantId, mirror.RtId);
    }

    /// <summary>
    ///     🔴 Revocation. A lender that has dropped out of the candidate set — the borrower was
    ///     re-parented, or the tree changed — is no longer returned by the tree walk, so a run driven
    ///     by candidates alone would never look at its mirrors again and they would stay forever,
    ///     naming a pool the borrower may no longer use. This fails if the stored mirrors stop being
    ///     part of the set of lenders a run walks.
    /// </summary>
    [Test]
    public async Task AMirrorOfALenderThatIsNoLongerACandidate_IsStillRemoved()
    {
        var mirror = ArrangeExistingMirror(OtherLenderTenantId, NewPoolRtId().ToString());
        ArrangeCandidateLenders(LenderTenantId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsRemoved).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .RemoveLentAdapterPoolMirrorAsync(BorrowerTenantId, mirror.RtId);
    }

    /// <summary>
    ///     🔴 An unreadable lender is unknown, never empty. With the association leading, deleting a
    ///     borrower's mirrors because a lender was mid-update breaks every adapter borrowing from it
    ///     — an outage caused by a transient read failure.
    /// </summary>
    [Test]
    public async Task AnUnreadableLender_KeepsTheMirrorsItAlreadyHas()
    {
        var mirror = ArrangeExistingMirror(LenderTenantId, NewPoolRtId().ToString());
        ArrangeCandidateLenders(LenderTenantId);
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(LenderTenantId)
            .ThrowsAsync(new InvalidOperationException("tenant is being updated"));

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.LendersUnreadable).IsEqualTo(1);
        await Assert.That(result.MirrorsRemoved).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .RemoveLentAdapterPoolMirrorAsync(BorrowerTenantId, mirror.RtId);
    }

    /// <summary>
    ///     One broken lender must not cost the borrower its mirrors of every other lender.
    /// </summary>
    [Test]
    public async Task AnUnreadableLender_DoesNotStopTheOtherLenders()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId, OtherLenderTenantId);
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(LenderTenantId)
            .ThrowsAsync(new InvalidOperationException("tenant is being updated"));
        ArrangePool(OtherLenderTenantId, poolRtId, lends: true);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.LendersUnreadable).IsEqualTo(1);
        await CommunicationRepository.Received(1).UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId,
            Arg.Is<LendableAdapterPool>(p => p.AdapterPoolRtId == poolRtId));
    }

    /// <summary>
    ///     Without the stored set the run cannot decide removals, and a run that only ever adds turns
    ///     a narrowed lending scope into a mirror that never goes away. Doing nothing is the honest
    ///     outcome.
    /// </summary>
    [Test]
    public async Task AnUnreadableBorrower_ChangesNothing()
    {
        CommunicationRepository.GetLentAdapterPoolMirrorsAsync(BorrowerTenantId)
            .ThrowsAsync(new InvalidOperationException("cache is being unloaded"));
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, NewPoolRtId().ToString(), lends: true);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.TenantsReconciled).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .UpsertLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<LendableAdapterPool>());
        await CommunicationRepository.DidNotReceive()
            .RemoveLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    /// <summary>
    ///     Re-running against an unchanged estate must upsert the same set rather than add a second
    ///     copy — the idempotency the startup re-check and the backfill both depend on. The upsert
    ///     itself is keyed on the Lender record (repository level); what is asserted here is that the
    ///     service asks for exactly the same pool both times and deletes nothing in between.
    /// </summary>
    [Test]
    public async Task RunningTwice_MirrorsTheSamePoolAndRemovesNothing()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeExistingMirror(LenderTenantId, poolRtId);

        await Service.ProvisionForBorrowerAsync(BorrowerTenantId);
        var second = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(second.MirrorsRemoved).IsEqualTo(0);
        await CommunicationRepository.Received(2).UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId,
            Arg.Is<LendableAdapterPool>(p => p.AdapterPoolRtId == poolRtId));
        await CommunicationRepository.DidNotReceive()
            .RemoveLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    /// <summary>
    ///     🔴 A reconcile over an unchanged estate must report that it changed nothing.
    /// </summary>
    /// <remarks>
    ///     It runs on every tenant load and every pool deploy, so counting every mirror as work
    ///     makes a no-op indistinguishable from a repair — and the Studio's Refresh then says
    ///     "3 pool(s) available" every time it is pressed. The repository decides this (it compares
    ///     the stored mirror against the pool); what is pinned here is that the service believes it.
    /// </remarks>
    [Test]
    public async Task AMirrorThatIsAlreadyCorrect_IsNotCountedAsWork()
    {
        var poolRtId = NewPoolRtId().ToString();
        ArrangeCandidateLenders(LenderTenantId);
        ArrangePool(LenderTenantId, poolRtId, lends: true);
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);

        CommunicationRepository
            .UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId, Arg.Any<LendableAdapterPool>())
            .Returns(_ => LentAdapterPoolMirrorUpsert.Unchanged(mirror.RtId));

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsCreatedOrUpdated).IsEqualTo(0);
        await Assert.That(result.MirrorsRemoved).IsEqualTo(0);
        await Assert.That(result.IsNoOp).IsTrue();
    }

    /// <summary>
    ///     A mirror whose <c>Lender</c> record says nothing usable cannot serve an adapter —
    ///     <c>LentFromReference</c> refuses it — and cannot be matched against any desired pool.
    ///     Removing it is what lets the next run write a correct one.
    /// </summary>
    [Test]
    public async Task AMirrorWithNoUsableLenderRecord_IsRemoved()
    {
        var broken = ArrangeBrokenExistingMirror();
        ArrangeCandidateLenders(LenderTenantId);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsRemoved).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .RemoveLentAdapterPoolMirrorAsync(BorrowerTenantId, broken.RtId);
    }

    /// <summary>
    ///     🔴 The mirrored SharingMode is display data in the borrower's own database, which the
    ///     borrower can edit. The decision must come from the lender's pool, so a run must ask the
    ///     resolver with the LENDER's scope — never with whatever the stored mirror says.
    /// </summary>
    [Test]
    public async Task TheLendingDecision_UsesTheLendersScopeAndNotTheMirrors()
    {
        var poolRtId = NewPoolRtId().ToString();
        var mirror = ArrangeExistingMirror(LenderTenantId, poolRtId);
        mirror.SharingMode = RtAdapterSharingModeEnum.DescendantsAndSiblings;

        ArrangeCandidateLenders(LenderTenantId);
        // The lender's own pool is NotShared, so nothing lends here whatever the mirror claims.
        ArrangePool(LenderTenantId, poolRtId, lends: false, sharingMode: LendingScope.NotShared);

        var result = await Service.ProvisionForBorrowerAsync(BorrowerTenantId);

        await Assert.That(result.MirrorsRemoved).IsEqualTo(1);
        await LendingScopeResolver.Received().MayLendAsync(LenderTenantId, BorrowerTenantId,
            Arg.Is<LendingScope>(s => s.Mode == LendingScope.NotShared), Arg.Any<CancellationToken>());
    }
}
