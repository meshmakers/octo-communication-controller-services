using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     The Error event of a failed key on deploy is best effort: when the event store throws, the other declarations
///     are still projected and the sweep still removes withdrawn defaults.
/// </summary>
internal sealed class SecretDefaultDeployEventFailureTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string MailKey = "ec-mail-secret";
    private const string MailSecret = "zq7245-mail-value";
    private const string WithdrawnKey = "ec-withdrawn-secret";
    private const string WithdrawnSecret = "zq7245-withdrawn-value";
    private const string UnreadableSecret = "zq7245-value-reads-empty";

    private static readonly string[] ServedTenantIds =
        [SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId];

    [Test]
    public async Task AnInvalidDeclarationWhoseEventCannotBeStored_StillProjectsTheOthersAndSweeps()
    {
        var application = await ArrangeDeployedWithWithdrawnKeyAsync();
        var entryWithoutValue = ProtectedEntry(CaptchaKey, SentinelSecret);
        entryWithoutValue.SecretValue = null;
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { entryWithoutValue, ProtectedEntry(MailKey, MailSecret) };
        ArrangeFailingEventStore();

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await AssertOthersProjectedAndWithdrawnKeySwept(result);
    }

    [Test]
    public async Task AnEmptyValueWhoseEventCannotBeStored_StillProjectsTheOthersAndSweeps()
    {
        var application = await ArrangeDeployedWithWithdrawnKeyAsync();
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { ProtectedEntry(CaptchaKey, UnreadableSecret), ProtectedEntry(MailKey, MailSecret) };
        ArrangeFailingEventStore();
        var serviceReadingOneValueEmpty = CreateServiceReadingEmpty(UnreadableSecret);

        var result = await serviceReadingOneValueEmpty.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await AssertOthersProjectedAndWithdrawnKeySwept(result);
    }

    [Test]
    public async Task AFailedWriteWhoseEventCannotBeStored_StillProjectsTheOtherTenantsAndSweeps()
    {
        var application = await ArrangeDeployedWithWithdrawnKeyAsync();
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { ProtectedEntry(MailKey, MailSecret) };
        ArrangeFailure(SecondChildTenantId, new InvalidOperationException("write refused"));
        ArrangeFailingEventStore();
        var writesBefore = Writes.Count;

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await Assert.That(Writes.Skip(writesBefore).Select(write => write.TargetTenantId))
            .IsEquivalentTo(new[] { SourceTenantId, FirstChildTenantId, GrandchildTenantId });
        foreach (var servedTenantId in ServedTenantIds.Where(tenantId => tenantId != SecondChildTenantId))
        {
            await Assert.That(StoredDefault(servedTenantId, WithdrawnKey)).IsNull();
        }
    }

    private async Task<RtApplication> ArrangeDeployedWithWithdrawnKeyAsync()
    {
        var application = ArrangeApplication(ProtectedEntry(WithdrawnKey, WithdrawnSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        return application;
    }

    private void ArrangeFailingEventStore()
    {
        CommunicationEventService
            .StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>())
            .ThrowsAsync(new InvalidOperationException("event store unreachable"));
    }

    private async Task AssertOthersProjectedAndWithdrawnKeySwept(SecretDefaultProvisioningResult result)
    {
        using var assertionScope = Assert.Multiple();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(4);
        await Assert.That(TenantsWritten(MailKey)).Count().IsEqualTo(4);
        await Assert.That(TenantsWritten(CaptchaKey)).IsEmpty();
        foreach (var servedTenantId in ServedTenantIds)
        {
            await Assert.That(StoredDefault(servedTenantId, WithdrawnKey)).IsNull();
        }
    }

    private SecretDefaultProvisioningService CreateServiceReadingEmpty(string plaintextReadAsEmpty)
    {
        var realEncryptionService = SecretEncryption.EncryptionService;
        var emptyReadingEncryptionService = Substitute.For<IWorkloadEncryptionService>();
        emptyReadingEncryptionService
            .RevealOrNull(Arg.Any<object?>(), Arg.Any<SecretAccessContext?>())
            .Returns(call =>
            {
                var revealedValue = realEncryptionService.RevealOrNull(call.ArgAt<object?>(0),
                    call.ArgAt<SecretAccessContext?>(1));
                return revealedValue == plaintextReadAsEmpty ? string.Empty : revealedValue;
            });
        return new SecretDefaultProvisioningService(CommunicationRepository, TenantScopeResolver,
            emptyReadingEncryptionService, CommunicationEventService, Clock);
    }
}
