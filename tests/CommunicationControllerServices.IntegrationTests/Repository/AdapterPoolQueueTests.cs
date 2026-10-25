using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#4924 increment 7 — the pool queue against real MongoDB.
/// </summary>
/// <remarks>
///     These pin what a mocked repository cannot: that a queued execution really has no
///     <c>StartedAt</c>, that the claim latch really is exclusive at the MongoDB filter level, and
///     that the lease span really lands on the entity. The lease-wait arithmetic is checked here
///     rather than in a unit test for the same reason — it is computed from a <c>QueuedAt</c> that
///     went through a round trip, and MongoDB stores dates at millisecond precision.
/// </remarks>
[Collection("CommunicationController")]
public class AdapterPoolQueueTests(CommunicationControllerFixture fixture)
{
    private const string LenderTenantId = "lender-tenant";
    private static readonly string PoolRtId = OctoObjectId.GenerateNewId().ToString();

    [Fact]
    public async Task EnqueueExecutionAsync_CreatesAQueuedEntryWithNoStartTime()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var queuedAt = DateTime.UtcNow.AddMinutes(-3);
            var executionId = await EnqueueAsync(data, pipeline, adapter, queuedAt);

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);

            loaded.Should().NotBeNull();
            loaded!.Status.Should().Be(RtPipelineExecutionStatusEnum.Queued);
            loaded.QueuedAt.Should().BeCloseTo(queuedAt, TimeSpan.FromMilliseconds(1));
            // 🔴 The whole reason StartedAt was relaxed to optional in 4.0.0: stamping it at enqueue
            // would make both AB#4280 reapers age a healthy queue wait out as a stuck execution.
            loaded.StartedAt.Should().BeNull();
            loaded.LeaseGrantedAt.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task TryClaimQueuedExecutionAsync_StampsTheLeaseSpanAndComputesLeaseWaitMs()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var queuedAt = DateTime.UtcNow.AddSeconds(-90);
            var executionId = await EnqueueAsync(data, pipeline, adapter, queuedAt);

            var grantedAt = queuedAt.AddSeconds(90);
            var claimed = await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, executionId,
                new LeaseClaim("lease-1", LenderTenantId, PoolRtId, "member-7", grantedAt));

            claimed.Should().BeTrue();

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);
            loaded!.Status.Should().Be(RtPipelineExecutionStatusEnum.Running);
            loaded.LeaseGrantedAt.Should().BeCloseTo(grantedAt, TimeSpan.FromMilliseconds(1));
            loaded.StartedAt.Should().BeCloseTo(grantedAt, TimeSpan.FromMilliseconds(1));
            // LeaseWaitMs = LeaseGrantedAt - QueuedAt. 90 s, within the millisecond the round trip
            // through MongoDB can cost.
            loaded.LeaseWaitMs.Should().BeInRange(89_999, 90_001);
            loaded.LeasedFromTenantId.Should().Be(LenderTenantId);
            loaded.LeasedFromAdapterPoolRtId.Should().Be(PoolRtId);
            loaded.LeasedOnMemberId.Should().Be("member-7");
            // Not released yet — the span is only half open at this point.
            loaded.LeaseReleasedAt.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 The claim latch, exercised in the shape the race actually has.
    /// </summary>
    /// <remarks>
    ///     Two controller pods, each holding different members of the same pool, can reach the claim
    ///     for the same work item. Both read it as <c>Queued</c>, then both write. Calling the method
    ///     twice in a row would NOT prove the latch — the second call's own pre-read already sees
    ///     <c>Running</c> and refuses. So the interleaving is reproduced deliberately: the status is
    ///     forced back to <c>Queued</c> behind the repository's back while <c>LeaseGrantedAt</c> stays
    ///     set, which is exactly what pod B observes when its read landed before pod A's write. Only
    ///     the <c>AttributeNewerThanGuard</c> on <c>leaseGrantedAt</c> stops B's write from applying,
    ///     and replacing the conditional update with a plain one fails this test and nothing else.
    /// </remarks>
    [Fact]
    public async Task TryClaimQueuedExecutionAsync_IsExclusiveAgainstAConcurrentClaimant()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var executionId = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddSeconds(-10));

            var first = await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, executionId,
                new LeaseClaim("lease-1", LenderTenantId, PoolRtId, "member-a", DateTime.UtcNow));
            first.Should().BeTrue();

            await ForceStatusBackToQueuedAsync(executionId);

            var second = await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, executionId,
                new LeaseClaim("lease-2", LenderTenantId, PoolRtId, "member-b", DateTime.UtcNow));

            second.Should().BeFalse();

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);
            loaded!.LeasedOnMemberId.Should().Be("member-a");
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     Writes <c>Status = Queued</c> straight onto the entity, leaving every lease attribute in
    ///     place. Reproduces what the losing claimant of a concurrent claim sees.
    /// </summary>
    private async Task ForceStatusBackToQueuedAsync(string executionId)
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var execution = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipelineExecution>>
            {
                EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(
                    new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution!.RtId),
                    new RtPipelineExecution { Status = RtPipelineExecutionStatusEnum.Queued })
            },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to reset the test execution: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
    }

    [Fact]
    public async Task TryCancelQueuedExecutionAsync_CancelsAWaitingEntryAndRefusesALeasedOne()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var waiting = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddSeconds(-10));
            var leased = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddSeconds(-10));
            await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, leased,
                new LeaseClaim("lease-1", LenderTenantId, PoolRtId, "member-a", DateTime.UtcNow));

            var cancelledWaiting = await repository.TryCancelQueuedExecutionAsync(fixture.TestTenantId, waiting,
                "cancelled by the operator");
            var cancelledLeased = await repository.TryCancelQueuedExecutionAsync(fixture.TestTenantId, leased, null);

            cancelledWaiting.Should().BeTrue();
            cancelledLeased.Should().BeFalse();

            var waitingEntity = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, waiting);
            waitingEntity!.Status.Should().Be(RtPipelineExecutionStatusEnum.Cancelled);
            waitingEntity.ErrorMessage.Should().Be("cancelled by the operator");

            // 🔴 The leased one is untouched: cancelling it means interrupting a running pipeline,
            // which is a different operation on a different path.
            var leasedEntity = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, leased);
            leasedEntity!.Status.Should().Be(RtPipelineExecutionStatusEnum.Running);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 <c>LeaseReleasedAt</c> on the TTL / crash path. It is a billing input (concept §4b), and
    ///     a span stamped only when everything went well silently under-bills the failures.
    /// </summary>
    [Fact]
    public async Task TryInterruptLeasedExecutionAsync_StampsLeaseReleasedAtAndReturnsTheEdges()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var executionId = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddSeconds(-30),
                inputData: "{\"x\":1}", triggerType: RtPipelineTriggerTypeEnum.Scheduled);
            await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, executionId,
                new LeaseClaim("lease-1", LenderTenantId, PoolRtId, "member-a", DateTime.UtcNow.AddSeconds(-20)));

            var releasedAt = DateTime.UtcNow;
            var interrupted = await repository.TryInterruptLeasedExecutionAsync(fixture.TestTenantId, executionId,
                releasedAt, "the lease expired");

            interrupted.Should().NotBeNull();
            interrupted!.PipelineRtEntityId.RtId.Should().Be(pipeline.RtId);
            interrupted.AdapterRtEntityId.RtId.Should().Be(adapter.RtId);
            interrupted.TriggerType.Should().Be(RtPipelineTriggerTypeEnum.Scheduled);
            interrupted.InputData.Should().Be("{\"x\":1}");

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);
            loaded!.Status.Should().Be(RtPipelineExecutionStatusEnum.Interrupted);
            loaded.LeaseReleasedAt.Should().BeCloseTo(releasedAt, TimeSpan.FromMilliseconds(1));
            loaded.LeaseGrantedAt.Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 AB#5864 — a lease the member refused without running it is re-queued at the position it
    ///     held. The interrupt hands back the original <c>QueuedAt</c>, and a retry enqueued with it
    ///     is served before work that arrived later — against real MongoDB, because the queue order
    ///     is the <c>QueuedAt</c> index and not anything a substitute could get wrong visibly.
    /// </summary>
    [Fact]
    public async Task ARefusedLease_RequeuedWithItsOriginalQueuedAt_KeepsItsPlaceInTheQueue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var originalQueuedAt = DateTime.UtcNow.AddSeconds(-30);
            var refused = await EnqueueAsync(data, pipeline, adapter, originalQueuedAt);
            var later = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddSeconds(-10));
            await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, refused,
                new LeaseClaim("lease-refused", LenderTenantId, PoolRtId, "member-draining", DateTime.UtcNow));

            var interrupted = await repository.TryInterruptLeasedExecutionAsync(fixture.TestTenantId, refused,
                DateTime.UtcNow, "refused by a draining member");

            interrupted.Should().NotBeNull();
            interrupted!.QueuedAt.Should().BeCloseTo(originalQueuedAt, TimeSpan.FromMilliseconds(1));

            var retryId = await EnqueueAsync(data, interrupted.PipelineRtEntityId, interrupted.AdapterRtEntityId,
                interrupted.QueuedAt!.Value);

            var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 10);
            queue.Select(q => q.ExecutionId).Should().ContainInOrder(retryId, later);
            queue.Select(q => q.ExecutionId).Should().NotContain(refused);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task GetQueuedExecutionsForAdapterAsync_ReturnsOnlyThisAdaptersQueuedWork()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var otherAdapter = await CreateAdapterAsync(data);

            var mineOld = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-10));
            var mineNew = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-1));
            var theirs = await EnqueueAsync(data, pipeline, otherAdapter, DateTime.UtcNow.AddMinutes(-5));
            // A running execution of the same adapter is not queue content.
            var running = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-20));
            await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, running,
                new LeaseClaim("lease-1", LenderTenantId, PoolRtId, "member-a", DateTime.UtcNow));

            var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 100);

            queue.Select(q => q.ExecutionId).Should().Equal(mineOld, mineNew);
            queue.Should().NotContain(q => q.ExecutionId == theirs);
            queue.Should().OnlyContain(q => q.PipelineRtId == pipeline.RtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     The position a surface shows has to be the order the scheduler will actually serve, which
    ///     is class first and arrival second — not arrival alone.
    /// </summary>
    [Fact]
    public async Task GetQueuedExecutionPositionAsync_PutsInteractiveAheadOfAnOlderBatch()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var dataFlow = await CreateDataFlowAsync(data);
            var adapter = await CreateAdapterAsync(data);
            var batchPipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter,
                RtPipelineExecutionClassEnum.Batch);
            var interactivePipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter,
                RtPipelineExecutionClassEnum.Interactive);

            var olderBatch = await EnqueueAsync(data, batchPipeline, adapter, DateTime.UtcNow.AddMinutes(-30));
            var newerInteractive =
                await EnqueueAsync(data, interactivePipeline, adapter, DateTime.UtcNow.AddMinutes(-1));

            var interactivePosition =
                await repository.GetQueuedExecutionPositionAsync(fixture.TestTenantId, newerInteractive);
            var batchPosition = await repository.GetQueuedExecutionPositionAsync(fixture.TestTenantId, olderBatch);

            interactivePosition.Should().Be(1);
            batchPosition.Should().Be(2);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     AB#4924 §9.9 / D4 — the input the work item was queued with reaches the scheduler, which is
    ///     what puts it on the lease. Pinned over real MongoDB because the projection reads it off the
    ///     entity rather than off anything the caller still holds.
    /// </summary>
    [Fact]
    public async Task GetQueuedExecutionsForAdapterAsync_CarriesTheInputTheItemWasQueuedWith()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            const string input = "{\"invoiceNumber\":\"BORROWER-PRIVATE-4711\"}";
            var withInput = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-2), input);
            var withoutInput = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-1));

            var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 100);

            queue.Single(q => q.ExecutionId == withInput).InputData.Should().Be(input);
            // 🔴 Null, not empty. "no input at all" and "an empty input" are different pipeline inputs,
            // and a node that branches on presence would see the wrong one.
            queue.Single(q => q.ExecutionId == withoutInput).InputData.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    private ExecutionEnvelope Envelope => (ExecutionEnvelope)fixture.GetService<IExecutionEnvelope>();

    /// <summary>Enqueues an execution whose invoker is sealed exactly as the enqueue path does.</summary>
    private async Task<RtPipelineExecution> EnqueueWithSealedCallerAsync(TestData data, RtEntityId pipeline,
        RtEntityId adapter, string subject = "user-42", string[]? roles = null, string? token = "a-bearer-token",
        ExecutePipelineMethodInvocation? methodInvocation = null)
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            ExecutionId = Guid.NewGuid().ToString(),
            TriggerType = RtPipelineTriggerTypeEnum.Manual
        };
        QueuedCaller.Apply(execution, fixture.TestTenantId, pipeline.RtId, adapter.RtId,
            new ExecutePipelineCaller
            {
                SubjectId = subject, TenantId = fixture.TestTenantId, Email = "u@example.test", Name = "User 42",
                Roles = roles ?? ["Reader"], TrustLevel = 2
            }, token, Envelope, methodInvocation);
        await repository.EnqueueExecutionAsync(fixture.TestTenantId, execution, pipeline, adapter,
            DateTime.UtcNow.AddMinutes(-1));
        data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId));
        return execution;
    }

    /// <summary>
    ///     What a generic runtime write does (GraphQL update mutation, MCP update_entity): replaces attributes of an
    ///     entity, with no knowledge of the controller's invariants.
    /// </summary>
    private async Task GenericUpdateAsync(OctoObjectId executionRtId, RtPipelineExecution patch)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipelineExecution>>
            {
                EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(
                    new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, executionRtId), patch)
            },
            operationResult);
        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Generic update failed: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
    }

    private async Task<QueuedExecution> QueuedAsync(RtEntityId adapter, RtPipelineExecution execution)
    {
        var queue = await fixture.GetService<ICommunicationRepository>()
            .GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 100);
        return queue.Single(q => q.ExecutionId == execution.ExecutionId);
    }

    /// <summary>
    ///     AB#5279 / AB#7472 — the sealed invoker survives the round trip through MongoDB and comes back on the
    ///     projection exactly as stored; it opens only for this tenant and this execution; the plain Caller*
    ///     attributes are not written.
    /// </summary>
    [Fact]
    public async Task GetQueuedExecutionsForAdapterAsync_CarriesTheSealedInvokerTheItemWasQueuedFor()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var execution = await EnqueueWithSealedCallerAsync(data, pipeline, adapter);
            var anonymous = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow);

            var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 100);

            var withCaller = queue.Single(q => q.ExecutionId == execution.ExecutionId);
            withCaller.ExecutionRtId.Should().Be(execution.RtId);
            withCaller.SealedCaller.Should().Be(execution.SealedCaller).And.StartWith("enc:v2:");
            var opened = QueuedCaller.Open(withCaller.SealedCaller, fixture.TestTenantId, withCaller.ExecutionRtId,
                Envelope);
            opened.Should().NotBeNull();
            opened!.Caller!.SubjectId.Should().Be("user-42");
            opened.Caller!.TenantId.Should().Be(fixture.TestTenantId);
            opened.Caller!.Email.Should().Be("u@example.test");
            opened.Caller!.Name.Should().Be("User 42");
            opened.Caller!.Roles.Should().Equal("Reader");
            opened.Caller!.TrustLevel.Should().Be(2);
            opened.AccessToken.Should().Be("a-bearer-token");
            QueuedCaller.Open(withCaller.SealedCaller, fixture.TestTenantId, OctoObjectId.GenerateNewId(), Envelope)
                .Should().BeNull();

            var stored = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!);
            stored!.CallerSubjectId.Should().BeNull();
            stored.CallerAccessToken.Should().BeNull();

            var withoutCaller = queue.Single(q => q.ExecutionId == anonymous);
            withoutCaller.SealedCaller.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     AB#7472, the acceptance test: a tenant user who can write the entity through a generic update changes
    ///     CallerRoles / CallerSubjectId / CallerTrustLevel / CallerAccessToken of a Queued execution. The lease must
    ///     still carry the original caller (and the plain attributes are not read at all).
    /// </summary>
    [Fact]
    public async Task AGenericUpdateOfTheCallerAttributes_DoesNotChangeTheInvokerTheLeaseCarries()
    {
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var execution = await EnqueueWithSealedCallerAsync(data, pipeline, adapter);

            await GenericUpdateAsync(execution.RtId, new RtPipelineExecution
            {
                CallerSubjectId = "administrator",
                CallerRoles = new AttributeStringValueList(["Admin", "TenantManagement"]),
                CallerTrustLevel = 2,
                CallerAccessToken = "a-forged-token"
            });

            var queued = await QueuedAsync(adapter, execution);
            var opened = QueuedCaller.Open(queued.SealedCaller, fixture.TestTenantId, queued.ExecutionRtId, Envelope);

            opened.Should().NotBeNull();
            opened!.Caller!.SubjectId.Should().Be("user-42");
            opened.Caller!.Roles.Should().Equal("Reader");
            opened.AccessToken.Should().Be("a-bearer-token");
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     The same user writes the sealed attribute itself: plain text, a forged JSON envelope, and the sealed value
    ///     of ANOTHER execution (a more privileged caller's). None of them opens: the lease carries no caller.
    /// </summary>
    [Fact]
    public async Task AGenericUpdateOfTheSealedAttribute_YieldsNoCaller()
    {
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var victim = await EnqueueWithSealedCallerAsync(data, pipeline, adapter);
            var privileged = await EnqueueWithSealedCallerAsync(data, pipeline, adapter, "administrator",
                ["Admin"], "admins-token");

            var forgedValues = new[]
            {
                "administrator",
                "{\"v\":1,\"p\":\"queued-caller\",\"t\":\"" + fixture.TestTenantId + "\",\"e\":\"" + victim.RtId +
                "\",\"d\":{\"SubjectId\":\"administrator\",\"Roles\":[\"Admin\"],\"TrustLevel\":2}}",
                privileged.SealedCaller!
            };

            foreach (var forged in forgedValues)
            {
                await GenericUpdateAsync(victim.RtId, new RtPipelineExecution { SealedCaller = forged });

                var queued = await QueuedAsync(adapter, victim);
                queued.SealedCaller.Should().Be(forged);
                QueuedCaller.Open(queued.SealedCaller, fixture.TestTenantId, queued.ExecutionRtId, Envelope)
                    .Should().BeNull();
            }

            // the privileged execution itself is untouched
            var untouched = await QueuedAsync(adapter, privileged);
            QueuedCaller.Open(untouched.SealedCaller, fixture.TestTenantId, untouched.ExecutionRtId, Envelope)!
                .Caller!.SubjectId.Should().Be("administrator");
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     AB#7472 — the delegation token is gone from the entity once the lease is granted (the claim), while the
    ///     caller stays so that a re-queue can still carry it.
    /// </summary>
    [Fact]
    public async Task TryClaimQueuedExecutionAsync_RemovesTheTokenButKeepsTheSealedCaller()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var execution = await EnqueueWithSealedCallerAsync(data, pipeline, adapter);

            var claimed = await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, execution.ExecutionId!,
                new LeaseClaim("lease-9", LenderTenantId, PoolRtId, "member-1", DateTime.UtcNow));

            claimed.Should().BeTrue();
            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!);
            loaded!.SealedCaller.Should().NotBe(execution.SealedCaller);
            var opened = QueuedCaller.Open(loaded.SealedCaller, fixture.TestTenantId, loaded.RtId, Envelope);
            opened.Should().NotBeNull();
            opened!.Caller!.SubjectId.Should().Be("user-42");
            opened.AccessToken.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     AB#7461 — the method-invocation marker survives MongoDB inside the sealed envelope and the claim (which
    ///     re-seals the value without the token) keeps it.
    /// </summary>
    [Fact]
    public async Task TryClaimQueuedExecutionAsync_KeepsTheMethodInvocationMarkerInTheSealedCaller()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var marker = new ExecutePipelineMethodInvocation
            {
                MethodId = "Model-1/Type-1/Create", InvocationId = Guid.NewGuid(), CkTypeId = "Model-1/Type-1",
                PipelineRtId = pipeline.RtId.ToString()
            };
            var execution = await EnqueueWithSealedCallerAsync(data, pipeline, adapter, methodInvocation: marker);

            var queued = await QueuedAsync(adapter, execution);
            QueuedCaller.Open(queued.SealedCaller, fixture.TestTenantId, queued.ExecutionRtId, Envelope)!
                .MethodInvocation.Should().Be(marker);

            (await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, execution.ExecutionId!,
                new LeaseClaim("lease-9", LenderTenantId, PoolRtId, "member-1", DateTime.UtcNow))).Should().BeTrue();

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!);
            var opened = QueuedCaller.Open(loaded!.SealedCaller, fixture.TestTenantId, loaded.RtId, Envelope);
            opened!.MethodInvocation.Should().Be(marker);
            opened.AccessToken.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 <b>One work item is ONE execution entity, from enqueue through claim to release.</b>
    /// </summary>
    /// <remarks>
    ///     This is the constraint that decided D4. The alternative resolution — sending an
    ///     <c>ExecutePipelineRequest</c> after the grant — would have had the member's trigger context
    ///     report an execution start, and <c>PipelineExecutionService.StartExecutionAsync</c> INSERTS a
    ///     new <c>RtPipelineExecution</c> with a new RtId and never looks an existing one up by
    ///     <c>ExecutionId</c>. Two entities for one piece of work means a reconciliation step and two
    ///     billing spans (concept §4b). Counting the entities by <c>ExecutionId</c> over real MongoDB is
    ///     the only assertion that can actually see a second one appear.
    /// </remarks>
    [Fact]
    public async Task OneWorkItemIsOneExecutionEntityFromEnqueueThroughClaimToRelease()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var executionId = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddSeconds(-30),
                "{\"x\":1}");

            (await CountEntitiesWithExecutionIdAsync(executionId)).Should().Be(1, "enqueue creates the entity");

            var grantedAt = DateTime.UtcNow;
            (await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, executionId,
                new LeaseClaim("lease-1", LenderTenantId, PoolRtId, "member-1", grantedAt))).Should().BeTrue();

            (await CountEntitiesWithExecutionIdAsync(executionId)).Should()
                .Be(1, "the claim moves the SAME entity Queued -> Running");

            // The release, exactly as LeaseService.ApplyLeaseOutcomeAsync performs it.
            await repository.StampLeaseReleasedAsync(fixture.TestTenantId, executionId, DateTime.UtcNow);
            await repository.UpdatePipelineExecutionAsync(fixture.TestTenantId, executionId,
                RtPipelineExecutionStatusEnum.Completed, DateTime.UtcNow, 1234, null, "{\"total\":42}");

            (await CountEntitiesWithExecutionIdAsync(executionId)).Should()
                .Be(1, "the release completes the SAME entity");

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);
            loaded!.Status.Should().Be(RtPipelineExecutionStatusEnum.Completed);
            loaded.InputData.Should().Be("{\"x\":1}");
            loaded.OutputData.Should().Be("{\"total\":42}");
            // The whole lease span is on that one entity, which is what prices the borrower.
            loaded.QueuedAt.Should().NotBeNull();
            loaded.LeaseGrantedAt.Should().NotBeNull();
            loaded.LeaseReleasedAt.Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     How many <c>PipelineExecution</c> entities carry this business execution id. Deliberately a
    ///     COUNT and not a single-read: <c>GetPipelineExecutionAsync</c> returns the first match and
    ///     would happily report success while a second entity sat next to it.
    /// </summary>
    private async Task<int> CountEntitiesWithExecutionIdAsync(string executionId)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.Equals, executionId);

        var resultSet =
            await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);
        return resultSet.Items.Count();
    }

    // ---- AB#7473: the lease queue accepts only executions the controller queued ---------------------------------

    private LeaseRequest RequestFor(QueuedExecution queued, RtEntityId adapter) =>
        new(fixture.TestTenantId, adapter.RtId, queued.ExecutionId, null, queued.PipelineRtId, queued.InputData,
            queued.ExecutionRtId, queued.SealedCaller);

    /// <summary>
    ///     The grant reads the per-tenant leasing switch first, so the gate is only reached with leasing on (both ends
    ///     are the fixture tenant). The switch is restored afterwards: other tests rely on the default.
    /// </summary>
    private async Task WithLeasingEnabledAsync(Func<Task> body)
    {
        var configuration = fixture.GetService<ILifecycleConfigurationService>();
        await configuration.SetConfigurationAsync(fixture.TestTenantId,
            new CommunicationLifecycleConfiguration { LeasingEnabled = true });
        try
        {
            await body();
        }
        finally
        {
            await configuration.SetConfigurationAsync(fixture.TestTenantId,
                new CommunicationLifecycleConfiguration { LeasingEnabled = false });
        }
    }

    private Task<LeaseGrantResult> GrantAsync(LeaseRequest request) =>
        fixture.GetService<ILeaseService>().GrantLeaseAsync(fixture.TestTenantId, new OctoObjectId(PoolRtId), request,
            TestContext.Current.CancellationToken);

    /// <summary>An execution exactly as the controller queues it (cron: no invoker).</summary>
    private async Task<RtPipelineExecution> EnqueueAsTheControllerAsync(TestData data, RtEntityId pipeline,
        RtEntityId adapter, string? input = null, bool withCaller = false, string? executionId = null,
        RtEntityId? edgePipeline = null, RtEntityId? edgeAdapter = null)
    {
        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            ExecutionId = executionId ?? Guid.NewGuid().ToString(),
            TriggerType = RtPipelineTriggerTypeEnum.Scheduled,
            InputData = input
        };
        QueuedCaller.Apply(execution, fixture.TestTenantId, pipeline.RtId, adapter.RtId,
            withCaller ? new ExecutePipelineCaller { SubjectId = "user-42", TrustLevel = 2 } : null, null, Envelope);
        // edgePipeline / edgeAdapter simulate an association that points elsewhere than at queue time.
        await fixture.GetService<ICommunicationRepository>().EnqueueExecutionAsync(fixture.TestTenantId, execution,
            edgePipeline ?? pipeline, edgeAdapter ?? adapter, DateTime.UtcNow.AddMinutes(-1));
        data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId));
        return execution;
    }

    [Fact]
    public async Task AGenericallyCreatedQueuedExecution_IsNeverLeased_AndEndsFailedWithAReason()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            // What a generic create (GraphQL / MCP) produces: Status=Queued, pipeline and adapter edges, no seal.
            var executionId = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-1),
                inputData: "{\"x\":1}");
            var queued = await repository.GetExecutionQueueEntryAsync(fixture.TestTenantId, executionId);

            await WithLeasingEnabledAsync(async () =>
            {
                var result = await GrantAsync(RequestFor(queued!, adapter));

                result.Granted.Should().BeFalse();
                result.Reason.Should().Be(LeaseRefusalReason.QueuedProvenanceInvalid);
            });

            var loaded = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);
            loaded!.Status.Should().Be(RtPipelineExecutionStatusEnum.Failed);
            loaded.ErrorMessage.Should().Contain("not queued by the controller");
            loaded.LeasedOnMemberId.Should().BeNull();
            loaded.LeaseGrantedAt.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     The controller's own executions (a cron tick has no invoker) pass the provenance gate: the grant goes on and
    ///     is refused for the NEXT reason in the chain (the fixture adapter names no lender), and the
    ///     entity is untouched. A full member grant needs a connected pool member, which this fixture does not run.
    /// </summary>
    [Fact]
    public async Task AControllerQueuedExecution_PassesTheProvenanceGate_AndStaysQueued()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var cron = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}");
            var invoked = await EnqueueAsTheControllerAsync(data, pipeline, adapter, withCaller: true);

            await WithLeasingEnabledAsync(async () =>
            {
                foreach (var execution in new[] { cron, invoked })
                {
                    var queued = await repository.TryGetQueuedExecutionAsync(fixture.TestTenantId,
                        execution.ExecutionId!);
                    var result = await GrantAsync(RequestFor(queued!, adapter));

                    result.Reason.Should().Be(LeaseRefusalReason.BorrowerNamesAnotherLender);
                    (await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!))!
                        .Status.Should().Be(RtPipelineExecutionStatusEnum.Queued);
                }
            });
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task AControllerQueuedExecution_ChangedAfterwards_IsNotLeased()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var otherPipeline = await CreatePipelineWithAdapterAsync(data, await CreateDataFlowAsync(data), adapter);
            var otherAdapter = await CreateAdapterAsync(data);

            var edited = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}");
            var copied = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}");
            var donor = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}", withCaller: true);
            // the association points at another pipeline / another adapter than the one the seal was made for
            var repointedPipeline = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}",
                edgePipeline: otherPipeline);
            var repointedAdapter = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}",
                edgeAdapter: otherAdapter);

            await GenericUpdateAsync(edited.RtId, new RtPipelineExecution { InputData = "{\"x\":2}" });
            await GenericUpdateAsync(copied.RtId, new RtPipelineExecution { SealedCaller = donor.SealedCaller });

            await WithLeasingEnabledAsync(async () =>
            {
                foreach (var (execution, onAdapter) in new[]
                         {
                             (edited, adapter), (copied, adapter), (repointedPipeline, adapter),
                             (repointedAdapter, otherAdapter)
                         })
                {
                    var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, onAdapter,
                        100);
                    var queued = queue.Single(q => q.ExecutionRtId == execution.RtId);
                    var result = await GrantAsync(RequestFor(queued, onAdapter));

                    result.Reason.Should().Be(LeaseRefusalReason.QueuedProvenanceInvalid, execution.ExecutionId);
                    (await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!))!
                        .Status.Should().Be(RtPipelineExecutionStatusEnum.Failed);
                }
            });
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     The business ExecutionId is a writable attribute and need not be unique. A forged execution that copies the
    ///     id of a real one is failed BY ITS RtId; the real one is untouched, and the claim latches the entity that
    ///     was verified, not "the first with that id". The forged head item does not hold back the real one.
    /// </summary>
    [Fact]
    public async Task AForgedExecutionWithTheSameExecutionId_IsFailedByRtId_AndTheRealOneIsUntouched()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var sharedId = Guid.NewGuid().ToString();

            // forged first (the head of the queue), copying the id of the real one that follows
            var forged = new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(), ExecutionId = sharedId,
                TriggerType = RtPipelineTriggerTypeEnum.Manual
            };
            await repository.EnqueueExecutionAsync(fixture.TestTenantId, forged, pipeline, adapter,
                DateTime.UtcNow.AddMinutes(-5));
            data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, forged.RtId));
            var real = await EnqueueAsTheControllerAsync(data, pipeline, adapter, executionId: sharedId);

            var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 100);
            var forgedEntry = queue.Single(q => q.ExecutionRtId == forged.RtId);
            var realEntry = queue.Single(q => q.ExecutionRtId == real.RtId);

            await WithLeasingEnabledAsync(async () =>
            {
                (await GrantAsync(RequestFor(forgedEntry, adapter))).Reason.Should()
                    .Be(LeaseRefusalReason.QueuedProvenanceInvalid);
                // the next item of the tenant is evaluated and passes the gate
                (await GrantAsync(RequestFor(realEntry, adapter))).Reason.Should()
                    .Be(LeaseRefusalReason.BorrowerNamesAnotherLender);
            });

            var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
            using (var session = await tenantRepository.GetSessionAsync())
            {
                (await tenantRepository.GetRtEntityByRtIdAsync<RtPipelineExecution>(session, forged.RtId))!.Status
                    .Should().Be(RtPipelineExecutionStatusEnum.Failed);
                (await tenantRepository.GetRtEntityByRtIdAsync<RtPipelineExecution>(session, real.RtId))!.Status
                    .Should().Be(RtPipelineExecutionStatusEnum.Queued);
            }

            // a second failure attempt does nothing (guarded: only while Queued)
            (await repository.TryFailQueuedExecutionAsync(fixture.TestTenantId, forged.RtId, "again")).Should()
                .BeFalse();

            // the claim addresses the verified entity by RtId
            (await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, sharedId,
                new LeaseClaim("lease-d", LenderTenantId, PoolRtId, "member-1", DateTime.UtcNow, real.RtId)))
                .Should().BeTrue();
            using (var session = await tenantRepository.GetSessionAsync())
            {
                (await tenantRepository.GetRtEntityByRtIdAsync<RtPipelineExecution>(session, real.RtId))!.Status
                    .Should().Be(RtPipelineExecutionStatusEnum.Running);
                (await tenantRepository.GetRtEntityByRtIdAsync<RtPipelineExecution>(session, forged.RtId))!.Status
                    .Should().Be(RtPipelineExecutionStatusEnum.Failed);
            }
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>B1: the hand-driven lease names nothing but an ExecutionId and goes through the same gate.</summary>
    [Fact]
    public async Task AHandDrivenLease_IsGatedOnTheExecutionItNames()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var forgedId = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow.AddMinutes(-1));
            var real = await EnqueueAsTheControllerAsync(data, pipeline, adapter);

            await WithLeasingEnabledAsync(async () =>
            {
                var forged = await GrantAsync(new LeaseRequest(fixture.TestTenantId, adapter.RtId, forgedId));
                forged.Reason.Should().Be(LeaseRefusalReason.QueuedProvenanceInvalid);

                var unknown = await GrantAsync(new LeaseRequest(fixture.TestTenantId, adapter.RtId, "no-such-id"));
                unknown.Reason.Should().Be(LeaseRefusalReason.QueuedProvenanceInvalid);

                // a controller-queued execution passes (and is then refused by the next check of the fixture)
                var ok = await GrantAsync(new LeaseRequest(fixture.TestTenantId, adapter.RtId, real.ExecutionId));
                ok.Reason.Should().Be(LeaseRefusalReason.BorrowerNamesAnotherLender);

                // a lease that names no work is not gated
                var none = await GrantAsync(new LeaseRequest(fixture.TestTenantId, adapter.RtId));
                none.Reason.Should().Be(LeaseRefusalReason.BorrowerNamesAnotherLender);
            });

            (await repository.GetPipelineExecutionAsync(fixture.TestTenantId, forgedId))!.Status.Should()
                .Be(RtPipelineExecutionStatusEnum.Failed);
            (await repository.GetPipelineExecutionAsync(fixture.TestTenantId, real.ExecutionId!))!.Status.Should()
                .Be(RtPipelineExecutionStatusEnum.Queued);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task ARetry_KeepsTheProvenance_ForTheNewExecution()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            foreach (var withCaller in new[] { false, true })
            {
                var original = await EnqueueAsTheControllerAsync(data, pipeline, adapter, "{\"x\":1}", withCaller);
                (await repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, original.ExecutionId!,
                    new LeaseClaim("lease-p", LenderTenantId, PoolRtId, "member-1", DateTime.UtcNow))).Should().BeTrue();
                // AB#7476: only a lease that took the single-use claim may requeue; the grant records it like this
                (await fixture.GetService<IExecutionLeaseClaimStore>().TryClaimAsync(fixture.TestTenantId,
                    QueuedCaller.Open(original.SealedCaller, fixture.TestTenantId, original.RtId, Envelope)!.ClaimId!,
                    "lease-p", original.RtId, TimeSpan.FromDays(1), TestContext.Current.CancellationToken)).Outcome.Should().Be(LeaseClaimOutcome.Claimed);

                var retryExecutionId = await fixture.GetService<ILeaseService>().InterruptAndRequeueAsync(
                    new LeaseDto
                    {
                        TenantId = fixture.TestTenantId, ExecutionId = original.ExecutionId!,
                        AdapterPoolTenantId = LenderTenantId, AdapterPoolRtId = PoolRtId, LeaseId = "lease-p",
                        GrantedAtUtc = DateTime.UtcNow
                    }, LeaseInterruptReason.MemberLost, "member lost");

                retryExecutionId.Should().NotBeNull();
                var retry = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, retryExecutionId!);
                data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, retry!.RtId));
                retry.SealedCaller.Should().NotBe(original.SealedCaller);

                // the retry opens for ITS execution, pipeline and input - and leases through the same gate
                QueuedCaller.TryOpen(retry.SealedCaller, fixture.TestTenantId, retry.RtId, pipeline.RtId, adapter.RtId,
                    retry.InputData, Envelope, out var opened, out var reason).Should()
                    .Be(QueuedCaller.Provenance.Valid, reason);
                (opened!.Caller is not null).Should().Be(withCaller);
                // and not for the interrupted one
                QueuedCaller.TryOpen(retry.SealedCaller, fixture.TestTenantId, original.RtId, pipeline.RtId,
                    adapter.RtId, retry.InputData, Envelope, out _, out _).Should()
                    .Be(QueuedCaller.Provenance.Forged);
            }
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    private async Task<(RtEntityId Pipeline, RtEntityId Adapter)> CreateWorkAsync(TestData data)
    {
        var adapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter);
        return (pipeline, adapter);
    }

    private async Task<string> EnqueueAsync(TestData data, RtEntityId pipeline, RtEntityId adapter,
        DateTime queuedAt, string? inputData = null,
        RtPipelineTriggerTypeEnum triggerType = RtPipelineTriggerTypeEnum.Manual)
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var executionId = Guid.NewGuid().ToString();

        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            ExecutionId = executionId,
            TriggerType = triggerType,
            InputData = inputData
        };

        await repository.EnqueueExecutionAsync(fixture.TestTenantId, execution, pipeline, adapter, queuedAt);
        data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId));
        return executionId;
    }

    // ---- AB#7476: a sealed execution is leased at most once -----------------------------------------------------

    /// <summary>
    ///     A lease service over the REAL repository, envelope, claim store (MongoDB) and connection manager; only the
    ///     collaborators of the tenant tree and the credentials are substitutes. The repository forwards exactly the
    ///     calls the grant makes, so the queue, the claim latch and the failure path are the real ones.
    /// </summary>
    private LeaseService CreateLeaseServiceForReplay(IAdapterPoolConnectionManager connectionManager,
        ISingleClientProxy memberProxy, RtEntityId adapter)
    {
        var real = fixture.GetService<ICommunicationRepository>();
        var tenant = fixture.TestTenantId;
        var repository = Substitute.For<ICommunicationRepository>();
        repository.GetWorkloadByRtIdAsync(tenant, adapter.RtId).Returns(_ => real.GetWorkloadByRtIdAsync(tenant, adapter.RtId));
        repository.GetLentAdapterPoolForAdapterAsync(tenant, adapter.RtId).Returns(new RtLentAdapterPool
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SystemCommunicationCkIds.RtCkLentAdapterPoolTypeId,
            Name = "lent",
            Lender = new RtLenderReferenceRecord { LenderTenantId = tenant, LenderAdapterPoolRtId = PoolRtId }
        });
        repository.TryGetAdapterPoolLendingScopeAsync(tenant, PoolRtId).Returns(new LendingScope(LendingScope.Descendants, null));
        repository.TryGetQueuedExecutionAsync(tenant, Arg.Any<string>())
            .Returns(c => real.TryGetQueuedExecutionAsync(tenant, c.ArgAt<string>(1)));
        repository.TryInterruptLeasedExecutionAsync(tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string>())
            .Returns(c => real.TryInterruptLeasedExecutionAsync(tenant, c.ArgAt<string>(1), c.ArgAt<DateTime>(2),
                c.ArgAt<string>(3)));
        repository.EnqueueExecutionAsync(tenant, Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtEntityId>(), Arg.Any<DateTime>())
            .Returns(c => real.EnqueueExecutionAsync(tenant, c.ArgAt<RtPipelineExecution>(1), c.ArgAt<RtEntityId>(2),
                c.ArgAt<RtEntityId>(3), c.ArgAt<DateTime>(4)));
        repository.TryFailQueuedExecutionAsync(tenant, Arg.Any<OctoObjectId>(), Arg.Any<string>())
            .Returns(c => real.TryFailQueuedExecutionAsync(tenant, c.ArgAt<OctoObjectId>(1), c.ArgAt<string>(2)));

        var clients = Substitute.For<IHubClients>();
        clients.Client(Arg.Any<string>()).Returns(memberProxy);
        var hub = Substitute.For<IHubContext<AdapterPoolHub>>();
        hub.Clients.Returns(clients);

        var lendingScope = Substitute.For<ITenantLendingScopeResolver>();
        lendingScope.MayLendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LendingScope>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var account = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(), CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "sa", ClientId = "client-id"
        };
        account.SetAttributeRawValue(nameof(RtServiceAccountConfiguration.ClientSecret), "a-client-secret");
        var accounts = Substitute.For<IPipelineServiceAccountResolver>();
        accounts.GetAdapterDefaultAsync(tenant, adapter.RtId).Returns(account);

        var encryption = Substitute.For<IWorkloadEncryptionService>();
        encryption.RevealOrNull(Arg.Any<object?>(), Arg.Any<SecretAccessContext?>()).Returns("a-client-secret");

        var databaseCredentials = Substitute.For<ITenantDatabaseCredentialResolver>();
        databaseCredentials.TryResolveAsync(tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantDatabaseCredential("db", "user", "password"));

        var adapterService = Substitute.For<IAdapterService>();
        adapterService.GetLeasedPipelineConfigurationAsync(tenant, Arg.Any<RtEntityId>(), Arg.Any<OctoObjectId>())
            .Returns(c => new PipelineConfigurationDto(OctoObjectId.GenerateNewId(),
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, c.ArgAt<OctoObjectId>(2)), false,
                "triggers: []\n", []));

        return new LeaseService(connectionManager, repository, fixture.GetService<ICommunicationEventService>(),
            encryption, fixture.GetService<IExecutionEnvelope>(), hub, lendingScope, accounts, databaseCredentials,
            adapterService, fixture.GetService<ILifecycleConfigurationService>(),
            Substitute.For<ILeaseSchedulerWakeSignal>(), fixture.GetService<IExecutionLeaseClaimStore>());
    }

    /// <summary>
    ///     🔴 AB#7476 — the reset of a completed execution (normal run and method run). The status and the lease
    ///     timestamps are ordinary attributes: one generic write puts a finished execution back to <c>Queued</c> with the
    ///     claim latch open again. The seal still opens, but the controller-owned claim record says it was used, so no
    ///     second lease is granted; once the in-flight window has passed the execution is failed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResetCompletedExecution_IsNotLeasedASecondTime(bool methodRun)
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var marker = methodRun
                ? new ExecutePipelineMethodInvocation
                {
                    MethodId = "Model-1/Type-1/Create", InvocationId = Guid.NewGuid(), CkTypeId = "Model-1/Type-1",
                    PipelineRtId = pipeline.RtId.ToString()
                }
                : null;
            var execution = await EnqueueWithSealedCallerAsync(data, pipeline, adapter, token: null,
                methodInvocation: marker);

            await WithLeasingEnabledAsync(async () =>
            {
                var connectionManager = new AdapterPoolConnectionManager();
                connectionManager.RegisterMember("conn-1", "member-1", fixture.TestTenantId, PoolRtId);
                var proxy = Substitute.For<ISingleClientProxy>();
                var service = CreateLeaseServiceForReplay(connectionManager, proxy, adapter);
                var pool = new OctoObjectId(PoolRtId);

                // the scheduler's gate: the real claim latch of the repository
                Task<bool> Gate(LeaseDto lease, PoolMemberConnection member, CancellationToken _) =>
                    repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, execution.ExecutionId!,
                        new LeaseClaim(lease.LeaseId, fixture.TestTenantId, PoolRtId, member.MemberId, DateTime.UtcNow,
                            execution.RtId));

                int Pushed() => proxy.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync));

                // 1. the first grant leases it and the execution runs to completion
                var first = await service.GrantLeaseAsync(fixture.TestTenantId, pool,
                    RequestFor(await QueuedAsync(adapter, execution), adapter), TestContext.Current.CancellationToken, Gate);
                first.Granted.Should().BeTrue();
                var pushed = (LeaseDto)((object?[])proxy.ReceivedCalls().Single(c =>
                    c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync)).GetArguments()[1]!)[0]!;
                pushed.MethodInvocation.Should().Be(marker);
                await repository.UpdatePipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!,
                    RtPipelineExecutionStatusEnum.Completed, DateTime.UtcNow, 10, null, null);
                connectionManager.ReleaseLease("conn-1", first.LeaseId!);

                // 2. a generic write puts it back to Queued AND opens the claim latch again
                await GenericUpdateAsync(execution.RtId, new RtPipelineExecution
                {
                    Status = RtPipelineExecutionStatusEnum.Queued, LeaseGrantedAt = DateTime.MinValue
                });
                var reset = await QueuedAsync(adapter, execution);
                QueuedCaller.Open(reset.SealedCaller, fixture.TestTenantId, execution.RtId, Envelope)
                    .Should().NotBeNull("the seal itself still opens - only the claim record knows it was used");

                // 3. no second lease, while the first claim is young (another instance may be granting) ...
                var second = await service.GrantLeaseAsync(fixture.TestTenantId, pool, RequestFor(reset, adapter),
                    TestContext.Current.CancellationToken, Gate);
                second.Granted.Should().BeFalse();
                Pushed().Should().Be(1);
                (await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!))!.Status
                    .Should().Be(RtPipelineExecutionStatusEnum.Queued);

                // 4. ... and failed for good once the claim is older than that window
                var claims = await ((ExecutionLeaseClaimStore)fixture.GetService<IExecutionLeaseClaimStore>())
                    .GetCollectionForTestsAsync();
                await claims.UpdateOneAsync(
                    new MongoDB.Bson.BsonDocument("tenantId", fixture.TestTenantId).Add("executionRtId",
                        execution.RtId.ToString()),
                    MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Update.Set("claimedAtUtc",
                        new MongoDB.Bson.BsonDateTime(DateTime.UtcNow.AddMinutes(-10))),
                    cancellationToken: TestContext.Current.CancellationToken);
                var third = await service.GrantLeaseAsync(fixture.TestTenantId, pool, RequestFor(reset, adapter),
                    TestContext.Current.CancellationToken, Gate);
                third.Granted.Should().BeFalse();
                third.Reason.Should().Be(LeaseRefusalReason.QueuedProvenanceInvalid);
                Pushed().Should().Be(1, "a sealed execution is leased at most once");

                var failed = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!);
                failed!.Status.Should().Be(RtPipelineExecutionStatusEnum.Failed);
                failed.ErrorMessage.Should().Contain("claim already recorded");
                connectionManager.GetMembers(fixture.TestTenantId, PoolRtId).All(m => m.IsAvailable).Should().BeTrue();
            });
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 AB#7476 — claim laundering. A used execution is reset to Queued, a HAND-DRIVEN lease names it (no claim is
    ///     taken for it), and the lease expires. The requeue of that lease must not interrupt the execution nor seal it
    ///     again under a fresh identity: only a lease that took a claim may requeue.
    /// </summary>
    [Fact]
    public async Task AHandDrivenLeaseOnAResetExecution_CannotLaunderTheClaimThroughARequeue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var execution = await EnqueueWithSealedCallerAsync(data, pipeline, adapter, token: null);

            await WithLeasingEnabledAsync(async () =>
            {
                var connectionManager = new AdapterPoolConnectionManager();
                connectionManager.RegisterMember("conn-1", "member-1", fixture.TestTenantId, PoolRtId);
                var proxy = Substitute.For<ISingleClientProxy>();
                var service = CreateLeaseServiceForReplay(connectionManager, proxy, adapter);
                var pool = new OctoObjectId(PoolRtId);

                Task<bool> Gate(LeaseDto lease, PoolMemberConnection member, CancellationToken _) =>
                    repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, execution.ExecutionId!,
                        new LeaseClaim(lease.LeaseId, fixture.TestTenantId, PoolRtId, member.MemberId, DateTime.UtcNow,
                            execution.RtId));

                LeaseDto LastLease() => (LeaseDto)((object?[])proxy.ReceivedCalls().Last(c =>
                    c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync)).GetArguments()[1]!)[0]!;

                // the legitimate run, then the reset through the generic write path
                var first = await service.GrantLeaseAsync(fixture.TestTenantId, pool,
                    RequestFor(await QueuedAsync(adapter, execution), adapter), TestContext.Current.CancellationToken, Gate);
                first.Granted.Should().BeTrue();
                await repository.UpdatePipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!,
                    RtPipelineExecutionStatusEnum.Completed, DateTime.UtcNow, 10, null, null);
                connectionManager.ReleaseLease("conn-1", first.LeaseId!);
                await GenericUpdateAsync(execution.RtId, new RtPipelineExecution
                {
                    Status = RtPipelineExecutionStatusEnum.Queued, LeaseGrantedAt = DateTime.MinValue
                });

                // a hand-driven lease that names the reset execution (no work is carried, no claim is taken)
                var byHand = await service.GrantLeaseAsync(fixture.TestTenantId, pool,
                    new LeaseRequest(fixture.TestTenantId, adapter.RtId, execution.ExecutionId),
                    TestContext.Current.CancellationToken);
                byHand.Granted.Should().BeTrue();
                var handLease = LastLease();

                // the lease expires: what ReapExpiredLeasesAsync does
                var requeued = await service.InterruptAndRequeueAsync(handLease, LeaseInterruptReason.TtlExpiry,
                    "expired");

                requeued.Should().BeNull();
                (await CountEntitiesWithExecutionIdAsync(execution.ExecutionId!)).Should().Be(1,
                    "no sealed retry was created");
                (await repository.GetPipelineExecutionAsync(fixture.TestTenantId, execution.ExecutionId!))!.Status
                    .Should().Be(RtPipelineExecutionStatusEnum.Queued, "a queued execution is not interrupted");
            });
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    ///     🔴 AB#7476 N1 — a lease rebuilt from member-supplied ids (a claimed lease id of its own plus the id of another
    ///     Running execution) requeues nothing, and the reaper and a release racing for one legitimate lease produce
    ///     exactly one retry.
    /// </summary>
    [Fact]
    public async Task TheRequeueIsBoundToTheSealOfItsOwnExecution_AndHappensOnce()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var a = await EnqueueWithSealedCallerAsync(data, pipeline, adapter, token: null);
            var b = await EnqueueWithSealedCallerAsync(data, pipeline, adapter, subject: "user-43", token: null);

            await WithLeasingEnabledAsync(async () =>
            {
                var connectionManager = new AdapterPoolConnectionManager();
                connectionManager.RegisterMember("conn-a", "member-a", fixture.TestTenantId, PoolRtId);
                connectionManager.RegisterMember("conn-b", "member-b", fixture.TestTenantId, PoolRtId);
                var proxy = Substitute.For<ISingleClientProxy>();
                var service = CreateLeaseServiceForReplay(connectionManager, proxy, adapter);
                var pool = new OctoObjectId(PoolRtId);

                Func<LeaseDto, PoolMemberConnection, CancellationToken, Task<bool>> GateFor(RtPipelineExecution e) =>
                    (lease, member, _) => repository.TryClaimQueuedExecutionAsync(fixture.TestTenantId, e.ExecutionId!,
                        new LeaseClaim(lease.LeaseId, fixture.TestTenantId, PoolRtId, member.MemberId, DateTime.UtcNow,
                            e.RtId));

                LeaseDto LastLease() => (LeaseDto)((object?[])proxy.ReceivedCalls().Last(c =>
                    c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync)).GetArguments()[1]!)[0]!;

                (await service.GrantLeaseAsync(fixture.TestTenantId, pool, RequestFor(await QueuedAsync(adapter, a),
                    adapter), TestContext.Current.CancellationToken, GateFor(a))).Granted.Should().BeTrue();
                var leaseA = LastLease();
                (await service.GrantLeaseAsync(fixture.TestTenantId, pool, RequestFor(await QueuedAsync(adapter, b),
                    adapter), TestContext.Current.CancellationToken, GateFor(b))).Granted.Should().BeTrue();

                // lease A's id, execution B (Running on its own lease): nothing is re-sealed
                var rebuilt = leaseA with { ExecutionId = b.ExecutionId! };
                (await service.InterruptAndRequeueAsync(rebuilt, LeaseInterruptReason.MemberLost, "lost")).Should().BeNull();
                (await CountEntitiesWithExecutionIdAsync(b.ExecutionId!)).Should().Be(1);

                // the legitimate lease A is reached by the reaper and by a release at the same time: one retry
                var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
                    service.InterruptAndRequeueAsync(leaseA, LeaseInterruptReason.TtlExpiry, "expired"))));
                var retries = results.Where(r => r is not null).ToList();
                retries.Should().HaveCount(1);
                var retry = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, retries[0]!);
                data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, retry!.RtId));
            });
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    private sealed class TestData
    {
        public List<RtEntityId> Pipelines { get; } = new();
        public List<RtEntityId> DataFlows { get; } = new();
        public List<RtEntityId> Adapters { get; } = new();
        public List<RtEntityId> Executions { get; } = new();
    }

    private async Task<RtEntityId> CreateAdapterAsync(TestData data)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = $"int-test-pool-borrower-{Guid.NewGuid():N}",
            CommunicationState = RtCommunicationStateEnum.Offline,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            ConfigurationState = RtConfigurationStateEnum.Unconfigured,
            // AB#5271: the borrower's lender is the adapter's LentFrom edge to a mirror, not two
            // attributes. This suite never resolves it — it exercises the queue writes, which only
            // need the adapter to BE leased — so nothing is linked here. The lender ids it does use
            // travel on the LeaseClaim, which is where the queue reads them from.
            LifecycleMode = RtLifecycleModeEnum.Leased
        };

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapter>> { EntityUpdateInfo<RtAdapter>.CreateInsert(adapter) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to insert test adapter: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
        data.Adapters.Add(adapterRtEntityId);
        return adapterRtEntityId;
    }

    private async Task<RtEntityId> CreateDataFlowAsync(TestData data)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var dataFlow = new RtDataFlow
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkDataFlowTypeId,
            Name = $"int-test-dataflow-{Guid.NewGuid():N}"
        };

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtDataFlow>> { EntityUpdateInfo<RtDataFlow>.CreateInsert(dataFlow) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to insert test data flow: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        var dataFlowRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkDataFlowTypeId, dataFlow.RtId);
        data.DataFlows.Add(dataFlowRtEntityId);
        return dataFlowRtEntityId;
    }

    private async Task<RtEntityId> CreatePipelineWithAdapterAsync(TestData data, RtEntityId dataFlow,
        RtEntityId adapter, RtPipelineExecutionClassEnum executionClass = RtPipelineExecutionClassEnum.Batch)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var pipeline = new RtPipeline
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTypeId,
            Name = $"int-test-pipeline-{Guid.NewGuid():N}",
            Enabled = true,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            PipelineDefinition = "name: test",
            ExecutionClass = executionClass
        };

        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId);

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipeline>> { EntityUpdateInfo<RtPipeline>.CreateInsert(pipeline) },
            new List<AssociationUpdateInfo>
            {
                AssociationUpdateInfo.CreateInsert(pipelineRtEntityId, dataFlow, SystemCkIds.RtCkParentChildRoleId),
                AssociationUpdateInfo.CreateInsert(pipelineRtEntityId, adapter,
                    SystemCommunicationCkIds.RtCkExecutesRoleId)
            },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException(
                $"Failed to insert test pipeline with associations: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        data.Pipelines.Add(pipelineRtEntityId);
        return pipelineRtEntityId;
    }

    private async Task CleanupAsync(TestData data)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        foreach (var execution in data.Executions)
        {
            await TryDeleteAsync<RtPipelineExecution>(tenantRepository, execution);
        }

        foreach (var pipeline in data.Pipelines)
        {
            await TryDeleteAsync<RtPipeline>(tenantRepository, pipeline);
        }

        foreach (var dataFlow in data.DataFlows)
        {
            await TryDeleteAsync<RtDataFlow>(tenantRepository, dataFlow);
        }

        foreach (var adapter in data.Adapters)
        {
            await TryDeleteAsync<RtAdapter>(tenantRepository, adapter);
        }
    }

    private static async Task TryDeleteAsync<TEntity>(ITenantRepository tenantRepository, RtEntityId entity)
        where TEntity : RtEntity
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var operationResult = new OperationResult();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<TEntity>> { EntityUpdateInfo<TEntity>.CreateDelete(entity) },
                operationResult);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
        }
    }
}
