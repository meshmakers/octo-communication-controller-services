using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     Connection gate of <c>/{tenantId}/adapterHub</c> (AB#5063).
/// </summary>
/// <remarks>
///     Two checks on every incoming connection, both governed by the single
///     <see cref="AdapterHubAuthorizationOptions.Mode" /> switch:
///     <list type="number">
///         <item>
///             <description>
///                 <b>Authentication.</b> <see cref="Constants.TenantCommunicationApiReadWritePolicy" />
///                 — the policy this service's own tenant-scoped write routes use. The hub is a write
///                 surface (an adapter registers itself, reports execution results, debug points and
///                 metrics), so the read-only policy would be the wrong bar.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Tenant binding.</b> The connected adapter must belong to the tenant whose hub
///                 path it uses. This is the half that has no equivalent anywhere else in the
///                 service: <c>TenantAuthorizationMiddleware</c> guards the tenant-addressed REST
///                 routes but never sees a hub connection, because it returns early on any request
///                 without an <c>Authorization: Bearer</c> header and a SignalR client on the
///                 WebSocket / SSE transports sends its token as <c>?access_token=</c> instead
///                 (accepted for the hub paths by <c>ConfigureJwtBearerOptions</c>, AB#5059).
///             </description>
///         </item>
///     </list>
///     <para>
///         The tenant rules are the middleware's, not new ones: exact match of <c>tenant_id</c>
///         against the route tenant, fail closed when the claim is absent, and a client-credentials
///         client crosses tenants only when an operator listed it in
///         <see cref="TenantAuthorizationOptions.CrossTenantServiceClientIds" /> — the same options
///         object, so one allow-list governs the whole service. The parent-tenant administration rule
///         (AB#5060) is <b>not</b> applied: it exists for user tokens on endpoints marked
///         <c>IAllowParentTenantAdministration</c>, and an adapter is not a user.
///     </para>
///     <para>
///         🔴 It runs in <see cref="AdapterHubAuthorizationMode.LogOnly" /> by default and must stay
///         there until the adapter fleet actually sends a token. It does not today: the SDK's
///         <c>AccessTokenProvider</c> (AB#5062) reads an <c>IServiceClientAccessToken</c> that nothing
///         in the adapter host fills before the hub connection is made, so an adapter connects
///         anonymously. See <see cref="AdapterHubAuthorizationOptions" /> for the full reasoning and
///         for how to arm it.
///     </para>
///     <para>
///         Registered per hub via <c>AddHubOptions&lt;AdapterHub&gt;</c> in <c>Program.cs</c>. Like
///         <see cref="OperatorHubAuthorizationFilter" /> it resolves its dependencies from the
///         connection's own <see cref="HubLifetimeContext.ServiceProvider" /> rather than through its
///         constructor, because SignalR caches filter instances for the lifetime of the host while
///         <c>IAuthorizationService</c> is transient.
///     </para>
/// </remarks>
internal class AdapterHubAuthorizationFilter : IHubFilter
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var options = context.ServiceProvider
            .GetRequiredService<IOptions<AdapterHubAuthorizationOptions>>().Value;
        var enforcing = options.Mode == AdapterHubAuthorizationMode.Enforce;
        // GetService, not GetRequiredService: a missing metrics registration must cost the counter,
        // never the connection. Program_RegistersTheDecisionMetrics pins the registration.
        var metrics = context.ServiceProvider.GetService<HubAuthorizationMetrics>();

        var user = await HubConnectionPrincipal.ResolveAsync(context);
        var routeTenantId = context.Context.GetHttpContext()?.GetTenantId();

        var verdict = await EvaluateAsync(context, user, routeTenantId);
        if (verdict.Refusal == null)
        {
            metrics?.Record(HubAuthorizationHub.Adapter, enforcing, HubAuthorizationOutcome.Allowed,
                verdict.Reason);
            await next(context);
            return;
        }

        metrics?.Record(HubAuthorizationHub.Adapter, enforcing, HubAuthorizationMetrics.RefusalOutcome(enforcing),
            verdict.Reason);

        var refusal = verdict.Refusal;
        var reasonCode = HubAuthorizationMetrics.ToTagValue(verdict.Reason);
        var caller = $"{HubConnectionPrincipal.Describe(user, context.Context.ConnectionId)}, " +
                     $"token tenant '{TokenTenantOrNone(user)}', " +
                     $"route tenant '{(string.IsNullOrEmpty(routeTenantId) ? "<none>" : routeTenantId)}'";

        if (enforcing)
        {
            Logger.Warn(
                "Refused an adapter connection to /{RouteTenantId}/adapterHub: {Reason}: {Caller} " +
                "(reason code {HubAuthorizationReason})",
                routeTenantId, refusal, caller, reasonCode);
            throw new HubException($"Adapter connection refused: {refusal}.");
        }

        // LogOnly — this line IS the consumer inventory. Read it before arming Enforce anywhere.
        // The reason code is the octo.hub.authorization.reason value of the decision counter, so a
        // metric spike and its log lines can be joined.
        Logger.Warn(
            "Adapter connection to /{RouteTenantId}/adapterHub {Reason} and would be refused when " +
            "AdapterHubAuthorization:Mode is Enforce: {Caller} (reason code {HubAuthorizationReason})",
            routeTenantId, refusal, caller, reasonCode);

        await next(context);
    }

    /// <summary>
    ///     A gate decision: the low-cardinality <see cref="Reason" /> that goes on the decision
    ///     counter, and the <see cref="Refusal" /> phrase for the log line — <c>null</c> when the
    ///     connection passes both checks.
    /// </summary>
    private readonly record struct Verdict(HubAuthorizationReason Reason, string? Refusal);

    /// <summary>
    ///     Why the connection would be refused, or a verdict without a refusal when it passes both
    ///     checks.
    /// </summary>
    /// <remarks>
    ///     A phrase rather than a boolean, because the whole point of
    ///     <see cref="AdapterHubAuthorizationMode.LogOnly" /> is an inventory somebody has to act on:
    ///     "no token at all" and "right token, wrong tenant" are two entirely different pieces of work.
    /// </remarks>
    private static async Task<Verdict> EvaluateAsync(HubLifetimeContext context, ClaimsPrincipal? user,
        string? routeTenantId)
    {
        if (user?.Identity is not { IsAuthenticated: true })
        {
            return new Verdict(HubAuthorizationReason.Unauthenticated,
                "does not satisfy '" + Constants.TenantCommunicationApiReadWritePolicy + "' (unauthenticated)");
        }

        var authorizationService = context.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var authorized = await authorizationService.AuthorizeAsync(user, null,
            Constants.TenantCommunicationApiReadWritePolicy);
        if (!authorized.Succeeded)
        {
            return new Verdict(HubAuthorizationReason.MissingScope,
                $"does not satisfy '{Constants.TenantCommunicationApiReadWritePolicy}'");
        }

        // No tenant in the path means the connection addresses no tenant at all — AdapterHub itself
        // aborts such a connection in OnConnectedAsync. Judging it as bound would be the one case
        // where this gate is more permissive than the hub it guards, so it fails closed.
        if (string.IsNullOrEmpty(routeTenantId))
        {
            return new Verdict(HubAuthorizationReason.NoRouteTenant, "carries no route tenant");
        }

        var tenantOptions = context.ServiceProvider
            .GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value;

        var isServiceToken = HubConnectionPrincipal.IsServiceToken(user);
        var clientId = user.FindFirst(HubConnectionPrincipal.ClientIdClaimType)?.Value;
        if (isServiceToken && tenantOptions.IsCrossTenantServiceClient(clientId))
        {
            // The operator's explicit escape hatch, shared with the HTTP gate. Never expected to
            // contain a pipeline service account — those are provisioned one per adapter inside one
            // tenant and must stay bound to it.
            return new Verdict(HubAuthorizationReason.CrossTenantClient, null);
        }

        var tokenTenantId = user.FindFirst(HubConnectionPrincipal.TenantIdClaimType)?.Value;
        if (string.IsNullOrEmpty(tokenTenantId))
        {
            return new Verdict(HubAuthorizationReason.NoTenantClaim, isServiceToken
                ? "presents a service token with no tenant_id claim"
                : "presents a user token with no tenant_id claim");
        }

        if (!string.Equals(tokenTenantId, routeTenantId, StringComparison.OrdinalIgnoreCase))
        {
            // 🔴 No parent/ancestor allowance here on purpose (AB#5060): that rule is for user tokens
            // on endpoints marked IAllowParentTenantAdministration, and a mirrored service client
            // carries its parent's secret while a token minted without acr_values falls back to the
            // system tenant — the root of the hierarchy.
            return new Verdict(HubAuthorizationReason.TenantMismatch,
                "belongs to a different tenant than the hub path it uses");
        }

        return new Verdict(HubAuthorizationReason.Authorized, null);
    }

    private static string TokenTenantOrNone(ClaimsPrincipal? user)
    {
        var tokenTenantId = user?.FindFirst(HubConnectionPrincipal.TenantIdClaimType)?.Value;
        return string.IsNullOrEmpty(tokenTenantId) ? "<none>" : tokenTenantId;
    }
}
