using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#5295 — a workload without a <c>HelmRepository</c> association resolves to the tenant's
///     repository whose <c>Purpose</c> matches the workload type, and only when that is unambiguous.
/// </summary>
/// <remarks>
///     <para>
///         Observed through the operator notification: the <c>WorkloadDeployedDto</c> carries the
///         resolved <c>RepositoryUrl</c>, so the assertion is on what the operator would actually pull
///         from, not on an internal call. The negative cases pin that the fallback never guesses —
///         two candidates, a repository without a purpose, or a repository of the other purpose all
///         end in the established "no Helm repository is linked" refusal with no operator call.
///     </para>
///     <para>
///         🔴 The explicit-edge case is here on purpose: the fallback must never override what an
///         operator linked by hand, even when a purpose-matching repository exists.
///     </para>
/// </remarks>
internal class HelmRepositoryFallbackTests : PoolServiceTestsBase
{
    private const string AdaptersUrl = "https://charts.test/adapters";
    private const string AppsUrl = "https://charts.test/apps";
    private const string ExplicitUrl = "https://charts.test/explicit";

    private static RtHelmRepositoryConfiguration Repo(string url, RtHelmRepositoryPurposeEnum? purpose,
        string name = "repo")
    {
        var repo = new RtHelmRepositoryConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
            RtWellKnownName = name,
            RepositoryUrl = url,
        };
        if (purpose is { } p)
        {
            repo.Purpose = p;
        }

        return repo;
    }

    private RtDeployableWorkload ArrangeCloudWorkload(bool application)
    {
        var deploymentSite = new RtDeploymentSite
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = "cloud-deploymentSite",
            Environment = RtEnvironmentEnum.Cloud
        };

        RtDeployableWorkload workload = application
            ? new RtApplication
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
                Name = "some-app"
            }
            : new RtAdapter
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
                Name = "mesh-adapter"
            };
        workload.ChartName = application ? "some-app" : "octo-mesh-adapter";
        workload.ChartVersion = "1.0.0";

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, workload.RtId).Returns(workload);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, workload.RtId).Returns(deploymentSite);
        // No explicit edge unless a test says otherwise.
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, workload.RtId)
            .Returns((RtHelmRepositoryConfiguration?)null);
        return workload;
    }

    private void GivenTenantRepositories(params RtHelmRepositoryConfiguration[] repos)
    {
        CommunicationRepository.GetHelmRepositoryConfigurationsAsync(TenantId).Returns(repos);
    }

    private async Task<string?> DeployAndReadRepositoryUrl(RtDeployableWorkload workload)
    {
        await DeploymentSiteService.DeployWorkloadAsync(TenantId, workload.RtId);
        var call = OperatorConnectionManager.ReceivedCalls()
            .FirstOrDefault(c => c.GetMethodInfo().Name == nameof(OperatorConnectionManager.NotifyWorkloadDeployedAsync));
        return (call?.GetArguments().FirstOrDefault() as WorkloadDeployedDto)?.RepositoryUrl;
    }

    // ---------------------------------------------------------------- precedence

    [Test]
    public async Task ExplicitEdge_WinsOverAPurposeMatch()
    {
        var adapter = ArrangeCloudWorkload(application: false);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(Repo(ExplicitUrl, RtHelmRepositoryPurposeEnum.Applications, "explicit"));
        GivenTenantRepositories(Repo(AdaptersUrl, RtHelmRepositoryPurposeEnum.Adapters, "public"));

        var url = await DeployAndReadRepositoryUrl(adapter);

        using var _ = Assert.Multiple();
        await Assert.That(url).IsEqualTo(ExplicitUrl);
        // The fallback was not even consulted.
        await CommunicationRepository.DidNotReceive().GetHelmRepositoryConfigurationsAsync(TenantId);
    }

    // ---------------------------------------------------------------- the fallback

    [Test]
    public async Task Adapter_WithoutEdge_ResolvesToTheAdaptersRepository()
    {
        var adapter = ArrangeCloudWorkload(application: false);
        GivenTenantRepositories(
            Repo(AppsUrl, RtHelmRepositoryPurposeEnum.Applications, "apps"),
            Repo(AdaptersUrl, RtHelmRepositoryPurposeEnum.Adapters, "public"));

        var url = await DeployAndReadRepositoryUrl(adapter);

        await Assert.That(url).IsEqualTo(AdaptersUrl);
    }

    [Test]
    public async Task Application_WithoutEdge_ResolvesToTheApplicationsRepository()
    {
        var app = ArrangeCloudWorkload(application: true);
        GivenTenantRepositories(
            Repo(AdaptersUrl, RtHelmRepositoryPurposeEnum.Adapters, "public"),
            Repo(AppsUrl, RtHelmRepositoryPurposeEnum.Applications, "apps"));

        var url = await DeployAndReadRepositoryUrl(app);

        await Assert.That(url).IsEqualTo(AppsUrl);
    }

    // ---------------------------------------------------------------- never guess

    [Test]
    public async Task TwoRepositoriesOfTheWantedPurpose_IsRefusedAsMissing()
    {
        var adapter = ArrangeCloudWorkload(application: false);
        GivenTenantRepositories(
            Repo(AdaptersUrl, RtHelmRepositoryPurposeEnum.Adapters, "public"),
            Repo("https://charts.test/mirror", RtHelmRepositoryPurposeEnum.Adapters, "mirror"));

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("no Helm repository is linked");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task RepositoryWithoutPurpose_IsNeverChosen()
    {
        // The pre-4.1.0 shape: a tenant seeded before the attribute existed. Its repositories keep
        // their exact previous behaviour — an unlinked workload still cannot deploy.
        var adapter = ArrangeCloudWorkload(application: false);
        GivenTenantRepositories(Repo(AdaptersUrl, purpose: null, "legacy"));

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("no Helm repository is linked");
    }

    [Test]
    public async Task OnlyTheOtherPurposeAvailable_IsRefusedAsMissing()
    {
        // An adapter must not silently pull from the apps index — that index does not carry it.
        var adapter = ArrangeCloudWorkload(application: false);
        GivenTenantRepositories(Repo(AppsUrl, RtHelmRepositoryPurposeEnum.Applications, "apps"));

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("no Helm repository is linked");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }
}
