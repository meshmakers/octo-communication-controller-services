using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#7476 — <b>a sealed queued execution is leased at most once.</b> The status and the timestamps of a
///     <c>PipelineExecution</c> are writable through generic surfaces, so a finished execution can be set back to
///     <c>Queued</c>. The seal (caller AND method-invocation marker) then still opens; only the controller-owned claim
///     record tells that it was used.
/// </summary>
/// <remarks>
///     The claim store is an in-memory stand-in with the same insert-if-absent rule as the MongoDB primary key; the
///     real collection is covered by the integration tests (<c>ExecutionLeaseClaimTests</c>).
/// </remarks>
internal class LeaseSingleUseClaimTests : LeaseServiceTestsBase
{
    private const string ExecutionId = "b7b9ca9b-0000-4000-8000-000000007476";

    private static ExecutePipelineMethodInvocation AMarker() => new()
    {
        MethodId = "Model-1/Type-1/Create", InvocationId = Guid.NewGuid(), CkTypeId = "Model-1/Type-1",
        PipelineRtId = PipelineRtId.ToString()
    };

    private (RtPipelineExecution Execution, LeaseRequest Request) AQueuedWork(
        ExecutePipelineMethodInvocation? marker = null, DateTime? sealedAt = null)
    {
        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(), ExecutionId = ExecutionId,
            Status = RtPipelineExecutionStatusEnum.Queued
        };
        QueuedCaller.Apply(execution, BorrowerTenantId, PipelineRtId, BorrowerAdapterRtId,
            new ExecutePipelineCaller { SubjectId = "user-42", TenantId = BorrowerTenantId, TrustLevel = 2 }, null,
            Envelope, marker, sealedAt);
        return (execution, WorkRequestFor(execution));
    }

    private LeaseRequest WorkRequestFor(RtPipelineExecution execution) =>
        new(BorrowerTenantId, BorrowerAdapterRtId, ExecutionId, null, PipelineRtId, null, execution.RtId,
            execution.SealedCaller);

    private int PushedLeases() => MemberProxy.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync));

    /// <summary>The state after the first run: the member released, the execution reset to Queued, time passed.</summary>
    private void ResetAfterRun(LeaseGrantResult first, TimeSpan age)
    {
        ConnectionManager.ReleaseLease(ConnectionId, first.LeaseId!);
        ClaimStore.AgeAll(age);
    }

    [Test]
    public async Task AFreshSealIsLeasedOnce_AndLeavesOneClaim()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (_, request) = AQueuedWork();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(ClaimStore.Count).IsEqualTo(1);
    }

    /// <summary>The core of the bug: a reset of a finished execution must not lead to a second lease.</summary>
    [Test]
    public async Task AResetExecution_WithTheSameSeal_IsNotLeasedASecondTime_FailedAndLoggedWithoutValues()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (execution, request) = AQueuedWork();
        var claimId = QueuedCaller.Open(execution.SealedCaller, BorrowerTenantId, execution.RtId, Envelope)!.ClaimId!;
        var first = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);
        ResetAfterRun(first, TimeSpan.FromMinutes(10));

        var memoryTarget = new NLog.Targets.MemoryTarget("claim-probe") { Layout = "${level}|${message}" };
        var previous = NLog.LogManager.Configuration;
        var probe = new NLog.Config.LoggingConfiguration();
        probe.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probe;
        LeaseGrantResult second;
        try
        {
            second = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);
        }
        finally
        {
            NLog.LogManager.Configuration = previous;
        }

        using var _ = Assert.Multiple();
        await Assert.That(first.Granted).IsTrue();
        await Assert.That(second.Granted).IsFalse();
        await Assert.That(second.Reason).IsEqualTo(LeaseRefusalReason.QueuedProvenanceInvalid);
        await Assert.That(PushedLeases()).IsEqualTo(1);
        // terminal: failed by RtId so the queue does not retry it every round
        await CommunicationRepository.Received(1).TryFailQueuedExecutionAsync(BorrowerTenantId, execution.RtId,
            Arg.Any<string>());
        // WARN, naming the execution, never the identity or any sealed value
        await Assert.That(memoryTarget.Logs.Any(l => l.StartsWith("Warn|", StringComparison.Ordinal))).IsTrue();
        await Assert.That(memoryTarget.Logs.Any(l => l.Contains(claimId, StringComparison.Ordinal))).IsFalse();
        await Assert.That(memoryTarget.Logs.Any(l => l.Contains(execution.SealedCaller!, StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(second.StatusMessage).DoesNotContain(claimId);
    }

    /// <summary>The marker lives in the same envelope: a reset method run is not leased a second time either.</summary>
    [Test]
    public async Task AResetMethodRun_IsNotLeasedASecondTime_SoTheMarkerIsNotGrantedTwice()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var marker = AMarker();
        var (_, request) = AQueuedWork(marker);
        var first = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);
        var firstMarker = CapturePushedLease().MethodInvocation;
        ResetAfterRun(first, TimeSpan.FromMinutes(10));

        var second = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(firstMarker).IsEqualTo(marker);
        await Assert.That(second.Granted).IsFalse();
        await Assert.That(second.Reason).IsEqualTo(LeaseRefusalReason.QueuedProvenanceInvalid);
        await Assert.That(PushedLeases()).IsEqualTo(1);
    }

    /// <summary>The token strip at grant rewrites the stored envelope; the identity must survive it.</summary>
    [Test]
    public async Task StrippingTheToken_KeepsTheSingleUseIdentityAndTheIssueTime()
    {
        var (execution, _) = AQueuedWork();
        var before = QueuedCaller.Open(execution.SealedCaller, BorrowerTenantId, execution.RtId, Envelope)!;

        var stripped = QueuedCaller.StripToken(execution.SealedCaller, BorrowerTenantId, execution.RtId, Envelope);
        var after = QueuedCaller.Open(stripped, BorrowerTenantId, execution.RtId, Envelope)!;

        using var _ = Assert.Multiple();
        await Assert.That(after.ClaimId).IsEqualTo(before.ClaimId);
        await Assert.That(after.IssuedAtUtc).IsEqualTo(before.IssuedAtUtc);
    }

    /// <summary>
    ///     The strip is also what is stored after the first grant: presenting that stored value again (the reset
    ///     execution) is the replay.
    /// </summary>
    [Test]
    public async Task TheStoredValueAfterTheGrant_IsAlsoRefused_WhenTheExecutionIsResetToQueued()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (execution, request) = AQueuedWork();
        var first = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);
        execution.SealedCaller = QueuedCaller.StripToken(execution.SealedCaller, BorrowerTenantId, execution.RtId, Envelope);
        ResetAfterRun(first, TimeSpan.FromMinutes(10));

        var second = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, WorkRequestFor(execution));

        await Assert.That(second.Granted).IsFalse();
        await Assert.That(PushedLeases()).IsEqualTo(1);
    }

    /// <summary>A retry is a new work item: it is sealed again with a new identity and is leasable once.</summary>
    [Test]
    public async Task ARequeue_SealsWithANewIdentity_AndTheRetryIsLeasableOnce()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (execution, request) = AQueuedWork(AMarker());
        var first = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);
        var retry = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(), ExecutionId = ExecutionId,
            Status = RtPipelineExecutionStatusEnum.Queued
        };
        retry.SealedCaller = QueuedCaller.Reseal(execution.SealedCaller, BorrowerTenantId, execution.RtId, retry.RtId,
            Envelope);
        var oldIdentity = QueuedCaller.Open(execution.SealedCaller, BorrowerTenantId, execution.RtId, Envelope)!.ClaimId;
        var newIdentity = QueuedCaller.Open(retry.SealedCaller, BorrowerTenantId, retry.RtId, Envelope)!.ClaimId;
        ResetAfterRun(first, TimeSpan.FromMinutes(10));

        var second = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, WorkRequestFor(retry));
        ConnectionManager.ReleaseLease(ConnectionId, second.LeaseId!);
        var third = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, WorkRequestFor(retry));

        using var _ = Assert.Multiple();
        await Assert.That(newIdentity).IsNotNull().And.IsNotEqualTo(oldIdentity);
        await Assert.That(second.Granted).IsTrue();
        await Assert.That(third.Granted).IsFalse();
    }

    /// <summary>
    ///     A second controller instance that reads the same queue loses the insert: refused, but not failed, because
    ///     the winner is starting the execution right now.
    /// </summary>
    [Test]
    public async Task AGrantOfTheSameSealWhileAnotherInstanceHoldsTheClaim_IsDeclined_NotFailed()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (_, request) = AQueuedWork();
        var first = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);
        ConnectionManager.ReleaseLease(ConnectionId, first.LeaseId!);

        var second = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(second.Granted).IsFalse();
        await Assert.That(second.Reason).IsEqualTo(LeaseRefusalReason.AdmissionGateDeclined);
        await CommunicationRepository.DidNotReceiveWithAnyArgs().TryFailQueuedExecutionAsync(default!, default,
            default!);
    }

    [Test]
    public async Task TwoGrantsOfOneSeal_RunningAtTheSameTime_LeaseItExactlyOnce()
    {
        ArrangeGrantableLease();
        ConnectionManager.RegisterMember("conn-pool-member-2", "octo-pool-1", LenderTenantId,
            AdapterPoolRtId.ToString());
        ArrangeProjectablePipeline();
        var (_, request) = AQueuedWork();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request)));

        await Assert.That(results.Count(r => r.Granted)).IsEqualTo(1);
    }

    /// <summary>A gate that did not start the execution gives the claim back; the seal was not used.</summary>
    [Test]
    public async Task ADeclinedAdmissionGate_GivesTheClaimBack_SoTheSealIsStillLeasable()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (_, request) = AQueuedWork();

        var declined = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request,
            admissionGate: (_, _, _) => Task.FromResult(false));
        var retried = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(declined.Granted).IsFalse();
        await Assert.That(retried.Granted).IsTrue();
    }

    [Test]
    public async Task AThrowingAdmissionGate_GivesTheClaimBack_TooAndTheMemberIsFree()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (_, request) = AQueuedWork();

        var failed = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request,
            admissionGate: (_, _, _) => throw new InvalidOperationException("boom"));
        var retried = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(failed.Granted).IsFalse();
        await Assert.That(retried.Granted).IsTrue();
    }

    /// <summary>Without the claim record a seal cannot be proven unused: no lease, the item stays queued.</summary>
    [Test]
    public async Task AnUnreachableClaimStore_GrantsNothing_AndFailsNothing()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (_, request) = AQueuedWork();
        ClaimStore.Unavailable = true;

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(PushedLeases()).IsEqualTo(0);
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId.ToString())
            .All(m => m.IsAvailable)).IsTrue();
        await CommunicationRepository.DidNotReceiveWithAnyArgs().TryFailQueuedExecutionAsync(default!, default,
            default!);
    }

    /// <summary>
    ///     An execution queued before the upgrade has no identity. Fail closed: it is not leased and not migrated
    ///     (adopting it would create exactly the unclaimable seal this change removes).
    /// </summary>
    [Test]
    public async Task ASealFromBeforeTheUpgrade_WithoutAnIdentity_IsRefusedAndFailed()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var execution = new RtPipelineExecution { RtId = OctoObjectId.GenerateNewId(), ExecutionId = ExecutionId };
        var emptyInputHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData([]));
        execution.SealedCaller = Envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller, new
        {
            Provenance = 1, PipelineRtId = PipelineRtId.ToString(), AdapterRtId = BorrowerAdapterRtId.ToString(),
            InputDataHash = emptyInputHash, SubjectId = "user-42", TenantId = BorrowerTenantId, TrustLevel = 2
        }, BorrowerTenantId, execution.RtId);

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, WorkRequestFor(execution));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(LeaseRefusalReason.QueuedProvenanceInvalid);
        await Assert.That(ClaimStore.Count).IsEqualTo(0);
        await CommunicationRepository.Received(1).TryFailQueuedExecutionAsync(BorrowerTenantId, execution.RtId,
            Arg.Any<string>());
    }

    /// <summary>
    ///     The claim records are deleted after the accepted age; a seal older than that must therefore be refused, or
    ///     the expiry of a record would make an old seal usable again.
    /// </summary>
    [Test]
    public async Task ASealOlderThanTheAcceptedAge_IsRefusedAndFailed_EvenWithoutAClaimRecord()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var (execution, request) = AQueuedWork(sealedAt: DateTime.UtcNow.AddDays(-31));

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, request);

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(LeaseRefusalReason.QueuedProvenanceInvalid);
        await Assert.That(ClaimStore.Count).IsEqualTo(0);
        await CommunicationRepository.Received(1).TryFailQueuedExecutionAsync(BorrowerTenantId, execution.RtId,
            Arg.Any<string>());
    }

    [Test]
    public async Task AHandDrivenLease_ConsumesNoClaim_BecauseItCarriesNoWork()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-hand"));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(ClaimStore.Count).IsEqualTo(0);
    }
}
