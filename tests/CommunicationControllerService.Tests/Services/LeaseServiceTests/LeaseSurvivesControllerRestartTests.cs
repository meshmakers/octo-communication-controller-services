using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#5826 — the outcome of a lease survives a controller that forgot it, and a dropped connection
///     no longer runs the work twice.
/// </summary>
/// <remarks>
///     <para>
///         Reproduced on test-2-dev (2026-10-07) and in the kind smoke: a controller process restart
///         mid-lease lost the in-memory lease, the member's release on the new connection was dropped
///         without a log line, the execution stayed <c>Running</c> and was failed 31 minutes later as
///         "orphaned by adapter restart" — the work had completed two minutes after the restart.
///     </para>
///     <para>
///         The fresh <see cref="LeaseService" /> of each test is the restarted controller: its
///         registry is empty, and the only thing that survived is the persisted execution, which the
///         repository substitute plays.
///     </para>
/// </remarks>
internal class LeaseSurvivesControllerRestartTests : LeaseServiceTestsBase
{
    private const string ExecutionId = "b7b9ca9b-0000-4000-8000-000000005826";
    private const string NewConnectionId = "conn-pool-member-1-after-restart";
    private const string LeaseId = "1b9baa13000000000000000000005826";

    private static readonly DateTime GrantedAt = new(2026, 10, 7, 19, 28, 13, DateTimeKind.Utc);

    /// <summary>A controller with the production default reconnect grace.</summary>
    private ILeaseService WithGrace(int seconds = 90)
    {
        return new LeaseService(ConnectionManager, CommunicationRepository, EventService, EncryptionService,
            HubContext, LendingScopeResolver, ServiceAccountResolver, DatabaseCredentialResolver, AdapterService,
            LifecycleConfiguration, WakeSignal,
            new OptionsWrapper<CommunicationControllerOptions>(new CommunicationControllerOptions
            {
                LeaseMemberReconnectGraceSeconds = seconds
            }));
    }

