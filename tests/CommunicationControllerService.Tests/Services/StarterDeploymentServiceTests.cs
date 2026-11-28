using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#8452 — the default site and adapter of a fresh starter tenant are deployed by the platform; anything that is
///     not the shape of a fresh tenant is left alone, and nothing is ever undeployed.
/// </summary>
internal class StarterDeploymentServiceTests
{
    private const string TenantId = "starter-a1";
    private const string BlueprintId = "System.Ai.Starter-1.2.0";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly IDeploymentSiteService _sites = Substitute.For<IDeploymentSiteService>();
    private readonly ICommunicationEventService _events = Substitute.For<ICommunicationEventService>();

    private StarterDeploymentService CreateSut() =>
        new(_repository, _sites, _events, NullLogger<StarterDeploymentService>.Instance);

    private static RtDeploymentSite Site(RtEnvironmentEnum environment = RtEnvironmentEnum.Cloud,
        RtDeploymentStateEnum state = RtDeploymentStateEnum.Undeployed) => new()
    {
        RtId = new OctoObjectId("670000000000000000000001"),
        CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
        Name = "Default Cloud",
        Environment = environment,
        DeploymentState = state
    };

    private static RtAdapter Adapter(RtDeploymentStateEnum state = RtDeploymentStateEnum.Undeployed,
        string id = "670000000000000000000002") => new()
    {
        RtId = new OctoObjectId(id),
        CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
        Name = "Mesh Adapter",
        DeploymentState = state
    };

    private void Tenant(RtDeploymentSite[] sites, RtDeployableWorkload[] workloads)
    {
        _repository.GetDeploymentSitesAsync(TenantId).Returns(sites);
        if (sites.Length > 0)
        {
            _repository.GetWorkloadsForDeploymentSiteAsync(TenantId, sites[0].RtId).Returns(workloads);
        }
    }

    private async Task AssertNothingDeployedOrUndeployed()
    {
        await _sites.DidNotReceiveWithAnyArgs().DeployDeploymentSiteAsync(default!, default);
        await _sites.DidNotReceiveWithAnyArgs().DeployWorkloadAsync(default!, default);
        await AssertNothingUndeployed();
    }

    private async Task AssertNothingUndeployed()
    {
        await _sites.DidNotReceiveWithAnyArgs().UndeployDeploymentSiteAsync(default!, default);
        await _sites.DidNotReceiveWithAnyArgs().UndeployWorkloadAsync(default!, default);
        await _sites.DidNotReceiveWithAnyArgs().UndeployAllCloudDeploymentSitesAsync(default!);
    }

    [Test]
    public async Task FreshTenant_DeploysSiteThenAdapter_AndWritesOneEvent()
    {
        var site = Site();
        var adapter = Adapter();
        Tenant([site], [adapter]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.Deployed);
        Received.InOrder(() =>
        {
            _sites.DeployDeploymentSiteAsync(TenantId, site.RtId);
            _sites.DeployWorkloadAsync(TenantId, adapter.RtId);
        });
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains(BlueprintId) && m.Contains("Mesh Adapter")));
        await AssertNothingUndeployed();
    }

    [Test]
    public async Task BothDeployedAlready_ChangesNothing()
    {
        Tenant([Site(state: RtDeploymentStateEnum.Deployed)], [Adapter(RtDeploymentStateEnum.Deployed)]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.NothingToDeploy);
        await AssertNothingDeployedOrUndeployed();
        await _events.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(default!, default!);
    }

    [Test]
    public async Task SiteDeployed_AdapterPending_ChangesNothing()
    {
        Tenant([Site(state: RtDeploymentStateEnum.Deployed)], [Adapter(RtDeploymentStateEnum.Pending)]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.NothingToDeploy);
        await AssertNothingDeployedOrUndeployed();
    }

    [Test]
    public async Task SiteDeployed_AdapterUndeployed_DeploysOnlyTheAdapter()
    {
        var adapter = Adapter();
        Tenant([Site(state: RtDeploymentStateEnum.Deployed)], [adapter]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.Deployed);
        await _sites.DidNotReceiveWithAnyArgs().DeployDeploymentSiteAsync(default!, default);
        await _sites.Received(1).DeployWorkloadAsync(TenantId, adapter.RtId);
    }

    [Test]
    public async Task SiteDisabled_IsNotDeployed()
    {
        // Disabled / Error are states someone or something decided on; only Undeployed is the fresh state.
        Tenant([Site(state: RtDeploymentStateEnum.Disabled)], [Adapter(RtDeploymentStateEnum.Disabled)]);

        await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await AssertNothingDeployedOrUndeployed();
    }

    [Test]
    public async Task TwoSites_IsNotAFreshTenant_ChangesNothing()
    {
        Tenant([Site(), Site()], [Adapter()]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.NotFreshTenantShape);
        await AssertNothingDeployedOrUndeployed();
    }

    [Test]
    public async Task NoSite_ChangesNothing()
    {
        Tenant([], []);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.NotFreshTenantShape);
        await AssertNothingDeployedOrUndeployed();
    }

    [Test]
    public async Task EdgeSite_ChangesNothing()
    {
        Tenant([Site(RtEnvironmentEnum.Edge)], [Adapter()]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.NotFreshTenantShape);
        await AssertNothingDeployedOrUndeployed();
    }

    [Test]
    public async Task SiteWithTwoAdapters_ChangesNothing()
    {
        Tenant([Site()], [Adapter(), Adapter(id: "670000000000000000000009")]);

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.NotFreshTenantShape);
        await AssertNothingDeployedOrUndeployed();
    }

    [Test]
    public async Task DeployFails_ReportsFailed_DoesNotThrow_AndUndeploysNothing()
    {
        Tenant([Site()], [Adapter()]);
        _sites.DeployDeploymentSiteAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .ThrowsAsync(new InvalidOperationException("operator unavailable"));

        var outcome = await CreateSut().DeployDefaultsAsync(TenantId, BlueprintId, Guid.NewGuid());

        await Assert.That(outcome).IsEqualTo(StarterDeploymentOutcome.Failed);
        await _sites.DidNotReceiveWithAnyArgs().DeployWorkloadAsync(default!, default);
        await AssertNothingUndeployed();
    }
}
