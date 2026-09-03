using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5111 — the configuration-bound endpoints:
/// <c>POST {tenantId}/v1/serviceAccount/reconcile?configurationRtId=…</c> and
/// <c>POST {tenantId}/v1/serviceAccount/{configurationRtId}/rotateSecret</c>, plus the security
/// gate: a user-initiated reconcile only materialises the declared roles when the caller holds the
/// <c>UserManagement</c> role.
/// </summary>
internal class ServiceAccountControllerTests
{
    private const string TenantId = "acme";

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private readonly IPipelineServiceAccountProvisioningService _provisioning =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly ICommunicationEventService _events = Substitute.For<ICommunicationEventService>();

    private ServiceAccountController CreateSut(params string[] callerRoles)
    {
        var sut = new ServiceAccountController(NullLogger<ServiceAccountController>.Instance,
            _repo, _provisioning, _events);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        // Same claim type the JWT bearer setup maps (ConfigureJwtBearerOptions:
        // RoleClaimType = JwtClaimTypes.Role), so User.IsInRole works like in production.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            callerRoles.Select(r => new Claim("role", r)), "TestAuth", "name", "role"));
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    private RtServiceAccountConfiguration ArrangeConfiguration()
    {
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        _repo.GetServiceAccountByRtIdAsync(TenantId, configuration.RtId).Returns(configuration);
        return configuration;
    }

    // ---------------------------------------------------------------- reconcile

    [Test]
    public async Task Reconcile_CallerWithUserManagement_PassesAPrivilegedUserContext()
    {
        var sut = CreateSut(CommonConstants.UserManagementRole);
        var configuration = ArrangeConfiguration();
        _provisioning
            .ReconcileConfigurationAsync(TenantId, configuration, Arg.Any<ServiceAccountReconcileContext>())
            .Returns(new ServiceAccountReconcileResult(PipelineServiceAccountProvisioningOutcome.Repaired,
                "octo-pipeline-sa-1", "pipeline-service-account-1", RoleChangesSkipped: false));

        var result = await sut.Reconcile(configuration.RtId.ToString());

        var dto = (result as OkObjectResult)!.Value as ReconcileServiceAccountResultDto;

        using var _ = Assert.Multiple();
        await Assert.That(dto!.Outcome).IsEqualTo("Repaired");
        await Assert.That(dto.RoleChangesSkipped).IsFalse();
        // The gate decision is made HERE, from the caller's principal — the service only executes it.
        await _provisioning.Received(1).ReconcileConfigurationAsync(TenantId, configuration,
            Arg.Is<ServiceAccountReconcileContext>(c => c.MaterializeRoles && c.Source == "User"));
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("octo-pipeline-sa-1") && m.Contains("(source: User)")));
    }

    [Test]
    public async Task Reconcile_CallerWithoutUserManagement_PassesAnUnprivilegedUserContext()
    {
        var sut = CreateSut("SomeOtherRole");
        var configuration = ArrangeConfiguration();
        _provisioning
            .ReconcileConfigurationAsync(TenantId, configuration, Arg.Any<ServiceAccountReconcileContext>())
            .Returns(new ServiceAccountReconcileResult(
                PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned,
                "octo-pipeline-sa-1", "pipeline-service-account-1", RoleChangesSkipped: true));

        var result = await sut.Reconcile(configuration.RtId.ToString());

        var dto = (result as OkObjectResult)!.Value as ReconcileServiceAccountResultDto;

        using var _ = Assert.Multiple();
        await _provisioning.Received(1).ReconcileConfigurationAsync(TenantId, configuration,
            Arg.Is<ServiceAccountReconcileContext>(c => !c.MaterializeRoles && c.Source == "User"));
        // The degradation is visible to the caller, not silent.
        await Assert.That(dto!.RoleChangesSkipped).IsTrue();
        await Assert.That(dto.Message).Contains(CommonConstants.UserManagementRole);
    }

    [Test]
    public async Task Reconcile_UnknownConfiguration_Returns404WithoutReconciling()
    {
        var sut = CreateSut();
        _repo.GetServiceAccountByRtIdAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns((RtServiceAccountConfiguration?)null);

        var result = await sut.Reconcile(OctoObjectId.GenerateNewId().ToString());

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _provisioning.DidNotReceiveWithAnyArgs()
            .ReconcileConfigurationAsync(default!, default!, default!);
    }

    [Test]
    public async Task Reconcile_MalformedRtId_Returns400()
    {
        var sut = CreateSut();

        var result = await sut.Reconcile("not-an-object-id");

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
        await _provisioning.DidNotReceiveWithAnyArgs()
            .ReconcileConfigurationAsync(default!, default!, default!);
    }

    [Test]
    public async Task Reconcile_Failure_Returns400WithTheCause()
    {
        var sut = CreateSut();
        var configuration = ArrangeConfiguration();
        _provisioning
            .ReconcileConfigurationAsync(TenantId, configuration, Arg.Any<ServiceAccountReconcileContext>())
            .ThrowsAsync(new InvalidOperationException("identity refused"));

        var result = await sut.Reconcile(configuration.RtId.ToString());

        var error = (result as BadRequestObjectResult)!.Value as ErrorResponse;
        await Assert.That(error!.ErrorMessage).Contains("identity refused");
    }

    // ---------------------------------------------------------------- rotate

    [Test]
    public async Task Rotate_HappyPath_ReturnsTheSharedRotationShapeWithARedeployHint()
    {
        var sut = CreateSut();
        var configuration = ArrangeConfiguration();
        _provisioning.RotateConfigurationSecretAsync(TenantId, configuration)
            .Returns(new PipelineServiceAccountRotationResult("octo-pipeline-sa-1",
                "pipeline-service-account-1", WasCreated: false));

        var result = await sut.RotateSecret(configuration.RtId.ToString());

        var dto = (result as OkObjectResult)!.Value as RotateServiceAccountSecretResultDto;

        using var _ = Assert.Multiple();
        // Same DTO as the adapter-scoped rotation — one shape for every client.
        await Assert.That(dto!.ClientId).IsEqualTo("octo-pipeline-sa-1");
        await Assert.That(dto.RequiresPipelineRedeploy).IsTrue();
        await Assert.That(dto.Message).Contains("Redeploy");
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("octo-pipeline-sa-1") && m.Contains("(source: User)")));
    }

    [Test]
    public async Task Rotate_Failure_Returns400StatingThatTheOldSecretStillWorks()
    {
        var sut = CreateSut();
        var configuration = ArrangeConfiguration();
        _provisioning.RotateConfigurationSecretAsync(TenantId, configuration)
            .ThrowsAsync(new InvalidOperationException("identity refused"));

        var result = await sut.RotateSecret(configuration.RtId.ToString());

        var error = (result as BadRequestObjectResult)!.Value as ErrorResponse;

        using var _ = Assert.Multiple();
        await Assert.That(error!.ErrorMessage).Contains("previous secret remains in effect");
        await _events.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("identity refused")));
    }

    [Test]
    public async Task Rotate_UnknownConfiguration_Returns404WithoutRotating()
    {
        var sut = CreateSut();
        _repo.GetServiceAccountByRtIdAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns((RtServiceAccountConfiguration?)null);

        var result = await sut.RotateSecret(OctoObjectId.GenerateNewId().ToString());

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _provisioning.DidNotReceiveWithAnyArgs().RotateConfigurationSecretAsync(default!, default!);
    }
}
