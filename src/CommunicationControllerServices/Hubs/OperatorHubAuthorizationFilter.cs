using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
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
///         🔴 It runs in <see cref="OperatorHubAuthorizationMode.LogOnly" /> by default. It was written
///         when the SDK sent the literal string <c>"Bearer your-access-token"</c>; AB#5062 has since
///         replaced that with an <c>AccessTokenProvider</c> and given the operator an
///         <c>OperatorAccessTokenService</c> that fills it, so the precondition named in
///         <see cref="OperatorHubAuthorizationOptions" /> may no longer hold. Whether this hub can be
///         armed is AB#5062's question — read the <c>LogOnly</c> inventory of an environment before
///         answering it there.
///     </para>
///     <para>
///         Registered per hub via <c>AddHubOptions&lt;OperatorHub&gt;</c> in <c>Program.cs</c>, not
///         globally: <c>AdapterHub</c> is tenant-addressed and needs a tenant-binding check this filter
///         does not make, plus its own consumer inventory. It has its own gate since AB#5063 — see
///         <see cref="AdapterHubAuthorizationFilter" />.
///     </para>
///     <para>
///         The filter resolves its dependencies from the connection's own
///         <see cref="HubLifetimeContext.ServiceProvider" /> rather than through its constructor,
///         because SignalR caches filter instances for the lifetime of the host while
///         <c>IAuthorizationService</c> is transient. Reading the caller — the principal fallback and
///         the shape of the inventory line — lives in <see cref="HubConnectionPrincipal" />, shared
///         with the adapter gate.
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

        var user = await HubConnectionPrincipal.ResolveAsync(context);
        var authorized = user != null &&
                         (await authorizationService.AuthorizeAsync(user, null,
                             Constants.SystemCommunicationApiPolicy)).Succeeded;

        if (authorized)
        {
            await next(context);
            return;
        }

        var caller = HubConnectionPrincipal.Describe(user, context.Context.ConnectionId);

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
}
