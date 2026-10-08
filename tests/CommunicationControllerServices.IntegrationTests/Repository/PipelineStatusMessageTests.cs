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
/// Integration tests for <see cref="ICommunicationRepository.SetPipelineStatusMessageAsync"/>
/// (AB#5385): the status line a trigger reports after every poll lands in the pipeline's live
/// <c>StatusMessage</c> and in NOTHING else — the deployment state and the persistent
/// <c>LastDeploymentError</c> pair the operator may still be looking at stay as they are.
/// </summary>
[Collection("CommunicationController")]
public class PipelineStatusMessageTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task SetPipelineStatusMessageAsync_WritesTheLine()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();
        var pipelineRtEntityId = await CreatePipelineAsync(data);

        try
        {
            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId,
                "2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox · seen 12, imported 12, failed 0, skipped 0", false, DateTime.UtcNow);

            var pipeline = await repository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            pipeline.Should().NotBeNull();
            pipeline!.StatusMessage.Should()
                .Be("2026-09-26T17:40:12Z · kbernkopf@tecob.at · Inbox · seen 12, imported 12, failed 0, skipped 0");
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task SetPipelineStatusMessageAsync_LeavesDeploymentStateAndErrorHistoryUntouched()
    {
        // The whole reason for a separate writer: a poll outcome must not clear the failure
        // context of the last deploy, and must not move the deployment state.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();
        var pipelineRtEntityId = await CreatePipelineAsync(data);

        try
        {
            await repository.SetPipelineDeploymentStateAsync(tenantId, pipelineRtEntityId,
                RtDeploymentStateEnum.Error, "trigger registration failed");

            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId,
                "ERROR 2026-09-26T17:40:12Z · Mail folder 'Inbox.02_Steuern' not found", true, DateTime.UtcNow);

            var pipeline = await repository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            pipeline.Should().NotBeNull();
            pipeline!.StatusMessage.Should().Be("ERROR 2026-09-26T17:40:12Z · Mail folder 'Inbox.02_Steuern' not found");
            pipeline.DeploymentState.Should().Be(RtDeploymentStateEnum.Error);
            pipeline.LastDeploymentError.Should().Be("trigger registration failed");
            pipeline.LastDeploymentErrorTimestamp.Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task SetPipelineStatusMessageAsync_EachPollReplacesThePreviousLine()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();
        var pipelineRtEntityId = await CreatePipelineAsync(data);

        try
        {
            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId, "first poll", false, DateTime.UtcNow);
            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId, "second poll", false, DateTime.UtcNow);

            var pipeline = await repository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            pipeline!.StatusMessage.Should().Be("second poll");
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    [Fact]
    public async Task SetPipelineStatusMessageAsync_FailureKeepsLastSuccessAndCountsTheStreak()
    {
        // AB#5618: an error line overwrites StatusMessage but not WHEN the pipeline last succeeded.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var data = new TestData();
        var pipelineRtEntityId = await CreatePipelineAsync(data);
        var succeededAt = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

        try
        {
            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId, "ok", false, succeededAt);
            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId, "ERROR 1", true,
                succeededAt.AddMinutes(5));
            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId, "ERROR 2", true,
                succeededAt.AddMinutes(10));

            var pipeline = await repository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            pipeline.Should().NotBeNull();
            pipeline!.StatusMessage.Should().Be("ERROR 2");
            pipeline.LastSuccessfulStatusAt.Should().Be(succeededAt);
            pipeline.ConsecutiveStatusFailures.Should().Be(2);

            await repository.SetPipelineStatusMessageAsync(tenantId, pipelineRtEntityId, "ok again", false,
                succeededAt.AddMinutes(15));

            pipeline = await repository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            pipeline!.LastSuccessfulStatusAt.Should().Be(succeededAt.AddMinutes(15));
            pipeline.ConsecutiveStatusFailures.Should().Be(0);
        }
        finally
        {
            await CleanupAsync(data);
        }
    }

    /// <summary>
    /// A pipeline cannot exist alone: the CK model requires the inbound ParentChild edge from a
    /// DataFlow and the Executes edge from an Adapter (both minimum multiplicity One), so the
    /// fixture creates the two parents and removes all three afterwards.
    /// </summary>
    private sealed class TestData
    {
        public List<RtEntityId> Pipelines { get; } = new();
        public List<RtEntityId> DataFlows { get; } = new();
        public List<RtEntityId> Adapters { get; } = new();
    }

    private async Task<RtEntityId> CreatePipelineAsync(TestData data)
    {
        var adapter = await CreateAdapterAsync(data);
        var dataFlow = await CreateDataFlowAsync(data);

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
            throw new InvalidOperationException($"Failed to insert test pipeline: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        data.Pipelines.Add(pipelineRtEntityId);
        return pipelineRtEntityId;
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

    private async Task CleanupAsync(TestData data)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

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
