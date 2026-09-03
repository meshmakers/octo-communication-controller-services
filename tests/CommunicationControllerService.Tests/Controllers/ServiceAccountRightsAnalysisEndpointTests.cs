using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5113 — the two read-only rights-analysis endpoints:
/// <c>GET {tenantId}/v1/serviceAccount/{configurationRtId}/rightsAnalysis</c> and
/// <c>GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rightsAnalysis</c>. The analysis
/// itself is covered by <c>ServiceAccountRightsAnalysisTests</c>; here only routing, lookup and
/// error mapping — same scope split as the AB#5112 health endpoint tests.
/// </summary>
internal class ServiceAccountRightsAnalysisEndpointTests
{
    private const string TenantId = "acme";

    private static readonly ServiceAccountRightsAnalysisDto SampleAnalysis = new(
        "0123456789abcdef01234567", "pipeline-service-account-1", [], [], [], [],
        null, [], [], [], "No pipelines execute under this service account — nothing to analyze.");

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private readonly IServiceAccountRightsAnalysisService _analysisService =
        Substitute.For<IServiceAccountRightsAnalysisService>();

    private static T WithHttpContext<T>(T controller) where T : ControllerBase
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth", "name", "role"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private ServiceAccountController CreateConfigurationSut()
    {
        return WithHttpContext(new ServiceAccountController(NullLogger<ServiceAccountController>.Instance,
            _repo, Substitute.For<IPipelineServiceAccountProvisioningService>(),
            Substitute.For<ICommunicationEventService>()));
    }

    private AdapterController CreateAdapterSut()
    {
        return WithHttpContext(new AdapterController(NullLogger<AdapterController>.Instance, _repo,
            Substitute.For<IAdapterService>()));
    }

    // ---------------------------------------------------------------- configuration-bound

    [Test]
    public async Task GetRightsAnalysis_ExistingConfiguration_ReturnsTheAnalysis()
    {
        var sut = CreateConfigurationSut();
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        _repo.GetServiceAccountByRtIdAsync(TenantId, configuration.RtId).Returns(configuration);
        _analysisService.AnalyzeConfigurationAsync(TenantId, configuration).Returns(SampleAnalysis);

        var result = await sut.GetRightsAnalysis(configuration.RtId.ToString(), _analysisService);

        var dto = (result as OkObjectResult)!.Value as ServiceAccountRightsAnalysisDto;
        await Assert.That(dto).IsEqualTo(SampleAnalysis);
    }

    [Test]
    public async Task GetRightsAnalysis_UnknownConfiguration_Returns404()
    {
        var sut = CreateConfigurationSut();

        var result = await sut.GetRightsAnalysis(OctoObjectId.GenerateNewId().ToString(), _analysisService);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _analysisService.DidNotReceiveWithAnyArgs()
            .AnalyzeConfigurationAsync(default!, default!);
    }

    [Test]
    public async Task GetRightsAnalysis_MalformedConfigurationRtId_Returns400()
    {
        var sut = CreateConfigurationSut();

        var result = await sut.GetRightsAnalysis("not-an-object-id", _analysisService);

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
    }

    // ---------------------------------------------------------------- adapter-scoped

    [Test]
    public async Task GetServiceAccountRightsAnalysis_ExistingAdapter_ReturnsTheAnalysis()
    {
        var sut = CreateAdapterSut();
        var adapter = RtEntityCreator.CreateAdapter();
        _repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        _analysisService.AnalyzeAdapterAsync(TenantId, adapter).Returns(SampleAnalysis);

        var result = await sut.GetServiceAccountRightsAnalysis(adapter.RtId.ToString(), _analysisService);

        var dto = (result as OkObjectResult)!.Value as ServiceAccountRightsAnalysisDto;
        await Assert.That(dto).IsEqualTo(SampleAnalysis);
    }

    [Test]
    public async Task GetServiceAccountRightsAnalysis_UnknownAdapter_Returns404()
    {
        var sut = CreateAdapterSut();
        _repo.GetAdaptersAsync(TenantId).Returns([]);

        var result = await sut.GetServiceAccountRightsAnalysis(OctoObjectId.GenerateNewId().ToString(),
            _analysisService);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _analysisService.DidNotReceiveWithAnyArgs().AnalyzeAdapterAsync(default!, default!);
    }

    [Test]
    public async Task GetServiceAccountRightsAnalysis_MalformedAdapterRtId_Returns400()
    {
        var sut = CreateAdapterSut();

        var result = await sut.GetServiceAccountRightsAnalysis("not-an-object-id", _analysisService);

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
    }
}
