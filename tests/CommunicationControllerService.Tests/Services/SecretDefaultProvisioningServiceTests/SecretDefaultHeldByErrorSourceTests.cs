using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     An Application in Error keeps the defaults its running release projected: declarations edited after that
///     release are not deployed, so a reconcile neither removes a key the edit dropped nor writes a value the edit
///     changed, until a deploy succeeds or the Application is withdrawn.
/// </summary>
internal sealed class SecretDefaultHeldByErrorSourceTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string EditedSecret = "zq7245-edited-value-not-deployed";

    private const string RefusalText = "[parent-p] Cannot deploy workload 'energy-community-app': MemoryLimit 'lots' " +
                                       "is not a valid Kubernetes quantity.";

    private static readonly string[] ServedTenantIds =
        [SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId];

    [Test]
    public async Task ARefusedRedeployThatDropsAKey_KeepsTheDeployedDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        RefuseRedeploy(application);

        var results = await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(results.Sum(result => result.DefaultsRemoved)).IsEqualTo(0);
        foreach (var tenantId in ServedTenantIds)
        {
            await Assert.That(StoredDefault(tenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        }
    }

    [Test]
    public async Task ARefusedRedeployThatChangesAValue_KeepsTheDeployedValue()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        ReplaceDeclarations(application, ProtectedEntry(CaptchaKey, EditedSecret));
        RefuseRedeploy(application);
        Writes.Clear();

        var results = await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(results.Sum(result => result.DefaultsWritten)).IsEqualTo(0);
        await Assert.That(Writes).IsEmpty();
        foreach (var tenantId in ServedTenantIds)
        {
            await Assert.That(StoredDefault(tenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        }
    }

    [Test]
    public async Task ARefusedRedeployThatAddsAKey_OffersNothingForIt()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        ReplaceDeclarations(application, ProtectedEntry(CaptchaKey, SentinelSecret),
            ProtectedEntry("added-after-release", EditedSecret));
        RefuseRedeploy(application);

        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        await Assert.That(StoredDefault(FirstChildTenantId, "added-after-release")).IsNull();
    }

    [Test]
    public async Task ASuccessfulDeployAfterTheRefusal_WritesTheEditedValue()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        ReplaceDeclarations(application, ProtectedEntry(CaptchaKey, EditedSecret));
        RefuseRedeploy(application);
        await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);
        application.DeploymentState = RtDeploymentStateEnum.Deployed;
        application.LastDeploymentError = null;

        await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        foreach (var tenantId in ServedTenantIds)
        {
            await Assert.That(StoredDefault(tenantId, CaptchaKey)!.RevealedValue).IsEqualTo(EditedSecret);
        }
    }

    [Test]
    public async Task ADeployedApplicationOfTheSameTenant_DoesNotReplaceTheHeldDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);
        RefuseRedeploy(application);
        ArrangeApplication(ProtectedEntry(CaptchaKey, EditedSecret));

        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(application.RtId.ToString());
    }

    [Test]
    public async Task ADeployedApplicationCloserToTheRoot_StillTakesTheKeyOver()
    {
        var childApplication = ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        RefuseRedeploy(childApplication);
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, EditedSecret));

        await Service.ProvisionForTargetAsync(GrandchildTenantId);

        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(parentApplication.RtId.ToString());
    }

    private static void RefuseRedeploy(RtApplication application)
    {
        application.DeploymentState = RtDeploymentStateEnum.Error;
        application.LastDeploymentError = RefusalText;
    }

    private static void ReplaceDeclarations(RtApplication application,
        params RtSecretDefaultProjectionRecord[] declarations)
    {
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        foreach (var declaration in declarations)
        {
            application.SecretDefaults.Add(declaration);
        }
    }
}
