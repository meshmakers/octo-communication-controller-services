using System.Security.Claims;
using System.Text.Encodings.Web;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;

/// <summary>
/// Options for the test authentication handler.
/// </summary>
public class TestAuthHandlerOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Test authentication handler that provides a pre-authenticated user for integration tests.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<TestAuthHandlerOptions>
{
    public const string SchemeName = "TestScheme";

    public TestAuthHandler(
        IOptionsMonitor<TestAuthHandlerOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "TestUser"),
            new(ClaimTypes.NameIdentifier, "test-user-id"),
            new(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
        };

        // Add tenant_id claim from the route tenant so TenantAuthorizationMiddleware passes.
        var pathSegments = Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments is { Length: > 0 })
        {
            claims.Add(new Claim("tenant_id", pathSegments[0]));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
