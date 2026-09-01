using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// AB#5027 phase 2 — the persistence half of provisioning, against a real MongoDB.
///
/// <para>
/// The unit tests substitute the repository, so this is the only place that proves the entity write
/// and the <c>PipelineServiceAccount</c> edge actually land, that the ZeroOrOne outbound
/// multiplicity survives a repeat run (re-inserting an existing edge would be rejected by the
/// engine), and that <c>GetServiceAccountForAdapterAsync</c> — the read the phase 1 deploy guard
/// depends on — sees what provisioning wrote.
/// </para>
/// </summary>
[Collection("CommunicationController")]
public class PipelineServiceAccountRepositoryTests(CommunicationControllerFixture fixture)
{
    private async Task<RtAdapter> CreateAdapterAsync(string name)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        var adapter = await tenantRepository.CreateTransientRtEntityAsync<RtAdapter>();
        adapter.RtId = OctoObjectId.GenerateNewId();
        adapter.Name = name;
        await tenantRepository.InsertOneRtEntityAsync(session, adapter);
        await session.CommitTransactionAsync();
        return adapter;
    }

    private static RtServiceAccountConfiguration BuildServiceAccount(string wellKnownName, string secret,
        string tenantId, OctoObjectId? rtId = null)
    {
        return new RtServiceAccountConfiguration
        {
            RtId = rtId ?? OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = wellKnownName,
            ClientId = "octo-pipeline-sa-test",
            ClientSecret = secret,
            IssuerUri = "https://identity.example.com",
            TenantId = tenantId
        };
    }

    [Fact]
    public async Task SavePipelineServiceAccountAsync_CreatesTheEntityAndTheAdapterEdge()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapter = await CreateAdapterAsync($"sa-adapter-{Guid.NewGuid():N}");
        var wellKnownName = $"pipeline-service-account-{adapter.RtId}";
        var serviceAccount = BuildServiceAccount(wellKnownName, "secret-one", fixture.TestTenantId);

        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId), serviceAccount,
            isNewEntity: true);

        // The read the phase 1 deploy guard performs.
        var linked = await repository.GetServiceAccountForAdapterAsync(fixture.TestTenantId, adapter.RtId);
        linked.Should().NotBeNull();
        linked!.RtId.Should().Be(serviceAccount.RtId);
        linked.ClientSecret.Should().Be("secret-one");

        // The read that makes a second provisioning run adopt its own earlier work.
        var byName = await repository.GetServiceAccountByWellKnownNameAsync(fixture.TestTenantId, wellKnownName);
        byName.Should().NotBeNull();
        byName!.RtId.Should().Be(serviceAccount.RtId);
    }

    [Fact]
    public async Task SavePipelineServiceAccountAsync_RunTwice_KeepsExactlyOneEdgeAndUpdatesInPlace()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapter = await CreateAdapterAsync($"sa-adapter-{Guid.NewGuid():N}");
        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
        var wellKnownName = $"pipeline-service-account-{adapter.RtId}";
        var serviceAccount = BuildServiceAccount(wellKnownName, "secret-one", fixture.TestTenantId);

        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId, adapterRtEntityId, serviceAccount,
            isNewEntity: true);

        // Second pass: same entity, edge already there. Re-inserting the edge would violate the
        // ZeroOrOne outbound multiplicity, so this is the case that would break a naive repeat run.
        var updated = BuildServiceAccount(wellKnownName, "secret-two", fixture.TestTenantId, serviceAccount.RtId);
        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId, adapterRtEntityId, updated,
            isNewEntity: false);

        var linked = await repository.GetServiceAccountForAdapterAsync(fixture.TestTenantId, adapter.RtId);
        linked.Should().NotBeNull();
        linked!.RtId.Should().Be(serviceAccount.RtId);
        linked.ClientSecret.Should().Be("secret-two");
    }

    [Fact]
    public async Task SavePipelineServiceAccountAsync_DifferentAccount_ReplacesTheEdge()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapter = await CreateAdapterAsync($"sa-adapter-{Guid.NewGuid():N}");
        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);

        var first = BuildServiceAccount($"sa-first-{adapter.RtId}", "secret-one", fixture.TestTenantId);
        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId, adapterRtEntityId, first,
            isNewEntity: true);

        var second = BuildServiceAccount($"sa-second-{adapter.RtId}", "secret-two", fixture.TestTenantId);
        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId, adapterRtEntityId, second,
            isNewEntity: true);

        // An adapter has exactly one default identity — the old edge must be gone, not coexist.
        var linked = await repository.GetServiceAccountForAdapterAsync(fixture.TestTenantId, adapter.RtId);
        linked.Should().NotBeNull();
        linked!.RtId.Should().Be(second.RtId);
    }

    [Fact]
    public async Task GetServiceAccountByWellKnownNameAsync_UnknownName_ReturnsNull()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var result = await repository.GetServiceAccountByWellKnownNameAsync(fixture.TestTenantId,
            $"does-not-exist-{Guid.NewGuid():N}");

        result.Should().BeNull();
    }
}
