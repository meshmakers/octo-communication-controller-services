using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     The write half of the SecretDefault projection against a real MongoDB: the engine encrypts the value,
///     a second write rotates it in place, and no other entity of the tenant is touched.
/// </summary>
[Collection("CommunicationController")]
public class SecretDefaultRepositoryTests(CommunicationControllerFixture fixture)
{
    private const string SourceTenantId = "secret-default-source";

    private static SecretDefaultWrite BuildWrite(string key, string plaintext, string sourceWorkloadRtId)
    {
        return new SecretDefaultWrite(key, RtSecretValue.Pending(plaintext), SourceTenantId, sourceWorkloadRtId,
            new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc));
    }

    private async Task<IReadOnlyList<RtSecretDefault>> ReadSecretDefaultsAsync(string key)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtSecretDefault>(session,
            RtEntityQueryOptions.Create());
        return resultSet.Items.Where(secretDefault => secretDefault.RtWellKnownName == key).ToList();
    }

    private string? Reveal(RtSecretValue? stored)
    {
        stored.Should().NotBeNull();
        stored!.IsProtected.Should().BeTrue("the engine encrypts every written secret");
        return fixture.GetService<ISecretAttributeProtector>().Unprotect(stored);
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_CreatesTheEntityUnderTheKeyWithAnEncryptedValue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        var sourceWorkloadRtId = OctoObjectId.GenerateNewId().ToString();

        var outcome = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7244-integration-first", sourceWorkloadRtId));

        outcome.Should().Be(SecretDefaultWriteOutcome.Created);
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        stored.Name.Should().Be(key);
        stored.SourceTenantId.Should().Be(SourceTenantId);
        stored.SourceWorkloadRtId.Should().Be(sourceWorkloadRtId);
        stored.LastProjectedAt.Should().NotBeNull();
        stored.Value!.Envelope.Should().NotContain("zq7244-integration-first");
        Reveal(stored.Value).Should().Be("zq7244-integration-first");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_ASecondWrite_RotatesTheValueInPlace()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        var sourceWorkloadRtId = OctoObjectId.GenerateNewId().ToString();
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7244-integration-first", sourceWorkloadRtId));
        var firstRtId = (await ReadSecretDefaultsAsync(key)).Single().RtId;

        var outcome = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7244-integration-rotated", sourceWorkloadRtId));

        outcome.Should().Be(SecretDefaultWriteOutcome.Updated);
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        stored.RtId.Should().Be(firstRtId);
        Reveal(stored.Value).Should().Be("zq7244-integration-rotated");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_LeavesAnotherSecretOfTheTenantUnchanged()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        var tenantOwnSecret = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = $"tenant-own-{Guid.NewGuid():N}",
            ClientId = "tenant-own-client",
            ClientSecret = RtSecretValue.Pending("zq7244-tenant-own"),
            IssuerUri = "https://identity.example.com",
            TenantId = fixture.TestTenantId
        };
        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            await tenantRepository.InsertOneRtEntityAsync(session, tenantOwnSecret);
            await session.CommitTransactionAsync();
        }

        var before = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId, tenantOwnSecret.RtId);

        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(tenantOwnSecret.RtWellKnownName!, "zq7244-default", OctoObjectId.GenerateNewId().ToString()));

        var after = await repository.GetServiceAccountByRtIdAsync(fixture.TestTenantId, tenantOwnSecret.RtId);
        after!.RtVersion.Should().Be(before!.RtVersion);
        after.ClientSecret!.Envelope.Should().Be(before.ClientSecret!.Envelope);
        Reveal(after.ClientSecret).Should().Be("zq7244-tenant-own");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_AnUnknownTenant_IsReportedUnavailable()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var outcome = await repository.UpsertSecretDefaultAsync($"missing-{Guid.NewGuid():N}",
            BuildWrite("secret-default-missing", "zq7244-unused", OctoObjectId.GenerateNewId().ToString()));

        outcome.Should().Be(SecretDefaultWriteOutcome.TenantUnavailable);
    }
}
