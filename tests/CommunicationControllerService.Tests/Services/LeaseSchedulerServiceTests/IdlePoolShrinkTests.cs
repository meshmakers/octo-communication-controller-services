using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#5256 — <c>IdleTimeoutMinutes</c> finally drains a pool member above <c>MinReplicas</c>
///     (concept §4a).
/// </summary>
/// <remarks>
///     <para>
///         Until this existed a pool only ever grew: the AB#4918 idle watchdog skips
///         <c>RtAdapterPool</c> on purpose — a pool has no pipelines of its own, so it would read as
///         idle since forever and drain to zero — and nothing else read the attribute for a pool.
///         Measured on the local kind cluster: a queue-driven scale-up to two members on 2026-09-23
///         was still two members days later against an empty queue.
///     </para>
///     <para>
///         🔴 Every test here shrinks (or refuses to shrink) through a <b>real scheduling round</b>,
///         not through the decision in isolation. The round is where the queue depth, the member list
///         and the pool entity are all true at the same instant, and the whole point of the design is
///         that the three are read together.
///     </para>
/// </remarks>
internal class IdlePoolShrinkTests : LeaseSchedulerServiceTestsBase
{
    private LeaseSchedulerService Service => (LeaseSchedulerService)Scheduler;

    /// <summary>
    ///     The idle window, in seconds, so a shrink can be observed inside a test at all — the pool
    ///     attribute is minutes and its default is thirty.
    /// </summary>
    private const int WindowSeconds = 1;

    /// <summary>
    ///     A pool with an empty queue, <paramref name="members" /> idle members and a one-second idle
    ///     window. The scale verb answers with what it was asked for, as the real one does once the
    ///     request is inside the pool's range.
    /// </summary>
    private RtAdapterPool ArrangeIdlePool(int minReplicas = 1, int members = 2, int maxReplicas = 3)
    {
        Options.LeaseIdleShrinkWindowSeconds = WindowSeconds;

        // The wait signal off and the depth threshold out of reach: this suite is about shrinking, and
        // a scale-up firing in the same round would make every assertion about the scale verb
        // ambiguous.
        var pool = ArrangePool(minReplicas, maxReplicas, scaleUpQueueDepthThreshold: 500,
            scaleUpQueueWaitSeconds: 0);

        DeploymentSiteService
            .ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, Arg.Any<int>())
            .Returns(call => call.ArgAt<int>(2));

