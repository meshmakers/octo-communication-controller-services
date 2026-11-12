using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     An invalid entry is never written, but its AllowedTenantIds still decide where its key reaches: a tenant the entry
///     excludes loses the default it holds, an allowed tenant keeps it. Entries of a duplicated key keep a tenant only
///     while every one of them allows it.
/// </summary>
internal sealed class SecretDefaultInvalidEntryRevocationTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string DuplicateSecret = "zq7245-duplicate-value";

    private static readonly string[] ServedTenantIds =
        [SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId];

    [Test]
    public async Task InvalidDeclaration_NarrowedAllowedTenants_ExcludedTenantLosesDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(SecondChildTenantId);
        application.SecretDefaults![0] = EntryWithoutValue(CaptchaKey, FirstChildTenantId);

        var result = await Service.ProvisionForTargetAsync(SecondChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(SecondChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task InvalidDeclaration_NarrowedAllowedTenants_AllowedTenantKeepsDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults![0] = EntryWithoutValue(CaptchaKey, FirstChildTenantId);
        var writesBefore = Writes.Count;

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(Writes.Count).IsEqualTo(writesBefore);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }

    [Test]
    public async Task DuplicatedKey_KeepsOnlyTenantsEveryDuplicateAllows()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        await Service.ProvisionForTargetAsync(SecondChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
        {
            ProtectedEntry(CaptchaKey, SentinelSecret, FirstChildTenantId),
            ProtectedEntry(CaptchaKey, DuplicateSecret, FirstChildTenantId, SecondChildTenantId)
        };

        var firstChildResult = await Service.ProvisionForTargetAsync(FirstChildTenantId);
        var secondChildResult = await Service.ProvisionForTargetAsync(SecondChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(firstChildResult.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        await Assert.That(secondChildResult.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(SecondChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task RedeployWithInvalidNarrowedEntry_SweepsTheExcludedTenantsAndKeepsTheAllowedOne()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults![0] = EntryWithoutValue(CaptchaKey, FirstChildTenantId);

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(3);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        foreach (var excludedTenantId in ServedTenantIds.Where(tenantId => tenantId != FirstChildTenantId))
        {
            await Assert.That(StoredDefault(excludedTenantId, CaptchaKey)).IsNull();
        }
    }

    [Test]
    public async Task RedeployWithDuplicatedKey_SweepsEveryTenantNotAllowedByAllDuplicates()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
        {
            ProtectedEntry(CaptchaKey, SentinelSecret, FirstChildTenantId, GrandchildTenantId),
            ProtectedEntry(CaptchaKey, DuplicateSecret, FirstChildTenantId, SecondChildTenantId)
        };

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(3);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        foreach (var excludedTenantId in ServedTenantIds.Where(tenantId => tenantId != FirstChildTenantId))
        {
            await Assert.That(StoredDefault(excludedTenantId, CaptchaKey)).IsNull();
        }
    }

    [Test]
    public async Task RedeployWithInvalidEntryWithoutAllowedTenants_KeepsEveryDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults![0] = EntryWithoutValue(CaptchaKey);

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        foreach (var servedTenantId in ServedTenantIds)
        {
            await Assert.That(StoredDefault(servedTenantId, CaptchaKey)).IsNotNull();
        }
    }

    private RtSecretDefaultProjectionRecord EntryWithoutValue(string key, params string[] allowedTenantIds)
    {
        var entry = ProtectedEntry(key, SentinelSecret, allowedTenantIds);
        entry.SecretValue = null;
        return entry;
    }
}
