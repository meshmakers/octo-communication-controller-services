using System.Security.Claims;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>
///     The caller of the System.Communication method a handler is running, for the services that judge "the caller of this
///     request" through the ambient HTTP request (AB#9048, Kassandra round 1): the elevation gate of a pipeline deploy
///     (<c>AdapterService.EnsurePipelineElevationAuthorizedAsync</c>, AB#5128: an elevated pipeline needs <c>UserManagement</c>)
///     and the identity-client lookups (<c>IdentityClientReader</c>, AB#5027: they forward the caller's bearer token).
/// </summary>
/// <remarks>
///     <para>
///         A handler runs without an HTTP request: on the in-process path the transport detaches it from the request (no
///         <c>HttpContext</c> reaches it), on the bus there never was one. Without this scope those services saw "no caller"
///         and took their system path (an elevated deploy was allowed, an identity lookup was skipped) - for a REST call that
///         a facade routes through the method that would widen a right. Every handler of <see cref="CommunicationMethodHost" />
///         opens the scope first, from the validated caller of the call (<see cref="CkMethodContext.CallerPrincipal" />, its
///         roles) and its forwarded token, so the services judge the same caller as on the REST path.
///     </para>
///     <para>
///         The services read the ambient HTTP request first and this scope only when there is none, so a REST call keeps
///         its own caller. Background paths (tenant start, post-provisioning redeploy) open no scope and stay system paths.
///     </para>
/// </remarks>
internal sealed class CommunicationMethodCaller
{
    private static readonly AsyncLocal<CommunicationMethodCaller?> Ambient = new();

    private CommunicationMethodCaller(ClaimsPrincipal user, string? accessToken)
    {
        User = user;
        AccessToken = accessToken;
    }

    /// <summary>The caller of the running method, or <c>null</c> outside a handler.</summary>
    public static CommunicationMethodCaller? Current => Ambient.Value;

    /// <summary>The caller as a principal: subject and role claims of the validated caller.</summary>
    public ClaimsPrincipal User { get; }

    /// <summary>The caller's forwarded access token, or <c>null</c>. Sensitive: never logged.</summary>
    public string? AccessToken { get; }

    /// <summary>Makes the caller of <paramref name="context" /> the ambient caller until the scope is disposed.</summary>
    public static IDisposable Begin(CkMethodContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Begin(context.CallerPrincipal, context.CallerAccessToken);
    }

    /// <summary>
    ///     Makes <paramref name="principal" /> the ambient caller. A call without a validated caller (the host refuses it before
    ///     the handler) gets an authenticated principal without roles: never the system path.
    /// </summary>
    public static IDisposable Begin(CkMethodPrincipal? principal, string? accessToken)
    {
        var claims = new List<Claim>();
        if (principal?.Subject is { Length: > 0 } subject)
        {
            claims.Add(new Claim("sub", subject));
        }

        claims.AddRange((principal?.Roles ?? []).Select(role => new Claim("role", role)));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "CkMethodCaller", "sub", "role"));

        var previous = Ambient.Value;
        Ambient.Value = new CommunicationMethodCaller(user, string.IsNullOrWhiteSpace(accessToken) ? null : accessToken);
        return new Restore(previous);
    }

    private sealed class Restore(CommunicationMethodCaller? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
