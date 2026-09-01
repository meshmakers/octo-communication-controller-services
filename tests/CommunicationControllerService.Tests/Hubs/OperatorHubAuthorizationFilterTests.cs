using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#5059 — <c>/operatorHub</c> is the tenant-crossing control plane of the operator fleet (pool
///     claims, workload deploy / scale acknowledgements, tenant lifecycle fan-out) and carried no
///     authorization at all: no <c>[Authorize]</c> on the hub, no <c>FallbackPolicy</c>, no
///     <c>RequireAuthorization()</c> on the endpoint.
///     <para>
///         The gate applies the service's own <see cref="Constants.SystemCommunicationApiPolicy" /> and
///         is staged, because the operator SDK sends the literal string
///         <c>"Bearer your-access-token"</c> — arming it today would disconnect every operator in the
///         estate. These tests pin both halves: the decision, and the fact that the default mode does
///         not change any outcome.
///     </para>
/// </summary>
internal class OperatorHubAuthorizationFilterTests
{
    private const string ConnectionId = "conn-operator-1";

    private sealed class TestHub : Hub;

    private static ServiceProvider BuildServiceProvider(OperatorHubAuthorizationMode mode)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options =>
            {
                // Mirrors the registration in Program.cs one for one.
                options.AddPolicy(Constants.SystemCommunicationApiPolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope,
                        CommonConstants.OctoApiFullAccess));
            })
            .Configure<OperatorHubAuthorizationOptions>(o => o.Mode = mode)
            .BuildServiceProvider();
    }

    private static HubLifetimeContext CreateContext(IServiceProvider serviceProvider, ClaimsPrincipal? user)
    {
        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns(ConnectionId);
        callerContext.User.Returns(user);
        // GetHttpContext() reads IHttpContextFeature; an empty collection makes the bearer fallback a
        // no-op instead of a NullReferenceException.
        callerContext.Features.Returns(new FeatureCollection());

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

    [Test]
    public async Task ScopedOperator_IsConnected_InEveryMode()
    {
        foreach (var mode in new[]
                 {
                     OperatorHubAuthorizationMode.LogOnly, OperatorHubAuthorizationMode.Enforce
                 })
        {
            await using var serviceProvider = BuildServiceProvider(mode);
            var context = CreateContext(serviceProvider,
                Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                    new Claim("client_id", "octo-operator")));

            var connected = false;
            await new OperatorHubAuthorizationFilter().OnConnectedAsync(context, _ =>
            {
                connected = true;
                return Task.CompletedTask;
            });

            await Assert.That(connected).IsTrue();
        }
    }

    /// <summary>
    ///     🔴 The staging contract. Today's operator fleet lands exactly here — an unauthenticated
    ///     connection — and it must keep connecting until the SDK is given a credential. A test that
    ///     went red on this line would mean the estate had been disconnected.
    /// </summary>
    [Test]
    public async Task UnauthenticatedOperator_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(OperatorHubAuthorizationMode.LogOnly);
        var context = CreateContext(serviceProvider, Anonymous());

        var connected = false;
        await new OperatorHubAuthorizationFilter().OnConnectedAsync(context, _ =>
        {
            connected = true;
            return Task.CompletedTask;
        });

        await Assert.That(connected).IsTrue();
    }

    [Test]
    public async Task UnauthenticatedOperator_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(OperatorHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider, Anonymous());

        var connected = false;

        await Assert.That(async () => await new OperatorHubAuthorizationFilter()
                .OnConnectedAsync(context, _ =>
                {
                    connected = true;
                    return Task.CompletedTask;
                }))
            .Throws<HubException>();

        await Assert.That(connected).IsFalse();
    }

    /// <summary>
    ///     A valid token is not enough — a front-end <c>openid profile</c> token, or a token carrying
    ///     only the read-only API scope, must not be able to claim pools or acknowledge deployments.
    ///     This is the same scope rule the service's <c>system/v{version}</c> routes apply.
    /// </summary>
    [Test]
    public async Task AuthenticatedWithoutTheSystemScope_IsRefused_WhenEnforcing()
    {
        await using var serviceProvider = BuildServiceProvider(OperatorHubAuthorizationMode.Enforce);
        var context = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiReadOnly),
                new Claim("sub", "some-user")));

        await Assert.That(async () => await new OperatorHubAuthorizationFilter()
                .OnConnectedAsync(context, _ => Task.CompletedTask))
            .Throws<HubException>();
    }

    [Test]
    public async Task AuthenticatedWithoutTheSystemScope_IsStillConnected_InLogOnly()
    {
        await using var serviceProvider = BuildServiceProvider(OperatorHubAuthorizationMode.LogOnly);
        var context = CreateContext(serviceProvider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiReadOnly)));

        var connected = false;
        await new OperatorHubAuthorizationFilter().OnConnectedAsync(context, _ =>
        {
            connected = true;
            return Task.CompletedTask;
        });

        await Assert.That(connected).IsTrue();
    }

    /// <summary>
    ///     The zero value of the enum is the migration mode, so an unbound configuration section
    ///     arrives in LogOnly rather than disconnecting the fleet on first deploy of this change.
    /// </summary>
    [Test]
    public async Task DefaultMode_IsLogOnly()
    {
        await Assert.That(new OperatorHubAuthorizationOptions().Mode)
            .IsEqualTo(OperatorHubAuthorizationMode.LogOnly);

        // The zero value matters independently of the property initializer: a binder that writes a
        // default(TEnum) over the initializer must still land in the migration mode.
        var zeroValue = (OperatorHubAuthorizationMode)Enum.ToObject(
            typeof(OperatorHubAuthorizationMode), 0);
        await Assert.That(zeroValue).IsEqualTo(OperatorHubAuthorizationMode.LogOnly);
    }
}
