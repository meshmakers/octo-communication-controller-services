using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// What a provisioning pass did to one adapter (AB#5027). Reported, never thrown — the whole
/// point of the backfill is that it converges quietly and never wedges tenant startup.
/// </summary>
public enum PipelineServiceAccountProvisioningOutcome
{
    /// <summary>The adapter already had a complete, linked service account. Nothing was written.</summary>
    AlreadyProvisioned = 0,

    /// <summary>A client and a configuration entity were created and linked for the first time.</summary>
    Provisioned = 1,

    /// <summary>
    /// A configuration entity existed but was unusable (missing client id / secret / issuer) or was
    /// not linked to the adapter. A fresh secret was issued where one was missing, and the link
    /// was restored.
    /// </summary>
    Repaired = 2,

    /// <summary>Provisioning failed for this adapter. The tenant keeps running; the next pass retries.</summary>
    Failed = 3
}

/// <summary>
/// Result of one tenant-wide provisioning sweep (AB#5027).
/// </summary>
/// <param name="Provisioned">Adapters that got a service account for the first time.</param>
/// <param name="Repaired">Adapters whose incomplete or unlinked account was fixed.</param>
/// <param name="AlreadyProvisioned">Adapters that were already healthy — the steady state.</param>
/// <param name="Failures">
///     One human-readable line per adapter (or for the tenant as a whole) that could not be
///     provisioned. Non-empty means the caller must surface a persistent, visible warning.
/// </param>
public record PipelineServiceAccountProvisioningReport(
    int Provisioned,
    int Repaired,
    int AlreadyProvisioned,
    IReadOnlyList<string> Failures)
{
    /// <summary>Nothing to do — a tenant with no adapters at all.</summary>
    public static readonly PipelineServiceAccountProvisioningReport Empty = new(0, 0, 0, []);

    /// <summary>True when at least one adapter could not be provisioned.</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>True when the sweep actually wrote something.</summary>
    public bool HasChanges => Provisioned > 0 || Repaired > 0;
}

/// <summary>
/// Who triggered a reconcile pass and how far it may go (AB#5111).
/// </summary>
/// <param name="MaterializeRoles">
///     Whether the pass may materialise the declared roles into the identity client. System
///     triggers (tenant start, workload deploy, blueprint-applied) always may — the declaration is
///     the tenant's own configuration and the platform executes it. A user-initiated call (REST)
///     may only when the caller could have granted the roles directly, i.e. carries the
///     <c>UserManagement</c> role; otherwise the client is still created/converged, just with its
///     roles left untouched, and a warning is logged (see
///     <see cref="ServiceAccountReconcileResult.RoleChangesSkipped" />).
/// </param>
/// <param name="Source">Audit label: <c>System</c> or <c>User</c>.</param>
public record ServiceAccountReconcileContext(bool MaterializeRoles, string Source)
{
    /// <summary>System-initiated triggers materialise the declaration as-is.</summary>
    public static readonly ServiceAccountReconcileContext System = new(true, "System");

    /// <summary>
    /// A user-initiated call. Role materialisation requires the caller to hold the
    /// <c>UserManagement</c> role — the same privilege needed to assign roles directly, so the
    /// reconcile endpoint cannot be used to escalate a declaration the caller could not grant.
    /// </summary>
    public static ServiceAccountReconcileContext User(bool callerHasUserManagementRole)
    {
        return new ServiceAccountReconcileContext(callerHasUserManagementRole, "User");
    }
}

/// <summary>
/// Result of one reconcile pass over a single service account (AB#5111).
/// </summary>
/// <param name="Outcome">What the pass did to the configuration entity.</param>
/// <param name="ClientId">The identity client the declaration was materialised into.</param>
/// <param name="WellKnownName">The <c>RtWellKnownName</c> of the configuration entity.</param>
/// <param name="RoleChangesSkipped">
///     <c>true</c> when the account declares roles but the pass did not materialise them because
///     the triggering caller lacks the <c>UserManagement</c> role. The client itself (secret,
///     grants, scope) was still converged.
/// </param>
public record ServiceAccountReconcileResult(
    PipelineServiceAccountProvisioningOutcome Outcome,
    string ClientId,
    string WellKnownName,
    bool RoleChangesSkipped);

