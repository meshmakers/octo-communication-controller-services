using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 §9.9 / D4 — <b>a lease must carry the work.</b>
/// </summary>
/// <remarks>
///     <para>
///         Increment 7 queued, rotated, granted, claimed, reaped and re-queued correctly, and a member
///         that received a lease still had nothing to run: <c>LeaseDto</c> carried an execution id and
///         nothing else, and there was no controller endpoint a member could ask. The resolution chosen
///         over "send an <c>ExecutePipelineRequest</c> after the grant" is this one, and the reason is
///         the entity count: the queued <c>PipelineExecution</c> already exists and the claim already
///         moved it <c>Queued → Running</c>, so a second entity would mean a reconciliation step and
///         two billing spans for one piece of work (concept §4b).
///     </para>
///     <para>
///         🔴 The projection is resolved <b>before</b> a member is reserved. A pipeline that cannot be
///         projected is a refusal, and refusing after a member was claimed would cost the pool a member
///         for every round the pipeline stays broken.
///     </para>
/// </remarks>
internal class LeaseCarriesTheWorkTests : LeaseServiceTestsBase
{
    [Test]
    public async Task AGrantedLeaseNamesThePipelineAndCarriesItsInputAndConfiguration()
    {
        ArrangeGrantableLease();
        var projection = ArrangeProjectablePipeline();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();

        var lease = CapturePushedLease();
        await Assert.That(lease.ExecutionId).IsEqualTo("exec-1");
        await Assert.That(lease.PipelineRtId).IsEqualTo(PipelineRtId.ToString());
        await Assert.That(lease.PipelineInput).IsEqualTo(PipelineInput);
        await Assert.That(lease.Pipeline).IsNotNull();
        await Assert.That(lease.Pipeline!.NodeConfiguration).IsEqualTo(projection.NodeConfiguration);
        await Assert.That(lease.Pipeline.PipelineRtEntityId.RtId).IsEqualTo(PipelineRtId);
    }

