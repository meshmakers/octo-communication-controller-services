using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// Integration tests for the AB#4363 retention fix against real MongoDB. Executions are
/// telemetry — the retention sweep must physically erase the documents. The engine default
/// (DeleteStrategies.Archive) only set rtState=Archived, so the collection grew unbounded
/// (1M+ docs per tenant in production). These tests pin that (a) old executions are gone
/// from MongoDB even when queried with includeArchived, (b) tombstones left behind by
/// earlier archive-only runs are drained, and (c) recent executions survive.
/// </summary>
[Collection("CommunicationController")]
public class DeleteOldExecutionsTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task DeleteOldExecutionsAsync_ErasesOldAndArchivedTombstones_SparesRecent()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var adapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter);

        var old = DateTime.UtcNow.AddHours(-1);
        var recent = DateTime.UtcNow;

        try
        {
            // Old completed execution -> must be physically erased.
            var oldExecution =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Completed, old);
            // Tombstone from a pre-fix archive-only retention run -> must be drained too.
            var archivedTombstone =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Completed, old);
            await ArchiveExecutionAsync(archivedTombstone);
            // Recent execution -> younger than the cutoff, must survive.
            var recentExecution =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Completed, recent);

            var cutoff = DateTime.UtcNow.AddMinutes(-5);

            var deletedCount = await repository.DeleteOldExecutionsAsync(tenantId, cutoff);

            // The shared test tenant may hold archived leftovers of sibling tests, so only
            // assert a lower bound instead of an exact count.
            deletedCount.Should().BeGreaterThanOrEqualTo(2);

            var remainingRtIds = await GetExecutionRtIdsIncludingArchivedAsync();
            remainingRtIds.Should().NotContain(oldExecution.RtId);
            remainingRtIds.Should().NotContain(archivedTombstone.RtId);
            remainingRtIds.Should().Contain(recentExecution.RtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    /// Queries the execution collection with includeArchived so an entity that was merely
    /// archived (rtState=Archived) still shows up — the assertion that an RtId is absent
    /// therefore proves physical erasure, not just archiving.
    /// </summary>
    private async Task<List<OctoObjectId>> GetExecutionRtIdsIncludingArchivedAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();

        var queryOptions = RtEntityQueryOptions.Create().Global(includeArchived: true);
        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(
            session, queryOptions, skip: 0, take: 1000);

        return resultSet.Items.Select(e => e.RtId).ToList();
    }

    /// <summary>
    /// Deletes the execution with the engine default strategy (Archive), producing the same
    /// rtState=Archived tombstone the pre-fix retention runs left behind.
    /// </summary>
    private async Task ArchiveExecutionAsync(RtEntityId execution)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipelineExecution>>
                { EntityUpdateInfo<RtPipelineExecution>.CreateDelete(execution) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to archive test execution: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
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

    private async Task<RtEntityId> CreateExecutionAsync(TestData data, RtEntityId pipeline, RtEntityId adapter,
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

        var executionRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId);
        data.Executions.Add(executionRtEntityId);
        return executionRtEntityId;
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
