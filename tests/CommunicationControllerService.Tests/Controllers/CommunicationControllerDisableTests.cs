using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// POST communication/disable maps the AB#4255 refusal to 409 with an
/// <see cref="OperationFailedErrorDto"/> body (the shape the Studio interceptor and the tenant
/// delete guard already use) and keeps every other configuration error at 400.
/// </summary>
internal class CommunicationControllerDisableTests
{
    private const string TenantId = "child-a";

    private static (CommunicationController sut, IConfigurationService configuration) CreateSut()
    {
        var configuration = Substitute.For<IConfigurationService>();
        var sut = new CommunicationController(NullLogger<CommunicationController>.Instance, configuration,
            Substitute.For<IExpressionValidationService>(), Substitute.For<IWorkloadEncryptionService>(),
            Substitute.For<IWorkloadTemplateResolver>());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (sut, configuration);
    }

    [Test]
    public async Task Disable_ReturnsNoContent_WhenTheServiceDisables()
    {
        var (sut, configuration) = CreateSut();

        var result = await sut.Disable();

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await configuration.Received(1).DisableAsync(TenantId);
    }

    [Test]
    public async Task Disable_ReturnsConflictWithTheReason_WhenResourcesAreStillDeployed()
    {
        var (sut, configuration) = CreateSut();
        const string reason = "Communication cannot be disabled for tenant 'child-a' while the following resources are still deployed: Pool 'edge-a' (Deployed).";
        configuration.DisableAsync(TenantId).ThrowsAsync(ConfigurationException.TenantDisableBlocked(reason));

        var result = await sut.Disable();

        var conflict = result as ConflictObjectResult;
        await Assert.That(conflict).IsNotNull();
        var body = conflict!.Value as OperationFailedErrorDto;
        await Assert.That(body).IsNotNull();
        await Assert.That(body!.Message).IsEqualTo(reason);
    }

    [Test]
    public async Task Disable_ReturnsBadRequest_ForEveryOtherConfigurationError()
    {
        var (sut, configuration) = CreateSut();
        configuration.DisableAsync(TenantId).ThrowsAsync(ConfigurationException.TenantIsAutoEnabled(TenantId));

        var result = await sut.Disable();

        var badRequest = result as BadRequestObjectResult;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value as string).Contains("auto enabled");
    }
}
