using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     Every target reconcile that writes or removes a default stores one Information event with counts in the
///     reconciled tenant, whether it ran on tenant load or through the reconcile endpoint; a withdrawal's hand-over
///     reports only in the source tenant.
/// </summary>
internal sealed class SecretDefaultTargetEventTests : SecretDefaultProvisioningServiceTestsBase
{
    [Test]
    public async Task TheEndpointReconcile_StoresAnInformationEventWithCountsInEveryChangedTenant()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(SecondChildTenantId, "orphaned-key", "zq7245-orphan-value", SourceTenantId,
            NewRtId().ToString());

        await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        using var assertionScope = Assert.Multiple();
        foreach (var tenantId in new[] { SourceTenantId, FirstChildTenantId, GrandchildTenantId })
        {
            await CommunicationEventService.Received(1).StoreInformationEventAsync(tenantId,
                "Secret defaults reconciled: 1 created, 0 updated, 0 removed, 0 kept.", Arg.Any<RtEntityId?>());
        }

        await CommunicationEventService.Received(1).StoreInformationEventAsync(SecondChildTenantId,
            "Secret defaults reconciled: 1 created, 0 updated, 1 removed, 0 kept.", Arg.Any<RtEntityId?>());
        await Assert.That(StoredEventMessages().Any(message => message.Contains(SentinelSecret))).IsFalse();
        await Assert.That(StoredEventMessages().Any(message => message.Contains("zq7245-orphan-value"))).IsFalse();
    }

    [Test]
    public async Task AReconcileThatChangesNothing_StoresNoInformationEvent()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        CommunicationEventService.ClearReceivedCalls();

        await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        await CommunicationEventService.DidNotReceiveWithAnyArgs()
            .StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ATenantLoadReconcile_StoresTheEventOnce()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForTargetAsync(GrandchildTenantId);

        await CommunicationEventService.Received(1).StoreInformationEventAsync(GrandchildTenantId,
            "Secret defaults reconciled: 1 created, 0 updated, 0 removed, 0 kept.", Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AFailingEventStore_DoesNotFailTheReconcile()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        CommunicationEventService.StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<RtEntityId?>())
            .ThrowsAsync(new InvalidOperationException("event store unreachable"));

        var result = await Service.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.Reconciled);
        await Assert.That(result.DefaultsCreated).IsEqualTo(1);
    }

    [Test]
    public async Task AWithdrawalHandOver_ReportsOnlyInTheSourceTenant()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.DeploymentState = RtDeploymentStateEnum.Undeployed;
        CommunicationEventService.ClearReceivedCalls();

        await Service.WithdrawForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await CommunicationEventService.Received(1).StoreInformationEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains("withdrawn")), Arg.Any<RtEntityId?>());
        await CommunicationEventService.DidNotReceive().StoreInformationEventAsync(Arg.Any<string>(),
            Arg.Is<string>(message => message.StartsWith("Secret defaults reconciled")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ATamperedSource_IsRestoredWithoutAnAttemptToRemoveIt()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(FirstChildTenantId, CaptchaKey, "zq7245-tampered-value", SourceTenantId,
            NewRtId().ToString());

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsUpdated).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(application.RtId.ToString());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .DeleteSecretDefaultIfUnchangedAsync(Arg.Any<string>(), Arg.Any<StoredSecretDefault>());
    }
}
