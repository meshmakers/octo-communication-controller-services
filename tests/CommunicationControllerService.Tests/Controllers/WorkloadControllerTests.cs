using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// Pins the branching of the <c>WorkloadController</c> endpoints introduced by
/// Epic 3054 / #4052. Coverage is the controller layer (status codes, validation,
/// event hookup) — repository behaviour lives in the integration tests.
/// </summary>
internal class WorkloadControllerTests
{
    private const string TenantId = "acme";

    private static (WorkloadController sut, ICommunicationRepository repo, ICommunicationEventService events) CreateSut()
    {
        var repo = Substitute.For<ICommunicationRepository>();
        var events = Substitute.For<ICommunicationEventService>();
        var sut = new WorkloadController(NullLogger<WorkloadController>.Instance, repo, events);

        // Route tenant id comes from `HttpContext.GetTenantId()` (extension over
        // route values); set it once so every test gets a sensible default.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (sut, repo, events);
    }

    // ----- GET workloads -----------------------------------------------------

    [Test]
    public async Task Get_NoMatchingWorkloads_ReturnsOkWithEmptyList()
    {
        var (sut, repo, _) = CreateSut();
        repo.GetWorkloadsByChartNameAsync(TenantId, "octo-mesh-adapter")
            .Returns(Array.Empty<RtDeployableWorkload>());

        var result = await sut.Get("octo-mesh-adapter");

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var dtos = ok!.Value as IEnumerable<WorkloadSummaryDto>;
        await Assert.That(dtos).IsNotNull();
        await Assert.That(dtos!.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task Get_MatchingWorkloads_ReturnsMappedDtos()
    {
        var (sut, repo, _) = CreateSut();
        var workload = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            Name = "mesh-adapter-1",
            ChartName = "octo-mesh-adapter",
            ChartVersion = "1.2.2",
            DeploymentState = RtDeploymentStateEnum.Deployed
        };
        repo.GetWorkloadsByChartNameAsync(TenantId, "octo-mesh-adapter")
            .Returns(new[] { (RtDeployableWorkload)workload });

        var result = await sut.Get("octo-mesh-adapter");

        var ok = result as OkObjectResult;
        var dtos = (ok!.Value as IEnumerable<WorkloadSummaryDto>)!.ToList();
        await Assert.That(dtos.Count).IsEqualTo(1);
        await Assert.That(dtos[0].Name).IsEqualTo("mesh-adapter-1");
        await Assert.That(dtos[0].ChartName).IsEqualTo("octo-mesh-adapter");
        await Assert.That(dtos[0].CurrentChartVersion).IsEqualTo("1.2.2");
    }

    // ----- PATCH chart-version ----------------------------------------------

    [Test]
    public async Task UpdateChartVersion_EmptyVersion_UpdatesAndLogsLatestMarker()
    {
        // Empty is the explicit "use latest from configured repo" signal — the
        // operator's HelmRunner omits --version in that case. The repository
        // stores the empty string; the audit log renders "(latest)" so a CI/CD
        // operator inspecting events sees what actually went into Mongo.
        var (sut, repo, events) = CreateSut();
        var workloadId = OctoObjectId.GenerateNewId();
        repo.UpdateWorkloadChartVersionAsync(TenantId, workloadId, string.Empty).Returns("1.2.2");

        var result = await sut.UpdateChartVersion(workloadId,
            new UpdateChartVersionDto(""));

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await repo.Received(1).UpdateWorkloadChartVersionAsync(TenantId, workloadId, string.Empty);
        await events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("1.2.2") && m.Contains("(latest)") && m.Contains("CI/CD")));
    }

    [Test]
    public async Task UpdateChartVersion_WhitespaceVersion_TreatedAsEmpty()
    {
        // Defensive: a trimmed-down whitespace input behaves the same as empty
        // so accidental padding from a CI script doesn't surface as a SemVer
        // failure.
        var (sut, repo, _) = CreateSut();
        var workloadId = OctoObjectId.GenerateNewId();
        repo.UpdateWorkloadChartVersionAsync(TenantId, workloadId, string.Empty).Returns((string?)null);

        var result = await sut.UpdateChartVersion(workloadId,
            new UpdateChartVersionDto("   "));

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await repo.Received(1).UpdateWorkloadChartVersionAsync(TenantId, workloadId, string.Empty);
    }

    [Test]
    public async Task UpdateChartVersion_InvalidSemVer_Returns400()
    {
        var (sut, repo, _) = CreateSut();

        var result = await sut.UpdateChartVersion(OctoObjectId.GenerateNewId(),
            new UpdateChartVersionDto("not-a-version"));

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
        await repo.DidNotReceiveWithAnyArgs().UpdateWorkloadChartVersionAsync(default!, default!, default!);
    }

    [Test]
    public async Task UpdateChartVersion_ValidSemVer_UpdatesAndLogsEvent()
    {
        var (sut, repo, events) = CreateSut();
        var workloadId = OctoObjectId.GenerateNewId();
        repo.UpdateWorkloadChartVersionAsync(TenantId, workloadId, "1.2.3").Returns("1.2.2");

        var result = await sut.UpdateChartVersion(workloadId,
            new UpdateChartVersionDto("1.2.3"));

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await repo.Received(1).UpdateWorkloadChartVersionAsync(TenantId, workloadId, "1.2.3");
        await events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("1.2.2") && m.Contains("1.2.3") && m.Contains("CI/CD")));
    }

    [Test]
    public async Task UpdateChartVersion_AcceptsPrereleaseAndBuildMetadata()
    {
        var (sut, repo, _) = CreateSut();
        var workloadId = OctoObjectId.GenerateNewId();
        repo.UpdateWorkloadChartVersionAsync(TenantId, workloadId, Arg.Any<string>()).Returns("1.0.0");

        // Pin the SemVer regex's intentional flexibility: pre-release identifiers
        // and build metadata both pass. Helm chart versions in the wild use both.
        var result = await sut.UpdateChartVersion(workloadId,
            new UpdateChartVersionDto("1.2.3-beta.1+gitsha.abc"));

        await Assert.That(result).IsTypeOf<NoContentResult>();
    }

    [Test]
    public async Task UpdateChartVersion_NoPreviousVersion_LogsSetMessage()
    {
        var (sut, repo, events) = CreateSut();
        var workloadId = OctoObjectId.GenerateNewId();
        repo.UpdateWorkloadChartVersionAsync(TenantId, workloadId, "1.0.0").Returns((string?)null);

        var result = await sut.UpdateChartVersion(workloadId, new UpdateChartVersionDto("1.0.0"));

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("set to 1.0.0") && !m.Contains("updated from")));
    }
}
