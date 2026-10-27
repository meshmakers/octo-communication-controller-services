using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#7319: the host's registrations put the reason into the 403 of a refused toggle, and a caller with the role passes
/// authorization. Driven through a real TestServer pipeline with the host's policy registration and
/// AddOctoTenantRoleAuthorization(); only the authentication handler is a stand-in. A source pin proves the host itself
/// makes the same call.
/// </summary>
internal class CommunicationToggleHttpTests
{
    [Test]
    public async Task WithoutTheRole_Is403WithTheReasonBody()
    {
        await using var host = await GateHost.StartAsync();

        var response = await host.PostAsync(roles: "");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(json.RootElement.GetProperty("message").GetString()!).Contains(CommonConstants.TenantManagementRole);
        await Assert.That(host.Executions).IsEqualTo(0);
    }

    [Test]
    public async Task WithTheRole_PassesAuthorization()
    {
        await using var host = await GateHost.StartAsync();

        var response = await host.PostAsync(roles: CommonConstants.TenantManagementRole);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.Executions).IsEqualTo(1);
    }

    [Test]
    public async Task Host_RegistersTheRoleAuthorizationHandlers()
    {
        var source = await File.ReadAllTextAsync(ProgramPath());
        var code = Regex.Replace(source, @"//.*?$", string.Empty, RegexOptions.Multiline);

        await Assert.That(code).Contains("AddOctoTenantRoleAuthorization()");
        await Assert.That(code).Contains("options.AddCapabilityTogglePolicy()");
    }

    private static string ProgramPath([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", "src",
            "CommunicationControllerServices", "Program.cs"));
    }

    private sealed class TestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new("sub", "user-1"), new("scope", CommonConstants.OctoApiFullAccess) };
            claims.AddRange(Request.Headers["X-Roles"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role")), SchemeName)));
        }
    }

    private sealed class GateHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private HttpClient? _client;
        private int _executions;

        private GateHost(WebApplication app)
        {
            _app = app;
        }

        public int Executions => _executions;

        public static async Task<GateHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(TestBearerHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestBearerHandler>(TestBearerHandler.SchemeName, null);
            builder.Services.AddAuthorization(o => o.AddCapabilityTogglePolicy());
            builder.Services.AddOctoTenantRoleAuthorization();

            var app = builder.Build();
            var host = new GateHost(app);
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapPost("/probe", () =>
            {
                Interlocked.Increment(ref host._executions);
                return Results.Ok();
            }).RequireAuthorization(Constants.TenantCommunicationCapabilityTogglePolicy);

            await app.StartAsync();
            host._client = app.GetTestClient();
            return host;
        }

        public Task<HttpResponseMessage> PostAsync(string roles)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/probe");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-real-token");
            request.Headers.TryAddWithoutValidation("X-Roles", roles);
            return _client!.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
