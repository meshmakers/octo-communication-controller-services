using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// AB#8982 — the discovery the automatic reconcile paths and the adapter health rely on, against a
/// real MongoDB: a standalone <c>ServiceAccountConfiguration</c> that a pipeline links via <c>Uses</c>
/// is found (also for the adapter that executes the pipeline), while an unused account, an
/// adapter-owned account and an account used only on another adapter are not. The unit tests
/// substitute the repository, so this is where the graph reads (reverse <c>Uses</c>, <c>Executes</c>,
/// <c>PipelineServiceAccount</c>) are proven to answer the way the lookup expects.
/// </summary>
[Collection("CommunicationController")]
public class UsesLinkedServiceAccountLookupTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task FindAsync_ReturnsUsesLinkedStandaloneAccounts_AndScopesThemToTheExecutingAdapter()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapter = await InsertAdapterAsync();
        var otherAdapter = await InsertAdapterAsync();
        var dataFlow = await InsertDataFlowAsync();

        var linked = BuildAccount("accounting-ai-prompt");
        var linkedElsewhere = BuildAccount("other-adapter-account");
        var unused = BuildAccount("unused-account");
        var adapterOwned = BuildAccount(PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId));
        foreach (var account in new[] { linked, linkedElsewhere, unused })
        {
            await InsertAsync(account);
        }

        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId, Id(adapter), adapterOwned,
            isNewEntity: true);
        await InsertPipelineAsync(dataFlow, adapter, linked);
        await InsertPipelineAsync(dataFlow, otherAdapter, linkedElsewhere);
        // An adapter-owned account linked via Uses as well is still reconciled through its adapter.
        await InsertPipelineAsync(dataFlow, adapter, adapterOwned);

        var tenantWide = (await UsesLinkedServiceAccountLookup.FindAsync(repository, fixture.TestTenantId))
            .Select(c => c.RtId).ToList();
        var forAdapter = (await UsesLinkedServiceAccountLookup.FindAsync(repository, fixture.TestTenantId,
            adapter.RtId)).Select(c => c.RtId).ToList();

        tenantWide.Should().Contain([linked.RtId, linkedElsewhere.RtId]);
        tenantWide.Should().NotContain([unused.RtId, adapterOwned.RtId]);
        forAdapter.Should().Equal(linked.RtId);
    }

    private static RtEntityId Id(RtAdapter adapter)
    {
        return new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
    }

    private static RtServiceAccountConfiguration BuildAccount(string wellKnownName)
    {
        // As a blueprint ships it: no client id is required for the lookup, but the attribute is
        // mandatory on the CK type, so the test writes a placeholder.
        return new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = $"{wellKnownName}-{Guid.NewGuid():N}",
            ClientId = "placeholder"
        };
    }

    private async Task<ITenantRepository> TenantRepositoryAsync()
    {
        return await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
    }

    private async Task<RtAdapter> InsertAdapterAsync()
    {
        var tenantRepository = await TenantRepositoryAsync();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var adapter = await tenantRepository.CreateTransientRtEntityAsync<RtAdapter>();
        adapter.RtId = OctoObjectId.GenerateNewId();
        adapter.Name = $"lookup-adapter-{Guid.NewGuid():N}";
        await tenantRepository.InsertOneRtEntityAsync(session, adapter);
        await session.CommitTransactionAsync();
        return adapter;
    }

    private async Task InsertAsync(RtServiceAccountConfiguration account)
    {
        var tenantRepository = await TenantRepositoryAsync();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(session, account);
        await session.CommitTransactionAsync();
    }

    private async Task<RtEntityId> InsertDataFlowAsync()
    {
        var tenantRepository = await TenantRepositoryAsync();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var dataFlow = new RtDataFlow
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkDataFlowTypeId,
            Name = $"lookup-dataflow-{Guid.NewGuid():N}"
        };
        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtDataFlow>> { EntityUpdateInfo<RtDataFlow>.CreateInsert(dataFlow) },
            operationResult);
        operationResult.HasErrors.Should().BeFalse(operationResult.GetMessages());
        await session.CommitTransactionAsync();
        return new RtEntityId(SystemCommunicationCkIds.RtCkDataFlowTypeId, dataFlow.RtId);
    }

    private async Task InsertPipelineAsync(RtEntityId dataFlow, RtAdapter adapter,
        RtServiceAccountConfiguration usesAccount)
    {
        var tenantRepository = await TenantRepositoryAsync();
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var pipeline = new RtPipeline
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTypeId,
            Name = $"lookup-pipeline-{Guid.NewGuid():N}",
            Enabled = true,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            PipelineDefinition = "name: test"
        };
        var pipelineId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId);
        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipeline>> { EntityUpdateInfo<RtPipeline>.CreateInsert(pipeline) },
            new List<AssociationUpdateInfo>
            {
                AssociationUpdateInfo.CreateInsert(pipelineId, dataFlow, SystemCkIds.RtCkParentChildRoleId),
                AssociationUpdateInfo.CreateInsert(pipelineId, Id(adapter), SystemCommunicationCkIds.RtCkExecutesRoleId),
                AssociationUpdateInfo.CreateInsert(pipelineId,
                    new RtEntityId(SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId, usesAccount.RtId),
                    SystemCommunicationCkIds.RtCkUsesRoleId)
            },
            operationResult);
        operationResult.HasErrors.Should().BeFalse(operationResult.GetMessages());
        operationResult.HasFatalErrors.Should().BeFalse(operationResult.GetMessages());
        await session.CommitTransactionAsync();
    }
}
