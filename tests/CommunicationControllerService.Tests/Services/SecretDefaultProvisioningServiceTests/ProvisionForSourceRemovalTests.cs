using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     A redeploy removes what the Application no longer provides: an entry taken out of SecretDefaults, or a
///     tenant that left the entry's AllowedTenantIds.
/// </summary>
internal sealed class ProvisionForSourceRemovalTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string OtherKey = "other-app-secret";

    [Test]
    public async Task ARedeployWithoutTheEntry_DeletesItInEveryTarget()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(4);
        foreach (var tenantId in new[] { SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId })
        {
            await Assert.That(StoredDefault(tenantId, CaptchaKey)).IsNull();
        }
    }

    [Test]
    public async Task ARedeployWithoutTheEntry_KeepsTheDefaultOfAnotherApplication()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        var otherApplication = ArrangeApplication(ProtectedEntry(OtherKey, "zq7245-other-app-sentinel"));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        await Service.ProvisionForSourceAsync(SourceTenantId, otherApplication.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(GrandchildTenantId, OtherKey)!.SourceWorkloadRtId)
            .IsEqualTo(otherApplication.RtId.ToString());
    }

    [Test]
    public async Task ATenantThatLeftAllowedTenantIds_LosesTheDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { ProtectedEntry(CaptchaKey, SentinelSecret, FirstChildTenantId) };

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(3);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNotNull();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ARemovalWhereAnotherAncestorDeclaresTheKey_HandsTheTenantToThatSource()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        parentApplication.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();

        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-child-value");
    }

    [Test]
    public async Task ADefaultOfTheSameKeyFromAnotherSource_IsNotRemovedByTheSweep()
    {
        var application = ArrangeApplication(ProtectedEntry(OtherKey, "zq7245-other-app-sentinel"));
        ArrangeStoredDefault(GrandchildTenantId, CaptchaKey, SentinelSecret, FirstChildTenantId,
            Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId.GenerateNewId().ToString());

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNotNull();
    }

    [Test]
    public async Task ARemovalWhoseHandOverWriteFails_KeepsTheOldDefaultInThatTenant()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        parentApplication.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        ArrangeFailure(GrandchildTenantId, new InvalidOperationException("write refused"));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-parent-value");
        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)).IsNull();
    }
}
