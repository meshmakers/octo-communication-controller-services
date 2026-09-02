using System.Security.Claims;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     Identity of a SignalR connection, shared by the two hub gates
///     (<see cref="OperatorHubAuthorizationFilter" />, AB#5059, and
///     <see cref="AdapterHubAuthorizationFilter" />, AB#5063).
/// </summary>
/// <remarks>
///     Deliberately one implementation rather than one per hub: both gates stand or fall with reading
///     the caller correctly, and a second copy that drifted would not fail loudly — it would quietly
///     report "anonymous" and produce a clean-looking, worthless inventory.
/// </remarks>
internal static class HubConnectionPrincipal
{
    /// <summary>
    ///     Claim type of the OAuth client id, as issued by the identity service.
    /// </summary>
    public const string ClientIdClaimType = "client_id";

    /// <summary>
    ///     Claim type of the subject. Its presence is what distinguishes a user token from a
    ///     client-credentials token — the same test <c>TenantAuthorizationMiddleware</c> makes,
    ///     including the mapped <see cref="ClaimTypes.NameIdentifier" /> alternative the JWT handler
    ///     produces when <c>MapInboundClaims</c> is on.
    /// </summary>
    public const string SubjectClaimType = "sub";

    /// <summary>
    ///     Claim type carrying the tenant a token was issued for. AB#5032 stamps it on
    ///     client-credentials tokens too.
    /// </summary>
    public const string TenantIdClaimType = "tenant_id";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     The connection's principal.
    /// </summary>
    /// <remarks>
    ///     <c>Context.User</c> is what <c>app.UseAuthentication()</c> left on the negotiate request, so
    ///     it depends on the default authenticate scheme being resolvable. The bearer scheme is
    ///     authenticated explicitly as a fallback so a gate — and above all its LogOnly inventory —
    ///     cannot silently report "anonymous" for a caller that did present a valid token. That
    ///     silent-no-op is the exact failure mode AB#5054 had to repair on the HTTP gate of this same
    ///     service.
    /// </remarks>
    public static async Task<ClaimsPrincipal?> ResolveAsync(HubLifetimeContext context)
    {
        var user = context.Context.User;
        if (user?.Identity is { IsAuthenticated: true })
        {
            return user;
        }

        var httpContext = context.Context.GetHttpContext();
        if (httpContext == null)
        {
            return user;
        }

        try
        {
            var result = await httpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            return result.Succeeded && result.Principal != null ? result.Principal : user;
        }
        catch (Exception e)
        {
            Logger.Debug(e, "Could not authenticate the bearer scheme for a hub connection");
            return user;
        }
    }

    /// <summary>
    ///     Caller description for the inventory log: connection id, <c>client_id</c>, <c>sub</c> and
    ///     the token's scopes.
    /// </summary>
    public static string Describe(ClaimsPrincipal? user, string connectionId)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return $"connection '{connectionId}', unauthenticated";
        }

        var clientId = user.FindFirst(ClientIdClaimType)?.Value ?? "<none>";
        var subject = user.FindFirst(SubjectClaimType)?.Value
                      ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "<none>";
        var scopes = string.Join(' ', user.FindAll(InfrastructureCommon.ClaimScope).Select(c => c.Value));

        return $"connection '{connectionId}', client_id '{clientId}', sub '{subject}', " +
               $"scopes '{(string.IsNullOrWhiteSpace(scopes) ? "<none>" : scopes)}'";
    }

    /// <summary>
    ///     Whether <paramref name="user" /> presented a client-credentials token, i.e. one with no
    ///     subject.
    /// </summary>
    public static bool IsServiceToken(ClaimsPrincipal user)
    {
        return !user.HasClaim(c => c.Type == SubjectClaimType || c.Type == ClaimTypes.NameIdentifier);
    }
}
