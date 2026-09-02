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

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#5063 — <c>/{tenantId}/adapterHub</c> is the adapter data plane (an adapter registers itself,
///     receives the tenant's pipeline configuration including its credentials, and writes execution
///     results, debug points and metrics back) and carried no authorization at all: no
///     <c>[Authorize]</c> on the hub, no <c>FallbackPolicy</c>, no <c>RequireAuthorization()</c> on the
///     endpoint.
///     <para>
///         Two halves are pinned here. The <b>policy</b> half mirrors the operator gate (AB#5059). The
///         <b>tenant</b> half is the one with no equivalent anywhere else in the service: the hub is
///         tenant-addressed, and <c>TenantAuthorizationMiddleware</c> never sees a hub connection
///         because a SignalR client sends its token as <c>?access_token=</c> rather than in an
///         <c>Authorization</c> header. Its rules are reused verbatim — exact <c>tenant_id</c> match,
///         fail closed without the claim, allow-list for genuine cross-tenant service clients, and
///         <b>no</b> parent/ancestor allowance (AB#5060 is user-token-only, and an adapter is not a
///         user).
///     </para>
/// </summary>
internal class AdapterHubAuthorizationFilterTests
{
    private const string ConnectionId = "conn-adapter-1";
    private const string RouteTenantId = "meshtest";

    private sealed class TestHub : Hub;

