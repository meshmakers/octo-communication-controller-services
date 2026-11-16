using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
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

    private const int MaxLoggedHeaderLength = 200;
    private const int MaxTokenLengthToInspect = 16 * 1024;

    /// <summary>
    ///     Where a connection came from, for the refusal and inventory log lines (AB#9030): the remote
    ///     address, the <c>X-Forwarded-For</c> chain, the user agent, the request path and — when the caller
    ///     presented a bearer token that was NOT accepted — the unverified <c>client_id</c> inside it.
    /// </summary>
    /// <remarks>
    ///     Without this a refusal names only the connection id, so an operator cannot tell a probing client
    ///     from a misconfigured one. Behind the ingress <c>RemoteIpAddress</c> is the ingress pod; the
    ///     client is in <c>X-Forwarded-For</c> (every hop appends, so only the right-most entries are
    ///     trustworthy; the line carries the whole chain and says so). Everything here is
    ///     caller-controlled text and is stripped of control characters and truncated before it is logged.
    ///     <b>Never the token</b>: only the <c>client_id</c> claim of an unvalidated token is read, and it is
    ///     labelled unverified. An accepted principal already carries a validated <c>client_id</c>
    ///     (<see cref="Describe" />), so no token is inspected for it.
    /// </remarks>
    public static string DescribeOrigin(HubLifetimeContext context, ClaimsPrincipal? user)
    {
        var http = context.Context.GetHttpContext();
        if (http == null)
        {
            return "origin unknown (no HTTP context)";
        }

        var remote = http.Connection.RemoteIpAddress?.ToString() ?? "<none>";
        var forwarded = Sanitize(http.Request.Headers["X-Forwarded-For"].ToString());
        var userAgent = Sanitize(http.Request.Headers.UserAgent.ToString());
        var path = Sanitize(http.Request.Path.Value);

        var origin = $"path '{path}', remote address '{remote}', x-forwarded-for '{forwarded}', " +
                     $"user agent '{userAgent}'";

        if (user?.Identity is not { IsAuthenticated: true })
        {
            origin += TryDescribeRejectedToken(http);
        }

        return origin;
    }

    private static string TryDescribeRejectedToken(HttpContext http)
    {
        var token = ExtractBearerToken(http);
        if (token == null)
        {
            return ", no token presented";
        }

        var clientId = ReadUnverifiedClientId(token);
        return clientId == null
            ? ", token presented but not readable as a JWT"
            : $", token presented and not accepted, client_id '{Sanitize(clientId)}' (unverified, read from the token without validation)";
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        const string prefix = "Bearer ";
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header[prefix.Length..].Trim();
        }

        // Browsers and the WebSocket transport send the token as access_token on the query string.
        var query = http.Request.Query["access_token"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }

    /// <summary>
    ///     The <c>client_id</c> claim of a JWT payload, read without validating anything; <c>null</c> when
    ///     the value is not a JWT or carries none. Reads the payload segment only.
    /// </summary>
    internal static string? ReadUnverifiedClientId(string token)
    {
        if (token.Length > MaxTokenLengthToInspect)
        {
            return null;
        }

        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(ClientIdClaimType, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Makes caller-controlled text safe for one log line: control characters (log forging) become
    ///     spaces, length is capped, empty becomes <c>&lt;none&gt;</c>.
    /// </summary>
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<none>";
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLoggedHeaderLength));
        foreach (var c in value.Take(MaxLoggedHeaderLength))
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return value.Length > MaxLoggedHeaderLength ? builder + "..." : builder.ToString();
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