    /// <summary>
    ///     The projection is the controller's, and it is asked for the <b>borrower's</b> adapter — the
    ///     one the lease was granted for. Asking for any other adapter would hand a member a pipeline
    ///     whose credential is not the one on its lease.
    /// </summary>
    [Test]
    public async Task TheProjectionIsAskedForTheBorrowersOwnAdapter()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        await AdapterService.Received(1).GetLeasedPipelineConfigurationAsync(BorrowerTenantId,
            Arg.Is<RtEntityId>(id => id.RtId == Borrower.RtId), PipelineRtId);
    }

    /// <summary>
    ///     🔴 A pipeline the controller cannot project is a <b>refusal with a named reason</b>, not a
    ///     lease with nothing in it. The member must stay available — the work item stays queued and
    ///     the next round serves somebody else rather than the pool shrinking by one.
    /// </summary>
    [Test]
    public async Task AnUnprojectablePipelineRefusesTheLeaseAndNeverReservesAMember()
    {
        ArrangeGrantableLease();
        ArrangeUnprojectablePipeline();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains(PipelineRtId.ToString());
        // Nothing was pushed, and the member is still idle.
        await Assert.That(MemberProxy.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))).IsFalse();
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId.ToString())
            .All(m => m.IsAvailable)).IsTrue();
        // The borrower can see why its queue entry did not move.
        await EventService.Received().StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains(PipelineRtId.ToString())));
    }

    /// <summary>
    ///     A hand-driven lease (increment 6's <c>POST {tenantId}/v1/adapterPool/{id}/lease</c>) names
    ///     no pipeline and stays legal: it carries no work, the member reports "nothing to run", and
    ///     the projection is never asked for.
    /// </summary>
    [Test]
    public async Task AHandDrivenLeaseCarriesNoWorkAndAsksForNoProjection()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();

        var lease = CapturePushedLease();
        await Assert.That(lease.PipelineRtId).IsEqualTo(string.Empty);
        await Assert.That(lease.Pipeline).IsNull();
        await Assert.That(lease.PipelineInput).IsNull();
        await AdapterService.DidNotReceiveWithAnyArgs()
            .GetLeasedPipelineConfigurationAsync(default!, default!, default!);
    }

    /// <summary>
    ///     🔴 The output of a leased execution has exactly one way home. A dedicated adapter reports it
    ///     on <c>IAdapterHub.ReportExecutionEndAsync</c>, whose tenant and adapter come from the
    ///     <i>connection</i>; a pool member holds a tenant-free management channel and has no such
    ///     connection. Without the release carrying it, a leased execution would complete with an empty
    ///     <c>OutputData</c> where a dedicated adapter would have filled it.
    /// </summary>
    [Test]
    public async Task TheReleaseCarriesThePipelineOutputOntoTheExistingExecution()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());
        var lease = CapturePushedLease();

        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, "exec-1")
            .Returns(new ConstructionKit.Models.System.Communication.Generated.System.Communication.v4
                .RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = "exec-1",
                Status = ConstructionKit.Models.System.Communication.Generated.System.Communication.v4
                    .RtPipelineExecutionStatusEnum.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-2)
            });

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = lease.LeaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            StatusMessage = "ok",
            OutputData = "{\"total\":42}",
            ReleasedAtUtc = DateTime.UtcNow
        });

        using var _ = Assert.Multiple();
        // 🔴 ONE entity: the existing execution is UPDATED by its id. Nothing on this path creates a
        // second one, and CreatePipelineExecutionAsync is never reached.
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, "exec-1",
            Arg.Any<ConstructionKit.Models.System.Communication.Generated.System.Communication.v4
                .RtPipelineExecutionStatusEnum>(),
            Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<string?>(), "{\"total\":42}");
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .CreatePipelineExecutionAsync(default!, default!, default!, default!);
        await CommunicationRepository.Received(1)
            .StampLeaseReleasedAsync(BorrowerTenantId, "exec-1", Arg.Any<DateTime>());
    }

    // ---- AB#5279: the lease carries the invoker ---------------------------------------------

    private static ExecutePipelineCaller ACaller() => new()
        { SubjectId = "user-42", TenantId = BorrowerTenantId, Name = "User 42", Roles = ["Admin"], TrustLevel = 2 };

    /// <summary>A compact JWT whose only claim is <c>exp</c>; the signature is not looked at.</summary>
    private static string AJwtExpiringAt(DateTimeOffset expiresAt)
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"eyJhbGciOiJub25lIn0.{payload}.sig";
    }

    private static readonly OctoObjectId ExecutionRtId = OctoObjectId.GenerateNewId();

    private LeaseRequest AWorkRequestFrom(string? sealedCaller, OctoObjectId? executionRtId = null) =>
        new(BorrowerTenantId, Borrower.RtId, "exec-1", null, PipelineRtId, PipelineInput,
            executionRtId ?? ExecutionRtId, sealedCaller);

    /// <summary>The value the enqueue writes: the invoker sealed for the borrower tenant and ONE execution.</summary>
    private string? Sealed(string? token, OctoObjectId? executionRtId = null, string tenantId = BorrowerTenantId,
        ExecutionEnvelope? envelope = null, ExecutePipelineCaller? caller = null)
    {
        var execution = new RtPipelineExecution { RtId = executionRtId ?? ExecutionRtId, InputData = PipelineInput };
        QueuedCaller.Apply(execution, tenantId, PipelineRtId, caller ?? ACaller(), token, envelope ?? Envelope);
        return execution.SealedCaller;
    }

    [Test]
    public async Task AGrantedLeaseCarriesTheVerifiedInvokerAndItsToken()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        var token = AJwtExpiringAt(DateTimeOffset.UtcNow.AddMinutes(30));

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId,
            AWorkRequestFrom(Sealed(token)));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        var lease = CapturePushedLease();
        await Assert.That(lease.Caller).IsNotNull();
        await Assert.That(lease.Caller!.SubjectId).IsEqualTo("user-42");
        await Assert.That(lease.Caller.TrustLevel).IsEqualTo(2);
        await Assert.That(lease.CallerAccessToken).IsEqualTo(token);
    }

    [Test]
    public async Task AnExpiredTokenIsDroppedButTheInvokerStillTravels()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId,
            AWorkRequestFrom(Sealed(AJwtExpiringAt(DateTimeOffset.UtcNow.AddMinutes(-5)))));

        using var _ = Assert.Multiple();
        var lease = CapturePushedLease();
        await Assert.That(lease.Caller!.SubjectId).IsEqualTo("user-42");
        // 🔴 A dead token would not fail the run at once - it would fail every delegated call inside
        // it with 401. Dropped up front, the run is a run as the caller without delegation.
        await Assert.That(lease.CallerAccessToken).IsNull();
    }

    // ---- AB#7472 / AB#7473: what is not provably the controller's own value is not leased -------

    private async Task AssertRefusedAndFailedAsync(string? stored, OctoObjectId? executionRtId = null,
        LeaseService? service = null)
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        var result = await (service ?? LeaseService).GrantLeaseAsync(LenderTenantId, AdapterPoolRtId,
            AWorkRequestFrom(stored, executionRtId));

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(LeaseRefusalReason.QueuedProvenanceInvalid);
        // never handed to a member ...
        await Assert.That(MemberProxy.ReceivedCalls().Any(c => c.GetMethodInfo().Name ==
                                                              nameof(IClientProxy.SendCoreAsync))).IsFalse();
        // ... and set to a terminal failed state with a reason that carries no value
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, "exec-1",
            RtPipelineExecutionStatusEnum.Failed, Arg.Any<DateTime?>(), Arg.Any<int?>(),
            Arg.Is<string?>(m => m != null && m.Contains("not queued by the controller") && !m.Contains("user-42")),
            Arg.Any<string?>());
    }

    [Test]
    public Task NoSealedValue_IsNotLeased() => AssertRefusedAndFailedAsync(null);

    [Test]
    public Task APlainTextValue_IsNotLeased() => AssertRefusedAndFailedAsync("user-42");

    [Test]
    public Task APlainTextJsonValue_IsNotLeased() => AssertRefusedAndFailedAsync(
        "{\"v\":1,\"p\":\"queued-caller\",\"t\":\"" + BorrowerTenantId + "\",\"e\":\"" + ExecutionRtId +
        "\",\"d\":{\"SubjectId\":\"admin\",\"TrustLevel\":2}}");

    [Test]
    public Task ALegacyEncV1Value_IsNotLeased() => AssertRefusedAndFailedAsync("enc:v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");

    [Test]
    public Task AValueSealedForAnotherExecution_IsNotLeased() =>
        AssertRefusedAndFailedAsync(Sealed("t", executionRtId: OctoObjectId.GenerateNewId()));

    [Test]
    public Task AValueSealedForAnotherTenant_IsNotLeased() =>
        AssertRefusedAndFailedAsync(Sealed("t", tenantId: "someone-else"));

    [Test]
    public Task AValueSealedWithAnotherKey_IsNotLeased() =>
        AssertRefusedAndFailedAsync(Sealed("t", envelope: TestExecutionEnvelope.Create()));

    [Test]
    public async Task AFlippedBit_IsNotLeased()
    {
        var sealedValue = Sealed("t")!;
        var chars = sealedValue.ToCharArray();
        var i = chars.Length - 4;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';
        await AssertRefusedAndFailedAsync(new string(chars));
    }

    [Test]
    public async Task ASecretOfAnotherAttribute_CopiedIntoTheValue_IsNotLeased()
    {
        // Any enc:v2 value of the same key ring - for instance a copied client secret - decrypts to something that
        // is not an envelope of this format.
        await AssertRefusedAndFailedAsync(
            TestSecretEncryption.Create().Protector.Protect("a-client-secret").Envelope);
    }

    [Test]
    public async Task AnEnvelopeOfAnotherPurpose_IsNotLeased()
    {
        var other = Envelope.Seal("another-purpose", new { SubjectId = "admin", TrustLevel = 2 }, BorrowerTenantId,
            ExecutionRtId);
        await AssertRefusedAndFailedAsync(other);
    }

    [Test]
    public async Task AValueSealedBeforeTheProvenanceMarker_IsNotLeased()
    {
        // The AB#7472 shape: caller only, no provenance marker, pipeline or input binding.
        var legacy = Envelope.Seal(ExecutionEnvelopePurposes.QueuedCaller,
            new QueuedCaller.Payload { SubjectId = "user-42", TrustLevel = 2 }, BorrowerTenantId, ExecutionRtId);
        await AssertRefusedAndFailedAsync(legacy);
    }

    [Test]
    public async Task AControllerQueuedValue_WhoseExecutionPointsAtAnotherPipelineNow_IsNotLeased()
    {
        var execution = new RtPipelineExecution { RtId = ExecutionRtId, InputData = PipelineInput };
        QueuedCaller.Apply(execution, BorrowerTenantId, OctoObjectId.GenerateNewId(), ACaller(), null, Envelope);
        await AssertRefusedAndFailedAsync(execution.SealedCaller);
    }

    [Test]
    public async Task AControllerQueuedValue_WhoseInputWasChangedAfterwards_IsNotLeased()
    {
        var execution = new RtPipelineExecution { RtId = ExecutionRtId, InputData = "{\"other\":1}" };
        QueuedCaller.Apply(execution, BorrowerTenantId, PipelineRtId, ACaller(), null, Envelope);
        await AssertRefusedAndFailedAsync(execution.SealedCaller);
    }

    [Test]
    public async Task WithoutAKeyRing_NothingOpens_AndTheExecutionIsFailed()
    {
        var noKeys = new LeaseService(ConnectionManager, CommunicationRepository, EventService, EncryptionService,
            TestExecutionEnvelope.Create(withKeyRing: false), HubContext, LendingScopeResolver, ServiceAccountResolver,
            DatabaseCredentialResolver, AdapterService, LifecycleConfiguration, WakeSignal);

        await AssertRefusedAndFailedAsync(Sealed("t"), service: noKeys);
    }

    [Test]
    public async Task AWorkItemQueuedWithoutAnInvoker_IsLeasedWithoutOne_BecauseTheControllerSealedIt()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        var lease = CapturePushedLease();
        await Assert.That(lease.Caller).IsNull();
        await Assert.That(lease.CallerAccessToken).IsNull();
        await CommunicationRepository.DidNotReceiveWithAnyArgs().UpdatePipelineExecutionAsync(default!, default!,
            default, default, default, default, default);
    }
}