    private sealed class TestHttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private static ServiceProvider BuildServiceProvider(AdapterHubAuthorizationMode mode,
        params string[] crossTenantServiceClientIds)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options =>
            {
                // Mirrors the registration in Program.cs one for one.
                options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess));
            })
            .Configure<AdapterHubAuthorizationOptions>(o => o.Mode = mode)
            .Configure<TenantAuthorizationOptions>(o =>
                o.CrossTenantServiceClientIds = crossTenantServiceClientIds.ToList())
            .BuildServiceProvider();
    }

    private static HubLifetimeContext CreateContext(IServiceProvider serviceProvider, ClaimsPrincipal? user,
        string? routeTenantId = RouteTenantId)
    {
        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        if (routeTenantId != null)
        {
            httpContext.Request.RouteValues["tenantId"] = routeTenantId;
        }

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns(ConnectionId);
        callerContext.User.Returns(user);
        callerContext.Features.Returns(features);

        return new HubLifetimeContext(callerContext, serviceProvider, new TestHub());
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
    }

    private static ClaimsPrincipal Anonymous()
    {
        return new ClaimsPrincipal(new ClaimsIdentity());
    }

    /// <summary>
    ///     The credential the adapter fleet is supposed to present once it authenticates: a
    ///     client-credentials token of the adapter's own tenant carrying the <c>octo_api</c> scope —
    ///     exactly what the per-adapter pipeline service account of AB#5027 is provisioned with.
    /// </summary>
    private static ClaimsPrincipal AdapterServiceToken(string tenantId = RouteTenantId,
        string clientId = "octo-pipeline-sa-abc")
    {
        return Principal(
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
            new Claim("client_id", clientId),
            new Claim("tenant_id", tenantId));
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

    [Test]
    public async Task ScopedAdapterOfTheRouteTenant_IsConnected_InEveryMode()
    {
        foreach (var mode in new[] { AdapterHubAuthorizationMode.LogOnly, AdapterHubAuthorizationMode.Enforce })
        {
            await using var serviceProvider = BuildServiceProvider(mode);
            var context = CreateContext(serviceProvider, AdapterServiceToken());

            await Assert.That(await ConnectAsync(context)).IsTrue();
        }
    }

    /// <summary>
    ///     Tenant ids are compared case-insensitively, like every other tenant comparison on the
    ///     platform — the route value is whatever the client typed into the URL.
    /// </summary>
    [Test]
    public async Task TenantMatch_IsCaseInsensitive()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider, AdapterServiceToken("MeshTest"));

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    /// <summary>
    ///     🔴 The staging contract. Today's adapter fleet lands exactly here — the SDK's
    ///     <c>AccessTokenProvider</c> (AB#5062) reads a token holder nothing fills before the hub
    ///     connection is made, so an adapter connects anonymously. It must keep connecting. A test
    ///     that went red on this line would mean the estate had been disconnected.
    /// </summary>
    [Test]
    public async Task UnauthenticatedAdapter_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.LogOnly);
        var context = CreateContext(serviceProvider, Anonymous());

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    [Test]
    public async Task UnauthenticatedAdapter_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider, Anonymous());

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    /// <summary>
    ///     A valid token is not enough. The hub is a write surface, so the read-only API scope — or a
    ///     plain front-end <c>openid profile</c> token — must not be able to register an adapter or
    ///     report execution results.
    /// </summary>
    [Test]
    public async Task AuthenticatedWithoutTheWriteScope_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiReadOnly),
                new Claim("sub", "some-user"),
                new Claim("tenant_id", RouteTenantId)));

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    [Test]
    public async Task AuthenticatedWithoutTheWriteScope_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.LogOnly);
        var context = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiReadOnly),
                new Claim("tenant_id", RouteTenantId)));

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    /// <summary>
    ///     🔴 The half that has no equivalent anywhere else in this service. A perfectly valid,
    ///     fully-scoped adapter credential of tenant A must not be able to register as an adapter of
    ///     tenant B by pointing at B's hub path — which is precisely what the endpoint allowed, and
    ///     what the HTTP tenant gate cannot catch here.
    /// </summary>
    [Test]
    public async Task ScopedAdapterOfAnotherTenant_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider, AdapterServiceToken("othertenant"));

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    [Test]
    public async Task ScopedAdapterOfAnotherTenant_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.LogOnly);
        var context = CreateContext(serviceProvider, AdapterServiceToken("othertenant"));

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    /// <summary>
    ///     Fail closed: a token that cannot be attributed to a tenant is not bound to the hub path it
    ///     uses. Same stance as <c>TenantAuthorizationMiddleware</c>'s service path.
    /// </summary>
    [Test]
    public async Task ServiceTokenWithoutTenantClaim_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("client_id", "some-legacy-client")));

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    /// <summary>
    ///     The operator's escape hatch, deliberately the same list the HTTP gate uses
    ///     (<c>TenantAuthorizationOptions.CrossTenantServiceClientIds</c>) rather than a second one
    ///     belonging to this filter — one allow-list per service.
    /// </summary>
    [Test]
    public async Task AllowListedCrossTenantServiceClient_IsConnected_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce,
            "octo-platform-*");
        var context = CreateContext(serviceProvider,
            AdapterServiceToken("othertenant", "octo-platform-worker"));

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    /// <summary>
    ///     🔴 The parent-tenant administration rule (AB#5060) does <b>not</b> extend to this hub. It is
    ///     scoped to user tokens on endpoints marked <c>IAllowParentTenantAdministration</c>; a hub is
    ///     not such an endpoint, and a service token's <c>tenant_id</c> proves far less anyway
    ///     (mirrored clients share the parent's secret, and a token minted without <c>acr_values</c>
    ///     falls back to the system tenant — the root of the hierarchy). A parent's credential is
    ///     therefore refused on a child's hub path exactly like any other foreign tenant.
    /// </summary>
    [Test]
    public async Task ParentTenantCredential_IsRefusedOnAChildsHubPath_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);

        var parentServiceToken = CreateContext(serviceProvider, AdapterServiceToken("octosystem"));
        await Assert.That(async () => await ConnectAsync(parentServiceToken)).Throws<HubException>();

        var parentUserToken = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("sub", "an-admin"),
                new Claim("tenant_id", "octosystem")));
        await Assert.That(async () => await ConnectAsync(parentUserToken)).Throws<HubException>();
    }

    /// <summary>
    ///     A user token of the route tenant passes — the pipeline debugger flows over this hub from
    ///     the adapter side today, but nothing about the gate is adapter-specific, and a future
    ///     interactive consumer must not need a special case.
    /// </summary>
    [Test]
    public async Task UserTokenOfTheRouteTenant_IsConnected_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("sub", "an-admin"),
                new Claim("tenant_id", RouteTenantId)));

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    /// <summary>
    ///     A connection with no tenant in its path addresses no tenant at all — <c>AdapterHub</c>
    ///     itself aborts it. The gate must not be the one place that judges it acceptable.
    /// </summary>
    [Test]
    public async Task ConnectionWithoutARouteTenant_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider, AdapterServiceToken(), null);

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    /// <summary>
    ///     The zero value of the enum is the migration mode, so an unbound configuration section
    ///     arrives in LogOnly rather than disconnecting the fleet on first deploy of this change.
    /// </summary>
    [Test]
    public async Task DefaultMode_IsLogOnly()
    {
        await Assert.That(new AdapterHubAuthorizationOptions().Mode)
            .IsEqualTo(AdapterHubAuthorizationMode.LogOnly);

        // The zero value matters independently of the property initializer: a binder that writes a
        // default(TEnum) over the initializer must still land in the migration mode.
        var zeroValue = (AdapterHubAuthorizationMode)Enum.ToObject(
            typeof(AdapterHubAuthorizationMode), 0);
        await Assert.That(zeroValue).IsEqualTo(AdapterHubAuthorizationMode.LogOnly);
    }
}
