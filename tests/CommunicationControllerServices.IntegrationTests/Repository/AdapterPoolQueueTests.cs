using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
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

    /// <summary>
    ///     AB#5279 — the invoker the work item was queued for survives the round trip through
    ///     MongoDB and comes back on the projection, token still encrypted. Pinned here because the
    ///     seven attributes are new in 4.2.0 and a mocked repository cannot prove they persist.
    /// </summary>
    [Fact]
    public async Task GetQueuedExecutionsForAdapterAsync_CarriesTheInvokerTheItemWasQueuedFor()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();

        try
        {
            var (pipeline, adapter) = await CreateWorkAsync(data);
            var execution = new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = Guid.NewGuid().ToString(),
                TriggerType = RtPipelineTriggerTypeEnum.Manual,
                CallerSubjectId = "user-42",
                CallerTenantId = fixture.TestTenantId,
                CallerEmail = "u@example.test",
                CallerName = "User 42",
                CallerRoles = new AttributeStringValueList(["Admin", "Reader"]),
                CallerTrustLevel = 2,
                CallerAccessToken = "enc:v1:ciphertext"
            };
            await repository.EnqueueExecutionAsync(fixture.TestTenantId, execution, pipeline, adapter,
                DateTime.UtcNow.AddMinutes(-1));
            data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId));
            var anonymous = await EnqueueAsync(data, pipeline, adapter, DateTime.UtcNow);

            var queue = await repository.GetQueuedExecutionsForAdapterAsync(fixture.TestTenantId, adapter, 100);

            var withCaller = queue.Single(q => q.ExecutionId == execution.ExecutionId);
            withCaller.Caller.Should().NotBeNull();
            withCaller.Caller!.SubjectId.Should().Be("user-42");
            withCaller.Caller.TenantId.Should().Be(fixture.TestTenantId);
            withCaller.Caller.Email.Should().Be("u@example.test");
            withCaller.Caller.Name.Should().Be("User 42");
            withCaller.Caller.Roles.Should().Equal("Admin", "Reader");
            withCaller.Caller.TrustLevel.Should().Be(2);
            // As stored: the repository never decrypts, the lease service does.
            withCaller.CallerAccessToken.Should().Be("enc:v1:ciphertext");

            var withoutCaller = queue.Single(q => q.ExecutionId == anonymous);
            withoutCaller.Caller.Should().BeNull();
            withoutCaller.CallerAccessToken.Should().BeNull();
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
