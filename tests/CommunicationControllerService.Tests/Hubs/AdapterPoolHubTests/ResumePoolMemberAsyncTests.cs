using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterPoolHubTests;

/// <summary>
///     AB#5826 — the hub half of a lease surviving a controller restart: a member that registers while
///     still running a lease is recorded busy with it, and the plain registration never takes a member's
///     word for a lease.
/// </summary>
internal class ResumePoolMemberAsyncTests : AdapterPoolHubTestsBase
{
    private const string ReconnectedConnectionId = "conn-pool-member-1-reconnected";
    private const string LeaseId = "lease-5826";

    private static PoolMemberActiveLeaseDto AnActiveLease() => new()
    {
        LeaseId = LeaseId,
        TenantId = BorrowerTenantId,
        ExecutionId = "exec-5826",
        GrantedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(14)
    };

    private static LeaseDto ALease(string leaseId = LeaseId, string executionId = "exec-5826") => new()
    {
        LeaseId = leaseId,
        TenantId = BorrowerTenantId,
        AdapterPoolTenantId = LenderTenantId,
        AdapterPoolRtId = AdapterPoolRtId,
        ExecutionId = executionId,
        GrantedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(14)
    };

    private PoolMemberRegistrationDto ARegistration(PoolMemberActiveLeaseDto? activeLease) => new()
    {
        AdapterPoolTenantId = LenderTenantId,
        AdapterPoolRtId = AdapterPoolRtId,
        MemberId = MemberId,
        ActiveLease = activeLease
    };

