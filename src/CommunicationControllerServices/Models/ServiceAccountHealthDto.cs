namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     One check of the AB#5112 service-account health aggregate.
/// </summary>
/// <param name="Check">
///     Machine-readable name of the check: <c>association</c>, <c>configuration</c>,
///     <c>client</c>, <c>secret</c>, <c>roles</c>, <c>delegation</c>, <c>tenant</c>,
///     <c>issuerUri</c>. The <c>association</c> check only appears on the adapter-scoped variant.
/// </param>
/// <param name="Status">
///     <c>Healthy</c>, <c>Violation</c>, <c>Unknown</c> (an identity-backed check that could not
///     be evaluated — identity down or no caller token; never a violation), or
///     <c>NotApplicable</c> (the check has no subject, e.g. the roles check of a legacy account
///     without a declaration, or any check downstream of a missing configuration).
/// </param>
/// <param name="Code">
///     Machine-readable violation code (<c>association-missing</c>, <c>configuration-missing</c>,
///     <c>client-missing</c>, <c>secret-missing</c>, <c>roles-drift</c>, <c>delegation-drift</c>,
///     <c>tenant-mismatch</c>, <c>issuer-uri-drift</c>); <c>null</c> unless Status is
///     <c>Violation</c>.
/// </param>
/// <param name="Message">Human-readable finding; always set except for a healthy check.</param>
/// <param name="MissingRoles">
///     Roles the declaration assigns but the identity client does not carry. Only on the
///     <c>roles</c> check, only when it was evaluated.
/// </param>
/// <param name="SuperfluousRoles">
///     Roles the identity client carries beyond the declaration. Only on the <c>roles</c> check.
/// </param>
public sealed record ServiceAccountHealthCheckDto(
    string Check,
    string Status,
    string? Code,
    string? Message,
    IReadOnlyList<string>? MissingRoles = null,
    IReadOnlyList<string>? SuperfluousRoles = null);

/// <summary>
///     Answer of the AB#5112 identity-health endpoints
///     (<c>GET {tenantId}/v1/serviceAccount/{configurationRtId}/health</c> and
///     <c>GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/health</c>).
/// </summary>
/// <remarks>
///     🔴 Carries no secret — the <c>secret</c> check reports presence only, never the value, for
///     the same reason as <c>ReconcileServiceAccountResultDto</c>. Controller-local for now;
///     promoting it into <c>Communication.Contracts</c> is the follow-up that wires
///     CLI/MCP/Studio onto the endpoint (same plan as the reconcile DTO).
/// </remarks>
/// <param name="OverallStatus">
///     <c>Healthy</c> (every evaluated check passed), <c>Unhealthy</c> (at least one violation),
///     or <c>Unknown</c> (no violation, but at least one identity-backed check could not be
///     evaluated).
/// </param>
/// <param name="ConfigurationRtId">RtId of the configuration entity, or <c>null</c> when none exists.</param>
/// <param name="ConfigurationWellKnownName">Its <c>RtWellKnownName</c>, when one exists.</param>
/// <param name="ClientId">The identity client id the account maps to, when one is set.</param>
/// <param name="Checks">The individual findings, in evaluation order.</param>
public sealed record ServiceAccountHealthDto(
    string OverallStatus,
    string? ConfigurationRtId,
    string? ConfigurationWellKnownName,
    string? ClientId,
    IReadOnlyList<ServiceAccountHealthCheckDto> Checks);
