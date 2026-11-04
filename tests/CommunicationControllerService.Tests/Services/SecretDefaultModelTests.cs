using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     Pins the AB#7243 shape of the System.Communication model: Applications declare SecretDefaults,
///     the platform-owned SecretDefault entity receives them, and the entity is not a Configuration.
/// </summary>
internal class SecretDefaultModelTests
{
    [Test]
    public async Task SecretDefault_IsNotAConfiguration()
    {
        await Assert.That(typeof(RtConfiguration).IsAssignableFrom(typeof(RtSecretDefault))).IsFalse();
    }

    [Test]
    public async Task SecretDefault_CarriesNameSourceAndSecretValue()
    {
        var secretDefault = new RtSecretDefault
        {
            Name = "ec-captcha-secret",
            SourceTenantId = "ec-apphost",
            SourceWorkloadRtId = "ec0000000000000000000f01",
            Value = RtSecretValue.Pending("plain")
        };

        using var _ = Assert.Multiple();
        await Assert.That(secretDefault.Name).IsEqualTo("ec-captcha-secret");
        await Assert.That(secretDefault.SourceTenantId).IsEqualTo("ec-apphost");
        await Assert.That(secretDefault.SourceWorkloadRtId).IsEqualTo("ec0000000000000000000f01");
        await Assert.That(secretDefault.Value).IsNotNull();
        await Assert.That(secretDefault.LastProjectedAt).IsNull();
    }

    [Test]
    public async Task Application_SecretDefaultsIsOptionalAndHoldsProjectionRecords()
    {
        var application = new RtApplication();
        await Assert.That(application.SecretDefaults).IsNull();

        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
        {
            new RtSecretDefaultProjectionRecord
            {
                Key = "ec-captcha-secret",
                SecretValue = RtSecretValue.Pending("plain"),
                AllowedTenantIds = new AttributeStringValueList { "tenant-a" },
                Description = "Turnstile secret"
            }
        };

        var entry = application.SecretDefaults!.Single();
        using var _ = Assert.Multiple();
        await Assert.That(entry.Key).IsEqualTo("ec-captcha-secret");
        await Assert.That(entry.AllowedTenantIds!.Single()).IsEqualTo("tenant-a");
        await Assert.That(entry.SecretValue).IsNotNull();
    }
}
