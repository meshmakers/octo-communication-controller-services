using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#5327 — the leasing validation runs BEFORE the Helm-field checks.
/// </summary>
/// <remarks>
///     <para>
///         The AB#5278 gate that refuses a <c>Leased</c> workload whose pipelines carry triggers a
///         lease can never deliver used to sit at the end of
///         <c>EnsureWorkloadIsHelmDeployableAsync</c>, behind the chart-name check. A canonical
///         leased adapter has no chart name on purpose — the borrower sample says so outright — so
///         that gate was unreachable for exactly the workload shape it judges: the deploy always
///         stopped at "set a Helm chart name", advice that is wrong for a workload which must never
///         have one, and an HTTP-triggered pipeline could be attached to a leased adapter without a
///         word and then never run.
///     </para>
///     <para>
///         🔴 The suite that covers the gate's CONTENT (<c>DeployWorkloadLifecycleValidationTests</c>)
///         cannot catch this: its arrange helper gives every adapter a chart name, so it always
///         entered through the door this test proves was shut.
///     </para>
/// </remarks>
internal class DeployWorkloadLeasedGateOrderTests : PoolServiceTestsBase
{
    private const string LenderTenantId = "lender";

    /// <summary>A leased adapter as the borrower sample authors one: no chart, no chart version.</summary>
    private RtAdapter GivenLeasedAdapterWithoutChart()
    {
        var deploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            // Edge keeps the arrange minimal: validation runs before any operator routing either way.
            Environment = RtEnvironmentEnum.Edge
        };
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "borrower",
            ChartName = string.Empty,
            ChartVersion = string.Empty,
            ValuesYaml = string.Empty,
            LifecycleMode = RtLifecycleModeEnum.Leased
        };

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId).Returns(adapter);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, adapter.RtId).Returns(deploymentSite);

        return adapter;
    }

    /// <summary>
    ///     The regression: same adapter, same pipelines, no chart name — and the answer is the
    ///     AB#5278 refusal naming the trigger, not a request to invent a chart name.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_LeasedWithoutChartName_StillReachesTheLeaseGate()
    {
        var adapter = GivenLeasedAdapterWithoutChart();
        OnDemandCapabilityService
            .EvaluateForLeaseAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(false,
                ["Pipeline 'api' uses trigger 'FromHttpRequest@2', which cannot produce a lease (AB#5258)"]));

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("FromHttpRequest@2");
        await Assert.That(ex!.Message).Contains("cron PipelineTrigger");
        // The message that used to win, and the instruction that made it harmful.
        await Assert.That(ex!.Message).DoesNotContain("Chart Name");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    /// <summary>
    ///     A leased adapter whose leasing is entirely sound still has nothing to deploy — and is told
    ///     that, instead of being sent to the Studio to fill in a field that must stay empty.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_LeasedAndValid_SaysThereIsNothingToDeploy()
    {
        var adapter = GivenLeasedAdapterWithoutChart();
        var poolRtId = OctoObjectId.GenerateNewId().ToString();

        OnDemandCapabilityService
            .EvaluateForLeaseAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(true, []));
        CommunicationRepository.ArrangeLentFrom(TenantId, adapter, LenderTenantId, poolRtId);
        CommunicationRepository.TryGetAdapterPoolLendingScopeAsync(LenderTenantId, poolRtId)
            .Returns(new LendingScope((int)RtAdapterSharingModeEnum.Descendants, null));
        LendingScopeResolver.MayLendAsync(LenderTenantId, TenantId, Arg.Any<LendingScope>()).Returns(true);

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("Leased");
        await Assert.That(ex!.Message).Contains("nothing to deploy");
        // It names the thing that IS deployed instead.
        await Assert.That(ex!.Message).Contains("POOL");
        await Assert.That(ex!.Message).DoesNotContain("Chart Name");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    /// <summary>
    ///     The reorder must not weaken the ordinary case: a dedicated adapter with no chart name is
    ///     still told to set one.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_DedicatedWithoutChartName_StillAsksForTheChartName()
    {
        var adapter = GivenLeasedAdapterWithoutChart();
        adapter.LifecycleMode = RtLifecycleModeEnum.AlwaysOn;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("Chart Name");
        await Assert.That(ex!.Message).DoesNotContain("nothing to deploy");
    }
}
