using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     A value whose reveal throws (a host without key ring, strict mode against legacy clear text) is an unreadable
///     value: it fails the write of its key only, never the whole projection or reconcile.
/// </summary>
internal sealed class SecretDefaultUnrevealableOfferTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string ChildSecret = "zq7245-child-value-unrevealable";

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
}
