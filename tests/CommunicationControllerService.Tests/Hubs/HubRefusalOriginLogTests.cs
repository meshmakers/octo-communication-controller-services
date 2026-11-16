using System.Net;
using System.Security.Claims;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#9030 — a hub refusal must say where the connection came from (remote address,
///     <c>X-Forwarded-For</c>, user agent, path, the unverified <c>client_id</c> of a rejected token) so an
///     operator can find the sender, and it must never log the token itself.
/// </summary>
internal class HubRefusalOriginLogTests
{
    private const string ConnectionId = "conn-origin-1";
    private const string TokenSignature = "SIGNATURE-PART-MUST-NOT-APPEAR";

    private sealed class TestHub : Hub;

    private static string Base64Url(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string FakeJwt(string payloadJson)
    {
        return $"{Base64Url("{\"alg\":\"RS256\"}")}.{Base64Url(payloadJson)}.{TokenSignature}";
    }

    private static HubLifetimeContext CreateContext(ServiceProvider services, ClaimsPrincipal? user,
        Action<HttpContext>? configureHttp)
    {
        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns(ConnectionId);
        callerContext.User.Returns(user);

        var features = new FeatureCollection();
        if (configureHttp != null)
        {
            var http = new DefaultHttpContext();
            configureHttp(http);
            features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = http });
        }

        callerContext.Features.Returns(features);
        return new HubLifetimeContext(callerContext, services, new TestHub());
    }

    private sealed class HttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static ServiceProvider BuildServices()
    {
        return new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddPolicy(Constants.SystemCommunicationApiPolicy, policy =>
                policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)))
            .Configure<OperatorHubAuthorizationOptions>(o => o.Mode = OperatorHubAuthorizationMode.Enforce)
            .BuildServiceProvider();
    }

    private static async Task<string> RefusedLineAsync(Action<HttpContext> configureHttp)
    {
        var memoryTarget = new NLog.Targets.MemoryTarget("hub-origin-probe") { Layout = "${message}" };
        var previous = NLog.LogManager.Configuration;
        var probe = new NLog.Config.LoggingConfiguration();
        probe.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probe;
        try
        {
            await using var services = BuildServices();
            var context = CreateContext(services, new ClaimsPrincipal(new ClaimsIdentity()), configureHttp);
            await Assert.That(async () => await new OperatorHubAuthorizationFilter()
                .OnConnectedAsync(context, _ => Task.CompletedTask)).Throws<HubException>();
            return memoryTarget.Logs.Single(l => l.StartsWith("Refused an operator connection", StringComparison.Ordinal) && l.Contains(ConnectionId));
        }
        finally
        {
            NLog.LogManager.Configuration = previous;
        }
    }

    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task OperatorRefusal_NamesAddressForwardedChainUserAgentAndPath()
    {
        var line = await RefusedLineAsync(http =>
        {
            http.Request.Path = "/operatorHub";
            http.Connection.RemoteIpAddress = IPAddress.Parse("10.244.1.7");
            http.Request.Headers["X-Forwarded-For"] = "203.0.113.9, 10.0.0.2";
            http.Request.Headers.UserAgent = "Mozilla/5.0 probe";
        });

        using var _ = Assert.Multiple();
        await Assert.That(line).Contains("Refused an operator connection");
        await Assert.That(line).Contains(ConnectionId);
        await Assert.That(line).Contains("path '/operatorHub'");
        await Assert.That(line).Contains("remote address '10.244.1.7'");
        await Assert.That(line).Contains("x-forwarded-for '203.0.113.9, 10.0.0.2'");
        await Assert.That(line).Contains("user agent 'Mozilla/5.0 probe'");
        await Assert.That(line).Contains("no token presented");
        await Assert.That(line).Contains("reason code unauthenticated");
    }

    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task OperatorRefusal_NamesTheUnverifiedClientIdOfARejectedToken_NeverTheToken()
    {
        var token = FakeJwt("{\"client_id\":\"octo-communication-operator\",\"exp\":1}");
        var line = await RefusedLineAsync(http =>
        {
            http.Request.Path = "/operatorHub";
            http.Request.Headers.Authorization = "Bearer " + token;
        });

        using var _ = Assert.Multiple();
        await Assert.That(line).Contains("client_id 'octo-communication-operator' (unverified");
        await Assert.That(line).DoesNotContain(token);
        await Assert.That(line).DoesNotContain(TokenSignature);
        await Assert.That(line).DoesNotContain(token.Split('.')[1]);
    }

    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task OperatorRefusal_ReadsTheQueryStringTokenAndNeverLogsIt()
    {
        var token = FakeJwt("{\"client_id\":\"some-client\"}");
        var line = await RefusedLineAsync(http =>
            http.Request.QueryString = new QueryString("?access_token=" + token));

        using var _ = Assert.Multiple();
        await Assert.That(line).Contains("client_id 'some-client' (unverified");
        await Assert.That(line).DoesNotContain(TokenSignature);
    }

    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task OperatorRefusal_StripsControlCharactersAndCapsLength()
    {
        var line = await RefusedLineAsync(http =>
        {
            http.Request.Headers.UserAgent = "evil\r\nINJECTED line" + new string('x', 500);
            http.Request.Headers["X-Forwarded-For"] = "1.2.3.4\nfake";
        });

        using var _ = Assert.Multiple();
        await Assert.That(line).DoesNotContain("\r");
        await Assert.That(line).DoesNotContain("\n");
        await Assert.That(line).Contains("evil  INJECTED line");
        await Assert.That(line).DoesNotContain(new string('x', 250));
        await Assert.That(line).Contains("...");
    }

    [Test]
    public async Task UnreadableToken_IsReportedAsSuch()
    {
        using var _ = Assert.Multiple();
        await Assert.That(HubConnectionPrincipal.ReadUnverifiedClientId("not-a-jwt")).IsNull();
        await Assert.That(HubConnectionPrincipal.ReadUnverifiedClientId("a.!!!.c")).IsNull();
        await Assert.That(HubConnectionPrincipal.ReadUnverifiedClientId(FakeJwt("{\"sub\":\"x\"}"))).IsNull();
        await Assert.That(HubConnectionPrincipal.ReadUnverifiedClientId(FakeJwt("[]"))).IsNull();
        await Assert.That(HubConnectionPrincipal.ReadUnverifiedClientId(FakeJwt("{\"client_id\":\"ok\"}")))
            .IsEqualTo("ok");
    }

    [Test]
    public async Task NoHttpContext_DoesNotThrow()
    {
        await using var services = BuildServices();
        var context = CreateContext(services, null, null);

        await Assert.That(HubConnectionPrincipal.DescribeOrigin(context, null)).Contains("origin unknown");
    }
}
