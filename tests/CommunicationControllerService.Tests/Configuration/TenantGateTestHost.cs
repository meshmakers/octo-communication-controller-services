using System.Security.Claims;
using System.Text.Encodings.Web;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#6833 — a real Kestrel host that wires the tenant gate exactly as <c>Program.cs</c> does
///     (the order is pinned against the source in <c>TenantGateProgramPinTests</c>):
///     tenant route constraint, <c>UserTokenEnforcement</c> code default first and the configuration
///     binding second, then <c>UseAuthentication → UseAuthorization → UseOctoTenantAuthorization()</c>.
///     Requests go over a real socket through the real shared middleware and the real
///     <c>{tenantId:tenantId}</c> route constraint; only the token validation is replaced by
///     <see cref="TestBearerHandler" />, because a signed JWT adds nothing to what the gate judges
///     (the claims on the authenticated principal).
/// </summary>
internal sealed class TenantGateTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _handlerCalls;

    /// <summary>How many times an endpoint handler (the "action") was executed.</summary>
    public int HandlerCalls => Volatile.Read(ref _handlerCalls);

    public HttpClient Client { get; }

    private TenantGateTestHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <param name="configuration">
    ///     The configuration the host binds <c>TenantAuthorization</c> from (what the operator sets).
    ///     <c>null</c> = no operator setting, i.e. the code default applies.
    /// </param>
    /// <param name="routes">Route templates mapped as protected endpoints (like the controllers).</param>
    public static async Task<TenantGateTestHost> StartAsync(IConfiguration? configuration, params string[] routes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddOctoTenantIdRouteConstraint();

        // The two registrations of Program.cs, in its order: code default first, binding second.
        builder.Services.AddOctoTenantAuthorization(o =>
            o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly);
        builder.Services.AddOctoTenantAuthorization(configuration ?? new ConfigurationBuilder().Build());

        builder.Services.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>("Bearer", _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        TenantGateTestHost? host = null;

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOctoTenantAuthorization();

        foreach (var route in routes)
        {
            app.MapMethods(route, ["GET", "POST", "PUT", "DELETE"], () =>
            {
                Interlocked.Increment(ref host!._handlerCalls);
                return Results.Ok(new { executed = true });
            }).RequireAuthorization();
        }

        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        host = new TenantGateTestHost(app, client);
        return host;
    }

    public static IConfiguration EnforceFromConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
            })
            .Build();
    }

    /// <summary>
    ///     A user token: carries <c>sub</c>. Encoded into the test bearer value
    ///     (<c>Bearer tenant=a;sub=x;client=y</c>) — no secret material is involved.
    /// </summary>
    public static string UserToken(string? tenantId, string subject = "user-1", string client = "octo-studio")
    {
        return Token(tenantId, subject, client);
    }

    private static string Token(string? tenantId, string? subject, string client)
    {
        var parts = new List<string> { $"client={client}" };
        if (tenantId != null)
        {
            parts.Add($"tenant={tenantId}");
        }

        if (subject != null)
        {
            parts.Add($"sub={subject}");
        }

        return string.Join(';', parts);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    ///     Stands in for the JWT bearer handler: turns the token string into a principal whose
    ///     identity is labelled <c>Bearer</c> (the label <c>ConfigureJwtBearerOptions</c> sets for the
    ///     real handler, see AB#5054). Reads the token from the <c>Authorization</c> header and — like
    ///     the real service on hub paths — from <c>?access_token=</c>.
    /// </summary>
    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? raw = null;
            var header = Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                raw = header["Bearer ".Length..];
            }
            else if (Request.Query.TryGetValue("access_token", out var query))
            {
                raw = query.ToString();
            }

            if (string.IsNullOrEmpty(raw))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>();
            foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                switch (kv[0])
                {
                    case "tenant": claims.Add(new Claim("tenant_id", kv[1])); break;
                    case "sub": claims.Add(new Claim("sub", kv[1])); break;
                    case "client": claims.Add(new Claim("client_id", kv[1])); break;
                }
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Bearer")));
        }
    }
}
