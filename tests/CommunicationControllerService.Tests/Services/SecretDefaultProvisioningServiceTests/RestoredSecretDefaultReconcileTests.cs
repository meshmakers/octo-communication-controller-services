using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     A SecretDefault that arrives in a tenant through a dump and restore, not through a projection, is removed by
///     the next reconcile of that tenant unless an Application that reaches the tenant provides exactly that default.
/// </summary>
internal sealed class RestoredSecretDefaultReconcileTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string RestoredTenantId = "restored-r1";

    [Test]
    public async Task ADefaultRestoredIntoAnUnrelatedTenant_IsRemovedAlthoughItsApplicationStillProvidesIt()
    {
        ArrangeTenant(RestoredTenantId, ancestorTenantIds: [], descendantTenantIds: []);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(RestoredTenantId, CaptchaKey, SentinelSecret, SourceTenantId,
            application.RtId.ToString());

        var result = await Service.ProvisionForTargetAsync(RestoredTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.Reconciled);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(result.DefaultsWritten).IsEqualTo(0);
        await Assert.That(StoredDefault(RestoredTenantId, CaptchaKey)).IsNull();
        await Assert.That(TenantsWritten(CaptchaKey)).DoesNotContain(RestoredTenantId);
    }

    [Test]
    public async Task ADefaultRestoredUnderItsSourceNamingAnApplicationThatDoesNotExist_IsRemoved()
    {
        ArrangeTenant(RestoredTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        ArrangeStoredDefault(RestoredTenantId, CaptchaKey, SentinelSecret, SourceTenantId, NewRtId().ToString());

        var result = await Service.ProvisionForTargetAsync(RestoredTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(RestoredTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ADefaultRestoredWithoutSourceFields_IsRemoved()
    {
        ArrangeTenant(RestoredTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        ArrangeStoredDefault(RestoredTenantId, CaptchaKey, SentinelSecret, sourceTenantId: null,
            sourceWorkloadRtId: null);

        var result = await Service.ProvisionForTargetAsync(RestoredTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(RestoredTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ADefaultRestoredUnderItsSource_IsReplacedByTheProjectedValueOfTheProvidingApplication()
    {
        ArrangeTenant(RestoredTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(RestoredTenantId, CaptchaKey, "zq7247-value-of-the-dumped-instance", SourceTenantId,
            application.RtId.ToString());

        await Service.ProvisionForTargetAsync(RestoredTenantId);

        await Assert.That(StoredDefault(RestoredTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }
}