/// <summary>
/// Result of a deliberate secret rotation for one adapter's pipeline service account (AB#5032).
/// </summary>
/// <param name="ClientId">The identity client whose secret was replaced.</param>
/// <param name="WellKnownName">
///     The <c>RtWellKnownName</c> of the <c>ServiceAccountConfiguration</c> that now holds the new
///     plaintext — the key the mesh adapter looks the account up by.
/// </param>
/// <param name="WasCreated">
///     <c>true</c> when the adapter had no service account at all and rotation degraded into a first
///     provisioning. Nothing was replaced in that case; nothing needs redeploying either, because
///     nothing was running under the old credential.
/// </param>
public record PipelineServiceAccountRotationResult(string ClientId, string WellKnownName, bool WasCreated)
{
    /// <summary>
    /// 🔴 Always <c>true</c> for an actual rotation. The adapter caches the pipeline's
    /// <c>GlobalConfiguration</c> — and with it the service-account credentials — at
    /// <b>pipeline registration</b> (<c>PipelineRegistryService</c>, octo-communication-sdk), and
    /// never refreshes it. Until the pipelines / data flows of this adapter are redeployed, every
    /// pipeline keeps presenting the old secret, which the identity service has already replaced.
    /// </summary>
    public bool RequiresPipelineRedeploy => !WasCreated;
}

/// <summary>
/// Creates and maintains the execution identity every adapter's pipelines run under
/// (Epic AB#4979 / AB#5027 phase 2).
///
/// <para>
/// Phase 1 made a resolvable service account a **precondition** for deploying any pipeline
/// (<c>AdapterService.EnsurePipelineHasServiceAccountAsync</c>). Nothing anywhere on the platform
/// created one — dynamic client registration deliberately produces public clients
/// (<c>RequireClientSecret=false</c>) and the operator's <c>CreateSecret</c> paths are Kubernetes
/// secrets. Without this service the guard would refuse **every** pipeline deploy in **every**
/// tenant, so phase 1 and phase 2 must ship together.
/// </para>
///
/// <para>
/// Both entry points are idempotent and never throw: a second run neither rotates the secret nor
/// creates a duplicate client or entity, and a failure is reported instead of propagated so one
/// broken tenant (or one broken adapter) cannot stop a service start.
/// </para>
///
/// <para>
/// 🔴 <b>An under-privileged service account fails silently.</b> What is provisioned here is the
/// baseline: the <c>octo_api</c> scope plus <c>CommunicationManagement</c>. The controller's own
/// endpoints authorize on the <b>scope</b> (every policy in <c>Program.cs</c> is a
/// <c>RequireClaim</c> on the scope claim), so that baseline is enough for the deploy calls a
/// pipeline makes back into this service. It is <b>not</b> enough for delegation (AB#5031): the
/// issued token carries the <b>intersection</b> of the service account's roles and the calling
/// user's roles, and an empty intersection is a <b>success</b> identity-side — a token is issued, it
/// just carries no <c>role</c> claim, and every role-gated consumer fails closed. The symptom is a
/// chat that goes quiet or an export that returns nothing, with no error anywhere. Whoever sets up
/// delegation for a tenant must grant this account the tenant's fachliche roles (e.g. the Accounting
/// roles) on top of the baseline — see CLAUDE.md § "Roles: an under-privileged service account fails
/// silently".
/// </para>
/// </summary>
public interface IPipelineServiceAccountProvisioningService
{
    /// <summary>
    /// Backfill / convergence sweep over every adapter of the tenant. This is the path that keeps
    /// tenants which pre-date AB#5027 out of the deploy guard.
    /// </summary>
    Task<PipelineServiceAccountProvisioningReport> EnsureTenantProvisionedAsync(string tenantId);

    /// <summary>
    /// Provisions a single adapter — used when one adapter becomes relevant on its own (a workload
    /// deploy) rather than during the tenant-wide sweep. A non-throwing wrapper around
    /// <see cref="ReconcileAdapterAsync" /> with the <see cref="ServiceAccountReconcileContext.System" />
    /// context.
    /// </summary>
    Task<PipelineServiceAccountProvisioningOutcome> EnsureAdapterProvisionedAsync(string tenantId, RtAdapter adapter);

