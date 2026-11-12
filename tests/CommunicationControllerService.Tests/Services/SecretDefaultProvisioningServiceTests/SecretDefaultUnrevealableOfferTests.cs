using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     A value whose reveal throws (a host without key ring, strict mode against legacy clear text) is an unreadable
///     value: it fails the write of its key only, never the whole projection or reconcile.
/// </summary>
internal sealed class SecretDefaultUnrevealableOfferTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string ChildSecret = "zq7245-child-value-unrevealable";
    private const string RotatedSecret = "zq7245-rotated-value-unrevealable";
    private const string MailKey = "ec-mail-secret";
    private const string MailSecret = "zq7245-mail-value";
    private const string WithdrawnKey = "ec-withdrawn-secret";

    [Test]
    public async Task AnUndeployWhoseReplacementCannotBeRevealed_StillRemovesTheWithdrawnDefault()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        parentApplication.DeploymentState = RtDeploymentStateEnum.Undeployed;
        var serviceThatCannotRevealTheChildOffer = CreateServiceFailingRevealsOf(FirstChildTenantId);

        var result = await serviceThatCannotRevealTheChildOffer.WithdrawForSourceAsync(SourceTenantId,
            parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)).IsNull();
        await Assert.That(result.TargetsFailed).IsEqualTo(0);
    }

    [Test]
    public async Task AReconcileWhoseOfferCannotBeRevealed_CountsAFailedWriteInsteadOfThrowing()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        var serviceThatCannotRevealTheParentOffer = CreateServiceFailingRevealsOf(SourceTenantId);

        var result = await serviceThatCannotRevealTheParentOffer.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ADeployWhoseDeclarationCannotBeRevealed_ProjectsTheOtherDeclarations()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, RotatedSecret),
            ProtectedEntry(MailKey, MailSecret));
        var serviceThatCannotRevealOneValue = CreateServiceFailingRevealsOfPlaintext(RotatedSecret);

        var result = await serviceThatCannotRevealOneValue.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await Assert.That(TenantsWritten(MailKey)).IsEquivalentTo(new[]
            { FirstChildTenantId, GrandchildTenantId, SourceTenantId, SecondChildTenantId });
        await Assert.That(TenantsWritten(CaptchaKey)).IsEmpty();
        await Assert.That(StoredEventMessages()).Contains(message =>
            message.Contains(CaptchaKey) && message.Contains("cannot be revealed on this host"));
        await Assert.That(StoredEventMessages().Any(message => message.Contains(RotatedSecret))).IsFalse();
    }

    [Test]
    public async Task ARedeployWhoseDeclarationCannotBeRevealed_KeepsItsDefaultAndStillSweepsWithdrawnKeys()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret),
            ProtectedEntry(WithdrawnKey, MailSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
        {
            ProtectedEntry(CaptchaKey, RotatedSecret)
        };
        var serviceThatCannotRevealTheRotation = CreateServiceFailingRevealsOfPlaintext(RotatedSecret);

        var result = await serviceThatCannotRevealTheRotation.ProvisionForSourceAsync(SourceTenantId,
            application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(4);
        foreach (var servedTenantId in new[]
                     { SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId })
        {
            await Assert.That(StoredDefault(servedTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
            await Assert.That(StoredDefault(servedTenantId, WithdrawnKey)).IsNull();
        }
    }
}
