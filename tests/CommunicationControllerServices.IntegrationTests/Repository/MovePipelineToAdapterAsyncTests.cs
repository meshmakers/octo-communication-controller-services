using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// Integration tests for <see cref="ICommunicationRepository.MovePipelineToAdapterAsync"/>.
/// Exercises the real MongoDB transaction that reassigns the <c>Executes</c>
/// association — the controller-level unit tests mock the repo and would not
/// have caught the missing <c>session.StartTransaction()</c> regression that
/// surfaced as a generic "Failed to move pipeline" error in the UI.
/// </summary>
[Collection("CommunicationController")]
public class MovePipelineToAdapterAsyncTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task MovePipelineToAdapterAsync_HappyPath_ReassignsExecutesAssociation()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var sourceAdapter = await CreateAdapterAsync(data);
        var targetAdapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, sourceAdapter);

        try
        {
            var result = await repository.MovePipelineToAdapterAsync(tenantId, pipeline.RtId,
                targetAdapter.RtId);

            result.PipelineRtId.Should().Be(pipeline.RtId);
            result.OldAdapterRtEntityId.RtId.Should().Be(sourceAdapter.RtId);
            result.NewAdapterRtEntityId.RtId.Should().Be(targetAdapter.RtId);

            // The Executes edge must now resolve to the target adapter.
            var adapterAfter = await repository.GetAdapterByPipelineAsync(tenantId, pipeline);
            adapterAfter.Should().NotBeNull();
            adapterAfter!.RtId.Should().Be(targetAdapter.RtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task MovePipelineToAdapterAsync_AlreadyOnTarget_IsNoOp()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var adapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, adapter);

        try
        {
            var result = await repository.MovePipelineToAdapterAsync(tenantId, pipeline.RtId,
                adapter.RtId);

            result.OldAdapterRtEntityId.RtId.Should().Be(adapter.RtId);
            result.NewAdapterRtEntityId.RtId.Should().Be(adapter.RtId);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task MovePipelineToAdapterAsync_TargetAdapterDoesNotExist_Throws()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var sourceAdapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);
        var pipeline = await CreatePipelineWithAdapterAsync(data, dataFlow, sourceAdapter);
        var ghostAdapterRtId = OctoObjectId.GenerateNewId();

        try
        {
            var act = async () => await repository.MovePipelineToAdapterAsync(tenantId,
                pipeline.RtId, ghostAdapterRtId);

            await act.Should().ThrowAsync<CommunicationRepositoryException>()
                .Where(e => e.Message.Contains("does not exist"));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task MovePipelineToAdapterAsync_PipelineDoesNotExist_Throws()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();

        var data = new TestData();
        var targetAdapter = await CreateAdapterAsync(data);
        var ghostPipelineRtId = OctoObjectId.GenerateNewId();

        try
        {
            var act = async () => await repository.MovePipelineToAdapterAsync(tenantId,
                ghostPipelineRtId, targetAdapter.RtId);

            await act.Should().ThrowAsync<CommunicationRepositoryException>()
                .Where(e => e.Message.Contains("does not exist"));
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    /// Tracks every entity created by a single test so the finally block can tear everything down
    /// in reverse-dependency order. Each test gets its own instance — they don't share state.
    /// </summary>
    private sealed class TestData
    {
        public List<RtEntityId> Pipelines { get; } = new();
        public List<RtEntityId> DataFlows { get; } = new();
        public List<RtEntityId> Adapters { get; } = new();
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
            throw new InvalidOperationException(
                $"Failed to insert test adapter: {operationResult.GetMessages()}");
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
            throw new InvalidOperationException(
                $"Failed to insert test data flow: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        var dataFlowRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkDataFlowTypeId, dataFlow.RtId);
        data.DataFlows.Add(dataFlowRtEntityId);
        return dataFlowRtEntityId;
    }

    /// <summary>
    /// Creates a pipeline together with the two associations it needs to satisfy minimum
    /// multiplicity constraints in a single transaction: a <c>ParentChild</c> link to a parent
    /// data flow (inbound multiplicity One on <c>RtPipeline</c>) and an <c>Executes</c> link
    /// to the adapter that runs it.
    /// </summary>
    private async Task<RtEntityId> CreatePipelineWithAdapterAsync(TestData data, RtEntityId dataFlow,
        RtEntityId adapter)
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
                AssociationUpdateInfo.CreateInsert(pipelineRtEntityId, dataFlow,
                    SystemCkIds.RtCkParentChildRoleId),
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

        // Delete in dependency order: children before parents.
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
                new List<EntityUpdateInfo<TEntity>>
                {
                    EntityUpdateInfo<TEntity>.CreateDelete(entity)
                },
                operationResult);

            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            // Cleanup is best-effort.
        }
    }
}
