using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     The deploy of an Application with SecretDefaults: the declaration is checked before anything reaches
///     Helm, the defaults never become Helm values, and the projection into the served tenants is best effort.
/// </summary>
internal class DeployWorkloadSecretDefaultsTests : PoolServiceTestsBase
{
    private const string CaptchaKey = "ec-captcha-secret";
    private const string SentinelSecret = "zq7244-deploy-value-sentinel";

    private RtApplication GivenApplication(params RtSecretDefaultProjectionRecord[] secretDefaults)
    {
        var deploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud
        };
        var application = new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "energy-community-app",
            ChartName = "energy-community-app",
            ChartVersion = "1.0.0",
            SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>()
        };
        foreach (var secretDefault in secretDefaults)
        {
            application.SecretDefaults.Add(secretDefault);
        }

        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, application.RtId).Returns(application);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, application.RtId).Returns(deploymentSite);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, application.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts"
            });
        return application;
    }

    private RtSecretDefaultProjectionRecord Entry(string? key, string? plaintext)
    {
        return new RtSecretDefaultProjectionRecord
        {
            Key = key!,
            SecretValue = plaintext is null ? null : SecretEncryption.Protector.Protect(plaintext)
        };
    }

    private async Task<Exception> AssertRejectedAsync(RtApplication application, params string[] expectedFragments)
    {
        var rejection = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId));

        using var assertionScope = Assert.Multiple();
        foreach (var fragment in expectedFragments)
        {
            await Assert.That(rejection!.Message).Contains(fragment);
        }

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
        await SecretDefaultProvisioningService.DidNotReceiveWithAnyArgs()
            .ProvisionForSourceAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<CancellationToken>());
        return rejection!;
    }

    private WorkloadDeployedDto CapturedDto()
    {
        return (WorkloadDeployedDto)OperatorConnectionManager.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IOperatorConnectionManager.NotifyWorkloadDeployedAsync))
            .GetArguments()[0]!;
    }

    [Test]
    public async Task DuplicateKeys_AreRejectedNamingTheApplicationAndTheKey()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret), Entry(CaptchaKey, "zq7244-other"));

        await AssertRejectedAsync(application, "energy-community-app", $"'{CaptchaKey}'", "more than once");
    }

    [Test]
    public async Task AnEmptyKey_IsRejectedNamingTheApplication()
    {
        var application = GivenApplication(Entry("  ", SentinelSecret));

        await AssertRejectedAsync(application, "energy-community-app", "empty Key");
    }

    [Test]
    public async Task ANullKey_IsRejectedAsAnEmptyKey()
    {
        var application = GivenApplication(Entry(null, SentinelSecret));

        await AssertRejectedAsync(application, "energy-community-app", "empty Key");
    }

    [Test]
    public async Task AKeyWithInvalidCharacters_IsRejectedNamingTheKey()
    {
        var application = GivenApplication(Entry("ec captcha/secret", SentinelSecret));

        await AssertRejectedAsync(application, "energy-community-app", "'ec captcha/secret'", "invalid");
    }

    [Test]
    public async Task AMissingSecretValue_IsRejectedNamingTheKey()
    {
        var application = GivenApplication(Entry(CaptchaKey, null));

        await AssertRejectedAsync(application, "energy-community-app", $"'{CaptchaKey}'", "no SecretValue");
    }

    [Test]
    public async Task ARejection_IsRecordedOnTheApplication()
    {
        var application = GivenApplication(Entry(CaptchaKey, null));

        await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId));

        await CommunicationRepository.Received(1).SetApplicationDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(entityId => entityId.RtId == application.RtId), RtDeploymentStateEnum.Error,
            Arg.Is<string?>(message => message != null && message.Contains(CaptchaKey)));
    }

    [Test]
    public async Task ARejection_NeverCarriesThePlaintext()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret), Entry(CaptchaKey, SentinelSecret));

        var rejection = await AssertRejectedAsync(application, CaptchaKey);

        await Assert.That(rejection.ToString()).DoesNotContain(SentinelSecret[..12]);
    }

    [Test]
    public async Task TheHelmValues_ContainNothingDerivedFromTheSecretDefaults()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret));

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        var dto = CapturedDto();
        using var assertionScope = Assert.Multiple();
        await Assert.That(dto.Values.Any(value => value.Path.Contains(CaptchaKey, StringComparison.OrdinalIgnoreCase)))
            .IsFalse();
        await Assert.That(dto.Values.Any(value => (value.Value ?? string.Empty).Contains(SentinelSecret[..12],
            StringComparison.Ordinal))).IsFalse();
        await Assert.That((dto.ValuesYaml ?? string.Empty).Contains(SentinelSecret[..12], StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task AnApplicationWithSecretDefaults_IsProjectedIntoItsServedTenants()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret));

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        await SecretDefaultProvisioningService.Received(1)
            .ProvisionForSourceAsync(TenantId, application.RtId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnApplicationWithoutSecretDefaults_IsStillProjectedSoItsFormerDefaultsAreRemoved()
    {
        var application = GivenApplication();
        application.SecretDefaults = null;

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        await SecretDefaultProvisioningService.Received(1)
            .ProvisionForSourceAsync(TenantId, application.RtId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployingTheDeploymentSite_DeploysNoWorkloadAndProjectsNothing()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret), Entry(CaptchaKey, "zq7245-duplicate"));
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { application });

        await DeploymentSiteService.DeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        using var assertionScope = Assert.Multiple();
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
        await SecretDefaultProvisioningService.DidNotReceiveWithAnyArgs()
            .ProvisionForSourceAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task APendingApplicationRedispatchedOnSiteRegistration_IsValidatedLikeADeploy()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret), Entry(CaptchaKey, "zq7245-duplicate"));
        application.DeploymentState = RtDeploymentStateEnum.Pending;
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { application });

        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        using var assertionScope = Assert.Multiple();
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
        await SecretDefaultProvisioningService.DidNotReceiveWithAnyArgs()
            .ProvisionForSourceAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task APendingApplicationRedispatchedOnSiteRegistration_IsProjectedLikeADeploy()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret));
        application.DeploymentState = RtDeploymentStateEnum.Pending;
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { application });

        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        await SecretDefaultProvisioningService.Received(1)
            .ProvisionForSourceAsync(TenantId, application.RtId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFailingProjection_DoesNotFailTheDeployAndIsStoredAsAnErrorEvent()
    {
        var application = GivenApplication(Entry(CaptchaKey, SentinelSecret));
        SecretDefaultProvisioningService
            .ProvisionForSourceAsync(TenantId, application.RtId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("tree walk failed"));

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
        await CommunicationEventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(message => message.Contains("energy-community-app")), Arg.Any<RtEntityId?>());
    }
}