    private RtPipelineExecution ArrangeLeasedExecution(RtPipelineExecutionStatusEnum status =
            RtPipelineExecutionStatusEnum.Running, string memberId = MemberId, string? poolRtId = null,
        string lenderTenantId = LenderTenantId, DateTime? leaseReleasedAt = null)
    {
        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
            ExecutionId = ExecutionId,
            Status = status,
            StartedAt = GrantedAt,
            LeaseGrantedAt = GrantedAt,
            LeaseReleasedAt = leaseReleasedAt,
            LeasedFromTenantId = lenderTenantId,
            LeasedFromAdapterPoolRtId = poolRtId ?? AdapterPoolRtId.ToString(),
            LeasedOnMemberId = memberId
        };
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId).Returns(execution);
        return execution;
    }

    private static LeaseResultDto ACompletedRelease(string leaseId = LeaseId, bool newMember = true,
        string? memberId = MemberId)
    {
        return new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            OutputData = "{\"booked\":42}",
            WorkDurationMs = 150_000,
            ReleasedAtUtc = DateTime.UtcNow,
            ExecutionId = newMember ? ExecutionId : null,
            TenantId = newMember ? BorrowerTenantId : null,
            MemberId = newMember ? memberId : null
        };
    }

    private PoolMemberActiveLeaseDto AnActiveLease(string leaseId = LeaseId, string executionId = ExecutionId)
    {
        return new PoolMemberActiveLeaseDto
        {
            LeaseId = leaseId,
            TenantId = BorrowerTenantId,
            ExecutionId = executionId,
            AdapterRtId = "665f0000000000000000d101",
            AdapterCkTypeId = "System.Communication/MeshAdapter",
            GrantedAtUtc = GrantedAt,
            ExpiresAtUtc = GrantedAt.AddMinutes(15)
        };
    }

    private Task AssertCompletedWithOutputAsync()
    {
        return CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, ExecutionId,
            RtPipelineExecutionStatusEnum.Completed, Arg.Any<DateTime?>(), Arg.Any<int?>(), null,
            "{\"booked\":42}");
    }

    private Task AssertNotCompletedAsync()
    {
        return CommunicationRepository.DidNotReceive().UpdatePipelineExecutionAsync(Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<RtPipelineExecutionStatusEnum>(), Arg.Any<DateTime?>(), Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    private Task AssertNotRequeuedAsync()
    {
        return CommunicationRepository.DidNotReceive().TryInterruptLeasedExecutionAsync(Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string>());
    }

    // ---------------------------------------------------------------------------------------------
    // The late release after a controller restart — attributed through the persisted execution
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 The F1 case itself. The restarted controller holds no lease; the release names the
    ///     execution, the execution proves it was leased to this member of this pool and is still
    ///     running, so the outcome — output included — lands on it instead of being dropped.
    /// </summary>
    [Test]
    public async Task ALateRelease_ProvenByTheExecution_CompletesItWithItsOutput()
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution();

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        using var _ = Assert.Multiple();
        await AssertCompletedWithOutputAsync();
        await CommunicationRepository.Received(1)
            .StampLeaseReleasedAsync(BorrowerTenantId, ExecutionId, Arg.Any<DateTime>());
        await AssertNotRequeuedAsync();
    }

    /// <summary>
    ///     The member reports pending outcomes before it registers again, so the release can arrive on
    ///     a connection that holds no registration yet. The member id then comes from the release.
    /// </summary>
    [Test]
    public async Task ALateRelease_OnAConnectionWithoutRegistration_IsAttributedByTheReportedMember()
    {
        ArrangeLeasedExecution();

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        await AssertCompletedWithOutputAsync();
    }

    [Test]
    public async Task ALateRelease_ForAnExecutionLeasedToAnotherMember_IsIgnored()
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution(memberId: "octo-pool-7");

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        await AssertNotCompletedAsync();
    }

    /// <summary>
    ///     🔴 An execution that has moved on — the controller interrupted and re-queued it, the TTL
    ///     reaper reclaimed it, or it completed — is never overwritten by a late outcome. The retry is
    ///     the work item now.
    /// </summary>
    [Test]
    [Arguments(RtPipelineExecutionStatusEnum.Interrupted)]
    [Arguments(RtPipelineExecutionStatusEnum.Failed)]
    [Arguments(RtPipelineExecutionStatusEnum.Completed)]
    public async Task ALateRelease_ForAnExecutionThatMovedOn_IsIgnored(RtPipelineExecutionStatusEnum status)
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution(status);

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        using var _ = Assert.Multiple();
        await AssertNotCompletedAsync();
        await CommunicationRepository.DidNotReceive()
            .StampLeaseReleasedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task ALateRelease_ForAnExecutionWhoseLeaseWasAlreadyReleased_IsIgnored()
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution(leaseReleasedAt: GrantedAt.AddMinutes(1));

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        await AssertNotCompletedAsync();
    }

    /// <summary>
    ///     A registered connection pins the pool: a member of pool A cannot complete an execution that
    ///     pool B leased out, even under the same member id.
    /// </summary>
    [Test]
    public async Task ALateRelease_ForAnExecutionOfAnotherPool_IsIgnored()
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution(poolRtId: OctoObjectId.GenerateNewId().ToString());

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        await AssertNotCompletedAsync();
    }

    [Test]
    public async Task ALateRelease_NamingAnotherMemberThanTheConnectionIsRegisteredAs_IsIgnored()
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution(memberId: "octo-pool-7");

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(memberId: "octo-pool-7"));

        using var _ = Assert.Multiple();
        await AssertNotCompletedAsync();
        await CommunicationRepository.DidNotReceive()
            .GetPipelineExecutionAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     A member built before AB#5826 names only the lease. With nothing to check it against, the
    ///     release is ignored as before — but no longer silently, and nothing reads a tenant database
    ///     on its account.
    /// </summary>
    [Test]
    public async Task ALateRelease_FromAnOlderMember_IsIgnoredWithoutReadingAnything()
    {
        ArrangeConnectedMember(NewConnectionId);
        ArrangeLeasedExecution();

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(newMember: false));

        using var _ = Assert.Multiple();
        await AssertNotCompletedAsync();
        await CommunicationRepository.DidNotReceive()
            .GetPipelineExecutionAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     The pool hub's tenant binding reaches the late release: under <c>Enforce</c> a token of
    ///     another tenant — the N4 shape, a connection rebuilt with the borrower's token — is refused;
    ///     under <c>LogOnly</c> it is logged and allowed, exactly like a registration.
    /// </summary>
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task ALateRelease_WithAForeignConnectionTenant_FollowsTheGateMode(bool enforce, bool applied)
    {
        ArrangeLeasedExecution();

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(), BorrowerTenantId, enforce);

        if (applied)
        {
            await AssertCompletedWithOutputAsync();
        }
        else
        {
            await AssertNotCompletedAsync();
        }
    }

    [Test]
    public async Task ALateRelease_UnderEnforce_WithTheLendersToken_IsApplied()
    {
        ArrangeLeasedExecution();

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(), LenderTenantId, true);

        await AssertCompletedWithOutputAsync();
    }

    // ---------------------------------------------------------------------------------------------
    // Resuming a running lease after a controller restart
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    ///     🔴 Adoption rebuilds the lease from identifiers: the member is busy with it, its TTL is the
    ///     one granted, and no secret is invented or carried — the member already holds them.
    /// </summary>
    [Test]
    public async Task AResumedLease_ProvenByTheExecution_IsAdoptedWithItsOwnTtlAndNoSecret()
    {
        ArrangeLeasedExecution();

        var resumption = await LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            AnActiveLease());

        using var _ = Assert.Multiple();
        await Assert.That(resumption.Adopted).IsTrue();
        await Assert.That(resumption.Lease.LeaseId).IsEqualTo(LeaseId);
        await Assert.That(resumption.Lease.ExecutionId).IsEqualTo(ExecutionId);
        await Assert.That(resumption.Lease.TenantId).IsEqualTo(BorrowerTenantId);
        await Assert.That(resumption.Lease.AdapterPoolTenantId).IsEqualTo(LenderTenantId);
        await Assert.That(resumption.Lease.ExpiresAtUtc).IsEqualTo(GrantedAt.AddMinutes(15));
        await Assert.That(resumption.Lease.ClientSecret).IsEqualTo(string.Empty);
        await Assert.That(string.IsNullOrEmpty(resumption.Lease.DatabasePassword)).IsTrue();
    }

    /// <summary>
    ///     End to end on the service: resume after the restart, then the ordinary release on the same
    ///     connection completes the execution — the path the member takes when the lease outlives the
    ///     outage.
    /// </summary>
    [Test]
    public async Task AnAdoptedLease_IsCompletedByTheOrdinaryRelease()
    {
        ArrangeLeasedExecution();
        var resumption = await LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            AnActiveLease());
        ConnectionManager.RegisterMember(NewConnectionId, MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            activeLease: resumption.Lease);

        var busy = ConnectionManager.TryGetMember(NewConnectionId)!.IsAvailable;
        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        using var _ = Assert.Multiple();
        await Assert.That(busy).IsFalse();
        await AssertCompletedWithOutputAsync();
        await Assert.That(ConnectionManager.TryGetMember(NewConnectionId)!.IsAvailable).IsTrue();
    }

    /// <summary>
    ///     🔴 A lease whose execution has moved on is NOT adopted — but the member is still busy, so it
    ///     is recorded with a lease that names no execution. Its release then frees the member and
    ///     touches nothing.
    /// </summary>
    [Test]
    public async Task AResumedLease_WhoseExecutionMovedOn_KeepsTheMemberBusyButCompletesNothing()
    {
        ArrangeLeasedExecution(RtPipelineExecutionStatusEnum.Interrupted);

        var resumption = await LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            AnActiveLease());
        ConnectionManager.RegisterMember(NewConnectionId, MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            activeLease: resumption.Lease);
        var busy = ConnectionManager.TryGetMember(NewConnectionId)!.IsAvailable;

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease());

        using var _ = Assert.Multiple();
        await Assert.That(resumption.Adopted).IsFalse();
        await Assert.That(resumption.StatusMessage).Contains("Interrupted");
        await Assert.That(resumption.Lease.ExecutionId).IsEqualTo(string.Empty);
        await Assert.That(busy).IsFalse();
        await AssertNotCompletedAsync();
        await Assert.That(ConnectionManager.TryGetMember(NewConnectionId)!.IsAvailable).IsTrue();
    }

    [Test]
    public async Task AResumedLease_OfAnotherMember_IsNotAdopted()
    {
        ArrangeLeasedExecution(memberId: "octo-pool-7");

        var resumption = await LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            AnActiveLease());

        using var _ = Assert.Multiple();
        await Assert.That(resumption.Adopted).IsFalse();
        await Assert.That(resumption.Lease.ExecutionId).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AResumedHandDrivenLease_IsAdoptedWithoutReadingAnything()
    {
        var resumption = await LeaseService.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            AnActiveLease(executionId: string.Empty));

        using var _ = Assert.Multiple();
        await Assert.That(resumption.Adopted).IsTrue();
        await CommunicationRepository.DidNotReceive()
            .GetPipelineExecutionAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    // ---------------------------------------------------------------------------------------------
    // A dropped connection with a living controller — no more double execution
    // ---------------------------------------------------------------------------------------------

    private async Task<(ILeaseService Service, string LeaseId)> GrantAndDisconnectAsync(int graceSeconds = 90)
    {
        var service = WithGrace(graceSeconds);
        ArrangeGrantableLease();
        var granted = await service.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest(ExecutionId));
        var member = ConnectionManager.RemoveMember(ConnectionId)!;
        await service.HandleMemberDisconnectedAsync(member);
        return (service, granted.LeaseId!);
    }

    /// <summary>
    ///     🔴 The member-recovery report's double execution: the controller used to interrupt and
    ///     re-queue at the disconnect while the member computed on and reconnected seconds later.
    ///     Within the grace nothing is re-queued; the member resumes the lease and its release
    ///     completes the original execution.
    /// </summary>
    [Test]
    public async Task ADisconnectMidLease_ThenResume_ReQueuesNothingAndCompletesTheOriginal()
    {
        var (service, leaseId) = await GrantAndDisconnectAsync();
        ArrangeLeasedExecution();

        var resumption = await service.ResumeLeaseAsync(MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            AnActiveLease(leaseId));
        ConnectionManager.RegisterMember(NewConnectionId, MemberId, LenderTenantId, AdapterPoolRtId.ToString(),
            activeLease: resumption.Lease);
        await service.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(leaseId));
        var swept = await service.SweepLostLeasesAsync(DateTime.UtcNow.AddHours(1));

        using var _ = Assert.Multiple();
        await Assert.That(resumption.Adopted).IsTrue();
        await Assert.That(swept).IsEqualTo(0);
        await AssertNotRequeuedAsync();
        await AssertCompletedWithOutputAsync();
    }

    /// <summary>
    ///     A member that finished while it was disconnected reports the release on its new connection
    ///     before it registers. Matched by lease id, so this works for a member built before AB#5826 as
    ///     well — it names nothing but the lease.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ADisconnectMidLease_ThenALateRelease_ReQueuesNothingAndCompletesTheOriginal(bool newMember)
    {
        var (service, leaseId) = await GrantAndDisconnectAsync();
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId)
            .Returns(new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = ExecutionId,
                Status = RtPipelineExecutionStatusEnum.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-30)
            });

        await service.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(leaseId, newMember));
        var swept = await service.SweepLostLeasesAsync(DateTime.UtcNow.AddHours(1));

        using var _ = Assert.Multiple();
        await Assert.That(swept).IsEqualTo(0);
        await AssertNotRequeuedAsync();
        await AssertCompletedWithOutputAsync();
    }

    /// <summary>
    ///     A member that never comes back: the work is re-queued once the grace has passed, exactly
    ///     once, and not before.
    /// </summary>
    [Test]
    public async Task ADisconnectMidLease_WithoutReturn_IsReQueuedOnceAfterTheGrace()
    {
        var (service, _) = await GrantAndDisconnectAsync(graceSeconds: 90);

        var early = await service.SweepLostLeasesAsync(DateTime.UtcNow.AddSeconds(30));
        var late = await service.SweepLostLeasesAsync(DateTime.UtcNow.AddSeconds(120));
        var again = await service.SweepLostLeasesAsync(DateTime.UtcNow.AddSeconds(240));

        using var _ = Assert.Multiple();
        await Assert.That(early).IsEqualTo(0);
        await Assert.That(late).IsEqualTo(1);
        await Assert.That(again).IsEqualTo(0);
        await CommunicationRepository.Received(1).TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Any<DateTime>(), Arg.Is<string>(r => r.Contains("did not come back")));
    }

    /// <summary>
    ///     A member that registers again WITHOUT the lease (a container restarted in place keeps its
    ///     pod name, i.e. its member id) does not have the work any more — the retry must not wait
    ///     out the grace.
    /// </summary>
    [Test]
    public async Task ADisconnectMidLease_ThenAnIdleRegistration_ReQueuesAtOnce()
    {
        var (service, _) = await GrantAndDisconnectAsync();

        var interrupted = await service.ReleaseLostLeasesOfMemberAsync(MemberId, LenderTenantId,
            AdapterPoolRtId.ToString());

        using var _ = Assert.Multiple();
        await Assert.That(interrupted).IsEqualTo(1);
        await CommunicationRepository.Received(1).TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Any<DateTime>(), Arg.Is<string>(r => r.Contains("registered again without it")));
        await Assert.That(await service.SweepLostLeasesAsync(DateTime.UtcNow.AddHours(1))).IsEqualTo(0);
    }

    [Test]
    public async Task AnIdleRegistrationOfAnotherMember_LeavesTheHeldBackLeaseAlone()
    {
        var (service, _) = await GrantAndDisconnectAsync();

        var interrupted = await service.ReleaseLostLeasesOfMemberAsync("octo-pool-7", LenderTenantId,
            AdapterPoolRtId.ToString());

        using var _ = Assert.Multiple();
        await Assert.That(interrupted).IsEqualTo(0);
        await AssertNotRequeuedAsync();
    }

    /// <summary>
    ///     Without a grace (the composition before AB#5826) a disconnect re-queues at once, as before.
    /// </summary>
    [Test]
    public async Task WithoutAGrace_ADisconnectMidLease_ReQueuesAtOnce()
    {
        await GrantAndDisconnectAsync(graceSeconds: 0);

        await CommunicationRepository.Received(1).TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Any<DateTime>(), Arg.Any<string>());
    }

    /// <summary>
    ///     The release arrives on the member's new connection while this instance still has the lease
    ///     registered under the old one (it has not noticed the old connection is gone). Matched by
    ///     lease id; the old registration is freed and the original execution completed.
    /// </summary>
    [Test]
    public async Task ALateRelease_WhileTheOldConnectionStillHoldsTheLease_CompletesTheOriginal()
    {
        ArrangeGrantableLease();
        var granted = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest(ExecutionId));
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId)
            .Returns(new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = ExecutionId,
                Status = RtPipelineExecutionStatusEnum.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-30)
            });

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease(granted.LeaseId!));

        using var _ = Assert.Multiple();
        await AssertCompletedWithOutputAsync();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease).IsNull();
    }

    /// <summary>
    ///     A lease id is the proof on these paths, so a guessed one frees nothing anywhere.
    /// </summary>
    [Test]
    public async Task AReleaseOfALeaseNobodyHolds_FreesNothingElsewhere()
    {
        ArrangeGrantableLease();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest(ExecutionId));

        await LeaseService.ReleaseLeaseAsync(NewConnectionId, ACompletedRelease("not-the-lease", newMember: false));

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease).IsNotNull();
        await AssertNotCompletedAsync();
    }
}
