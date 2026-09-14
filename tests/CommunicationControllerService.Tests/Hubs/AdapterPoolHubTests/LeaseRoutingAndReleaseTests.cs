using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterPoolHubTests;

/// <summary>
///     AB#4924 increment 6 — what happens to a lease once it exists: whose connection it lands on,
///     what a release does, and what a disconnect mid-lease does.
/// </summary>
/// <remarks>
///     These exercise the real <c>AdapterPoolConnectionManager</c> through the hub, because the
///     property worth pinning is the one the registry enforces: <b>at most one lease per member at
///     any instant</b>. That is the isolation invariant of concept §4 expressed in the controller —
///     the process serves exactly one tenant at a time because the controller never hands a second
///     lease to a member that holds one.
/// </remarks>
internal class LeaseRoutingAndReleaseTests : AdapterPoolHubTestsBase
{
    private const string SecondConnectionId = "conn-pool-member-2";

    private static LeaseDto ALease(string leaseId = "lease-1", string tenantId = BorrowerTenantId) => new()
    {
        LeaseId = leaseId,
        TenantId = tenantId,
        PoolTenantId = LenderTenantId,
        PoolRtId = PoolRtId,
        AdapterRtId = "6ad562f3ff7c40ff80275b85",
        AdapterCkTypeId = "System.Communication/Adapter",
        ClientId = "octo-pipeline-sa-borrower",
        ClientSecret = "the-plaintext-secret",
        GrantedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
    };

    private async Task RegisterAsync(string connectionId = ConnectionId, string memberId = MemberId)
    {
        var context = Substitute.For<Microsoft.AspNetCore.SignalR.HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        context.Items.Returns(ContextItems);
        Hub.Context = context;
        ArrangeConnectionTenant(LenderTenantId);

        await Hub.RegisterPoolMemberAsync(new PoolMemberRegistrationDto
        {
            PoolTenantId = LenderTenantId,
            PoolRtId = PoolRtId,
            MemberId = memberId
        });
    }