        // Declares TenantA as a borrower without queueing anything, which is what puts the pool into
        // the topology at all: the sweep is built from the BORROWERS' declarations.
        ArrangeQueue(TenantA);
        ArrangeMembers(members);
        return pool;
    }

    /// <summary>Runs one round, lets the idle window elapse, and runs the round that may shrink.</summary>
    private async Task RunPastTheIdleWindowAsync()
    {
        await Scheduler.RunSchedulingRoundAsync();
        await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 0.3));
        await Scheduler.RunSchedulingRoundAsync();
    }

    /// <summary>
    ///     The happy path: empty queue, nobody holding a lease, idle for longer than the window, and
    ///     more members than the floor — one member is drained and the pool is asked to run one fewer.
    /// </summary>
    [Test]
    public async Task AnIdlePoolAboveItsFloor_DrainsOneMemberAndAsksForOneFewer()
    {
        // Arrange
        ArrangeIdlePool(minReplicas: 1, members: 2);

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        using var _ = Assert.Multiple();
        await LeaseService.Received(1).DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, 1);
        // The member we nominated is out of the running before the scale takes effect, which is the
        // only part of the victim choice this controller can actually enforce.
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId.ToString())
            .Count(m => m.IsDraining)).IsEqualTo(1);
        // It is an event, not a per-round observation: the lender is paying for one member fewer.
        await EventService.Received(1).StoreInformationEventAsync(LenderTenantId,
            Arg.Is<string>(m => m.Contains("gave up a member")));
    }

    /// <summary>
    ///     🔴 One member per round. The next round re-derives everything from the new reading instead
    ///     of continuing a plan, so work arriving in between is never raced by a half-finished shrink.
    /// </summary>
    [Test]
    public async Task OnlyOneMemberIsGivenUpPerRound()
    {
        // Arrange — three members against a floor of one, so two could go on paper.
        ArrangeIdlePool(minReplicas: 1, members: 3);

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        using var _ = Assert.Multiple();
        await LeaseService.Received(1).DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, 2);
    }

    /// <summary>
    ///     🔴 The member count does not drop the instant the scale is requested — the pod needs seconds
    ///     to disconnect. Without the hold-off the very next round reads three members again, finds the
    ///     pool idle and above its floor, and issues a second scale on a count it already acted on.
    /// </summary>
    [Test]
    public async Task AScaleAlreadyRequested_HoldsOffTheNextRoundUntilTheCountCatchesUp()
    {
        // Arrange
        ArrangeIdlePool(minReplicas: 1, members: 3);

        // Act — the shrink, then two further rounds with the member set unchanged, exactly as it looks
        // while a pod is terminating.
        await RunPastTheIdleWindowAsync();
        await Scheduler.RunSchedulingRoundAsync();
        await Scheduler.RunSchedulingRoundAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.Received(1)
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.Received(1).DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     Once the leaving member has really disconnected the counts agree again — which is what makes
    ///     the hold-off self-limiting and lets the next shrink happen with no timer anywhere.
    /// </summary>
    [Test]
    public async Task OnceTheMemberHasDisconnected_TheNextShrinkIsAllowed()
    {
        // Arrange
        ArrangeIdlePool(minReplicas: 1, members: 3);

        // Act
        await RunPastTheIdleWindowAsync();
        var leaving = ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId.ToString())
            .First(m => m.IsDraining);
        ConnectionManager.RemoveMember(leaving.ConnectionId);
        await Scheduler.RunSchedulingRoundAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, 2);
        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, 1);
    }

    /// <summary>
    ///     🔴 Seeded when the pool is FIRST SEEN in a round, not from zero. The pool below has been
    ///     idle for longer than its window before this controller ever looked at it — a fresh process
    ///     after a rolling upgrade sees exactly this — and the first round must still not shrink it,
    ///     because "idle since forever" is what a controller that has just started cannot tell apart
    ///     from "scaled up one second ago by the pod that went away".
    /// </summary>
    [Test]
    public async Task AFreshlySeenPool_IsNotShrunkByTheRoundThatFirstSeesIt()
    {
        // Arrange
        ArrangeIdlePool(minReplicas: 1, members: 2);
        await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 0.3));

        // Act
        await Scheduler.RunSchedulingRoundAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.DidNotReceive().DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());

        // And the delay really was long enough — the seeding is what held it, not the clock.
        await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 0.3));
        await Scheduler.RunSchedulingRoundAsync();
        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, 1);
    }

    /// <summary>
    ///     Queued work is the whole reason the pool has the members it has. A non-empty queue is not
    ///     evaluated for a shrink at all, however long nothing has been granted.
    /// </summary>
    [Test]
    public async Task AQueueWithWorkInIt_IsNeverShrunk()
    {
        // Arrange — two members, one of them busy, so nothing can be granted and the queue stays
        // deep across both rounds.
        ArrangeIdlePool(minReplicas: 1, members: 0);
        ArrangeBusyMember(TenantB);
        ArrangeQueue(TenantA, Entry("a-0", minutesAgo: 10));

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.DidNotReceive().DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     🔴 The gate is "the WHOLE pool is idle", not "this member is idle". Scaling a Deployment
    ///     down lets the ReplicaSet controller choose the pod that dies and the controller cannot steer
    ///     it, so a single member holding a lease has to stop the shrink — the member Kubernetes picks
    ///     might be that one.
    /// </summary>
    [Test]
    public async Task AMemberHoldingALease_StopsTheShrink()
    {
        // Arrange — an empty queue, two members, one of them leased out to a borrower.
        ArrangeIdlePool(minReplicas: 1, members: 1);
        ArrangeBusyMember(TenantB);

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.DidNotReceive().DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     The window has to have elapsed. Two rounds back to back are the shape of a pool that was
    ///     busy a moment ago, and the cold start of a member is exactly what the timeout is trading
    ///     against.
    /// </summary>
    [Test]
    public async Task BeforeTheIdleWindowHasElapsed_NothingIsDrained()
    {
        // Arrange
        ArrangeIdlePool(minReplicas: 1, members: 2);
        Options.LeaseIdleShrinkWindowSeconds = 600;

        // Act
        await Scheduler.RunSchedulingRoundAsync();
        await Scheduler.RunSchedulingRoundAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.DidNotReceive().DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     <c>MinReplicas</c> is the floor and a pool sitting on it is finished shrinking. Asked for
    ///     less and corrected by the clamp would make the log line disagree with what the pool does.
    /// </summary>
    [Test]
    public async Task AtMinReplicas_NothingIsDrained()
    {
        // Arrange
        ArrangeIdlePool(minReplicas: 2, members: 2);

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.DidNotReceive().DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     A shrink never asks for less than the floor in the first place, rather than relying on
    ///     <c>ClampToAdapterPoolRange</c> to correct it afterwards.
    /// </summary>
    [Test]
    public async Task TheRequestNeverGoesBelowMinReplicas()
    {
        // Arrange — three members against a floor of two: one may go, the second may not.
        ArrangeIdlePool(minReplicas: 2, members: 3);

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, AdapterPoolRtId, 2);
    }

    /// <summary>
    ///     A pool entity that could not be read carries neither a floor nor a timeout, and a shrink on
    ///     assumed defaults is the one thing this must never do — the same stance
    ///     <c>EvaluateScaleUpAsync</c> takes.
    /// </summary>
    [Test]
    public async Task WithoutThePoolEntity_NothingIsDrained()
    {
        // Arrange — borrower and members, but no pool entity behind them.
        Options.LeaseIdleShrinkWindowSeconds = WindowSeconds;
        ArrangeQueue(TenantA);
        ArrangeMembers(2);

        // Act
        await RunPastTheIdleWindowAsync();

        // Assert
        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await LeaseService.DidNotReceive().DrainMemberAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    ///     ⚠️ <b>Log volume.</b> A round runs every five seconds per pool, so the evaluation itself has
    ///     to be silent — the same reasoning that moved the pool-exhaustion line onto its transition
    ///     and keeps depth, wait and member states in metrics only. A shrink is an event and says so
    ///     once; a round that decides against one says nothing at all, in the log or in the tenant's
    ///     event log.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Asserted against a real <c>MemoryTarget</c> rather than against a specific statement, for
    ///         the same reason <c>LeaseSecretLogTargetTests</c> is: a probe that inspected one call site
    ///         would pass while a line added next to it flooded the log.
    ///     </para>
    ///     <para>
    ///         🔴 NLog's configuration is process-wide, and so is the target this installs — every test
    ///         running concurrently writes into it. The assertion therefore counts only the lines that
    ///         name <b>this</b> test's pool, which every line the shrink can write does. The second half
    ///         is the positive control: with the window shortened the same pool produces exactly one
    ///         line, so an empty first half cannot be an empty probe.
    ///     </para>
    /// </remarks>
    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task TheEvaluationIsSilentAndOnlyAnActualShrinkSpeaks()
    {
        // Arrange — everything true except the elapsed window, which is the state a pool spends
        // virtually all of its life in.
        ArrangeIdlePool(minReplicas: 1, members: 2);
        Options.LeaseIdleShrinkWindowSeconds = 600;

        var memoryTarget = new NLog.Targets.MemoryTarget("idle-shrink-probe")
        {
            Layout = "${level}|${message}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            // Act — four rounds, i.e. twenty seconds of a pool nobody is using.
            for (var round = 0; round < 4; round++)
            {
                await Scheduler.RunSchedulingRoundAsync();
            }

            var whileEvaluating = LinesAboutThisPool(memoryTarget);

            // And now the same pool with a window it has already passed.
            Options.LeaseIdleShrinkWindowSeconds = WindowSeconds;
            await Task.Delay(TimeSpan.FromSeconds(WindowSeconds + 0.3));
            await Scheduler.RunSchedulingRoundAsync();

            var afterShrinking = LinesAboutThisPool(memoryTarget);

            // Assert
            using var _ = Assert.Multiple();
            await Assert.That(whileEvaluating).IsEmpty();
            await Assert.That(afterShrinking).Count().IsEqualTo(1);
            await EventService.Received(1)
                .StoreInformationEventAsync(LenderTenantId, Arg.Any<string>());
            await EventService.DidNotReceive().StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    private List<string> LinesAboutThisPool(NLog.Targets.MemoryTarget target)
    {
        return target.Logs
            .Where(l => l.Contains(AdapterPoolRtId.ToString(), StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    ///     Which value the window comes from, pinned. <c>IdleTimeoutMinutes</c> is what the model and
    ///     concept §4a declare it to be; the controller option overrides every pool at once. The two
    ///     differ by three orders of magnitude, so reading the wrong one is invisible in any single
    ///     round — the mistake AB#5329 already made once with a memo retention window.
    /// </summary>
    [Test]
    public async Task TheWindowComesFromThePoolUnlessTheOptionOverridesIt()
    {
        // Arrange
        var pool = ArrangePool();
        pool.IdleTimeoutMinutes = 30;

        // Act
        Options.LeaseIdleShrinkWindowSeconds = 0;
        var fromThePool = Service.ResolveIdleShrinkWindow(pool);

        Options.LeaseIdleShrinkWindowSeconds = 5;
        var fromTheOption = Service.ResolveIdleShrinkWindow(pool);

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(fromThePool).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(fromTheOption).IsEqualTo(TimeSpan.FromSeconds(5));
    }
}
