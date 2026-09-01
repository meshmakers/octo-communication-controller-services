using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

/// <summary>
///     Configures the JWT bearer scheme this service authenticates its API callers with.
/// </summary>
/// <remarks>
///     🔴 <b>This is the only configurator of the bearer scheme. Keep it that way (AB#5054).</b>
///     <c>Program.cs</c> used to add a second one through <c>AddJwtBearer(jwt =&gt; …)</c> that
///     assigned a brand-new <c>TokenValidationParameters</c>. The options factory runs configurators
///     in registration order, so that assignment ran last and silently discarded everything set
///     here — the explicit <c>ValidIssuer</c>, and the <c>AuthenticationType</c> below that a
///     security gate depends on. Nothing about that failure is visible: it compiles, and a unit test
///     of this class in isolation still passes. octo-ai-services shipped a full release in exactly
///     that state (AB#5051 → AB#5056).
/// </remarks>
internal class ConfigureJwtBearerOptions(
    IOptions<CommunicationControllerOptions> communicationControllerOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        var authorityUrl = communicationControllerOptions.Value.AuthorityUrl.EnsureEndsWith("/");
        options.Authority = authorityUrl;
        options.Audience = CommonConstants.OctoApi;

        options.TokenValidationParameters.NameClaimType = JwtClaimTypes.Name;
        options.TokenValidationParameters.RoleClaimType = JwtClaimTypes.Role;

        // Explicitly set the valid issuer so token validation does not depend on fetching
        // the OIDC discovery document. This prevents IDX10204 errors when the identity
        // service is temporarily unreachable (e.g. during rolling updates).
        options.TokenValidationParameters.ValidIssuer = authorityUrl;

        // AB#5054 — label the authenticated identity "Bearer" so TenantAuthorizationMiddleware
        // (UseOctoTenantAuthorization(), AB#5032/AB#5047) actually runs its route-tenant vs
        // tenant_id check. The middleware deliberately skips principals whose AuthenticationType
        // is not "Bearer" to avoid false 403s on cookie/OIDC principals — and the JWT handler's
        // default label is "AuthenticationTypes.Federation", not "Bearer", so without this line
        // the whole gate (user path AND the AB#5032 service-token audit log) is a silent no-op on
        // every bearer request. Same fix as octo-mcp-service (AB#4315) and octo-ai-services
        // (AB#5051/AB#5056).
        options.TokenValidationParameters.AuthenticationType = JwtBearerDefaults.AuthenticationScheme;
    }
}
