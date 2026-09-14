using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#4924 increment 6 — the gate of <c>/adapterPoolHub</c>, in the AB#5063 matrix.
/// </summary>
/// <remarks>
///     <para>
///         The policy half mirrors the adapter gate one for one, deliberately: a pool member is an
///         adapter data-plane connection, not a control-plane one, so
///         <c>SystemCommunicationApiPolicy</c> — which the operator gate uses — was rejected as too
///         much authority (concept §4, Q4).
///     </para>
///     <para>
///         🔴 The half that differs is the tenant binding. The adapter gate compares the token tenant
///         against a <b>route</b> tenant; this route deliberately has none, because a pool member
///         belongs to no tenant. So the gate binds the connection to the tenant <i>in the token</i>
///         and records it, and <c>AdapterPoolHub.RegisterPoolMemberAsync</c> completes the check
///         against the declared pool. What is pinned here is that the recording happens, that it fails
///         closed when there is no tenant to record, and that the staged mode governs it.
///     </para>
/// </remarks>
internal class AdapterPoolHubAuthorizationFilterTests
{
    private const string ConnectionId = "conn-pool-1";
    private const string LenderTenantId = "lender";

    private sealed class TestHub : Hub;

    private sealed class TestHttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private static ServiceProvider BuildServiceProvider(AdapterPoolHubAuthorizationMode mode)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options =>
            {
                // Mirrors the registration in Program.cs one for one.
                options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
                options.AddPolicy(Constants.SystemCommunicationApiPolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
            })
            .Configure<AdapterPoolHubAuthorizationOptions>(o => o.Mode = mode)
            .BuildServiceProvider();
    }

    private static (HubLifetimeContext Context, Dictionary<object, object?> Items) CreateContext(
        IServiceProvider serviceProvider, ClaimsPrincipal? user)
    {
        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var items = new Dictionary<object, object?>();
        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns(ConnectionId);
        callerContext.User.Returns(user);
        callerContext.Features.Returns(features);
        callerContext.Items.Returns(items);

        return (new HubLifetimeContext(callerContext, serviceProvider, new TestHub()), items);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    /// <summary>
    ///     The credential a pool member presents: a client-credentials token of the <b>lending</b>
    ///     tenant carrying <c>octo_api</c> — the pool's own <c>PoolServiceAccount</c> (AB#4924 §2.4).
    /// </summary>
    private static ClaimsPrincipal PoolMemberToken(string tenantId = LenderTenantId,
        string clientId = "octo-pool-sa-abc")
    {
        return Principal(
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
            new Claim("client_id", clientId),
            new Claim("tenant_id", tenantId));
    }

    private static async Task<bool> ConnectAsync(HubLifetimeContext context)
    {
        var connected = false;
        await new AdapterPoolHubAuthorizationFilter().OnConnectedAsync(context, _ =>
        {
            connected = true;
            return Task.CompletedTask;
        });

        return connected;
    }

    [Test]
    public async Task ATenantBoundPoolMember_IsConnected_InEveryMode()
    {
        foreach (var mode in new[]
                 {
                     AdapterPoolHubAuthorizationMode.LogOnly, AdapterPoolHubAuthorizationMode.Enforce
                 })
        {
            await using var serviceProvider = BuildServiceProvider(mode);
            var (context, _) = CreateContext(serviceProvider, PoolMemberToken());

            await Assert.That(await ConnectAsync(context)).IsTrue();
        }
    }

    /// <summary>
    ///     🔴 The binding the hub later completes. Without this the hub cannot tell whose pool the
    ///     connection may register into, and it fails closed — so a gate that stopped recording would
    ///     silently refuse every registration under Enforce, and silently accept every one under
    ///     LogOnly.
    /// </summary>
    [Test]
    public async Task TheConnectionTenantIsRecordedForTheHub()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.Enforce);
        var (context, items) = CreateContext(serviceProvider, PoolMemberToken());

        await ConnectAsync(context);

        await Assert.That(items[AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey])
            .IsEqualTo(LenderTenantId);
    }

    /// <summary>
    ///     Recorded in LogOnly too. An inventory that did not produce the value an enforcing run acts
    ///     on would not be an inventory of that run.
    /// </summary>
    [Test]
    public async Task TheConnectionTenantIsRecordedInLogOnlyAsWell()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.LogOnly);
        var (context, items) = CreateContext(serviceProvider, PoolMemberToken());

        await ConnectAsync(context);

        await Assert.That(items[AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey])
            .IsEqualTo(LenderTenantId);
    }

    [Test]
    public async Task UnauthenticatedMember_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.Enforce);
        var (context, _) = CreateContext(serviceProvider, Anonymous());

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    [Test]
    public async Task UnauthenticatedMember_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.LogOnly);
        var (context, items) = CreateContext(serviceProvider, Anonymous());

        using var _ = Assert.Multiple();
        await Assert.That(await ConnectAsync(context)).IsTrue();
        // Nothing to record — and the hub must therefore refuse the registration under Enforce.
        await Assert.That(items.ContainsKey(AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey))
            .IsFalse();
    }

    /// <summary>
    ///     A valid token is not enough. The channel is a write surface — the member registers itself
    ///     and reports results — so a read-only or front-end token must not open it.
    /// </summary>
    [Test]
    public async Task AuthenticatedWithoutTheWriteScope_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.Enforce);
        var (context, _) = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiReadOnly),
                new Claim("client_id", "some-client"),
                new Claim("tenant_id", LenderTenantId)));

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    /// <summary>
    ///     Fail closed: a token that cannot be attributed to a tenant cannot be attributed to a pool
    ///     owner either, and the whole authority model of this hub rests on knowing which tenant owns
    ///     the processes on the other end.
    /// </summary>
    [Test]
    public async Task ServiceTokenWithoutTenantClaim_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.Enforce);
        var (context, _) = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("client_id", "some-legacy-client")));

        await Assert.That(async () => await ConnectAsync(context)).Throws<HubException>();
    }

    [Test]
    public async Task ServiceTokenWithoutTenantClaim_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.LogOnly);
        var (context, _) = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("client_id", "some-legacy-client")));

        await Assert.That(await ConnectAsync(context)).IsTrue();
    }

    /// <summary>
    ///     A user token of a tenant is accepted by the connection gate — it satisfies the policy and
    ///     names a tenant. The gate is not where a human is distinguished from a process; the pool it
    ///     may register into is still bounded by the same tenant binding, and every authority inside
    ///     a borrower still comes from the lease.
    /// </summary>
    [Test]
    public async Task UserTokenOfTheLendingTenant_IsConnected_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(AdapterPoolHubAuthorizationMode.Enforce);
        var (context, items) = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("sub", "an-admin"),
                new Claim("tenant_id", LenderTenantId)));

        using var _ = Assert.Multiple();
        await Assert.That(await ConnectAsync(context)).IsTrue();
        await Assert.That(items[AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey])
            .IsEqualTo(LenderTenantId);
    }

    /// <summary>
    ///     The zero value of the enum is the migration mode, so an unbound configuration section
    ///     arrives in LogOnly — the same contract the other two gates make.
    /// </summary>
    [Test]
    public async Task DefaultMode_IsLogOnly()
    {
        using var _ = Assert.Multiple();
        await Assert.That(new AdapterPoolHubAuthorizationOptions().Mode)
            .IsEqualTo(AdapterPoolHubAuthorizationMode.LogOnly);

        var zeroValue = (AdapterPoolHubAuthorizationMode)Enum.ToObject(
            typeof(AdapterPoolHubAuthorizationMode), 0);
        await Assert.That(zeroValue).IsEqualTo(AdapterPoolHubAuthorizationMode.LogOnly);
    }
}
