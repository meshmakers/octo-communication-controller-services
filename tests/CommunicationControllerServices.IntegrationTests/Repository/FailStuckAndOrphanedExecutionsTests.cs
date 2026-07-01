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
/// Integration tests for the AB#4280 durability reaper against real MongoDB. These pin the
/// behaviour the controller-level unit tests (which mock the repo) cannot: the connection-aware
/// filtering that must never fail a long-running execution on a live adapter, plus the
/// adapter-scoped orphan resolution driven by a fresh process start time.
/// </summary>
[Collection("CommunicationController")]
public class FailStuckAndOrphanedExecutionsTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task FailStuckExecutionsAsync_IsConnectionAware_AndSparesLiveLongRunners()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var onlineAdapter = await CreateAdapterAsync(data, RtCommunicationStateEnum.Online);
        var offlineAdapter = await CreateAdapterAsync(data, RtCommunicationStateEnum.Offline);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, onlineAdapter);

        var stale = DateTime.UtcNow.AddHours(-1);
        var recent = DateTime.UtcNow;

        try
        {
            // Long-running pipeline on a live adapter, started long ago -> must be spared.
            var runningOnOnline =
                await CreateExecutionAsync(data, pipeline, onlineAdapter, RtPipelineExecutionStatusEnum.Running, stale);
            // Running on an offline adapter, stale -> orphaned -> must be failed.
            var runningOnOffline =
                await CreateExecutionAsync(data, pipeline, offlineAdapter, RtPipelineExecutionStatusEnum.Running, stale);
            // Interrupted (adapter disconnected), stale -> must be failed regardless of adapter state.
            var interrupted =
                await CreateExecutionAsync(data, pipeline, onlineAdapter, RtPipelineExecutionStatusEnum.Interrupted, stale);
            // Running on an offline adapter but younger than the grace cutoff -> must be spared.
            var recentOnOffline =
                await CreateExecutionAsync(data, pipeline, offlineAdapter, RtPipelineExecutionStatusEnum.Running, recent);

            var graceCutoff = DateTime.UtcNow.AddMinutes(-15);

            var failedCount = await repository.FailStuckExecutionsAsync(tenantId, graceCutoff);

            failedCount.Should().Be(2);
            await AssertStatus(repository, runningOnOnline, RtPipelineExecutionStatusEnum.Running);
            await AssertStatus(repository, runningOnOffline, RtPipelineExecutionStatusEnum.Failed);
            await AssertStatus(repository, interrupted, RtPipelineExecutionStatusEnum.Failed);
            await AssertStatus(repository, recentOnOffline, RtPipelineExecutionStatusEnum.Running);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task FailOrphanedExecutionsForAdapterAsync_FailsOnlyThisAdaptersPreStartExecutions()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var adapter = await CreateAdapterAsync(data, RtCommunicationStateEnum.Online);
        var otherAdapter = await CreateAdapterAsync(data, RtCommunicationStateEnum.Online);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter);

        var beforeStart = DateTime.UtcNow.AddHours(-1);
        var afterStart = DateTime.UtcNow;
        var processStart = DateTime.UtcNow.AddMinutes(-30);

        try
        {
            // Orphans of the previous process (started before the new process began).
            var runningOrphan =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Running, beforeStart);
            var interruptedOrphan =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Interrupted, beforeStart);
            // Started after the process start -> belongs to the new process -> must be spared.
            var runningAfterStart =
                await CreateExecutionAsync(data, pipeline, adapter, RtPipelineExecutionStatusEnum.Running, afterStart);
            // Belongs to a different adapter -> must not be touched.
            var otherAdapterRunning =
                await CreateExecutionAsync(data, pipeline, otherAdapter, RtPipelineExecutionStatusEnum.Running, beforeStart);

            var failedCount = await repository.FailOrphanedExecutionsForAdapterAsync(tenantId, adapter, processStart);

            failedCount.Should().Be(2);
            await AssertStatus(repository, runningOrphan, RtPipelineExecutionStatusEnum.Failed);
            await AssertStatus(repository, interruptedOrphan, RtPipelineExecutionStatusEnum.Failed);
            await AssertStatus(repository, runningAfterStart, RtPipelineExecutionStatusEnum.Running);
            await AssertStatus(repository, otherAdapterRunning, RtPipelineExecutionStatusEnum.Running);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    private async Task AssertStatus(ICommunicationRepository repository, string executionId,
        RtPipelineExecutionStatusEnum expected)
    {
        var execution = await repository.GetPipelineExecutionAsync(fixture.TestTenantId, executionId);
        execution.Should().NotBeNull();
        execution!.Status.Should().Be(expected);
    }

    private sealed class TestData
    {
        public List<RtEntityId> Pipelines { get; } = new();
        public List<RtEntityId> DataFlows { get; } = new();
        public List<RtEntityId> Adapters { get; } = new();
        public List<RtEntityId> Executions { get; } = new();
    }

    private async Task<RtEntityId> CreateAdapterAsync(TestData data, RtCommunicationStateEnum state)
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
            CommunicationState = state,
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

    private async Task<string> CreateExecutionAsync(TestData data, RtEntityId pipeline, RtEntityId adapter,
        RtPipelineExecutionStatusEnum status, DateTime startedAt)
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var executionId = Guid.NewGuid().ToString();

        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            ExecutionId = executionId,
            Status = status,
            TriggerType = RtPipelineTriggerTypeEnum.Manual,
            StartedAt = startedAt
        };

        await repository.CreatePipelineExecutionAsync(fixture.TestTenantId, execution, pipeline, adapter);

        data.Executions.Add(new RtEntityId(SystemCommunicationCkIds.RtCkPipelineExecutionTypeId, execution.RtId));
        return executionId;
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
