using System.Reflection;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
///     <c>POST {tenantId}/v1/secretDefaults/reconcile</c>: reconciles the route tenant and its subtree and answers
///     with counts per tenant, under the same authorisation as the adapter-pool mirror reconcile.
/// </summary>
internal sealed class SecretDefaultsControllerTests
{
    private const string TenantId = "parent-p";

    private readonly ISecretDefaultProvisioningService _provisioningService =
        Substitute.For<ISecretDefaultProvisioningService>();

    private SecretDefaultsController CreateSut(string? routeTenantId = TenantId)
    {
        var controller = new SecretDefaultsController(_provisioningService,
            NullLogger<SecretDefaultsController>.Instance);
        var httpContext = new DefaultHttpContext();
        if (routeTenantId is not null)
        {
            httpContext.Request.RouteValues["tenantId"] = routeTenantId;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Test]
    public async Task Reconcile_ReturnsTheCountsOfEveryTenant()
    {
        IReadOnlyList<SecretDefaultTargetResult> perTenantResults =
        [
            new(TenantId, SecretDefaultTargetStatus.Reconciled, 1, 0, 0, 0, 0, 0),
            new("child-c1", SecretDefaultTargetStatus.Reconciled, 1, 0, 1, 0, 0, 0),
            SecretDefaultTargetResult.Unchanged("child-c2", SecretDefaultTargetStatus.ModelMissing)
        ];
        _provisioningService.ReconcileTenantAndSubtreeAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(perTenantResults);

        var response = await CreateSut().ReconcileAsync();

        var body = (response as OkObjectResult)?.Value as IReadOnlyList<SecretDefaultTargetResult>;
        using var assertionScope = Assert.Multiple();
        await Assert.That(body).IsNotNull();
        await Assert.That(body!.Select(result => result.TenantId))
            .IsEquivalentTo(new[] { TenantId, "child-c1", "child-c2" });
        await Assert.That(body.Single(result => result.TenantId == "child-c1").DefaultsRemoved).IsEqualTo(1);
    }

    [Test]
    public async Task Reconcile_WithoutARouteTenant_IsNotFound()
    {
        var response = await CreateSut(routeTenantId: null).ReconcileAsync();

        await Assert.That(response).IsTypeOf<NotFoundObjectResult>();
        await _provisioningService.DidNotReceiveWithAnyArgs()
            .ReconcileTenantAndSubtreeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconcile_RequiresTheSamePolicyAsTheMirrorReconcile()
    {
        var reconcilePolicy = typeof(SecretDefaultsController)
            .GetMethod(nameof(SecretDefaultsController.ReconcileAsync))!
            .GetCustomAttributes<AuthorizeAttribute>().Single().Policy;
        var mirrorReconcilePolicy = typeof(AdapterPoolController)
            .GetMethod(nameof(AdapterPoolController.RefreshMirrorsAsync))!
            .GetCustomAttributes<AuthorizeAttribute>().Single().Policy;

        using var assertionScope = Assert.Multiple();
        await Assert.That(reconcilePolicy).IsNotNull();
        await Assert.That(reconcilePolicy).IsEqualTo(mirrorReconcilePolicy);
        await Assert.That(typeof(SecretDefaultsController).GetCustomAttributes<AuthorizeAttribute>().Single()
                .AuthenticationSchemes)
            .IsEqualTo(typeof(AdapterPoolController).GetCustomAttributes<AuthorizeAttribute>().Single()
                .AuthenticationSchemes);
    }

    [Test]
    public async Task Reconcile_IsAPostOnTheReconcileRoute()
    {
        var postAttribute = typeof(SecretDefaultsController)
            .GetMethod(nameof(SecretDefaultsController.ReconcileAsync))!
            .GetCustomAttribute<HttpPostAttribute>();

        await Assert.That(postAttribute?.Template).IsEqualTo("reconcile");
    }

    [Test]
    public async Task TheResultType_HasNoMemberThatCouldCarryAValue()
    {
        var memberNames = typeof(SecretDefaultTargetResult).GetProperties().Select(property => property.Name);

        await Assert.That(memberNames).IsEquivalentTo(new[]
        {
            nameof(SecretDefaultTargetResult.TenantId), nameof(SecretDefaultTargetResult.Status),
            nameof(SecretDefaultTargetResult.DefaultsCreated), nameof(SecretDefaultTargetResult.DefaultsUpdated),
            nameof(SecretDefaultTargetResult.DefaultsWritten), nameof(SecretDefaultTargetResult.DefaultsRemoved),
            nameof(SecretDefaultTargetResult.DefaultsKept), nameof(SecretDefaultTargetResult.SourcesUnreadable),
            nameof(SecretDefaultTargetResult.WritesFailed), nameof(SecretDefaultTargetResult.DefaultsUnchanged)
        });
    }
}
