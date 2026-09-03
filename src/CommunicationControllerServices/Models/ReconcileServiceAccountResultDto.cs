namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Answer of the AB#5111 reconcile endpoints
///     (<c>POST {tenantId}/v1/serviceAccount/reconcile</c> and
///     <c>POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile</c>).
/// </summary>
/// <remarks>
///     🔴 Carries no secret, for the same reason as <c>RotateServiceAccountSecretResultDto</c>: the
///     plaintext lives in exactly two places — the tenant's <c>ServiceAccountConfiguration</c>
///     entity and the identity client's hash. Controller-local for now (unlike the rotate DTO,
///     whose shape lives in <c>Communication.Contracts</c>): promoting it into the shared contracts
///     package is the follow-up that wires CLI/MCP/Studio onto the endpoint.
/// </remarks>
/// <param name="Outcome">
///     What the pass did to the configuration entity: <c>AlreadyProvisioned</c>, <c>Provisioned</c>
///     or <c>Repaired</c> (string form of <c>PipelineServiceAccountProvisioningOutcome</c>).
/// </param>
/// <param name="ClientId">The identity client the declaration was materialised into.</param>
/// <param name="ConfigurationWellKnownName">
///     <c>RtWellKnownName</c> of the configuration entity — the key the mesh adapter resolves its
///     execution identity by.
/// </param>
/// <param name="RoleChangesSkipped">
///     <c>true</c> when the account declares roles but the caller lacks the <c>UserManagement</c>
///     role, so the roles were deliberately not materialised (the client itself was still
///     converged). A UI should surface this — the account is degraded until a sufficiently
///     privileged caller (or the next system-initiated reconcile) materialises the declaration.
/// </param>
/// <param name="Message">Operator-facing summary.</param>
public sealed record ReconcileServiceAccountResultDto(
    string Outcome,
    string ClientId,
    string ConfigurationWellKnownName,
    bool RoleChangesSkipped,
    string Message);
