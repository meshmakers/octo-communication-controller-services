using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
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
            // AB#5537: plaintext input — the engine encrypts it on write (Secret since 4.8).
            ClientSecret = RtSecretValue.Pending(secret),
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
        Reveal(linked!.ClientSecret).Should().Be("secret-one");

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
        Reveal(linked!.ClientSecret).Should().Be("secret-two");
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

    // ------------------------------------------------------------------------- AB#5111

    [Fact]
    public async Task GetAdapterForServiceAccountAsync_ResolvesTheOwningAdapterThroughTheInboundEdge()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapter = await CreateAdapterAsync($"sa-adapter-{Guid.NewGuid():N}");
        var adapterRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
        var serviceAccount = BuildServiceAccount($"pipeline-service-account-{adapter.RtId}", "secret-one",
            fixture.TestTenantId);
        await repository.SavePipelineServiceAccountAsync(fixture.TestTenantId, adapterRtEntityId, serviceAccount,
            isNewEntity: true);

        // The reverse lookup the configuration-bound reconcile/rotate endpoints route through.
        var owner = await repository.GetAdapterForServiceAccountAsync(fixture.TestTenantId, serviceAccount.RtId);

        owner.Should().NotBeNull();
        owner!.RtId.Should().Be(adapter.RtId);
    }

    [Fact]
    public async Task GetAdapterForServiceAccountAsync_StandaloneAccount_ReturnsNull()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var standalone = BuildServiceAccount($"sa-standalone-{Guid.NewGuid():N}", "secret-one",
            fixture.TestTenantId);
        // Written without any adapter edge — a per-pipeline override account.
        await InsertServiceAccountAsync(standalone);

        var owner = await repository.GetAdapterForServiceAccountAsync(fixture.TestTenantId, standalone.RtId);

        owner.Should().BeNull();
    }

    [Fact]
    public async Task GetServiceAccountByRtIdAsync_RoundTripsWithUpdateServiceAccountAsync()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var standalone = BuildServiceAccount($"sa-standalone-{Guid.NewGuid():N}", "secret-one",
            fixture.TestTenantId);
        await InsertServiceAccountAsync(standalone);

        var loaded = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId, standalone.RtId);
        loaded.Should().NotBeNull();
        Reveal(loaded!.ClientSecret).Should().Be("secret-one");

        // The standalone write path of the AB#5111 reconcile/rotation — entity only, no edge.
        var updated = BuildServiceAccount(standalone.RtWellKnownName!, "secret-two", fixture.TestTenantId,
            standalone.RtId);
        await repository.UpdateServiceAccountAsync(fixture.TestTenantId, updated);

        var reloaded = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId, standalone.RtId);
        reloaded.Should().NotBeNull();
        Reveal(reloaded!.ClientSecret).Should().Be("secret-two");
    }

    [Fact]
    public async Task GetServiceAccountByRtIdAsync_UnknownRtId_ReturnsNull()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var result = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId,
            OctoObjectId.GenerateNewId());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetServiceAccountsAsync_ListsEveryServiceAccountConfiguration()
    {
        // AB#5537: the input of the impersonation switch.
        var repository = fixture.GetService<ICommunicationRepository>();
        var first = BuildServiceAccount($"sa-list-{Guid.NewGuid():N}", "secret-one", fixture.TestTenantId);
        var second = BuildServiceAccount($"sa-list-{Guid.NewGuid():N}", "secret-two", fixture.TestTenantId);
        await InsertServiceAccountAsync(first);
        await InsertServiceAccountAsync(second);

        var all = await repository.GetServiceAccountsAsync(fixture.TestTenantId);

        all.Select(a => a.RtId).Should().Contain([first.RtId, second.RtId]);
    }

    [Fact]
    public async Task UpdateServiceAccountAsync_ExplicitNullSecret_ClearsTheStoredSecret()
    {
        // AB#5537 / AB#5114: the impersonation switch clears the secret with an explicit null on an
        // update — an omitted attribute would leave it in place.
        var repository = fixture.GetService<ICommunicationRepository>();
        var standalone = BuildServiceAccount($"sa-clear-{Guid.NewGuid():N}", "secret-one", fixture.TestTenantId);
        await InsertServiceAccountAsync(standalone);

        var cleared = BuildServiceAccount(standalone.RtWellKnownName!, "unused", fixture.TestTenantId,
            standalone.RtId);
        cleared.SetAttributeRawValue(nameof(RtServiceAccountConfiguration.ClientSecret), null);
        await repository.UpdateServiceAccountAsync(fixture.TestTenantId, cleared);

        var reloaded = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId, standalone.RtId);
        reloaded.Should().NotBeNull();
        reloaded!.GetAttributeValueOrDefault(nameof(RtServiceAccountConfiguration.ClientSecret)).Should().BeNull();
        reloaded.ClientId.Should().Be("octo-pipeline-sa-test");
    }

    /// <summary>AB#5537: the stored secret is an enc:v2 envelope; decrypt it with the fixture's key ring.</summary>
    private string? Reveal(RtSecretValue? stored)
    {
        stored.Should().NotBeNull();
        stored!.IsProtected.Should().BeTrue("the engine encrypts every written secret");
        return fixture.GetService<ISecretAttributeProtector>().Unprotect(stored);
    }

    [Fact]
    public async Task StoredSecret_IsAnEncryptedEnvelope_NeverThePlaintext()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var standalone = BuildServiceAccount($"sa-envelope-{Guid.NewGuid():N}", "envelope-secret",
            fixture.TestTenantId);
        await InsertServiceAccountAsync(standalone);

        var loaded = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId, standalone.RtId);

        loaded!.ClientSecret!.Envelope.Should().StartWith("enc:v2:k1:");
        loaded.ClientSecret.Envelope.Should().NotContain("envelope-secret");
        Reveal(loaded.ClientSecret).Should().Be("envelope-secret");
    }

    private async Task InsertServiceAccountAsync(RtServiceAccountConfiguration serviceAccount)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(session, serviceAccount);
        await session.CommitTransactionAsync();
    }
}
