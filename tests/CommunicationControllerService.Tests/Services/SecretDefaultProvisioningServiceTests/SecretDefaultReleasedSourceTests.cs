using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     The deploy is the release: only an Application in a deployed state is a source of SecretDefaults, and an
///     undeploy withdraws what it projected.
/// </summary>
internal sealed class SecretDefaultReleasedSourceTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string NewChildTenantId = "child-new";

    [Test]
    [Arguments(RtDeploymentStateEnum.Pending)]
    [Arguments(RtDeploymentStateEnum.Deployed)]
    public async Task ADeployedApplication_OffersItsDefaults(RtDeploymentStateEnum deploymentState)
    {
        ArrangeTenant(NewChildTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        application.DeploymentState = deploymentState;

        await Service.ProvisionForTargetAsync(NewChildTenantId);

        await Assert.That(StoredDefault(NewChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }

    [Test]
    [Arguments(RtDeploymentStateEnum.Undeployed)]
    [Arguments(RtDeploymentStateEnum.Error)]
    [Arguments(RtDeploymentStateEnum.Disabled)]
    public async Task AnApplicationNeverDeployedRefusedOrDisabled_OffersNothing(RtDeploymentStateEnum deploymentState)
    {
        ArrangeTenant(NewChildTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        application.DeploymentState = deploymentState;

        var result = await Service.ProvisionForTargetAsync(NewChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsWritten).IsEqualTo(0);
        await Assert.That(StoredDefault(NewChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task AnUndeployedApplication_LosesItsDefaultsOnTheNextReconcile()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.DeploymentState = RtDeploymentStateEnum.Undeployed;

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task AnApplicationRefusedAfterItsDeploy_LosesItsDefaultsOnTheNextReconcile()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.DeploymentState = RtDeploymentStateEnum.Error;

        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task WithdrawForSource_RemovesTheDefaultsFromTheWholeSubtree()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.DeploymentState = RtDeploymentStateEnum.Undeployed;

        var result = await Service.WithdrawForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(4);
        foreach (var tenantId in new[] { SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId })
        {
            await Assert.That(StoredDefault(tenantId, CaptchaKey)).IsNull();
        }

        await CommunicationEventService.Received(1).StoreInformationEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains("withdrawn")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task WithdrawForSource_HandsTheKeyToAnotherDeployedSource()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        await Service.ProvisionForSourceAsync(SourceTenantId, parentApplication.RtId);
        parentApplication.DeploymentState = RtDeploymentStateEnum.Undeployed;

        await Service.WithdrawForSourceAsync(SourceTenantId, parentApplication.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-child-value");
    }

    [Test]
    public async Task WithdrawForSource_KeepsTheDefaultsOfAnotherApplication()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        var otherApplication = ArrangeApplication(ProtectedEntry("other-app-secret", "zq7245-other-app-sentinel"));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        await Service.ProvisionForSourceAsync(SourceTenantId, otherApplication.RtId);
        application.DeploymentState = RtDeploymentStateEnum.Undeployed;

        await Service.WithdrawForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(GrandchildTenantId, "other-app-secret")).IsNotNull();
    }

    [Test]
    public async Task ASourceWithoutTheModel_KeepsItsDefaultsAndWarns()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        ArrangeSourceWithoutModel(SourceTenantId);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.SourcesUnreadable).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(application.RtId.ToString());
        await CommunicationEventService.Received(1).StoreWarningEventAsync(FirstChildTenantId,
            Arg.Is<string>(message => message.Contains($"'{SourceTenantId}'")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ASourceWithoutTheModelThatProjectedNothingHere_StoresNoWarning()
    {
        ArrangeSourceWithoutModel(SourceTenantId);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.SourcesUnreadable).IsEqualTo(1);
        await CommunicationEventService.DidNotReceiveWithAnyArgs()
            .StoreWarningEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }
}
