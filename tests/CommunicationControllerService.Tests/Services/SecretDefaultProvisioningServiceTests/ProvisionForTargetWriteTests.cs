using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     The write half of the target reconcile: an unchanged default is not written again, the winner is written
///     before the losers are removed, and a default without source fields is removed.
/// </summary>
internal sealed class ProvisionForTargetWriteTests : SecretDefaultProvisioningServiceTestsBase
{
    [Test]
    public async Task AnUnchangedReconcile_WritesNothing()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        var writesBefore = Writes.Count;

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Writes.Count).IsEqualTo(writesBefore);
        await Assert.That(result.DefaultsWritten).IsEqualTo(0);
        await Assert.That(result.DefaultsUnchanged).IsEqualTo(1);
    }

    [Test]
    public async Task ARotatedSourceValue_IsWritten()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { ProtectedEntry(CaptchaKey, "zq7245-rotated-value") };

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsUpdated).IsEqualTo(1);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-rotated-value");
    }

    [Test]
    public async Task AHolderChange_ReplacesTheDefaultInPlaceWithoutRemovingItFirst()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        parentApplication.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();

        var result = await Service.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Deletions).IsEmpty();
        await Assert.That(result.DefaultsUpdated).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.SourceTenantId)
            .IsEqualTo(FirstChildTenantId);
    }

    [Test]
    public async Task ADefaultWithoutSourceFields_IsRemoved()
    {
        ArrangeStoredDefault(FirstChildTenantId, CaptchaKey, SentinelSecret, sourceTenantId: null,
            sourceWorkloadRtId: null);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task AFailedWriteInTheApplicationsOwnTenant_IsLinkedToTheApplication()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeFailure(SourceTenantId, new InvalidOperationException("write refused"));

        await Service.ProvisionForTargetAsync(SourceTenantId);

        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(CaptchaKey)),
            Arg.Is<RtEntityId?>(link => link != null && link.Value.RtId == application.RtId &&
                                        link.Value.CkTypeId == SystemCommunicationCkIds.RtCkApplicationTypeId));
    }

    [Test]
    public async Task AFailedWriteInAnotherTenant_NamesTheApplicationInsteadOfLinkingIt()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeFailure(FirstChildTenantId, new InvalidOperationException("write refused"));

        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        await CommunicationEventService.Received(1).StoreErrorEventAsync(FirstChildTenantId,
            Arg.Is<string>(message => message.Contains("energy-community-app") && message.Contains(SourceTenantId)),
            Arg.Is<RtEntityId?>(link => link == null));
    }

    [Test]
    public async Task AFailedRemovalInTheApplicationsOwnTenant_IsLinkedToTheApplication()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(SourceTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        CommunicationRepository
            .DeleteSecretDefaultIfUnchangedAsync(SourceTenantId, Arg.Any<StoredSecretDefault>())
            .Returns<SecretDefaultDeleteOutcome>(_ => throw new InvalidOperationException("delete refused"));

        await Service.ProvisionForTargetAsync(SourceTenantId);

        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(CaptchaKey)),
            Arg.Is<RtEntityId?>(link => link != null && link.Value.RtId == application.RtId));
    }

    [Test]
    public async Task AFailedReplacement_KeepsTheWithdrawnDefaultInsteadOfLeavingAGap()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        parentApplication.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        ArrangeFailure(GrandchildTenantId, new InvalidOperationException("write refused"));

        var result = await Service.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(Deletions).IsEmpty();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-parent-value");
    }
}
