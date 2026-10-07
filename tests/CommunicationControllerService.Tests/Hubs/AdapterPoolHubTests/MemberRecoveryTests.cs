using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterPoolHubTests;

/// <summary>
///     AB#4924 AP-I5 — the controller's half of pool member recovery: what the registry does when a
///     member registers again, on the same connection or on a new one, and how a member learns that
///     the controller holds no registration for it.
/// </summary>
/// <remarks>
///     The member side (comm-sdk <c>AdapterPoolMemberService</c>) re-registers whenever it believes it
///     is not registered and defers the registration while a lease is still running. These tests pin
///     the three registry properties that behaviour relies on; each one fails silently when it breaks
///     — a member that is connected, heartbeating and never leased again.
/// </remarks>
internal class MemberRecoveryTests : AdapterPoolHubTestsBase
{
    private const string ReconnectedConnectionId = "conn-pool-member-1-reconnected";
    private const string OtherPoolRtId = "6ad562f3ff7c40ff80275b99";

    private static LeaseDto ALease(string leaseId = "lease-1") => new()
    {
        LeaseId = leaseId,
        TenantId = BorrowerTenantId,
        AdapterPoolTenantId = LenderTenantId,
        AdapterPoolRtId = AdapterPoolRtId,
        AdapterRtId = "6ad562f3ff7c40ff80275b85",
        AdapterCkTypeId = "System.Communication/Adapter",
        ClientId = "octo-pipeline-sa-borrower",
        ClientSecret = "the-plaintext-secret",
        GrantedAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15)
    };

    private void OnConnection(string connectionId)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        context.Items.Returns(ContextItems);
        Hub.Context = context;
        ArrangeConnectionTenant(LenderTenantId);
    }

    private async Task<PoolMemberRegistrationResultDto> RegisterOnAsync(string connectionId,
        string memberId = MemberId, string poolRtId = AdapterPoolRtId)
    {
        OnConnection(connectionId);
        return await Hub.RegisterPoolMemberAsync(new PoolMemberRegistrationDto
        {
            AdapterPoolTenantId = LenderTenantId,
            AdapterPoolRtId = poolRtId,
            MemberId = memberId
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Re-registration on the same connection
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 A member re-registers on the same connection when it thinks the controller lost it — an
    ///     invoke that timed out on the member but landed here is enough. A lease granted in between
    ///     must survive: dropping it would offer a busy process as free, and the next grant would be
    ///     refused by the member, failing that borrower's execution.
    /// </summary>
    [Test]
    public async Task ReRegistrationOnTheSameConnection_KeepsTheHeldLease()
    {
        await RegisterOnAsync(ConnectionId);
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease());

        await RegisterOnAsync(ConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease?.LeaseId).IsEqualTo("lease-1");
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease("lease-2")))
            .IsNull();
    }

    /// <summary>A drained member stays drained; re-registering is not a way back into the rotation.</summary>
    [Test]
    public async Task ReRegistrationOnTheSameConnection_KeepsTheDrainFlag()
    {
        await RegisterOnAsync(ConnectionId);
        ConnectionManager.MarkDraining(ConnectionId);

        await RegisterOnAsync(ConnectionId);

        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsDraining).IsTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // Re-registration on a new connection: the stale registration is superseded
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 After a reconnect the old connection's registration lingers until this instance notices
    ///     the old connection is gone. Idle and least recently seen, it is the claim's favourite — and
    ///     every lease handed to it goes down a dead connection. The new registration replaces it.
    /// </summary>
    [Test]
    public async Task AReconnectedMember_SupersedesItsStaleRegistration()
    {
        await RegisterOnAsync(ConnectionId);

        await RegisterOnAsync(ReconnectedConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)).IsNull();
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId)).Count().IsEqualTo(1);
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease())!.ConnectionId)
            .IsEqualTo(ReconnectedConnectionId);
    }

    /// <summary>
    ///     A superseded registration that held a lease is a disconnect mid-lease: the lease service
    ///     gets it with the lease, for the at-least-once re-queue of concept §6.
    /// </summary>
    [Test]
    public async Task ASupersededRegistrationHoldingALease_IsHandledLikeADisconnectMidLease()
    {
        await RegisterOnAsync(ConnectionId);
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease());
        ShutdownState.IsShuttingDown.Returns(false);

        await RegisterOnAsync(ReconnectedConnectionId);

        await LeaseService.Received(1).HandleMemberDisconnectedAsync(Arg.Is<PoolMemberConnection>(m =>
            m.ConnectionId == ConnectionId && m.ActiveLease != null && m.ActiveLease.LeaseId == "lease-1"));
        await Assert.That(ConnectionManager.TryGetMember(ReconnectedConnectionId)!.ActiveLease).IsNull();
    }

    /// <summary>
    ///     The old connection's disconnect usually arrives afterwards. It must find nothing left to
    ///     report — the same lease interrupted twice would be re-queued twice.
    /// </summary>
    [Test]
    public async Task TheLateDisconnectOfASupersededConnection_ReportsNothingAgain()
    {
        await RegisterOnAsync(ConnectionId);
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease());
        ShutdownState.IsShuttingDown.Returns(false);
        await RegisterOnAsync(ReconnectedConnectionId);
        LeaseService.ClearReceivedCalls();

        OnConnection(ConnectionId);
        await Hub.OnDisconnectedAsync(exception: null);

        using var _ = Assert.Multiple();
        await LeaseService.DidNotReceive().HandleMemberDisconnectedAsync(Arg.Any<PoolMemberConnection>());
        await Assert.That(ConnectionManager.TryGetMember(ReconnectedConnectionId)).IsNotNull();
    }

    /// <summary>The same shutdown guard as the disconnect path: a surviving pod owns the work.</summary>
    [Test]
    public async Task WhileShuttingDown_ASupersededLeaseIsNotReportedAsInterrupted()
    {
        await RegisterOnAsync(ConnectionId);
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease());
        ShutdownState.IsShuttingDown.Returns(true);

        // Shutting down refuses the registration itself, so the stale entry is not touched either.
        var result = await RegisterOnAsync(ReconnectedConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsFalse();
        await LeaseService.DidNotReceive().HandleMemberDisconnectedAsync(Arg.Any<PoolMemberConnection>());
    }

    /// <summary>Another member of the same pool is a replica, not a stale registration.</summary>
    [Test]
    public async Task ADifferentMemberOfTheSamePool_IsNotSuperseded()
    {
        await RegisterOnAsync(ConnectionId, "octo-pool-0");

        await RegisterOnAsync(ReconnectedConnectionId, "octo-pool-1");

        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId)).Count().IsEqualTo(2);
    }

    /// <summary>The same member id in another pool is someone else's member.</summary>
    [Test]
    public async Task TheSameMemberIdInAnotherPool_IsNotSuperseded()
    {
        await RegisterOnAsync(ConnectionId, MemberId, OtherPoolRtId);

        await RegisterOnAsync(ReconnectedConnectionId);

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)).IsNotNull();
        await Assert.That(ConnectionManager.TryGetMember(ReconnectedConnectionId)).IsNotNull();
    }

    // ---------------------------------------------------------------------------------------------
    // Heartbeat on a connection without a registration
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 The only moment a member can learn it is not registered while its connection stays up.
    ///     Swallowing the heartbeat, as before, left it connected, heartbeating and never leased.
    /// </summary>
    [Test]
    public async Task AHeartbeatWithoutARegistration_TellsTheMemberToRegisterAgain()
    {
        OnConnection(ReconnectedConnectionId);

        await Assert.That(async () => await Hub.HeartbeatAsync(new PoolMemberHeartbeatDto
            {
                MemberId = MemberId,
                SampledAtUtc = DateTime.UtcNow
            }))
            .Throws<HubException>()
            .WithMessageContaining("register again");
    }

    [Test]
    public async Task AHeartbeatOfASupersededConnection_TellsTheMemberToRegisterAgain()
    {
        await RegisterOnAsync(ConnectionId);
        await RegisterOnAsync(ReconnectedConnectionId);

        OnConnection(ConnectionId);

        await Assert.That(async () => await Hub.HeartbeatAsync(new PoolMemberHeartbeatDto { MemberId = MemberId }))
            .Throws<HubException>();
    }
}