    /// <summary>
    ///     🔴 The claim must land on exactly one member and must make that member unavailable. Two
    ///     borrowers sharing one process at the same instant is the cross-tenant incident this whole
    ///     design exists to make impossible.
    /// </summary>
    [Test]
    public async Task AClaimedMemberIsNotOfferedASecondLease()
    {
        await RegisterAsync();

        var first = ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease());
        var second = ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease("lease-2", "other"));

        using var _ = Assert.Multiple();
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.MemberId).IsEqualTo(MemberId);
        await Assert.That(second).IsNull();
    }

    /// <summary>
    ///     With two members the second lease goes to the other connection — the routing that makes a
    ///     pool a pool rather than a single process with a queue in front of it.
    /// </summary>
    [Test]
    public async Task ASecondLeaseIsRoutedToADifferentMember()
    {
        await RegisterAsync();
        await RegisterAsync(SecondConnectionId, "octo-pool-1");

        var first = ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease());
        var second = ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease("lease-2", "other"));

        using var _ = Assert.Multiple();
        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.ConnectionId).IsNotEqualTo(first!.ConnectionId);
    }

    /// <summary>
    ///     A pool is addressed by (tenant, rtId). A member of another tenant's pool — or of another
    ///     pool in the same tenant — is not a candidate, however idle it is.
    /// </summary>
    [Test]
    public async Task AMemberOfADifferentPoolIsNeverClaimed()
    {
        await RegisterAsync();

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryClaimMember("othertenant", PoolRtId, ALease())).IsNull();
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId,
            "6ad562f3ff7c40ff80275b99", ALease())).IsNull();
    }

    [Test]
    public async Task ReleaseFreesTheMemberForTheNextLease()
    {
        await RegisterAsync();
        ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease());

        await Hub.ReleaseLeaseAsync(new LeaseResultDto
        {
            LeaseId = "lease-1",
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            ReleasedAtUtc = DateTime.UtcNow
        });

        // The hub delegates to the lease service, which owns the registry transition; the substitute
        // does not, so the release is applied here to assert the end state the real service produces.
        await LeaseService.Received(1).ReleaseLeaseAsync(ConnectionId,
            Arg.Is<LeaseResultDto>(r => r.LeaseId == "lease-1"));

        ConnectionManager.ReleaseLease(ConnectionId, "lease-1");
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease("lease-2")))
            .IsNotNull();
    }

    /// <summary>
    ///     🔴 A release naming a lease the member no longer holds is <b>stale</b> — it arrives from a
    ///     lease the controller already expired, after the member was handed a new one. Honouring it
    ///     would free a lease that is genuinely in flight and let a second borrower onto the process
    ///     while the first is still running.
    /// </summary>
    [Test]
    public async Task AStaleReleaseDoesNotFreeTheCurrentLease()
    {
        await RegisterAsync();
        ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease("lease-current"));

        var released = ConnectionManager.ReleaseLease(ConnectionId, "lease-expired-long-ago");

        using var _ = Assert.Multiple();
        await Assert.That(released).IsNull();
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease("lease-3")))
            .IsNull();
    }

    [Test]
    public async Task ReleaseWithoutALeaseIdIsIgnored()
    {
        await RegisterAsync();

        await Hub.ReleaseLeaseAsync(new LeaseResultDto { LeaseId = "  " });

        await LeaseService.DidNotReceive().ReleaseLeaseAsync(Arg.Any<string>(), Arg.Any<LeaseResultDto>());
    }

    /// <summary>
    ///     A disconnect mid-lease must reach the lease service <b>with the lease it held</b>: that is
    ///     what makes the at-least-once re-queue of concept §6 possible. A disconnect that only
    ///     removed the registration would lose the fact that a borrower's work stopped.
    /// </summary>
    [Test]
    public async Task DisconnectMidLease_ReportsTheHeldLease()
    {
        await RegisterAsync();
        ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease());
        ShutdownState.IsShuttingDown.Returns(false);

        await Hub.OnDisconnectedAsync(exception: null);

        await LeaseService.Received(1).HandleMemberDisconnectedAsync(
            Arg.Is<PoolMemberConnection>(m => m.ActiveLease != null && m.ActiveLease.LeaseId == "lease-1"));
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)).IsNull();
    }

    [Test]
    public async Task DisconnectWithoutALease_StillDropsTheRegistration()
    {
        await RegisterAsync();
        ShutdownState.IsShuttingDown.Returns(false);

        await Hub.OnDisconnectedAsync(exception: null);

        await LeaseService.Received(1).HandleMemberDisconnectedAsync(
            Arg.Is<PoolMemberConnection>(m => m.ActiveLease == null));
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)).IsNull();
    }

    /// <summary>
    ///     🔴 The <c>IShutdownState</c> guard on the disconnect path, the same one
    ///     <c>AdapterHub</c> and <c>OperatorHub</c> make. During this pod's own shutdown the member
    ///     has already reconnected elsewhere; reporting an interruption from here would fail a work
    ///     item that the surviving pod is about to re-lease. The registration is still dropped so a
    ///     late hub call does not see a stale member.
    /// </summary>
    [Test]
    public async Task ShuttingDown_DropsTheRegistrationButReportsNoInterruption()
    {
        await RegisterAsync();
        ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease());
        ShutdownState.IsShuttingDown.Returns(true);

        await Hub.OnDisconnectedAsync(exception: null);

        using var _ = Assert.Multiple();
        await LeaseService.DidNotReceive().HandleMemberDisconnectedAsync(Arg.Any<PoolMemberConnection>());
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)).IsNull();
    }

    /// <summary>
    ///     A drained member finishes what it holds and is never offered another lease — concept §4a's
    ///     scale-in and §6's "release never arrives" both depend on it.
    /// </summary>
    [Test]
    public async Task ADrainingMemberIsNeverClaimed()
    {
        await RegisterAsync();
        ConnectionManager.MarkDraining(ConnectionId);

        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, PoolRtId, ALease())).IsNull();
    }

    [Test]
    public async Task HeartbeatKeepsTheMemberRegistered()
    {
        await RegisterAsync();
        var sampledAt = DateTime.UtcNow.AddSeconds(5);

        await Hub.HeartbeatAsync(new PoolMemberHeartbeatDto
        {
            MemberId = MemberId,
            SampledAtUtc = sampledAt
        });

        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.LastSeenUtc).IsEqualTo(sampledAt);
    }
}
