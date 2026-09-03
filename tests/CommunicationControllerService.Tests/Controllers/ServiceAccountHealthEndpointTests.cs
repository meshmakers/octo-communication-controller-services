using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5112 — the two read-only identity-health endpoints:
/// <c>GET {tenantId}/v1/serviceAccount/{configurationRtId}/health</c> and
/// <c>GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/health</c>. The aggregate itself is
/// covered by <c>ServiceAccountHealthTests</c>; here only routing, lookup and error mapping.
/// </summary>
internal class ServiceAccountHealthEndpointTests
{
    private const string TenantId = "acme";

    private static readonly ServiceAccountHealthDto SampleHealth = new("Healthy",
        "0123456789abcdef01234567", "pipeline-service-account-1", "octo-pipeline-sa-1", []);

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private readonly IServiceAccountHealthService _healthService =
        Substitute.For<IServiceAccountHealthService>();

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
    public async Task GetHealth_ExistingConfiguration_ReturnsTheAggregate()
    {
        var sut = CreateConfigurationSut();
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        _repo.GetServiceAccountByRtIdAsync(TenantId, configuration.RtId).Returns(configuration);
        _healthService.GetConfigurationHealthAsync(TenantId, configuration).Returns(SampleHealth);

        var result = await sut.GetHealth(configuration.RtId.ToString(), _healthService);

        var dto = (result as OkObjectResult)!.Value as ServiceAccountHealthDto;
        await Assert.That(dto).IsEqualTo(SampleHealth);
    }

    [Test]
    public async Task GetHealth_UnknownConfiguration_Returns404()
    {
        var sut = CreateConfigurationSut();

        var result = await sut.GetHealth(OctoObjectId.GenerateNewId().ToString(), _healthService);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _healthService.DidNotReceiveWithAnyArgs()
            .GetConfigurationHealthAsync(default!, default!);
    }

    [Test]
    public async Task GetHealth_MalformedConfigurationRtId_Returns400()
    {
        var sut = CreateConfigurationSut();

        var result = await sut.GetHealth("not-an-object-id", _healthService);

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
    }

    // ---------------------------------------------------------------- adapter-scoped

    [Test]
    public async Task GetServiceAccountHealth_ExistingAdapter_ReturnsTheAggregate()
    {
        var sut = CreateAdapterSut();
        var adapter = RtEntityCreator.CreateAdapter();
        _repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        _healthService.GetAdapterHealthAsync(TenantId, adapter).Returns(SampleHealth);

        var result = await sut.GetServiceAccountHealth(adapter.RtId.ToString(), _healthService);

        var dto = (result as OkObjectResult)!.Value as ServiceAccountHealthDto;
        await Assert.That(dto).IsEqualTo(SampleHealth);
    }

    [Test]
    public async Task GetServiceAccountHealth_UnknownAdapter_Returns404()
    {
        var sut = CreateAdapterSut();
        _repo.GetAdaptersAsync(TenantId).Returns([]);

        var result = await sut.GetServiceAccountHealth(OctoObjectId.GenerateNewId().ToString(), _healthService);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _healthService.DidNotReceiveWithAnyArgs().GetAdapterHealthAsync(default!, default!);
    }

    [Test]
    public async Task GetServiceAccountHealth_MalformedAdapterRtId_Returns400()
    {
        var sut = CreateAdapterSut();

        var result = await sut.GetServiceAccountHealth("not-an-object-id", _healthService);

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
    }
}
