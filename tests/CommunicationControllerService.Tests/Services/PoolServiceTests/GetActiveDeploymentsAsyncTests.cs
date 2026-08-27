using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

/// <summary>
/// Pins the repository-based "what still owns operator resources" answer behind the
/// Communication disable guard (AB#4255): Deployed / Pending / Error count, Undeployed and
/// Disabled do not, pools come before workloads, and a read failure is never reported as
/// "nothing deployed".
/// </summary>
internal class GetActiveDeploymentsAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task ReturnsEmpty_WhenEveryPoolAndWorkloadIsResting()
    {
        GivenPools(
            Pool("edge", RtDeploymentStateEnum.Disabled, RtEnvironmentEnum.Edge),
            Pool("cloud", RtDeploymentStateEnum.Undeployed, RtEnvironmentEnum.Cloud));
        GivenWorkloads(
            Adapter("mesh", RtDeploymentStateEnum.Undeployed),
            Application("grafana", RtDeploymentStateEnum.Disabled));

        var result = await PoolService.GetActiveDeploymentsAsync(TenantId);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task ReturnsDeployedPendingAndError_PoolsFirst_ThenWorkloadsByName()
    {
        GivenPools(
            Pool("zeta", RtDeploymentStateEnum.Pending, RtEnvironmentEnum.Cloud),
            Pool("alpha", RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud),
            Pool("resting", RtDeploymentStateEnum.Undeployed, RtEnvironmentEnum.Cloud));
        GivenWorkloads(
            Application("grafana", RtDeploymentStateEnum.Error),
            Adapter("mesh", RtDeploymentStateEnum.Deployed),
            Adapter("idle", RtDeploymentStateEnum.Undeployed));

        var result = await PoolService.GetActiveDeploymentsAsync(TenantId);

        // Joined so the ORDER is pinned too (pools first, then workloads, each by name).
        await Assert.That(string.Join(" | ", result.Select(d => d.ToString()))).IsEqualTo(
            "Pool 'alpha' (Deployed) | Pool 'zeta' (Pending) | Application 'grafana' (Error) | Adapter 'mesh' (Deployed)");
    }

    [Test]
    public async Task ReportsALeftoverWorkloadUnderAnEdgePool()
    {
        // A Cloud pool switched to Edge while its adapter was deployed: the pool rests as
        // Disabled, but the adapter still owns a helm release until it is undeployed.
        GivenPools(Pool("edge", RtDeploymentStateEnum.Disabled, RtEnvironmentEnum.Edge));
        GivenWorkloads(Adapter("leftover", RtDeploymentStateEnum.Deployed));

        var result = await PoolService.GetActiveDeploymentsAsync(TenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Kind).IsEqualTo(ActiveDeployment.AdapterKind);
        await Assert.That(result[0].Name).IsEqualTo("leftover");
    }

    [Test]
    public async Task FallsBackToTheRuntimeId_WhenAnEntityHasNoName()
    {
        var pool = Pool(null, RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud);
        GivenPools(pool);
        GivenWorkloads();

        var result = await PoolService.GetActiveDeploymentsAsync(TenantId);

        await Assert.That(result[0].Name).IsEqualTo(pool.RtId.ToString());
    }

    [Test]
    public async Task PropagatesRepositoryFailures_InsteadOfAnsweringNothingDeployed()
    {
        GivenPools(Pool("alpha", RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud));
        CommunicationRepository.GetWorkloadsAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        await Assert.That(async () => await PoolService.GetActiveDeploymentsAsync(TenantId))
            .Throws<InvalidOperationException>();
    }

    private void GivenPools(params RtPool[] pools)
    {
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(pools);
    }

    private void GivenWorkloads(params RtDeployableWorkload[] workloads)
    {
        CommunicationRepository.GetWorkloadsAsync(TenantId).Returns(workloads);
    }

    private static RtPool Pool(string? name, RtDeploymentStateEnum state, RtEnvironmentEnum environment)
    {
        return new RtPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
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
