using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#5537 (System.Communication 4.8.0): <c>ServiceAccountConfiguration.ClientSecret</c> is a Secret
///     attribute, so the raw value the lease path reads is an <c>RtSecretValue</c>, not a string. The
///     lane's <c>ResolveBorrowerCredentialAsync</c> read it with <c>as string</c> — after the switch that
///     is null for every migrated account, and every lease would be refused as "no usable pipeline
///     service account". These pin that the borrower's secret goes through the controller's reveal
///     point in every storage form a 4.x tenant can hold, and that an unreadable one refuses the lease
///     instead of throwing out of the hub method.
/// </summary>
internal class LeaseBorrowerSecretRevealTests : LeaseServiceTestsBase
{
    private void ArrangeEverythingButTheCredential()
    {
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ArrangeConnectedMember();
    }

    [Test]
    public async Task AProtectedSecret_ReachesTheMemberAsPlaintext()
    {
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw(SecretEncryption.Protector.Protect("protected-borrower-secret"));

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(CapturePushedLease().ClientSecret).IsEqualTo("protected-borrower-secret");
    }

    [Test]
    public async Task TheReveal_IsTaggedWithTheBorrowerTenantAndTheAttribute()
    {
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw(SecretEncryption.Protector.Protect("protected-borrower-secret"));

        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        EncryptionService.Received(1).RevealOrNull(Arg.Any<object?>(),
            Arg.Is<SecretAccessContext?>(c => c != null && c.TenantId == BorrowerTenantId &&
                                              c.AttributeName == "ClientSecret"));
    }

    [Test]
    public async Task ALegacyPlaintextString_StillGrants()
    {
        // A pre-4.8.0 String value the encrypt sweep has not reached yet.
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw("legacy-plaintext-secret");

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(CapturePushedLease().ClientSecret).IsEqualTo("legacy-plaintext-secret");
    }

    [Test]
    public async Task ALegacyPlaintextSecretValue_StillGrants()
    {
        // The same legacy value as the engine surfaces it inside a Secret slot.
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw(RtSecretValue.LegacyPlaintext("legacy-plaintext-secret"));

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();
        await Assert.That(CapturePushedLease().ClientSecret).IsEqualTo("legacy-plaintext-secret");
    }

    [Test]
    public async Task ASecretUnderAnUnknownKeyId_IsRefusedAsMissing()
    {
        // Protected in another environment (key id "foreign"): unreadable here, reads as not set.
        var foreign = TestSecretEncryption.Create(keyId: "foreign");
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw(foreign.Protector.Protect("foreign-secret"));

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("no usable pipeline service account");
        await Assert.That(result.StatusMessage!.Contains("foreign-secret", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ARevealThatThrows_RefusesTheLeaseInsteadOfThrowing()
    {
        // e.g. a host without any key ring: the reveal throws; the hub method must not.
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw(SecretEncryption.Protector.Protect("protected-borrower-secret"));
        EncryptionService.RevealOrNull(Arg.Any<object?>(), Arg.Any<SecretAccessContext?>())
            .Throws(new InvalidOperationException("no key ring"));

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains("no usable pipeline service account");
    }

    [Test]
    public async Task ANotSetSecret_IsRefused()
    {
        ArrangeEverythingButTheCredential();
        ArrangeBorrowerCredentialRaw(null);

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        await Assert.That(result.Granted).IsFalse();
    }
}
