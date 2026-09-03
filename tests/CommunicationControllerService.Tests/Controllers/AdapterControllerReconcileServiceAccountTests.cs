using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5111 — <c>POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile</c>, the
/// adapter-scoped sibling of the configuration-bound reconcile.
/// </summary>
internal class AdapterControllerReconcileServiceAccountTests
{
    private const string TenantId = "acme";

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private readonly IPipelineServiceAccountProvisioningService _provisioning =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly ICommunicationEventService _events = Substitute.For<ICommunicationEventService>();

    private AdapterController CreateSut(params string[] callerRoles)
    {
        var sut = new AdapterController(NullLogger<AdapterController>.Instance, _repo,
            Substitute.For<IAdapterService>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            callerRoles.Select(r => new Claim("role", r)), "TestAuth", "name", "role"));
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return sut;
    }

    [Test]
    public async Task Reconcile_HappyPath_MapsTheCallerRoleIntoTheContext()
    {
        var sut = CreateSut(CommonConstants.UserManagementRole);
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        _repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        _provisioning.ReconcileAdapterAsync(TenantId, adapter, Arg.Any<ServiceAccountReconcileContext>())
            .Returns(new ServiceAccountReconcileResult(PipelineServiceAccountProvisioningOutcome.Provisioned,
                "octo-pipeline-sa-1", "pipeline-service-account-1", RoleChangesSkipped: false));

        var result = await sut.ReconcileServiceAccount(adapter.RtId.ToString(), _provisioning, _events);

        var dto = (result as OkObjectResult)!.Value as ReconcileServiceAccountResultDto;

        using var _ = Assert.Multiple();
        await Assert.That(dto!.Outcome).IsEqualTo("Provisioned");
        await _provisioning.Received(1).ReconcileAdapterAsync(TenantId, adapter,
            Arg.Is<ServiceAccountReconcileContext>(c => c.MaterializeRoles && c.Source == "User"));
        await _events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("mesh-adapter") && m.Contains("(source: User)")));
    }

    [Test]
    public async Task Reconcile_CallerWithoutUserManagement_IsGatedButStillRuns()
    {
        var sut = CreateSut("SomeOtherRole");
        var adapter = RtEntityCreator.CreateAdapter();
        _repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        _provisioning.ReconcileAdapterAsync(TenantId, adapter, Arg.Any<ServiceAccountReconcileContext>())
            .Returns(new ServiceAccountReconcileResult(
                PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned,
                "octo-pipeline-sa-1", "pipeline-service-account-1", RoleChangesSkipped: true));

        var result = await sut.ReconcileServiceAccount(adapter.RtId.ToString(), _provisioning, _events);

        var dto = (result as OkObjectResult)!.Value as ReconcileServiceAccountResultDto;

        using var _ = Assert.Multiple();
        // The endpoint does NOT refuse — the client convergence half is legitimate for any
        // communication-management caller; only the role half is withheld.
        await _provisioning.Received(1).ReconcileAdapterAsync(TenantId, adapter,
            Arg.Is<ServiceAccountReconcileContext>(c => !c.MaterializeRoles));
        await Assert.That(dto!.RoleChangesSkipped).IsTrue();
    }

    [Test]
    public async Task Reconcile_UnknownAdapter_Returns404()
    {
        var sut = CreateSut();
        _repo.GetAdaptersAsync(TenantId).Returns([]);

        var result = await sut.ReconcileServiceAccount(OctoObjectId.GenerateNewId().ToString(),
            _provisioning, _events);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await _provisioning.DidNotReceiveWithAnyArgs().ReconcileAdapterAsync(default!, default!, default!);
    }

    [Test]
    public async Task Reconcile_MalformedAdapterRtId_Returns400()
    {
        var sut = CreateSut();

        var result = await sut.ReconcileServiceAccount("not-an-object-id", _provisioning, _events);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
        await _provisioning.DidNotReceiveWithAnyArgs().ReconcileAdapterAsync(default!, default!, default!);
    }
}
