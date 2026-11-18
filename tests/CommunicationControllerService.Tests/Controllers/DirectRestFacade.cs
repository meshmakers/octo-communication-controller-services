using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
///     The controller unit tests check the REST behaviour of an endpoint itself. Since AB#9048/AB#9049 every operation endpoint
///     goes through <see cref="ICommunicationMethodFacade" />; these tests attach the real facade with the method host switched
///     off, so the call takes the guarded direct path - the REST code the tests describe - behind the caller-tenant check and
///     the audit event. The caller gets the claims a real access token of the route tenant carries.
/// </summary>
internal static class DirectRestFacade
{
    /// <summary>Attaches the facade to <paramref name="httpContext" /> and makes its user a caller of <paramref name="tenantId" />.</summary>
    public static ICkMethodAuditRecorder Attach(HttpContext httpContext, string tenantId)
    {
        var recorder = Substitute.For<ICkMethodAuditRecorder>();
        var facade = new CommunicationMethodFacade(new ServiceCollection().BuildServiceProvider(),
            Substitute.For<ICkMethodAuthorizationDefinitionSource>(),
            new CkMethodAuthorizationEvaluator(Microsoft.Extensions.Options.Options.Create(new CkMethodAuthorizationOptions())), recorder,
            Microsoft.Extensions.Options.Options.Create(new CommunicationMethodsOptions { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new TenantAuthorizationOptions()), Substitute.For<IHostApplicationLifetime>(),
            NullLogger<CommunicationMethodFacade>.Instance);
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton<ICommunicationMethodFacade>(facade)
            .BuildServiceProvider();

        var claims = httpContext.User.Claims.ToList();
        if (claims.All(c => c.Type != "sub"))
        {
            claims.Add(new Claim("sub", "rest-user"));
        }

        claims.RemoveAll(c => c.Type == "tenant_id");
        claims.Add(new Claim("tenant_id", tenantId));
        claims.Add(new Claim("scope", "octo_api"));
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
        return recorder;
    }
}
