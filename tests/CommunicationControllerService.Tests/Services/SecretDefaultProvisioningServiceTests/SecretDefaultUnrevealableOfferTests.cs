using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     An offer whose value cannot be revealed because the reveal throws (a host without key ring, strict mode against
///     legacy clear text) is an unreadable value: the key keeps its old default instead of losing it.
/// </summary>
internal sealed class SecretDefaultUnrevealableOfferTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string ChildSecret = "zq7245-child-value-unrevealable";

    [Test]
    public async Task AWithdrawalWhoseReplacementCannotBeRevealed_KeepsTheWithdrawnDefault()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        parentApplication.DeploymentState = RtDeploymentStateEnum.Undeployed;
        var serviceThatCannotRevealTheChildOffer = CreateServiceFailingRevealsOf(FirstChildTenantId);

        var result = await serviceThatCannotRevealTheChildOffer.WithdrawForSourceAsync(SourceTenantId,
            parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)).IsNull();
        await Assert.That(result.TargetsFailed).IsGreaterThan(0);
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

    private SecretDefaultProvisioningService CreateServiceFailingRevealsOf(string sourceTenantIdWithoutKeyRing)
    {
        var realEncryptionService = SecretEncryption.EncryptionService;
        var failingEncryptionService = Substitute.For<IWorkloadEncryptionService>();
        failingEncryptionService
            .RevealOrNull(Arg.Any<object?>(), Arg.Any<SecretAccessContext?>())
            .Returns(call =>
            {
                var storedValue = call.ArgAt<object?>(0);
                var accessContext = call.ArgAt<SecretAccessContext?>(1);
                if (string.Equals(accessContext?.TenantId, sourceTenantIdWithoutKeyRing,
                        StringComparison.OrdinalIgnoreCase) &&
                    accessContext?.CkTypeId != SecretDefaultCkTypeId)
                {
                    throw new SecretEncryptionNotConfiguredException("No key ring in this host.");
                }

                return realEncryptionService.RevealOrNull(storedValue, accessContext);
            });
        return new SecretDefaultProvisioningService(CommunicationRepository, TenantScopeResolver,
            failingEncryptionService, CommunicationEventService, Clock);
    }

    private static string SecretDefaultCkTypeId => SystemCommunicationCkIds.RtCkSecretDefaultTypeId.ToString();
}
