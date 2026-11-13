namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     Revocation wins over keeping the old value: a failed replacement keeps only a default whose source still
///     reaches the tenant; a default whose source no longer reaches it is removed, and the tenant has no default for
///     that key until the replacement can be written.
/// </summary>
internal sealed class SecretDefaultRevocationTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string ParentSecret = "zq7245-parent-value-revoked";
    private const string RotatedParentSecret = "zq7245-parent-value-rotated";
    private const string ChildSecret = "zq7245-child-value-unrevealable";
    private const string NewParentTenantId = "parent-q";
    private const string NewParentSecret = "zq7245-new-parent-value-unrevealable";

    [Test]
    public async Task ATenantDroppedFromAllowedTenants_LosesTheDefaultAlthoughTheReplacementCannotBeRevealed()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, ParentSecret));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildSecret));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        parentApplication.SecretDefaults![0] =
            ProtectedEntry(CaptchaKey, ParentSecret, FirstChildTenantId, SecondChildTenantId);
        var serviceThatCannotRevealTheChildOffer = CreateServiceFailingRevealsOf(FirstChildTenantId);

        var result = await serviceThatCannotRevealTheChildOffer.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ATenantMovedToAnotherParent_LosesTheFormerParentsDefaultAlthoughTheNewOfferCannotBeRevealed()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, ParentSecret));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        ArrangeTenant(NewParentTenantId, ancestorTenantIds: [], descendantTenantIds: [GrandchildTenantId]);
        ArrangeTenant(GrandchildTenantId, ancestorTenantIds: [NewParentTenantId], descendantTenantIds: []);
        ArrangeApplicationIn(NewParentTenantId, ProtectedEntry(CaptchaKey, NewParentSecret));
        var serviceThatCannotRevealTheNewOffer = CreateServiceFailingRevealsOf(NewParentTenantId);

        var result = await serviceThatCannotRevealTheNewOffer.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task AStillReachingSourceWhoseRotationCannotBeRevealed_KeepsItsOldValue()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, ParentSecret));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        parentApplication.SecretDefaults![0] = ProtectedEntry(CaptchaKey, RotatedParentSecret);
        var serviceThatCannotRevealTheRotation = CreateServiceFailingRevealsOfPlaintext(RotatedParentSecret);

        var result = await serviceThatCannotRevealTheRotation.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ParentSecret);
    }

    [Test]
    public async Task AStillReachingSourceWhoseTakeOverCannotBeRevealed_KeepsItsOldValue()
    {
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildSecret));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        ArrangeApplication(ProtectedEntry(CaptchaKey, ParentSecret));
        var serviceThatCannotRevealTheParentOffer = CreateServiceFailingRevealsOf(SourceTenantId);

        var result = await serviceThatCannotRevealTheParentOffer.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ChildSecret);
    }
}
