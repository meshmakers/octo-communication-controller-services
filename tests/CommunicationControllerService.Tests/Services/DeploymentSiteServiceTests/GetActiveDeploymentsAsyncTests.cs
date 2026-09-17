using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
/// Pins the repository-based "what still owns operator resources" answer behind the
/// Communication disable guard (AB#4255): Deployed / Pending / Error count, Undeployed and
/// Disabled do not, deploymentSites come before workloads, and a read failure is never reported as
/// "nothing deployed".
/// </summary>
internal class GetActiveDeploymentsAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task ReturnsEmpty_WhenEveryPoolAndWorkloadIsResting()
    {
        GivenPools(
            DeploymentSite("edge", RtDeploymentStateEnum.Disabled, RtEnvironmentEnum.Edge),
            DeploymentSite("cloud", RtDeploymentStateEnum.Undeployed, RtEnvironmentEnum.Cloud));
        GivenWorkloads(
            Adapter("mesh", RtDeploymentStateEnum.Undeployed),
            Application("grafana", RtDeploymentStateEnum.Disabled));

        var result = await DeploymentSiteService.GetActiveDeploymentsAsync(TenantId);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task ReturnsDeployedPendingAndError_PoolsFirst_ThenWorkloadsByName()
    {
        GivenPools(
            DeploymentSite("zeta", RtDeploymentStateEnum.Pending, RtEnvironmentEnum.Cloud),
            DeploymentSite("alpha", RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud),
            DeploymentSite("resting", RtDeploymentStateEnum.Undeployed, RtEnvironmentEnum.Cloud));
        GivenWorkloads(
            Application("grafana", RtDeploymentStateEnum.Error),
            Adapter("mesh", RtDeploymentStateEnum.Deployed),
            Adapter("idle", RtDeploymentStateEnum.Undeployed));

        var result = await DeploymentSiteService.GetActiveDeploymentsAsync(TenantId);

        // Joined so the ORDER is pinned too (deploymentSites first, then workloads, each by name).
        await Assert.That(string.Join(" | ", result.Select(d => d.ToString()))).IsEqualTo(
            "DeploymentSite 'alpha' (Deployed) | DeploymentSite 'zeta' (Pending) | Application 'grafana' (Error) | Adapter 'mesh' (Deployed)");
    }

    [Test]
    public async Task ReportsALeftoverWorkloadUnderAnEdgePool()
    {
        // A Cloud deploymentSite switched to Edge while its adapter was deployed: the deploymentSite rests as
        // Disabled, but the adapter still owns a helm release until it is undeployed.
        GivenPools(DeploymentSite("edge", RtDeploymentStateEnum.Disabled, RtEnvironmentEnum.Edge));
        GivenWorkloads(Adapter("leftover", RtDeploymentStateEnum.Deployed));

        var result = await DeploymentSiteService.GetActiveDeploymentsAsync(TenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Kind).IsEqualTo(ActiveDeployment.AdapterKind);
        await Assert.That(result[0].Name).IsEqualTo("leftover");
    }

    [Test]
    public async Task FallsBackToTheRuntimeId_WhenAnEntityHasNoName()
    {
        var deploymentSite = DeploymentSite(null, RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud);
        GivenPools(deploymentSite);
        GivenWorkloads();

        var result = await DeploymentSiteService.GetActiveDeploymentsAsync(TenantId);

        await Assert.That(result[0].Name).IsEqualTo(deploymentSite.RtId.ToString());
    }

    [Test]
    public async Task PropagatesRepositoryFailures_InsteadOfAnsweringNothingDeployed()
    {
        GivenPools(DeploymentSite("alpha", RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud));
        CommunicationRepository.GetWorkloadsAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        await Assert.That(async () => await DeploymentSiteService.GetActiveDeploymentsAsync(TenantId))
            .Throws<InvalidOperationException>();
    }

    private void GivenPools(params RtDeploymentSite[] deploymentSites)
    {
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(deploymentSites);
    }

    private void GivenWorkloads(params RtDeployableWorkload[] workloads)
    {
        CommunicationRepository.GetWorkloadsAsync(TenantId).Returns(workloads);
    }

    private static RtDeploymentSite DeploymentSite(string? name, RtDeploymentStateEnum state, RtEnvironmentEnum environment)
    {
        return new RtDeploymentSite
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = name,
            DeploymentState = state,
            Environment = environment
        };
    }

    private static RtAdapter Adapter(string name, RtDeploymentStateEnum state)
    {
        return new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = name,
            DeploymentState = state
        };
    }

    private static RtApplication Application(string name, RtDeploymentStateEnum state)
    {
        return new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = name,
            DeploymentState = state
        };
    }
}
