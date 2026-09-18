using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.
    AdapterPoolMirrorProvisioningServiceTests;

/// <summary>
///     The lender-side fan-out (AB#5271): a pool changed, so every tenant that could be borrowing
///     from it is reconciled.
/// </summary>
internal sealed class ProvisionForLenderAsyncTests : AdapterPoolMirrorProvisioningServiceTestsBase
{
    private const string SecondBorrowerTenantId = "second-borrower";

    [Test]
    public async Task EveryBorrowerInScope_IsReconciled()
    {
        var poolRtId = NewPoolRtId().ToString();
        var pool = ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeLendableTenants(pool, BorrowerTenantId, SecondBorrowerTenantId);
        ArrangeBorrowerSide(SecondBorrowerTenantId, pool);
        ArrangeCandidateLenders(LenderTenantId);

        var result = await Service.ProvisionForLenderAsync(LenderTenantId);

        await Assert.That(result.TenantsReconciled).IsEqualTo(2);
        await CommunicationRepository.Received(1).UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId,
            Arg.Is<LendableAdapterPool>(p => p.AdapterPoolRtId == poolRtId));
        await CommunicationRepository.Received(1).UpsertLentAdapterPoolMirrorAsync(SecondBorrowerTenantId,
            Arg.Is<LendableAdapterPool>(p => p.AdapterPoolRtId == poolRtId));
    }

    /// <summary>
    ///     A tenant borrowing from two pools of the same lender is one borrower, not two. Reconciling
    ///     it twice would be harmless but wasteful, and the per-borrower pass already covers every
    ///     pool of every lender.
    /// </summary>
    [Test]
    public async Task ATenantInScopeOfTwoPools_IsReconciledOnce()
    {
        var first = ArrangePool(LenderTenantId, NewPoolRtId().ToString(), lends: true, name: "first");
        var second = ArrangePool(LenderTenantId, NewPoolRtId().ToString(), lends: true, name: "second");
        ArrangeLendableTenants(first, BorrowerTenantId);
        ArrangeLendableTenants(second, BorrowerTenantId);
        ArrangeCandidateLenders(LenderTenantId);

        var result = await Service.ProvisionForLenderAsync(LenderTenantId);

        await Assert.That(result.TenantsReconciled).IsEqualTo(1);
        await Assert.That(result.MirrorsCreatedOrUpdated).IsEqualTo(2);
    }

    /// <summary>
    ///     A pool nobody borrows from produces no work at all — the normal case for a private pool,
    ///     and the reason this hook is cheap enough to hang off every pool deploy.
    /// </summary>
    [Test]
    public async Task APoolThatLendsToNobody_ReconcilesNoTenant()
    {
        var pool = ArrangePool(LenderTenantId, NewPoolRtId().ToString(), lends: false,
            sharingMode: LendingScope.NotShared);
        ArrangeLendableTenants(pool);

        var result = await Service.ProvisionForLenderAsync(LenderTenantId);

        await Assert.That(result.TenantsReconciled).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .UpsertLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<LendableAdapterPool>());
    }

    /// <summary>
    ///     One failing borrower must not stop the rest: the fan-out runs on a pool deploy, and a
    ///     single tenant mid-update would otherwise leave every other borrower without its mirror.
    /// </summary>
    [Test]
    public async Task AFailingBorrower_DoesNotStopTheOthers()
    {
        var poolRtId = NewPoolRtId().ToString();
        var pool = ArrangePool(LenderTenantId, poolRtId, lends: true);
        ArrangeLendableTenants(pool, BorrowerTenantId, SecondBorrowerTenantId);
        ArrangeBorrowerSide(SecondBorrowerTenantId, pool);
        ArrangeCandidateLenders(LenderTenantId);

        CommunicationRepository.UpsertLentAdapterPoolMirrorAsync(BorrowerTenantId,
                Arg.Any<LendableAdapterPool>())
            .ThrowsAsync(new InvalidOperationException("borrower is being updated"));

        var result = await Service.ProvisionForLenderAsync(LenderTenantId);

        await Assert.That(result.TenantsReconciled).IsEqualTo(2);
        await CommunicationRepository.Received(1).UpsertLentAdapterPoolMirrorAsync(SecondBorrowerTenantId,
            Arg.Is<LendableAdapterPool>(p => p.AdapterPoolRtId == poolRtId));
    }

    [Test]
    public async Task AnUnreadableLender_ReconcilesNobody()
    {
        CommunicationRepository.GetAdapterPoolsForMirroringAsync(LenderTenantId)
            .ThrowsAsync(new InvalidOperationException("tenant is being updated"));

        var result = await Service.ProvisionForLenderAsync(LenderTenantId);

        await Assert.That(result.TenantsReconciled).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .UpsertLentAdapterPoolMirrorAsync(Arg.Any<string>(), Arg.Any<LendableAdapterPool>());
    }

    private void ArrangeLendableTenants(LendableAdapterPool pool, params string[] borrowerTenantIds)
    {
        LendingScopeResolver
            .ResolveLendableTenantsAsync(pool.LenderTenantId, pool.Scope, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)borrowerTenantIds.ToList());
    }

    /// <summary>
    ///     The base class arranges the borrower side for <see cref="BorrowerTenantId" /> only; a
    ///     second borrower needs its own stored-mirror read and its own lending decision.
    /// </summary>
    private void ArrangeBorrowerSide(string borrowerTenantId, LendableAdapterPool pool)
    {
        CommunicationRepository.GetLentAdapterPoolMirrorsAsync(borrowerTenantId)
            .Returns((IReadOnlyCollection<RtLentAdapterPool>)[]);
        LendingScopeResolver.ResolveCandidateLenderTenantsAsync(borrowerTenantId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)[pool.LenderTenantId]);
        LendingScopeResolver
            .MayLendAsync(pool.LenderTenantId, borrowerTenantId, pool.Scope, Arg.Any<CancellationToken>())
            .Returns(true);
    }
}
