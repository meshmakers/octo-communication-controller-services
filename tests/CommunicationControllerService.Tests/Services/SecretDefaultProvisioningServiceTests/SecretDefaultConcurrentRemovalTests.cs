using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     A removal only erases the default it read: a deploy that writes the same default between the read and the
///     removal wins, on the target reconcile and on the source sweep alike.
/// </summary>
internal sealed class SecretDefaultConcurrentRemovalTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string ConcurrentValue = "zq7245-concurrent-deploy-value";

    private void RewriteBeforeTheRemoval(string rewrittenTenantId, string sourceTenantId, string sourceWorkloadRtId)
    {
        BeforeConditionalDelete = (tenantId, expectedDefault) =>
        {
            if (!string.Equals(tenantId, rewrittenTenantId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Clock.UtcNow = Clock.UtcNow.AddSeconds(1);
            ArrangeStoredDefault(tenantId, expectedDefault.Key, ConcurrentValue, sourceTenantId, sourceWorkloadRtId);
        };
    }

    [Test]
    public async Task TheTargetReconcile_KeepsADefaultTheSameApplicationRewroteMeanwhile()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        RewriteBeforeTheRemoval(FirstChildTenantId, SourceTenantId, application.RtId.ToString());

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(result.WritesFailed).IsEqualTo(0);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ConcurrentValue);
    }

    [Test]
    public async Task TheTargetReconcile_KeepsADefaultAnotherSourceTookOverMeanwhile()
    {
        ArrangeStoredDefault(FirstChildTenantId, CaptchaKey, SentinelSecret, SiblingTenantId,
            NewRtId().ToString());
        var takingOverWorkloadRtId = NewRtId().ToString();
        RewriteBeforeTheRemoval(FirstChildTenantId, SourceTenantId, takingOverWorkloadRtId);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(Deletions).IsEmpty();
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(takingOverWorkloadRtId);
    }

    [Test]
    public async Task TheSourceSweep_KeepsADefaultRewrittenMeanwhileAndRemovesTheOthers()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        RewriteBeforeTheRemoval(GrandchildTenantId, SourceTenantId, application.RtId.ToString());

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(3);
        await Assert.That(result.TargetsFailed).IsEqualTo(0);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(ConcurrentValue);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ADefaultRemovedMeanwhile_IsNeitherCountedNorAFailure()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        CommunicationRepository
            .DeleteSecretDefaultIfUnchangedAsync(FirstChildTenantId, Arg.Any<StoredSecretDefault>())
            .Returns(SecretDefaultDeleteOutcome.NotFound);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(result.WritesFailed).IsEqualTo(0);
        await CommunicationEventService.DidNotReceiveWithAnyArgs()
            .StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AFailedRemovalWhoseEventCannotBeStored_DoesNotStopTheSweep()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        CommunicationRepository
            .DeleteSecretDefaultIfUnchangedAsync(FirstChildTenantId, Arg.Any<StoredSecretDefault>())
            .Returns<SecretDefaultDeleteOutcome>(_ => throw new InvalidOperationException("tenant database down"));
        CommunicationEventService
            .StoreErrorEventAsync(FirstChildTenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>())
            .Returns<Task>(_ => throw new InvalidOperationException("tenant database down"));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(3);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
    }
}
