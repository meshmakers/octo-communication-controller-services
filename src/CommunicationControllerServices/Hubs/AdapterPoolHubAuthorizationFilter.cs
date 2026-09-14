using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     Connection gate of <c>/adapterPoolHub</c> (AB#4924, concept §8 Q4).
/// </summary>
/// <remarks>
///     <para>
///         Two checks, both governed by the single <see cref="AdapterPoolHubAuthorizationOptions.Mode" />
///         switch:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>Authentication.</b> <see cref="Constants.TenantCommunicationApiReadWritePolicy" />
///                 — the same policy <c>/{tenantId}/adapterHub</c> uses, because a pool member is an
///                 adapter data-plane connection: it registers itself, reports results and is handed
///                 pipeline configuration. <c>SystemCommunicationApiPolicy</c> was considered and
///                 rejected as too much authority for the job (concept §4).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Tenant binding.</b> The connection must carry a <c>tenant_id</c>; that tenant is
///                 remembered on the connection as <see cref="ConnectionTenantIdItemKey" /> and is the
///                 <b>lending</b> tenant the member may register a pool for. The route carries no
///                 tenant — that is the point of this hub — so the binding is against the token and
///                 is completed by <see cref="AdapterPoolHub.RegisterPoolMemberAsync" />, which is
///                 the first moment a declared pool tenant exists to compare against.
///             </description>
///         </item>
///     </list>
///     <para>
///         🔴 <b>The borrower is not part of this.</b> A pool member's connection proves membership of
///         a pool owned by one tenant, nothing more. Every authority it exercises inside a
///         <i>borrowing</i> tenant arrives on the lease as that tenant's own service-account
///         credential and dies with the lease. Widening this gate to cover borrowers — for instance by
///         accepting a system token — would turn every pool member into a standing cross-tenant
///         credential, which is the opposite of what leasing is for.
///     </para>
///     <para>
///         The cross-tenant service-client allow-list of <see cref="TenantAuthorizationOptions" /> is
///         deliberately <b>not</b> consulted here. It exists so a named platform client may address a
///         tenant other than its own; a pool member addresses no tenant at connect time, and at
///         registration time the only question is whether it belongs to the pool's owner. Reading the
///         list here would create a second, differently-shaped meaning for the same option.
///     </para>
///     <para>
///         Registered per hub via <c>AddHubOptions&lt;AdapterPoolHub&gt;</c> in <c>Program.cs</c>.
///         Like the other two it resolves its dependencies from the connection's own
///         <see cref="HubLifetimeContext.ServiceProvider" /> rather than through its constructor,
///         because SignalR caches filter instances for the lifetime of the host while
///         <c>IAuthorizationService</c> is transient.
///     </para>
/// </remarks>
internal class AdapterPoolHubAuthorizationFilter : IHubFilter
{
    /// <summary>
    ///     Key under which the connection's token tenant is stored on <c>HubCallerContext.Items</c>.
    /// </summary>
    /// <remarks>
    ///     🔴 Written here rather than re-derived in the hub, because deriving it is not free of
    ///     judgement: <c>Context.User</c> is only populated when the default authenticate scheme
    ///     resolved, and <see cref="HubConnectionPrincipal.ResolveAsync" /> falls back to an explicit
    ///     bearer authentication precisely so a gate cannot silently report "anonymous" for a caller
    ///     that did present a token. Two copies of that logic would drift, and the copy that drifted
    ///     would fail open.
    /// </remarks>
    public const string ConnectionTenantIdItemKey = "octo.adapterPool.connectionTenantId";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     The tenant this connection's token was issued for, or <c>null</c> when it presented none.
    /// </summary>
    public static string? GetConnectionTenantId(HubCallerContext context)
    {
        return context.Items.TryGetValue(ConnectionTenantIdItemKey, out var value) ? value as string : null;
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var options = context.ServiceProvider
            .GetRequiredService<IOptions<AdapterPoolHubAuthorizationOptions>>().Value;

        var user = await HubConnectionPrincipal.ResolveAsync(context);
        var tokenTenantId = user?.FindFirst(HubConnectionPrincipal.TenantIdClaimType)?.Value;
        if (!string.IsNullOrEmpty(tokenTenantId))
        {
            // Recorded even in LogOnly: the hub's own registration check reads it, and a LogOnly run
            // must produce the same inventory an enforcing run would act on.
            context.Context.Items[ConnectionTenantIdItemKey] = tokenTenantId;
        }

        var refusal = await RefusalReasonAsync(context, user);
        if (refusal == null)
        {
            await next(context);
            return;
        }

        var caller = $"{HubConnectionPrincipal.Describe(user, context.Context.ConnectionId)}, " +
                     $"token tenant '{(string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId)}'";

        if (options.Mode == AdapterPoolHubAuthorizationMode.Enforce)
        {
            Logger.Warn("Refused a pool-member connection to /adapterPoolHub: {Reason}: {Caller}",
                refusal, caller);
            throw new HubException($"Adapter pool connection refused: {refusal}.");
        }

        // LogOnly — this line IS the consumer inventory. Read it before arming Enforce anywhere.
        Logger.Warn(
            "Pool-member connection to /adapterPoolHub {Reason} and would be refused when " +
            "AdapterPoolHubAuthorization:Mode is Enforce: {Caller}",
            refusal, caller);

        await next(context);
    }

    /// <summary>
    ///     Why the connection would be refused, or <c>null</c> when it passes both checks. A phrase
    ///     rather than a boolean, for the same reason as the other two gates: "no token at all" and
    ///     "token without a tenant" are different pieces of work for whoever reads the inventory.
    /// </summary>
    private static async Task<string?> RefusalReasonAsync(HubLifetimeContext context, ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return "does not satisfy '" + Constants.TenantCommunicationApiReadWritePolicy +
                   "' (unauthenticated)";
        }

        var authorizationService = context.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var authorized = await authorizationService.AuthorizeAsync(user, null,
            Constants.TenantCommunicationApiReadWritePolicy);
        if (!authorized.Succeeded)
        {
            return $"does not satisfy '{Constants.TenantCommunicationApiReadWritePolicy}'";
        }

        // Fail closed. A token that cannot be attributed to a tenant cannot be attributed to a pool
        // owner either, and the whole authority model of this hub rests on knowing which tenant owns
        // the processes on the other end.
        var tokenTenantId = user.FindFirst(HubConnectionPrincipal.TenantIdClaimType)?.Value;
        if (string.IsNullOrEmpty(tokenTenantId))
        {
            return HubConnectionPrincipal.IsServiceToken(user)
                ? "presents a service token with no tenant_id claim"
                : "presents a user token with no tenant_id claim";
        }

        return null;
    }
}
