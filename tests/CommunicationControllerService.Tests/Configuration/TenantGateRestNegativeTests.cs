using System.Net;
using Microsoft.Extensions.Configuration;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#6833 (Lane C of AB#6728) — negative tests for the foreign-tenant user-token gate on the
///     communication-controller's REST routes. Nothing here is a unit test of the middleware (that
///     lives in octo-common-services); what is proven is that a request travels the real
///     <c>UseOctoTenantAuthorization()</c> pipeline of this service's shape and is refused
///     <b>before the action runs</b> once the mode is <c>Enforce</c> through configuration.
/// </summary>
internal class TenantGateRestNegativeTests
{
    // Route families of the tenant API (see TenantApi/v1/Controllers): adapters, pipelines, data flows,
    // deployment sites, workloads. The harness maps them with the real {tenantId:tenantId} template.
    private static readonly string[] Families =
    [
        "adapter", "pipeline", "dataflow", "deploymentsite", "workload", "adapterpool"
    ];

    private static string[] Templates()
    {
        return Families.Select(f => $"{{tenantId:tenantId}}/v{{version}}/{f}").ToArray();
    }

    [Test]
    [Arguments("adapter")]
    [Arguments("pipeline")]
    [Arguments("dataflow")]
    [Arguments("deploymentsite")]
    [Arguments("workload")]
    [Arguments("adapterpool")]
    public async Task ForeignTenantUserToken_Gets403AndTheActionDoesNotRun_WhenEnforcing(string family)
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        var response = await host.SendAsync(HttpMethod.Get, $"/tenant-b/v1/{family}",
            TenantGateTestHost.UserToken("tenant-a"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.HandlerCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ForeignTenant403_CarriesTheJsonBodyNamingBothTenants()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/tenant-b/v1/adapter",
            TenantGateTestHost.UserToken("tenant-a"));
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(body).Contains("\"message\"");
        await Assert.That(body).Contains("tenant-a");
        await Assert.That(body).Contains("tenant-b");
    }

    [Test]
    public async Task EveryWriteVerb_OfAForeignTenantUserToken_IsRefused_WhenEnforcing()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var response = await host.SendAsync(method, "/tenant-b/v1/pipeline",
                TenantGateTestHost.UserToken("tenant-a"));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        await Assert.That(host.HandlerCalls).IsEqualTo(0);
    }

    [Test]
    public async Task MatchingTenantUserToken_PassesAndTheActionRuns_WhenEnforcing()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/tenant-a/v1/adapter",
            TenantGateTestHost.UserToken("tenant-a"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.HandlerCalls).IsEqualTo(1);
    }

    [Test]
    public async Task UserTokenWithoutTenantId_Gets403_WhenEnforcing()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/tenant-b/v1/adapter",
            TenantGateTestHost.UserToken(null));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.HandlerCalls).IsEqualTo(0);
    }

    /// <summary>
    ///     A parent administrator gets no free pass: without the marker no tenant is "below" the
    ///     token's tenant, whichever tenant the token comes from.
    /// </summary>
    [Test]
    public async Task UserTokenOfTheSystemTenant_OnAnotherTenantsRoute_Gets403_WhenEnforcing()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/tenant-b/v1/adapter",
            TenantGateTestHost.UserToken("octosystem"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(host.HandlerCalls).IsEqualTo(0);
    }

    /// <summary>
    ///     The platform stays on the migration mode until an operator flips it (no behaviour change in
    ///     this story): same request, no configuration, the action runs (the gate only logs).
    /// </summary>
    [Test]
    public async Task ForeignTenantUserToken_StillPasses_WhenNothingIsConfigured()
    {
        await using var host = await TenantGateTestHost.StartAsync(null, Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/tenant-b/v1/adapter",
            TenantGateTestHost.UserToken("tenant-a"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.HandlerCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ForeignTenantUserToken_StillPasses_WhenConfiguredLogOnly_ThisIsTheRollback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "LogOnly"
            })
            .Build();
        await using var host = await TenantGateTestHost.StartAsync(configuration, Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/tenant-b/v1/adapter",
            TenantGateTestHost.UserToken("tenant-a"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    ///     The route constraint is part of the pipeline: a segment that cannot name a tenant never
    ///     reaches the gate nor the action.
    /// </summary>
    [Test]
    public async Task MalformedTenantSegment_DoesNotReachTheAction()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), Templates());

        var response = await host.SendAsync(HttpMethod.Get, "/not%20a%20tenant/v1/adapter",
            TenantGateTestHost.UserToken("tenant-a"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(host.HandlerCalls).IsEqualTo(0);
    }

    /// <summary>
    ///     Links the harness to the real service: every tenant-addressed controller really uses the
    ///     <c>{tenantId:tenantId}</c> route template the gate keys on, so a route moved out of that
    ///     shape would escape the gate silently.
    /// </summary>
    [Test]
    public async Task EveryTenantApiController_UsesTheTenantIdRouteTemplate()
    {
        var controllers = typeof(Meshmakers.Octo.Backend.CommunicationControllerServices.Constants).Assembly
            .GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.Contains(".TenantApi.") &&
                        typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t))
            .ToList();

        await Assert.That(controllers.Count).IsGreaterThan(5);
        foreach (var controller in controllers)
        {
            var template = ((Microsoft.AspNetCore.Mvc.RouteAttribute?)Attribute.GetCustomAttribute(controller,
                typeof(Microsoft.AspNetCore.Mvc.RouteAttribute)))?.Template;
            await Assert.That(template).IsNotNull();
            await Assert.That(template!.StartsWith("{tenantId:tenantId}/", StringComparison.Ordinal)).IsTrue();
        }
    }
}
