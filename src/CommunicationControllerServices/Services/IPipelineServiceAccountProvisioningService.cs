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
    /// deploy) rather than during the tenant-wide sweep.
    /// </summary>
    Task<PipelineServiceAccountProvisioningOutcome> EnsureAdapterProvisionedAsync(string tenantId, RtAdapter adapter);
}
