using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 — <b>the lease carries the borrower's database credential, or it is refused.</b>
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>What this suite stands between.</b> A mesh adapter opens MongoDB directly, and the
///         Communication Operator deliberately refuses an adapter pool the cluster's shared data-store
///         credentials — "tenant-scoped data access arrives with the lease and leaves with it". Before
///         these fields existed that sentence was false: the lease carried an OAuth credential and
///         nothing else, so a pool member could reach a tenant database only by inheriting one from its
///         own process environment. That is the failure mode with two shapes, and both are asserted
///         here: a lease that carries the <i>wrong</i> tenant's credential, and a lease that carries
///         <i>none</i> and lets the member fall back to whatever it happens to hold.
///     </para>
///     <para>
///         Every assertion names the borrower and the lender apart. A test that only checked "the
///         lease has a database user" would pass while the member was being handed the lender's.
///     </para>
/// </remarks>
internal class LeaseCarriesTheDatabaseCredentialTests : LeaseServiceTestsBase
{
    /// <summary>
    ///     🔴 The credential on the lease is the <b>borrower's</b> — not the lender's, whose pool the
    ///     member belongs to, and not the process's, which is what the member would otherwise use.
    /// </summary>
    [Test]
    public async Task TheLeaseCarriesTheBorrowersDatabaseCredential()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"));

        var lease = CapturePushedLease();
        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(lease.DatabaseName).IsEqualTo(BorrowerDatabaseName);
        await Assert.That(lease.DatabaseUser).IsEqualTo(BorrowerDatabaseUser);
        await Assert.That(lease.DatabasePassword).IsEqualTo(BorrowerDatabasePassword);

        // And explicitly not the lender's. The lender owns the pool and the process; it owns none of
        // the borrower's data, and a member handed its credential would read the wrong tenant.
        await Assert.That(lease.DatabaseUser).IsNotEqualTo(LenderDatabaseUser);
        await Assert.That(lease.DatabaseName).IsNotEqualTo("lenderdb");
    }

    /// <summary>
    ///     🔴 Resolved <b>for the borrowing tenant</b>. The lender is the tenant the grant call names
    ///     first and the one whose pool is being lent, so it is the easy mistake — and it is the one
    ///     that would produce a perfectly working member reading another tenant's database.
    /// </summary>
    [Test]
    public async Task TheCredentialIsResolvedForTheBorrowerAndNeverForTheLender()
    {
        ArrangeGrantableLease();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await DatabaseCredentialResolver.Received(1)
            .TryResolveAsync(BorrowerTenantId, Arg.Any<CancellationToken>());
        await DatabaseCredentialResolver.DidNotReceive()
            .TryResolveAsync(LenderTenantId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     🔴 <b>A lease that cannot resolve the credential is refused, with a named reason, and
    ///     nothing is granted.</b> Granting it blank is the failure this whole design exists to
    ///     prevent: the member would then execute the borrower's pipeline against whatever credentials
    ///     its own process holds — everything on a developer's machine, nothing in a cluster, and in
    ///     neither case the borrower's.
    /// </summary>
    [Test]
    public async Task AnUnresolvableDatabaseCredential_RefusesTheLeaseWithItsOwnReason()
    {
        ArrangeGrantableLease();
        ArrangeUnresolvableBorrowerDatabaseCredential();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.LeaseId).IsNull();
        await Assert.That(result.MemberId).IsNull();
        // The machine-readable half, and its own value: sharing BorrowerCredentialMissing would send
        // an operator to the identity service for a database problem.
        await Assert.That(result.Reason)
            .IsEqualTo(LeaseRefusalReason.BorrowerDatabaseCredentialUnresolvable);
        await Assert.That(result.StatusMessage).Contains("database credential");
    }

    /// <summary>
    ///     🔴 And nothing was granted in the literal sense: no lease reached a member, and the member
    ///     is still available. A refusal that had already claimed a member would cost the pool one
    ///     member per attempt for as long as the tenant record stays broken.
    /// </summary>
    [Test]
    public async Task AnUnresolvableDatabaseCredential_PushesNothingAndLeavesTheMemberIdle()
    {
        ArrangeGrantableLease();
        ArrangeUnresolvableBorrowerDatabaseCredential();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"));

        using var _ = Assert.Multiple();
        await Assert.That(MemberProxy.ReceivedCalls()).IsEmpty();
        var member = ConnectionManager.GetMembers(LenderTenantId, PoolRtId.ToString()).Single();
        await Assert.That(member.ActiveLease).IsNull();
        await Assert.That(member.IsAvailable).IsTrue();
    }

    /// <summary>
    ///     The borrower is told, in its own event log. A controller log line would leave the tenant
    ///     with a queue entry that never moves and no explanation anywhere it can see.
    /// </summary>
    [Test]
    public async Task AnUnresolvableDatabaseCredential_IsAuditedOnTheBorrower()
    {
        ArrangeGrantableLease();
        ArrangeUnresolvableBorrowerDatabaseCredential();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"));

        await EventService.Received(1).StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains("database credential")));
    }

    /// <summary>
    ///     🔴 Order of least trust, extended. The database credential is produced only <b>after</b> the
    ///     borrowing relationship has been proven real — a request naming an adapter it has no business
    ///     naming must not reach the code that reads a tenant's data credential at all.
    /// </summary>
    [Test]
    public async Task TheDatabaseCredentialIsNotEvenResolvedWhenTheRelationshipIsRefused()
    {
        ArrangeBorrower(lentFromTenantId: "someoneelse");
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        ArrangeConnectedMember();

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());

        await DatabaseCredentialResolver.DidNotReceive()
            .TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
