using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Repository;

/// <summary>
///     A stored default carries its encrypted value for the unchanged check, but never prints it, and a removal
///     compares the snapshot it was read as.
/// </summary>
internal sealed class StoredSecretDefaultTests
{
    private readonly TestSecretEncryption _secretEncryption = TestSecretEncryption.Create();

    [Test]
    public async Task ToString_ContainsNeitherTheEnvelopeNorThePlaintext()
    {
        var protectedValue = _secretEncryption.Protector.Protect("zq7245-printed-sentinel");
        var storedDefault = new StoredSecretDefault(OctoObjectId.GenerateNewId(), "ec-captcha-secret", "parent-p",
            OctoObjectId.GenerateNewId().ToString()) { Value = protectedValue };

        var printed = storedDefault.ToString();

        using var assertionScope = Assert.Multiple();
        await Assert.That(printed).Contains("ec-captcha-secret");
        await Assert.That(printed).DoesNotContain(protectedValue.Envelope!);
        await Assert.That(printed).DoesNotContain("zq7245-printed-sentinel");
    }

    [Test]
    public async Task IsUnchanged_RefusesAnEntityRewrittenSinceTheRead()
    {
        var projectedAt = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        var currentDefault = new RtSecretDefault
        {
            RtId = OctoObjectId.GenerateNewId(),
            RtWellKnownName = "ec-captcha-secret",
            SourceTenantId = "parent-p",
            SourceWorkloadRtId = OctoObjectId.GenerateNewId().ToString(),
            LastProjectedAt = projectedAt
        };
        var readDefault = new StoredSecretDefault(currentDefault.RtId, "ec-captcha-secret", "parent-p",
            currentDefault.SourceWorkloadRtId) { LastProjectedAt = projectedAt };

        var unchangedBeforeTheRewrite = SecretDefaultIdentity.IsUnchanged(currentDefault, readDefault);
        currentDefault.LastProjectedAt = projectedAt.AddSeconds(1);
        var unchangedAfterTheRewrite = SecretDefaultIdentity.IsUnchanged(currentDefault, readDefault);

        using var assertionScope = Assert.Multiple();
        await Assert.That(unchangedBeforeTheRewrite).IsTrue();
        await Assert.That(unchangedAfterTheRewrite).IsFalse();
    }
}