    private void OnConnection(string connectionId)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        context.Items.Returns(ContextItems);
        Hub.Context = context;
        ArrangeConnectionTenant(LenderTenantId);
    }

    [Test]
    public async Task AnAdoptedLease_RecordsTheMemberBusyAndSaysSo()
    {
        OnConnection(ConnectionId);
        LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId, Arg.Any<PoolMemberActiveLeaseDto>(),
                Arg.Any<LeaseDto?>())
            .Returns(new LeaseResumption(ALease(), true, null));

        var result = await Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease()));

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(result.ActiveLeaseAdopted).IsTrue();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease?.LeaseId).IsEqualTo(LeaseId);
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease("next"))).IsNull();
    }

    /// <summary>
    ///     🔴 Not adopted still means busy: the process is running that lease whatever the execution
    ///     says, and a second lease would land on a member that must refuse it.
    /// </summary>
    [Test]
    public async Task ALeaseThatIsNotAdopted_StillKeepsTheMemberBusy()
    {
        OnConnection(ConnectionId);
        LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId, Arg.Any<PoolMemberActiveLeaseDto>(),
                Arg.Any<LeaseDto?>())
            .Returns(new LeaseResumption(ALease(executionId: string.Empty), false, "it is Interrupted already"));

        var result = await Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease()));

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsTrue();
        await Assert.That(result.ActiveLeaseAdopted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("Interrupted");
        await Assert.That(ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease("next"))).IsNull();
    }

    /// <summary>
    ///     🔴 The plain registration never honours a lease in the DTO — that is what keeps an older
    ///     controller's behaviour (and this method's) unambiguous.
    /// </summary>
    [Test]
    public async Task ThePlainRegistration_IgnoresAnAnnouncedLease()
    {
        OnConnection(ConnectionId);

        var result = await Hub.RegisterPoolMemberAsync(ARegistration(AnActiveLease()));

        using var _ = Assert.Multiple();
        await Assert.That(result.ActiveLeaseAdopted).IsFalse();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
        await LeaseService.DidNotReceiveWithAnyArgs()
            .ResumeLeaseAsync(default!, default!, default!, default!, default);
    }

    /// <summary>
    ///     The plain registration re-queues at once what was held back for this member: the member is
    ///     back and reports holding nothing.
    /// </summary>
    [Test]
    public async Task ThePlainRegistration_ReleasesTheMembersHeldBackLeases()
    {
        OnConnection(ConnectionId);

        await Hub.RegisterPoolMemberAsync(ARegistration(null));

        await LeaseService.Received(1).ReleaseLostLeasesOfMemberAsync(MemberId, LenderTenantId, AdapterPoolRtId, null);
    }

    [Test]
    public async Task AResumption_ReleasesOnlyTheOtherHeldBackLeases()
    {
        OnConnection(ConnectionId);
        LeaseService.ResumeLeaseAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new LeaseResumption(ALease(), true, null));

        await Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease()));

        await LeaseService.Received(1)
            .ReleaseLostLeasesOfMemberAsync(MemberId, LenderTenantId, AdapterPoolRtId, LeaseId);
    }

    /// <summary>
    ///     The member reconnected before this instance noticed the old connection was gone. The old
    ///     registration held the very lease the member resumes: it moves to the new connection instead
    ///     of being reported as a disconnect mid-lease.
    /// </summary>
    [Test]
    public async Task ASupersededRegistrationHoldingTheResumedLease_HandsItOverInsteadOfInterrupting()
    {
        OnConnection(ConnectionId);
        await Hub.RegisterPoolMemberAsync(ARegistration(null));
        var held = ALease();
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, held);
        LeaseService.ResumeLeaseAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(call => new LeaseResumption(call.ArgAt<LeaseDto?>(4)!, true, null));

        OnConnection(ReconnectedConnectionId);
        var result = await Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease()));

        using var _ = Assert.Multiple();
        await Assert.That(result.ActiveLeaseAdopted).IsTrue();
        await LeaseService.Received(1).ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId,
            Arg.Any<PoolMemberActiveLeaseDto>(), held);
        await LeaseService.DidNotReceiveWithAnyArgs().HandleMemberDisconnectedAsync(default!);
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)).IsNull();
        await Assert.That(ConnectionManager.TryGetMember(ReconnectedConnectionId)!.ActiveLease?.LeaseId)
            .IsEqualTo(LeaseId);
    }

    [Test]
    public async Task ASupersededRegistrationHoldingAnotherLease_IsStillReportedAsADisconnect()
    {
        OnConnection(ConnectionId);
        await Hub.RegisterPoolMemberAsync(ARegistration(null));
        ConnectionManager.TryClaimMember(LenderTenantId, AdapterPoolRtId, ALease("an-older-lease"));
        LeaseService.ResumeLeaseAsync(default!, default!, default!, default!, default)
            .ReturnsForAnyArgs(new LeaseResumption(ALease(), true, null));

        OnConnection(ReconnectedConnectionId);
        await Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease()));

        await LeaseService.Received(1).HandleMemberDisconnectedAsync(
            Arg.Is<PoolMemberConnection>(m => m.ActiveLease!.LeaseId == "an-older-lease"));
    }

    /// <summary>The same gates as the plain registration: a pod that shuts down refuses.</summary>
    [Test]
    public async Task WhileShuttingDown_AResumptionIsRefusedAndAdoptsNothing()
    {
        OnConnection(ConnectionId);
        ShutdownState.IsShuttingDown.Returns(true);

        var result = await Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease()));

        using var _ = Assert.Multiple();
        await Assert.That(result.Accepted).IsFalse();
        await LeaseService.DidNotReceiveWithAnyArgs()
            .ResumeLeaseAsync(default!, default!, default!, default!, default);
    }

    [Test]
    public async Task UnderEnforce_AResumptionWithAForeignToken_IsRefused()
    {
        OnConnection(ConnectionId);
        ArrangeConnectionTenant(BorrowerTenantId);
        AuthorizationOptions.Mode = AdapterPoolHubAuthorizationMode.Enforce;

        await Assert.ThrowsAsync<HubException>(() => Hub.ResumePoolMemberAsync(ARegistration(AnActiveLease())));

        await LeaseService.DidNotReceiveWithAnyArgs()
            .ResumeLeaseAsync(default!, default!, default!, default!, default);
    }

    /// <summary>
    ///     The release hands the hub gate's view of the connection to the service, so a late release is
    ///     judged by the same staged tenant binding as a registration.
    /// </summary>
    [Test]
    [Arguments(AdapterPoolHubAuthorizationMode.LogOnly, false)]
    [Arguments(AdapterPoolHubAuthorizationMode.Enforce, true)]
    public async Task ARelease_PassesTheConnectionTenantAndTheGateMode(AdapterPoolHubAuthorizationMode mode,
        bool enforce)
    {
        OnConnection(ConnectionId);
        AuthorizationOptions.Mode = mode;

        await Hub.ReleaseLeaseAsync(new LeaseResultDto { LeaseId = LeaseId });

        await LeaseService.Received(1).ReleaseLeaseAsync(ConnectionId, Arg.Any<LeaseResultDto>(), LenderTenantId,
            enforce);
    }
}
