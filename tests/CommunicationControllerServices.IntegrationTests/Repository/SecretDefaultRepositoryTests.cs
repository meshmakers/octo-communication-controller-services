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

        var writeResult = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7244-integration-first", sourceWorkloadRtId));

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.Created);
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

        var writeResult = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7244-integration-rotated", sourceWorkloadRtId));

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.Updated);
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

        var writeResult = await repository.UpsertSecretDefaultAsync($"missing-{Guid.NewGuid():N}",
            BuildWrite("secret-default-missing", "zq7244-unused", OctoObjectId.GenerateNewId().ToString()));

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.TenantUnavailable);
    }

    private async Task<RtSecretDefault> InsertSecretDefaultAsync(string key, string plaintext, string sourceTenantId)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        var secretDefault = await tenantRepository.CreateTransientRtEntityAsync<RtSecretDefault>();
        secretDefault.RtId = OctoObjectId.GenerateNewId();
        secretDefault.RtWellKnownName = key;
        secretDefault.Name = key;
        secretDefault.Value = RtSecretValue.Pending(plaintext);
        secretDefault.SourceTenantId = sourceTenantId;
        secretDefault.SourceWorkloadRtId = OctoObjectId.GenerateNewId().ToString();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(session, secretDefault);
        await session.CommitTransactionAsync();
        return secretDefault;
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_TwoDefaultsWithTheSameName_LeavesExactlyOneWithTheNewValue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        await InsertSecretDefaultAsync(key, "zq7244-integration-old-one", SourceTenantId);
        await InsertSecretDefaultAsync(key, "zq7244-integration-old-two", SourceTenantId);

        var writeResult = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7244-integration-new", OctoObjectId.GenerateNewId().ToString()));

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.Updated);
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        Reveal(stored.Value).Should().Be("zq7244-integration-new");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_ADefaultFromAProtectedSource_IsKeptUnchanged()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        var ancestorDefault = await InsertSecretDefaultAsync(key, "zq7244-integration-ancestor", "ancestor-tenant");
        var write = new SecretDefaultWrite(key, RtSecretValue.Pending("zq7244-integration-descendant"),
            "descendant-tenant", OctoObjectId.GenerateNewId().ToString(),
            new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc), ["ancestor-tenant"]);

        var writeResult = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId, write);

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.KeptAncestorSource);
        writeResult.KeptSourceTenantId.Should().Be("ancestor-tenant");
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        stored.RtId.Should().Be(ancestorDefault.RtId);
        stored.SourceTenantId.Should().Be("ancestor-tenant");
        Reveal(stored.Value).Should().Be("zq7244-integration-ancestor");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_ASupersededDefaultFromAProtectedSource_IsReplacedInPlace()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        var ancestorDefault = await InsertSecretDefaultAsync(key, "zq7245-integration-withdrawn", "ancestor-tenant");
        var supersededDefault = (await repository.GetSecretDefaultsAsync(fixture.TestTenantId)).Defaults
            .Single(storedDefault => storedDefault.Key == key);
        var write = new SecretDefaultWrite(key, RtSecretValue.Pending("zq7245-integration-takeover"),
            "descendant-tenant", OctoObjectId.GenerateNewId().ToString(),
            new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc), ["ancestor-tenant"], [supersededDefault]);

        var writeResult = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId, write);

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.Updated);
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        stored.RtId.Should().Be(ancestorDefault.RtId);
        stored.SourceTenantId.Should().Be("descendant-tenant");
        Reveal(stored.Value).Should().Be("zq7245-integration-takeover");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_AProtectedDefaultRewrittenAfterTheRead_IsKept()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        await InsertSecretDefaultAsync(key, "zq7245-integration-withdrawn", "ancestor-tenant");
        var supersededDefault = (await repository.GetSecretDefaultsAsync(fixture.TestTenantId)).Defaults
            .Single(storedDefault => storedDefault.Key == key);
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            new SecretDefaultWrite(key, RtSecretValue.Pending("zq7245-integration-redeployed"), "ancestor-tenant",
                supersededDefault.SourceWorkloadRtId!, new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc)));
        var write = new SecretDefaultWrite(key, RtSecretValue.Pending("zq7245-integration-takeover"),
            "descendant-tenant", OctoObjectId.GenerateNewId().ToString(),
            new DateTime(2026, 11, 6, 11, 0, 0, DateTimeKind.Utc), ["ancestor-tenant"], [supersededDefault]);

        var writeResult = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId, write);

        writeResult.Outcome.Should().Be(SecretDefaultWriteOutcome.KeptAncestorSource);
        Reveal((await ReadSecretDefaultsAsync(key)).Single().Value).Should().Be("zq7245-integration-redeployed");
    }

    [Fact]
    public async Task GetSecretDefaultsAsync_PrintsNoValue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-printed", OctoObjectId.GenerateNewId().ToString()));

        var listed = (await repository.GetSecretDefaultsAsync(fixture.TestTenantId)).Defaults
            .Single(storedDefault => storedDefault.Key == key);

        listed.Value.Should().NotBeNull();
        listed.ToString().Should().NotContain(listed.Value!.Envelope);
        listed.ToString().Should().NotContain("zq7245-integration-printed");
    }

    [Fact]
    public async Task UpsertSecretDefaultAsync_TwoConcurrentWritesOfANewKey_LeaveExactlyOneEntity()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var concreteRepository = repository.Should().BeOfType<CommunicationRepository>().Subject;
        var key = $"secret-default-{Guid.NewGuid():N}";
        var firstWorkloadRtId = OctoObjectId.GenerateNewId().ToString();
        var secondWorkloadRtId = OctoObjectId.GenerateNewId().ToString();
        var bothWritersHaveRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsOfTheKey = 0;
        concreteRepository.AfterSecretDefaultUpsertReadAsync = async readKey =>
        {
            if (readKey != key)
            {
                return;
            }

            var readNumber = Interlocked.Increment(ref readsOfTheKey);
            if (readNumber > 2)
            {
                return;
            }

            if (readNumber == 2)
            {
                bothWritersHaveRead.TrySetResult();
            }

            await bothWritersHaveRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };

        SecretDefaultWriteResult[] writeResults;
        try
        {
            writeResults = await Task.WhenAll(
                Task.Run(() => repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
                    BuildWrite(key, "zq7245-integration-first-writer", firstWorkloadRtId))),
                Task.Run(() => repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
                    BuildWrite(key, "zq7245-integration-second-writer", secondWorkloadRtId))));
        }
        finally
        {
            concreteRepository.AfterSecretDefaultUpsertReadAsync = null;
        }

        readsOfTheKey.Should().BeGreaterThan(2, "both writers read before either wrote, so one had to repeat");
        writeResults.Select(writeResult => writeResult.Outcome).Should().BeEquivalentTo(
            [SecretDefaultWriteOutcome.Created, SecretDefaultWriteOutcome.Updated]);
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        Reveal(stored.Value).Should().BeOneOf("zq7245-integration-first-writer", "zq7245-integration-second-writer");
    }

    [Fact]
    public async Task GetSecretDefaultsAsync_ListsKeyAndSourceButNoValue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        var sourceWorkloadRtId = OctoObjectId.GenerateNewId().ToString();
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-listed", sourceWorkloadRtId));

        var inventory = await repository.GetSecretDefaultsAsync(fixture.TestTenantId);

        inventory.Availability.Should().Be(SecretDefaultTenantAvailability.Available);
        var listed = inventory.Defaults.Should().ContainSingle(storedDefault => storedDefault.Key == key).Subject;
        listed.SourceTenantId.Should().Be(SourceTenantId);
        listed.SourceWorkloadRtId.Should().Be(sourceWorkloadRtId);
        listed.ToString().Should().NotContain("zq7245-integration-listed");
    }

    private static async Task<StoredSecretDefault> ListedDefaultAsync(ICommunicationRepository repository,
        string tenantId, string key)
    {
        return (await repository.GetSecretDefaultsAsync(tenantId)).Defaults
            .Single(storedDefault => storedDefault.Key == key);
    }

    [Fact]
    public async Task DeleteSecretDefaultIfUnchangedAsync_ErasesTheEntityAndANewWriteCreatesItAgain()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-deleted", OctoObjectId.GenerateNewId().ToString()));
        var listedDefault = await ListedDefaultAsync(repository, fixture.TestTenantId, key);

        var deleteOutcome = await repository.DeleteSecretDefaultIfUnchangedAsync(fixture.TestTenantId, listedDefault);
        var afterDelete = await ReadSecretDefaultsAsync(key);
        var recreated = await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-recreated", OctoObjectId.GenerateNewId().ToString()));

        deleteOutcome.Should().Be(SecretDefaultDeleteOutcome.Deleted);
        afterDelete.Should().BeEmpty();
        recreated.Outcome.Should().Be(SecretDefaultWriteOutcome.Created);
        Reveal((await ReadSecretDefaultsAsync(key)).Single().Value).Should().Be("zq7245-integration-recreated");
    }

    [Fact]
    public async Task DeleteSecretDefaultIfUnchangedAsync_ADefaultWrittenAfterTheRead_IsKept()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-read", OctoObjectId.GenerateNewId().ToString()));
        var listedDefault = await ListedDefaultAsync(repository, fixture.TestTenantId, key);
        var takingOverWorkloadRtId = OctoObjectId.GenerateNewId().ToString();
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-rewritten", takingOverWorkloadRtId));

        var deleteOutcome = await repository.DeleteSecretDefaultIfUnchangedAsync(fixture.TestTenantId, listedDefault);

        deleteOutcome.Should().Be(SecretDefaultDeleteOutcome.Changed);
        var stored = (await ReadSecretDefaultsAsync(key)).Should().ContainSingle().Subject;
        stored.SourceWorkloadRtId.Should().Be(takingOverWorkloadRtId);
        Reveal(stored.Value).Should().Be("zq7245-integration-rewritten");
    }

    [Fact]
    public async Task DeleteSecretDefaultIfUnchangedAsync_ADefaultThatIsGone_IsReportedNotFound()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var key = $"secret-default-{Guid.NewGuid():N}";
        await repository.UpsertSecretDefaultAsync(fixture.TestTenantId,
            BuildWrite(key, "zq7245-integration-gone", OctoObjectId.GenerateNewId().ToString()));
        var listedDefault = await ListedDefaultAsync(repository, fixture.TestTenantId, key);
        await repository.DeleteSecretDefaultIfUnchangedAsync(fixture.TestTenantId, listedDefault);

        var deleteOutcome = await repository.DeleteSecretDefaultIfUnchangedAsync(fixture.TestTenantId, listedDefault);

        deleteOutcome.Should().Be(SecretDefaultDeleteOutcome.NotFound);
    }

    [Fact]
    public async Task GetSecretDefaultsAsync_AnUnknownTenant_IsReportedUnavailable()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var inventory = await repository.GetSecretDefaultsAsync($"missing-{Guid.NewGuid():N}");

        inventory.Availability.Should().Be(SecretDefaultTenantAvailability.TenantUnavailable);
    }

    [Fact]
    public async Task GetSecretDefaultSourceApplicationsAsync_AnUnknownTenant_IsReportedUnavailable()
    {
        var repository = fixture.GetService<ICommunicationRepository>();

        var sources = await repository.GetSecretDefaultSourceApplicationsAsync($"missing-{Guid.NewGuid():N}");

        sources.Availability.Should().Be(SecretDefaultTenantAvailability.TenantUnavailable);
        sources.Applications.Should().BeEmpty();
    }
}
