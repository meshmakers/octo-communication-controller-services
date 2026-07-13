using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// Integration tests for the repository building blocks behind the execution fold (AB#4370)
/// against real MongoDB: the terminal-only drain query (NotEquals status filter), the physical
/// erase of folded executions, and the MongoDB round-trip of the HourlyBuckets record array
/// (incl. the Int64 duration sum) on RtPipelineStatistics.
/// </summary>
[Collection("CommunicationController")]
public class FoldAndPruneRepositoryTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task GetTerminalExecutionsOlderThan_ExcludesRunningAndRecent_ThenDeleteErasesPhysically()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var adapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter);

        var old = DateTime.UtcNow.AddHours(-3);
        var older = DateTime.UtcNow.AddHours(-4);
        var recent = DateTime.UtcNow;

        try
        {
            var oldCompleted =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Completed, old);
            var olderFailed =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Failed, older);
            // Running executions are never drained, no matter how old
            await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Running, older);
            // Recent terminal executions stay inside the retention window
            await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Completed, recent);

            var cutoff = DateTime.UtcNow.AddHours(-1);

            var batch = await repository.GetTerminalExecutionsOlderThanAsync(tenantId, pipeline, cutoff, 100);

            batch.Should().HaveCount(2);
            // Oldest first for monotonic drain progress
            batch[0].RtId.Should().Be(olderFailed.RtId);
            batch[1].RtId.Should().Be(oldCompleted.RtId);

            var deleted = await repository.DeleteExecutionsAsync(tenantId,
                batch.Select(e => new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, e.RtId))
                    .ToList());

            deleted.Should().Be(2);

            var remainingRtIds = await GetExecutionRtIdsIncludingArchivedAsync();
            remainingRtIds.Should().NotContain(oldCompleted.RtId);
            remainingRtIds.Should().NotContain(olderFailed.RtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task UpsertPipelineStatistics_RoundTripsHourlyBuckets()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var adapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter);

        var hour = new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc);

        try
        {
            var statistics = new RtPipelineStatistics
            {
                Last24HoursSuccessCount = 4,
                LastUpdatedAt = DateTime.UtcNow,
                HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(
                    new List<RtRecord>
                    {
                        new RtPipelineStatisticsHourBucketRecord
                        {
                            HourStartAt = hour,
                            SuccessCount = 3,
                            FailureCount = 1,
                            // Int64 on purpose — exceeds int to pin the CK Int64 value type
                            TotalDurationMs = 5_000_000_000L,
                            DurationCount = 4
                        },
                        new RtPipelineStatisticsHourBucketRecord
                        {
                            HourStartAt = hour.AddHours(1),
                            SuccessCount = 1,
                            FailureCount = 0,
                            TotalDurationMs = 100L,
                            DurationCount = 1
                        }
                    })
            };

            await repository.UpsertPipelineStatisticsAsync(tenantId, statistics, pipeline);

            var loaded = await repository.GetPipelineStatisticsAsync(tenantId, pipeline);

            loaded.Should().NotBeNull();
            var buckets = loaded!.HourlyBuckets.Should().NotBeNull().And.Subject!.ToList();
            buckets.Should().HaveCount(2);
            var first = buckets.Single(b => b.HourStartAt == hour);
            first.SuccessCount.Should().Be(3);
            first.FailureCount.Should().Be(1);
            first.TotalDurationMs.Should().Be(5_000_000_000L);
            first.DurationCount.Should().Be(4);

            // Second upsert replaces the buckets (rebuild-and-reassign contract)
            var updated = new RtPipelineStatistics
            {
                Last24HoursSuccessCount = 5,
                LastUpdatedAt = DateTime.UtcNow,
                HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(
                    new List<RtRecord>
                    {
                        new RtPipelineStatisticsHourBucketRecord
                        {
                            HourStartAt = hour.AddHours(2), SuccessCount = 9, FailureCount = 0,
                            TotalDurationMs = 90L, DurationCount = 9
                        }
                    })
            };

            await repository.UpsertPipelineStatisticsAsync(tenantId, updated, pipeline);

            var reloaded = await repository.GetPipelineStatisticsAsync(tenantId, pipeline);
            reloaded!.HourlyBuckets!.Should().HaveCount(1);
            reloaded.HourlyBuckets!.Single().SuccessCount.Should().Be(9);
        }
        finally
        {
            await CleanupStatisticsAsync(pipeline);
            await CleanupAsync(data);
        }
    }

    private async Task<List<OctoObjectId>> GetExecutionRtIdsIncludingArchivedAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();

        var queryOptions = Meshmakers.Octo.Runtime.Contracts.Repositories.Query.RtEntityQueryOptions.Create()
            .Global(includeArchived: true);
        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(
            session, queryOptions, skip: 0, take: 1000);

        return resultSet.Items.Select(e => e.RtId).ToList();
    }

    private async Task CleanupStatisticsAsync(RtEntityId pipeline)
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var statistics = await repository.GetPipelineStatisticsAsync(fixture.TestTenantId, pipeline);
        if (statistics == null)
        {
            return;
        }

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);
        await TryDeleteAsync<RtPipelineStatistics>(tenantRepository,
            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineStatisticsTypeId, statistics.RtId));
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
            Name = $"int-test-adapter-{Guid.NewGuid():N}",
            CommunicationState = RtCommunicationStateEnum.Online,
            DeploymentState = RtDeploymentStateEnum.Undeployed,
            ConfigurationState = RtConfigurationStateEnum.Unconfigured
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

    private async Task<RtEntityId> CreatePipelineWithAdapterAsync(TestData data, RtEntityId dataFlow, RtEntityId adapter)
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
            PipelineDefinition = "name: test"
        };

        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId);

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipeline>> { EntityUpdateInfo<RtPipeline>.CreateInsert(pipeline) },
            new List<AssociationUpdateInfo>
            {
                AssociationUpdateInfo.CreateInsert(pipelineRtEntityId, dataFlow, SystemCkIds.RtCkParentChildRoleId),
                AssociationUpdateInfo.CreateInsert(pipelineRtEntityId, adapter, SystemCommunicationCkIds.RtCkExecutesRoleId)
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

    private async Task<RtPipelineExecution> CreateExecutionAsync(TestData data, RtEntityId pipeline, RtEntityId adapter,
        RtPipelineExecutionStatusEnum status, DateTime startedAt)
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            ExecutionId = Guid.NewGuid().ToString(),
            Status = status,
            TriggerType = RtPipelineTriggerTypeEnum.Manual,
            StartedAt = startedAt
        };

        await repository.CreatePipelineExecutionAsync(fixture.TestTenantId, execution, pipeline, adapter);

        data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId));
        return execution;
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
