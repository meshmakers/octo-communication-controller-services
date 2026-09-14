using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 increment 6 — granting a lease.
/// </summary>
/// <remarks>
///     🔴 <b>Lending is consensual in both directions.</b> The lender's <c>SharingMode</c> says who
///     <i>may</i> borrow; the borrower's own <c>LentFromTenantId</c> / <c>LentFromPoolRtId</c> say
///     from whom it <i>does</i>. Either half alone is not enough, and the refusals below are what
///     make that true rather than documented — without the borrower half a lender could push
///     executions into any descendant that never asked for them, under an identity that descendant
///     never intended to hand out.
/// </remarks>
internal class GrantLeaseAsyncTests : LeaseServiceTestsBase
{
    [Test]
    public async Task AGrantableLease_IsPushedToTheMemberAndReported()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(result.MemberId).IsEqualTo(MemberId);
        await Assert.That(result.LeaseId).IsNotNull();

        var lease = CapturePushedLease();
        await Assert.That(lease.TenantId).IsEqualTo(BorrowerTenantId);
        await Assert.That(lease.PoolTenantId).IsEqualTo(LenderTenantId);
        await Assert.That(lease.PoolRtId).IsEqualTo(PoolRtId.ToString());
        await Assert.That(lease.AdapterRtId).IsEqualTo(Borrower.RtId.ToString());
        await Assert.That(lease.ExecutionId).IsEqualTo("exec-1");
    }

    /// <summary>
    ///     🔴 Q6: the lease carries the <b>borrower's</b> own pipeline service account, not the
    ///     pool's. That is what makes the member act exactly as the borrower's own adapter would,
    ///     without creating any standing grant in the borrower tenant.
    /// </summary>
    [Test]
    public async Task TheLeaseCarriesTheBorrowersOwnCredential()
    {
        ArrangeGrantableLease();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        var lease = CapturePushedLease();
        using var _ = Assert.Multiple();
        await Assert.That(lease.ClientId).IsEqualTo("octo-pipeline-sa-borrower");
        await Assert.That(lease.ClientSecret).IsEqualTo(ClientSecret);
        // Resolved against the BORROWER's tenant. Reading the lender's account here would hand the
        // member the wrong identity and the borrower's data would be read as somebody else.
        await ServiceAccountResolver.Received(1).GetAdapterDefaultAsync(BorrowerTenantId, Borrower.RtId);
    }

    /// <summary>
    ///     An encrypted secret must reach the member usable. The provisioning path writes plaintext
    ///     today, so <c>Decrypt</c> is normally a pass-through — but the lease sits in the same lane
    ///     as every other secret leaving this service.
    /// </summary>
    [Test]
    public async Task AnEncryptedClientSecret_ReachesTheMemberDecrypted()
    {
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential("enc:v1:cipher");
        ArrangeConnectedMember();
        EncryptionService.Decrypt("enc:v1:cipher").Returns("plaintext-secret");

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        await Assert.That(CapturePushedLease().ClientSecret).IsEqualTo("plaintext-secret");
    }

    [Test]
    public async Task TheLeaseExpires_AtTheRequestedTtl()
    {
        ArrangeGrantableLease();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest(ttl: TimeSpan.FromMinutes(2)));

        var lease = CapturePushedLease();
        await Assert.That(lease.ExpiresAtUtc - lease.GrantedAtUtc).IsEqualTo(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task WithoutATtl_TheServiceDefaultApplies()
    {
        ArrangeGrantableLease();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        var lease = CapturePushedLease();
        await Assert.That(lease.ExpiresAtUtc - lease.GrantedAtUtc).IsEqualTo(ILeaseService.DefaultLeaseTtl);
    }

    /// <summary>
    ///     🔴 The borrower half of consent. An adapter that does not name this pool is not borrowing
    ///     from it, whatever the pool's sharing scope says.
    /// </summary>
    [Test]
    public async Task ABorrowerThatNamesADifferentPool_IsRefused()
    {
        ArrangeBorrower(lentFromPoolRtId: "6ad562f3ff7c40ff80275b99");
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("borrows from pool");
        await Assert.That(MemberProxy.ReceivedCalls()).IsEmpty();
    }

    [Test]
    public async Task ABorrowerThatNamesADifferentLender_IsRefused()
    {
        ArrangeBorrower(lentFromTenantId: "someoneelse");
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("borrows from tenant");
    }

    /// <summary>
    ///     An adapter that runs its own process has nothing to borrow. <c>Leased</c> means "has no
    ///     process of its own" — concept §2 — and only that mode may be served by a pool.
    /// </summary>
    [Test]
    [Arguments(RtLifecycleModeEnum.AlwaysOn)]
    [Arguments(RtLifecycleModeEnum.OnDemand)]
    public async Task ANonLeasedAdapter_IsRefused(RtLifecycleModeEnum lifecycleMode)
    {
        ArrangeBorrower(lifecycleMode);
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("is not Leased");
    }

    /// <summary>
    ///     🔴 The lender half of consent, and the one that is security-relevant: a pool that does not
    ///     lend into this tenant must never serve it, however the borrower is configured. Audited in
    ///     the borrower's own event log, not only in a controller pod's stdout — a lease attempt
    ///     across a boundary the tenant tree does not allow is the shape a cross-tenant incident would
    ///     take.
    /// </summary>
    [Test]
    public async Task APoolThatDoesNotLendHere_IsRefusedAndAudited()
    {
        ArrangeBorrower();
        ArrangeLendingPool(lends: false);
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("does not lend to tenant");
        await EventService.Received(1).StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains("does not")));
        await Assert.That(MemberProxy.ReceivedCalls()).IsEmpty();
    }

    /// <summary>
    ///     Concept §6, "parent tenant deleted while lending": a pool that has disappeared since the
    ///     borrower was deployed surfaces here, with a reason, rather than as a null reference.
    /// </summary>
    [Test]
    public async Task AnUnresolvablePool_IsRefused()
    {
        ArrangeBorrower();
        ArrangeNoPool();
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("has no adapter pool");
    }

    /// <summary>
    ///     🔴 The credential is read <b>after</b> the two consent checks, never before. A request
    ///     naming an adapter it has no business naming must not reach the code that decrypts a client
    ///     secret at all.
    /// </summary>
    [Test]
    public async Task TheCredentialIsNotEvenReadWhenTheRelationshipIsRefused()
    {
        ArrangeBorrower(lentFromTenantId: "someoneelse");
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        await ServiceAccountResolver.DidNotReceive()
            .GetAdapterDefaultAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task ABorrowerWithoutACredential_IsRefused()
    {
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ServiceAccountResolver.GetAdapterDefaultAsync(BorrowerTenantId, Borrower.RtId)
            .Returns((RtServiceAccountConfiguration?)null);
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("no usable pipeline service account");
    }

    [Test]
    public async Task AnUnknownBorrowerAdapter_IsRefused()
    {
        ArrangeBorrower();
        CommunicationRepository.GetWorkloadByRtIdAsync(BorrowerTenantId, Borrower.RtId)
            .Returns((RtDeployableWorkload?)null);
        ArrangeLendingPool(lends: true);
        ArrangeConnectedMember();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("has no adapter with rtId");
    }

    /// <summary>
    ///     🔴 There is no queue in this increment, on purpose (implementation plan §8: "No scheduler
    ///     yet"). A request that finds nothing idle is refused with a reason the caller can act on —
    ///     it is not parked on something that would become a second, unintended scheduling policy.
    /// </summary>
    [Test]
    public async Task WithNoIdleMember_TheRequestIsRefusedRatherThanQueued()
    {
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        // No member registered at all.

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("No idle member");
    }

    [Test]
    public async Task WithEveryMemberBusy_TheRequestIsRefused()
    {
        ArrangeGrantableLease();
        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        var second = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(second.Granted).IsFalse();
        await Assert.That(second.StatusMessage).Contains("No idle member");
    }

    /// <summary>
    ///     AB#4924 increment 7 — the admission gate runs AFTER a member is reserved and BEFORE the
    ///     lease is pushed. That position is the whole point: the scheduler's claim needs the member
    ///     id, and it has to land before the member is handed anything.
    /// </summary>
    [Test]
    public async Task TheAdmissionGate_SeesTheReservedMemberAndRunsBeforeThePush()
    {
        ArrangeGrantableLease();
        PoolMemberConnection? seen = null;
        var memberWasReservedWhenTheGateRan = false;
        var pushedBeforeTheGate = false;

        var granted = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"),
            CancellationToken.None,
            (_, member, _) =>
            {
                seen = member;
                memberWasReservedWhenTheGateRan = ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease != null;
                pushedBeforeTheGate = MemberProxy.ReceivedCalls()
                    .Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync));
                return Task.FromResult(true);
            });

        using var _ = Assert.Multiple();
        await Assert.That(granted.Granted).IsTrue();
        await Assert.That(seen!.MemberId).IsEqualTo(MemberId);
        await Assert.That(memberWasReservedWhenTheGateRan).IsTrue();
        await Assert.That(pushedBeforeTheGate).IsFalse();
    }

    /// <summary>
    ///     🔴 A gate that declines must put the member back. Otherwise every work item another
    ///     controller instance claimed first costs this one a member, and the pool shrinks to zero
    ///     usable members while every entity still reads Deployed.
    /// </summary>
    [Test]
    public async Task ARefusedAdmissionGate_PutsTheMemberBack()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"),
            CancellationToken.None, (_, _, _) => Task.FromResult(false));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
        // And nothing was pushed: the member must never learn about a lease it is not going to serve.
        await Assert.That(MemberProxy.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))).IsFalse();
    }

    /// <summary>
    ///     A gate that throws decided nothing, so the member goes back too.
    /// </summary>
    [Test]
    public async Task AThrowingAdmissionGate_PutsTheMemberBack()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"),
            CancellationToken.None,
            (_, _, _) => throw new InvalidOperationException("the tenant database went away"));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
    }

    /// <summary>
    ///     🔴 A push that fails must undo the claim. A member left marked busy for a lease it never
    ///     received is a member that never takes work again — the failure mode is a pool that silently
    ///     shrinks to zero usable members while every entity still reads Deployed.
    /// </summary>
    [Test]
    public async Task AFailedPush_UndoesTheClaimAndLeavesTheMemberAvailable()
    {
        ArrangeGrantableLease();
        MemberProxy.SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("connection went away")));

        var failed = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(failed.Granted).IsFalse();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
    }
}
