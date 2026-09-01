using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     Connection gate of <c>/operatorHub</c> (AB#5059).
/// </summary>
/// <remarks>
///     Applies <see cref="Constants.SystemCommunicationApiPolicy" /> — the policy the service's own
///     <c>system/v{version}</c> routes already use — to every incoming operator connection. Why that
///     policy and not a new one: the hub is not tenant-scoped (one operator process, one connection,
///     any number of tenants and pools), and everything it does — claiming pools, reporting deploy and
///     scale outcomes, receiving tenant lifecycle events — is a system-level write. That is exactly
///     what <c>SystemCommunicationApiPolicy</c> describes (<c>RequireClaim(scope, octo_api)</c>).
///     <para>
///         🔴 It runs in <see cref="OperatorHubAuthorizationMode.LogOnly" /> by default and must stay
///         there until the operator actually sends a token — the SDK currently sends the literal string
///         <c>"Bearer your-access-token"</c>. See <see cref="OperatorHubAuthorizationOptions" /> for the
///         full reasoning and for how to arm it.
///     </para>
///     <para>
///         Registered per hub via <c>AddHubOptions&lt;OperatorHub&gt;</c> in <c>Program.cs</c>, not
///         globally: <c>AdapterHub</c> is authenticated by the same absent mechanism and closing it is a
///         separate exercise with its own consumer inventory (the mesh adapter fleet plus Studio's
///         pipeline debugger).
///     </para>
///     <para>
///         The filter resolves its dependencies from the connection's own
///         <see cref="HubLifetimeContext.ServiceProvider" /> rather than through its constructor,
///         because SignalR caches filter instances for the lifetime of the host while
///         <c>IAuthorizationService</c> is transient.
///     </para>
/// </remarks>
internal class OperatorHubAuthorizationFilter : IHubFilter
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var options = context.ServiceProvider
            .GetRequiredService<IOptions<OperatorHubAuthorizationOptions>>().Value;
        var authorizationService = context.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var user = await ResolvePrincipalAsync(context);
        var authorized = user != null &&
                         (await authorizationService.AuthorizeAsync(user, null,
                             Constants.SystemCommunicationApiPolicy)).Succeeded;

        if (authorized)
        {
            await next(context);
            return;
        }

        var caller = Describe(user, context.Context.ConnectionId);

        if (options.Mode == OperatorHubAuthorizationMode.Enforce)
        {
            Logger.Warn(
                "Refused an operator connection to /operatorHub that does not satisfy '{PolicyName}': {Caller}",
                Constants.SystemCommunicationApiPolicy, caller);
            throw new HubException(
                $"Operator connection refused: the caller does not satisfy '{Constants.SystemCommunicationApiPolicy}'.");
        }

        // LogOnly — this line IS the consumer inventory. Read it before arming Enforce anywhere.
        Logger.Warn(
            "Operator connection to /operatorHub does not satisfy '{PolicyName}' and would be refused when " +
            "OperatorHubAuthorization:Mode is Enforce: {Caller}",
            Constants.SystemCommunicationApiPolicy, caller);

        await next(context);
    }

    /// <summary>
    ///     The connection's principal.
    /// </summary>
    /// <remarks>
    ///     <c>Context.User</c> is what <c>app.UseAuthentication()</c> left on the negotiate request, so
    ///     it depends on the default authenticate scheme being resolvable. The bearer scheme is
    ///     authenticated explicitly as a fallback so the gate — and above all the LogOnly inventory —
    ///     cannot silently report "anonymous" for a caller that did present a valid token. That
    ///     silent-no-op is the exact failure mode AB#5054 had to repair on the HTTP gate of this same
    ///     service.
    /// </remarks>
    private static async Task<ClaimsPrincipal?> ResolvePrincipalAsync(HubLifetimeContext context)
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
            Logger.Debug(e, "Could not authenticate the bearer scheme for an /operatorHub connection");
            return user;
        }
    }

    private static string Describe(ClaimsPrincipal? user, string connectionId)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return $"connection '{connectionId}', unauthenticated";
        }

        var clientId = user.FindFirst("client_id")?.Value ?? "<none>";
        var subject = user.FindFirst("sub")?.Value
                      ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "<none>";
        var scopes = string.Join(' ', user.FindAll(Meshmakers.Octo.Services.Infrastructure
            .InfrastructureCommon.ClaimScope).Select(c => c.Value));

        return $"connection '{connectionId}', client_id '{clientId}', sub '{subject}', " +
               $"scopes '{(string.IsNullOrWhiteSpace(scopes) ? "<none>" : scopes)}'";
    }
}
