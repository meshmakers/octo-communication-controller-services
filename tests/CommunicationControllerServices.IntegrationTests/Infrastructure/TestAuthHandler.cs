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
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "TestUser"),
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.CommunicationSystemApiFullAccess),
            new Claim(InfrastructureCommon.ClaimScope, CommonConstants.CommunicationTenantApiFullAccess),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
