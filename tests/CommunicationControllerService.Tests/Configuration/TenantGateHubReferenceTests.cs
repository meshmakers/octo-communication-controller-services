using System.Net;
using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#6833 — reference check for the SignalR hubs. They are <b>outside</b> the REST tenant gate:
///     a SignalR client sends its token as <c>?access_token=</c>, not as an <c>Authorization</c>
///     header, and <c>TenantAuthorizationMiddleware</c> returns early on such a request. The tenant
///     isolation of <c>/{tenantId}/adapterHub</c> is <c>AdapterHubAuthorizationFilter</c>, enforced on
///     all clusters (AB#5977). The two tests below document both halves for a <b>user</b> token
///     (carries <c>sub</c>), so nobody reads "the gate is Enforce" as "the hubs are covered by it".
/// </summary>
internal class TenantGateHubReferenceTests
{
    /// <summary>The gate does not see the hub request: a foreign-tenant user token passes it, even in Enforce.</summary>
    [Test]
    public async Task HubRequestWithQueryToken_IsNotJudgedByTheRestGate_EvenWhenEnforcing()
    {
        await using var host = await TenantGateTestHost.StartAsync(
            TenantGateTestHost.EnforceFromConfiguration(), "{tenantId:tenantId}/adapterHub");

        var token = Uri.EscapeDataString(TenantGateTestHost.UserToken("tenant-a"));
        var response = await host.SendAsync(HttpMethod.Get, $"/tenant-b/adapterHub?access_token={token}", null);

        // The REST gate let it through; refusing it is the hub filter's job (next test).
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.HandlerCalls).IsEqualTo(1);
    }

    /// <summary>
    ///     The actual isolation: a user token (<c>sub</c>) of tenant A connecting to tenant B's adapter
    ///     hub is refused in Enforce; the same token on its own tenant's hub is accepted.
    /// </summary>
    [Test]
    public async Task UserTokenOfTenantA_OnTenantBsAdapterHub_IsRefused_WhenEnforcing()
    {
        await using var provider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);

        await Assert.That(async () => await ConnectAsync(CreateContext(provider, UserToken("tenant-a"), "tenant-b")))
            .Throws<HubException>();
        await Assert.That(await ConnectAsync(CreateContext(provider, UserToken("tenant-a"), "tenant-a"))).IsTrue();
    }

    private static ClaimsPrincipal UserToken(string tenantId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
            new Claim("sub", "user-1"),
            new Claim("client_id", "octo-studio"),
            new Claim("tenant_id", tenantId)
        ], JwtBearerDefaults.AuthenticationScheme));
    }

    private static ServiceProvider BuildServiceProvider(AdapterHubAuthorizationMode mode)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options =>
                options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)))
            .Configure<AdapterHubAuthorizationOptions>(o => o.Mode = mode)
            .Configure<TenantAuthorizationOptions>(_ => { })
            .BuildServiceProvider();
    }

    private static HubLifetimeContext CreateContext(IServiceProvider serviceProvider, ClaimsPrincipal user,
        string routeTenantId)
    {
        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        httpContext.Request.RouteValues["tenantId"] = routeTenantId;

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new HttpContextFeature(httpContext));

        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns("conn-reference");
        callerContext.User.Returns(user);
        callerContext.Features.Returns(features);

        return new HubLifetimeContext(callerContext, serviceProvider, new ReferenceHub());
    }

    private static async Task<bool> ConnectAsync(HubLifetimeContext context)
    {
        var connected = false;
        await new AdapterHubAuthorizationFilter().OnConnectedAsync(context, _ =>
        {
            connected = true;
            return Task.CompletedTask;
        });
        return connected;
    }

    private sealed class ReferenceHub : Hub;

    private sealed class HttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
