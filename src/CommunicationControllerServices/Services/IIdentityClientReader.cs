using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// What an identity-client lookup established (AB#5112).
/// </summary>
public enum IdentityClientLookupStatus
{
    /// <summary>The client exists in the tenant's identity.</summary>
    Found = 0,

    /// <summary>The identity service answered authoritatively: no such client in this tenant.</summary>
    NotFound = 1,

    /// <summary>
    /// The question could not be answered — identity unreachable, no caller token to ask with, or
    /// a non-authoritative response (401/403/5xx). Callers must treat this as "unknown", never as
    /// "missing": the deploy guard passes through with a warning, the health aggregate reports the
    /// identity-backed checks as unknown.
    /// </summary>
    Unavailable = 2
}

/// <summary>
/// Result of one identity-client lookup (AB#5112).
/// </summary>
/// <param name="Status">Whether the client exists, is missing, or could not be checked.</param>
/// <param name="Client">The client definition when <see cref="IdentityClientLookupStatus.Found" />, else <c>null</c>.</param>
/// <param name="AssignedRoleNames">
///     The names of the roles directly assigned to the client — populated only when the lookup was
///     asked to include roles AND both role reads succeeded. <c>null</c> with a Found client means
///     "roles unknown" (partial identity degradation), not "no roles".
/// </param>
/// <param name="UnavailableReason">
///     Human-readable reason when <see cref="IdentityClientLookupStatus.Unavailable" /> — for the
///     warning log / the health check message. Never contains a secret.
/// </param>
public record IdentityClientLookup(
    IdentityClientLookupStatus Status,
    ClientDto? Client,
    IReadOnlyList<string>? AssignedRoleNames,
    string? UnavailableReason)
{
    /// <summary>Convenience factory for the unavailable case.</summary>
    public static IdentityClientLookup Unavailable(string reason)
    {
        return new IdentityClientLookup(IdentityClientLookupStatus.Unavailable, null, null, reason);
    }

    /// <summary>The authoritative "no such client" answer.</summary>
    public static readonly IdentityClientLookup NotFound =
        new(IdentityClientLookupStatus.NotFound, null, null, null);
}

/// <summary>
/// Result of one impersonation-actors lookup (AB#5114) — the answer of identity's MayActAs read
/// surface, <c>GET {tenantId}/v1/Clients/{id}/actors</c>.
/// </summary>
/// <param name="Status">
///     <see cref="IdentityClientLookupStatus.Found" /> = identity answered authoritatively (the
///     actor list is complete, possibly empty); <see cref="IdentityClientLookupStatus.NotFound" />
///     = the TARGET client does not exist in the tenant (so no edge can exist either);
///     <see cref="IdentityClientLookupStatus.Unavailable" /> = the question could not be answered.
/// </param>
/// <param name="ActorClientIds">
///     The client ids holding a <c>MayActAs</c> edge onto the target — only when
///     <see cref="IdentityClientLookupStatus.Found" />, else <c>null</c>.
/// </param>
/// <param name="UnavailableReason">
///     Human-readable reason when <see cref="IdentityClientLookupStatus.Unavailable" />. Never
///     contains a secret.
/// </param>
public record IdentityClientActorsLookup(
    IdentityClientLookupStatus Status,
    IReadOnlyList<string>? ActorClientIds,
    string? UnavailableReason)
{
    /// <summary>Convenience factory for the unavailable case.</summary>
    public static IdentityClientActorsLookup Unavailable(string reason)
    {
        return new IdentityClientActorsLookup(IdentityClientLookupStatus.Unavailable, null, reason);
    }

    /// <summary>The authoritative actor list (possibly empty).</summary>
    public static IdentityClientActorsLookup Found(IReadOnlyList<string> actorClientIds)
    {
        return new IdentityClientActorsLookup(IdentityClientLookupStatus.Found, actorClientIds, null);
    }

    /// <summary>The authoritative "no such target client" answer.</summary>
    public static readonly IdentityClientActorsLookup NotFound =
        new(IdentityClientLookupStatus.NotFound, null, null);
}

/// <summary>
/// Read-only view onto the tenant's identity clients (AB#5112, Epic AB#4979) — the query
/// counterpart of the write-only <c>CreateIdentityDataCommandRequest</c> bus command the
/// provisioning service converges clients with.
///
/// <para>
/// 🔴 <b>Why this is an HTTP read with the caller's own bearer token and not a bus request or the
/// SDK service client.</b> The distribution event hub carries no identity <i>query</i> surface
/// (the only controller→identity channel is the create/converge command), and adding one means
/// changing contracts + consumer in two other repositories. The
/// <c>Meshmakers.Octo.Sdk.ServiceClient</c> package was already deliberately rejected by AB#5027
/// ("a second identity transport", and its token holder needs a client-credentials identity no
/// controller deployment seeds — see <c>PipelineServiceAccountProvisioningService</c>). What the
/// controller <i>does</i> have on every path that needs this read is the calling user's bearer
/// token: identity's <c>GET {tenantId}/v1/Clients/…</c> endpoints authorize on exactly the
/// <c>octo_api</c> scope family the controller's own policies require, so forwarding the caller's
/// token asks the question with the caller's own privileges — no new service credential, no
/// escalation. A call without an ambient HTTP request (background paths) yields
/// <see cref="IdentityClientLookupStatus.Unavailable" />, which every consumer treats as
/// non-blocking by contract.
/// </para>
/// </summary>
public interface IIdentityClientReader
{
    /// <summary>
    /// Looks one client up in the tenant's identity.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="clientId">The identity client id (e.g. <c>octo-pipeline-sa-…</c>).</param>
    /// <param name="includeRoles">
    ///     Whether to additionally resolve the names of the client's directly assigned roles (two
    ///     extra identity round trips). A failure of the role reads degrades the result's
    ///     <see cref="IdentityClientLookup.AssignedRoleNames" /> to <c>null</c> without changing
    ///     the Found status.
    /// </param>
    Task<IdentityClientLookup> GetClientAsync(string tenantId, string clientId, bool includeRoles);

    /// <summary>
    /// Reads which clients may act for (impersonate) <paramref name="clientId" /> — identity's
    /// <c>MayActAs</c> read surface, <c>GET {tenantId}/v1/Clients/{id}/actors</c> (AB#5114). Same
    /// transport contract as <see cref="GetClientAsync" />: the caller's bearer is forwarded, and
    /// anything short of an authoritative answer (no ambient request, 401/403/5xx, identity down)
    /// degrades to <see cref="IdentityClientLookupStatus.Unavailable" />, never to "no edge".
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="clientId">The identity client id of the impersonation TARGET.</param>
    Task<IdentityClientActorsLookup> GetActorClientIdsAsync(string tenantId, string clientId);
}
