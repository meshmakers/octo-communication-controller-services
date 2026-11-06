using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     Two sources declaring the same key (the source closer to the root wins) and entries the service refuses
///     on its own, whatever the deploy guard let through.
/// </summary>
internal sealed class ProvisionPrecedenceAndValidationTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string ParentValue = "zq7244-parent-value";
    private const string ChildValue = "zq7244-child-value";

    [Test]
    public async Task AChildDeployingAKeyItsParentProjects_KeepsTheParentValueAndWarns()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, ParentValue));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        var childApplication = ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildValue));

        var result = await Service.ProvisionForSourceAsync(FirstChildTenantId, childApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceTenantId).IsEqualTo(SourceTenantId);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ParentValue);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ParentValue);
        await Assert.That(result.DefaultsWritten).IsEqualTo(0);
        await Assert.That(result.TargetsSkipped).IsEqualTo(2);
        await CommunicationEventService.Received(1).StoreWarningEventAsync(FirstChildTenantId,
            Arg.Is<string>(message => message.Contains($"tenant '{GrandchildTenantId}'") &&
                                      message.Contains(CaptchaKey) &&
                                      message.Contains($"'{SourceTenantId}'") &&
                                      message.Contains($"'{FirstChildTenantId}'")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AParentDeployingAKeyItsChildProjected_Overwrites()
    {
        var childApplication = ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildValue));
        await Service.ProvisionForSourceAsync(FirstChildTenantId, childApplication.RtId);
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, ParentValue));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsWritten).IsEqualTo(4);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceTenantId).IsEqualTo(SourceTenantId);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ParentValue);
    }

    [Test]
    public async Task TheParentRedeployingWithANewValue_StillRotatesUnderItsOwnDefaults()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, ParentValue));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        parentApplication.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { ProtectedEntry(CaptchaKey, "zq7244-parent-rotated") };

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsWritten).IsEqualTo(4);
        await Assert.That(result.TargetsSkipped).IsEqualTo(0);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7244-parent-rotated");
    }

    [Test]
    public async Task UnresolvableAncestors_StopTheProjectionBeforeAnyWrite()
    {
        TenantScopeResolver.ResolveAncestorTenantsAsync(FirstChildTenantId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("tenant tree unreadable"));
        var childApplication = ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, ChildValue));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Service.ProvisionForSourceAsync(FirstChildTenantId, childApplication.RtId));

        await Assert.That(Writes).IsEmpty();
    }

    [Test]
    public async Task AnEntryWithoutAKey_IsSkippedWithAnErrorAndTheValidEntryIsWritten()
    {
        var withoutKey = ProtectedEntry("placeholder", SentinelSecret);
        withoutKey.Key = null!;
        var application = ArrangeApplication(withoutKey, ProtectedEntry(CaptchaKey, ParentValue));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey).Count).IsEqualTo(4);
        await Assert.That(Writes.All(write => write.Key == CaptchaKey)).IsTrue();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains("without a key")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AMalformedKey_IsSkippedWithAnError()
    {
        var application = ArrangeApplication(ProtectedEntry("ec captcha/secret", SentinelSecret),
            ProtectedEntry(CaptchaKey, ParentValue));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Writes.All(write => write.Key == CaptchaKey)).IsTrue();
        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains("'ec captcha/secret'") && message.Contains("invalid")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ADuplicatedKey_SkipsEveryEntryOfThatKeyAndWritesTheOthers()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret),
            ProtectedEntry(CaptchaKey, ParentValue), ProtectedEntry("ec-maps-key", ChildValue));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey)).IsEmpty();
        await Assert.That(TenantsWritten("ec-maps-key").Count).IsEqualTo(4);
        await Assert.That(result.TargetsFailed).IsEqualTo(2);
        await CommunicationEventService.Received(2).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(CaptchaKey) && message.Contains("more than once")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AMissingValue_IsSkippedWithAnError()
    {
        var withoutValue = ProtectedEntry(CaptchaKey, ParentValue);
        withoutValue.SecretValue = null;
        var application = ArrangeApplication(withoutValue, ProtectedEntry("ec-maps-key", ChildValue));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey)).IsEmpty();
        await Assert.That(TenantsWritten("ec-maps-key").Count).IsEqualTo(4);
        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains($"'{CaptchaKey}'") && message.Contains("no SecretValue")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task OnlyInvalidEntries_WriteNothingAndDoNotWalkTheTree()
    {
        var application = ArrangeApplication(ProtectedEntry(" ", SentinelSecret));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Writes).IsEmpty();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await TenantScopeResolver.DidNotReceive().ResolveLendableTenantsAsync(Arg.Any<string>(),
            Arg.Any<LendingScope>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefusalEvents_NeverCarryTheValue()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret),
            ProtectedEntry(CaptchaKey, SentinelSecret), ProtectedEntry("bad key", SentinelSecret));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await Assert.That(StoredEventMessages().Any(message =>
            message.Contains(SentinelSecret[..12], StringComparison.Ordinal))).IsFalse();
    }
}