    /// <summary>
    /// AB#5111 — materialises the declaration of the adapter's pipeline service account: ensures
    /// the configuration entity exists (creating it with the declaration defaults —
    /// <c>AssignedRoleNames</c> = the platform's default role, <c>AllowDelegation</c> = true),
    /// ensures the identity client exists with the hashed secret, syncs its role edges to the
    /// declared <c>AssignedRoleNames</c> (add missing, remove superfluous), sets or removes the
    /// on-behalf-of grant per <c>AllowDelegation</c>, and (re-)derives <c>TenantId</c> and the
    /// <c>IssuerUri</c> default (<c>{{service.authority}}</c>). Idempotent; an existing secret is
    /// <b>never</b> rotated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Legacy accounts keep their roles.</b> An entity that does not carry the
    /// <c>AssignedRoleNames</c> attribute (everything provisioned before CK 3.32.0) is reconciled
    /// with its identity roles left completely untouched: role edges granted by hand or by a
    /// blueprint (the documented delegation setup, see the interface remarks above) must survive
    /// the upgrade. Declarative role management is opted into by setting the attribute.
    /// </para>
    /// <para>
    /// Unlike the Ensure* wrappers this method <b>throws</b> — it backs the REST endpoint, whose
    /// caller is waiting for an answer.
    /// </para>
    /// </remarks>
    Task<ServiceAccountReconcileResult> ReconcileAdapterAsync(string tenantId, RtAdapter adapter,
        ServiceAccountReconcileContext context);

    /// <summary>
    /// AB#5111 — the configuration-bound variant of <see cref="ReconcileAdapterAsync" />. When the
    /// configuration is linked to an adapter through the <c>PipelineServiceAccount</c> edge, the
    /// adapter-bound reconcile runs (the adapter defines the deterministic names). A standalone
    /// configuration — e.g. a per-pipeline override linked only via <c>Uses</c> — is reconciled in
    /// place: its own <c>ClientId</c> is used (derived deterministically from the configuration's
    /// rtId when unset), and no adapter edge is written.
    /// </summary>
    Task<ServiceAccountReconcileResult> ReconcileConfigurationAsync(string tenantId,
        RtServiceAccountConfiguration configuration, ServiceAccountReconcileContext context);

    /// <summary>
    /// Deliberately replaces the secret of one adapter's pipeline service account (AB#5032), keeping
    /// both sides consistent: a fresh plaintext is hashed into the identity client and written into
    /// the tenant's <c>ServiceAccountConfiguration</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The convergence paths above deliberately never rotate — they re-send the plaintext they
    /// already hold so a service restart cannot invalidate the credential every running adapter is
    /// using. This is the explicit counterpart: the only way to retire a secret that leaked, aged
    /// out or was seen by someone who should not have seen it.
    /// </para>
    /// <para>
    /// Unlike the convergence paths this one <b>throws</b>. A rotation is a user request with a
    /// caller waiting for an answer, and a silent failure would leave that caller believing the old
    /// secret is dead when it is not.
    /// </para>
    /// <para>
    /// 🔴 Ordering is the consistency argument: identity is written <b>first</b>. If that fails,
    /// nothing has changed anywhere and the rotation is a clean no-op. If the configuration write
    /// then fails, the previous plaintext is pushed back to identity so the adapters still running
    /// on it keep working, and the failure is reported. A second call after any failure rotates
    /// again from a consistent state.
    /// </para>
    /// </remarks>
    Task<PipelineServiceAccountRotationResult> RotateAdapterSecretAsync(string tenantId, RtAdapter adapter);

    /// <summary>
    /// AB#5111 — the configuration-bound variant of <see cref="RotateAdapterSecretAsync" />, for
    /// callers that hold a <c>ServiceAccountConfiguration</c> rather than an adapter. A
    /// configuration linked to an adapter rotates through the adapter path (same core logic, same
    /// consistency ordering); a standalone configuration rotates in place. Roles are never touched
    /// by a rotation.
    /// </summary>
    Task<PipelineServiceAccountRotationResult> RotateConfigurationSecretAsync(string tenantId,
        RtServiceAccountConfiguration configuration);
}
