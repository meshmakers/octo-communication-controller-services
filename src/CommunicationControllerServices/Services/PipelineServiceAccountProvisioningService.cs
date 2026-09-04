using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IPipelineServiceAccountProvisioningService" />
/// <remarks>
/// 🔴 The identity command client is resolved <b>per call from a fresh scope</b>, exactly like
/// <see cref="CommunicationEventService" /> resolves <c>IEventRepository</c>. Constructor-injecting
/// it fails DI validation: <c>ICommandClient&lt;T&gt;</c> wraps MassTransit's
/// <c>IRequestClient&lt;T&gt;</c>, which is <b>scoped</b>, and this service is a singleton consumed
/// by the singleton <c>PoolService</c>.
/// </remarks>
internal class PipelineServiceAccountProvisioningService(
    ICommunicationRepository communicationRepository,
    IPipelineServiceAccountResolver serviceAccountResolver,
    IServiceProvider serviceProvider,
    ICommunicationEventService eventService,
    IOptions<CommunicationControllerOptions> options)
    : IPipelineServiceAccountProvisioningService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Entropy of a generated client secret. 384 bits, well past anything brute-forceable, and it
    /// base64url-encodes to exactly 64 characters with no padding to strip.
    /// </summary>
    internal const int SecretByteLength = 48;

    /// <summary>
    /// The AB#5111 deploy-time token (<c>IWorkloadTemplateResolver</c>, <c>{{service.NAME}}</c>
    /// family) — the canonical <c>IssuerUri</c> default between 3.32.0 and AB#5115. Since AB#5115
    /// the canonical spelling of "this installation" is an <b>empty</b> <c>IssuerUri</c> (the
    /// adapter resolves it against its own authority configuration), the reconcile converges
    /// entities still carrying the token to empty, and the token survives only as a legacy value:
    /// the projection (<c>AdapterService.ResolveServiceAccountIssuerUri</c>) still resolves it for
    /// entities the sweep has not reached yet, and the health aggregate still counts it healthy.
    /// </summary>
    internal const string IssuerUriToken = "{{service.authority}}";

    /// <summary>
    /// AB#5111: the declaration default for <c>AssignedRoleNames</c> on a freshly created service
    /// account — exactly the role AB#5027 hard-coded, so a new account behaves like every account
    /// before it. See <see cref="SendIdentityClientAsync" /> for why it is this role.
    /// </summary>
    internal static readonly string[] DefaultAssignedRoleNames = [CommonConstants.CommunicationManagementRole];

    /// <summary>
    /// Recognises the <c>{{service.authority}}</c> token, case-insensitively and tolerant of the
    /// same inner whitespace <c>WorkloadTemplateResolver.PlaceholderPattern</c> accepts — the check
    /// must never be stricter than the resolver, or a value the projection resolves fine would be
    /// "repaired" on every sweep.
    /// </summary>
    private static readonly Regex IssuerUriTokenPattern = new(
        @"^\s*\{\{\s*service\.authority\s*\}\}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Bounds how long one adapter's identity round trip may take. Deliberately a plain
    /// <c>GetResponse</c> with a timeout instead of <c>GetResponseWithRetry</c> (5 × 30 s): this runs
    /// on the tenant-start path, and a retry storm against an identity service that is still coming
    /// up would serialise into the cold start. The sweep is idempotent and re-runs on every tenant
    /// load, so a missed pass costs nothing but a delay.
    /// </summary>
    private static readonly TimeSpan IdentityCommandTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<PipelineServiceAccountProvisioningReport> EnsureTenantProvisionedAsync(string tenantId)
    {
        IReadOnlyCollection<RtAdapter> adapters;
        try
        {
            adapters = await communicationRepository.GetAdaptersAsync(tenantId);
        }
        catch (Exception e)
        {
            // A tenant whose CK cache is being unloaded concurrently (tenant update / disable) is the
            // common case here. Report it; the next tenant load sweeps again.
            Logger.Warn(e, "[{TenantId}] Pipeline service account backfill skipped: adapter lookup failed", tenantId);
            return new PipelineServiceAccountProvisioningReport(0, 0, 0,
                [$"Adapter lookup failed: {e.Message}"]);
        }

        if (adapters.Count == 0)
        {
            return PipelineServiceAccountProvisioningReport.Empty;
        }

        var provisioned = 0;
        var repaired = 0;
        var alreadyProvisioned = 0;
        var failures = new List<string>();

        foreach (var adapter in adapters)
        {
            // Per-adapter isolation: one adapter whose provisioning fails must not stop the others,
            // exactly like DefaultConfigurationInitializationService isolates one tenant from the rest.
            var outcome = await EnsureAdapterProvisionedAsync(tenantId, adapter);
            switch (outcome)
            {
                case PipelineServiceAccountProvisioningOutcome.Provisioned:
                    provisioned++;
                    break;
                case PipelineServiceAccountProvisioningOutcome.Repaired:
                    repaired++;
                    break;
                case PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned:
                    alreadyProvisioned++;
                    break;
                default:
                    failures.Add(
                        $"Adapter '{adapter.Name ?? adapter.RtId.ToString()}' ({adapter.RtId}) has no pipeline service account.");
                    break;
            }
        }

        return new PipelineServiceAccountProvisioningReport(provisioned, repaired, alreadyProvisioned, failures);
    }

    /// <inheritdoc />
    public async Task<PipelineServiceAccountProvisioningOutcome> EnsureAdapterProvisionedAsync(string tenantId,
        RtAdapter adapter)
    {
        try
        {
            var result = await ReconcileAdapterAsync(tenantId, adapter, ServiceAccountReconcileContext.System);
            return result.Outcome;
        }
        catch (Exception e)
        {
            // Never throws: the caller is a startup path, and the deploy guard already refuses the
            // pipelines of an unprovisioned adapter with an actionable message. Loud (Error log +
            // persistent tenant event) rather than fatal — an operator must be able to see WHY a
            // tenant's pipelines started refusing to deploy without reading pod logs.
            Logger.Error(e,
                "[{TenantId}] Failed to provision the pipeline service account of adapter '{AdapterName}' ({AdapterRtId})",
                tenantId, adapter.Name, adapter.RtId);

            try
            {
                await eventService.StoreErrorEventAsync(tenantId,
                    $"Could not provision the pipeline service account of adapter '{adapter.Name ?? adapter.RtId.ToString()}' (AB#5027): {e.Message}. " +
                    "Until it exists, deploying any pipeline of this adapter is refused. The next tenant load retries automatically; " +
                    "to retry now, re-run the tenant setup (PUT {systemTenant}/v1/tenants/clearCache?childTenantId=<tenant> on the asset repository).");
            }
            catch (Exception eventException)
            {
                Logger.Warn(eventException, "[{TenantId}] Could not store the service-account provisioning error event",
                    tenantId);
            }

            return PipelineServiceAccountProvisioningOutcome.Failed;
        }
    }

    /// <inheritdoc />
    public async Task<ServiceAccountReconcileResult> ReconcileAdapterAsync(string tenantId, RtAdapter adapter,
        ServiceAccountReconcileContext context)
    {
        var wellKnownName = BuildWellKnownName(adapter.RtId);
        var clientId = BuildClientId(adapter.RtId);

        var linked = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);

        // The entity may exist while the edge does not — a half-applied earlier run, or an operator
        // who unlinked it. Look it up by its deterministic well-known name so we adopt our own
        // earlier work instead of creating a second credential entity next to it.
        var existing = linked ?? await communicationRepository
            .GetServiceAccountByWellKnownNameAsync(tenantId, wellKnownName);

        var result = await ReconcileCoreAsync(tenantId, existing, linked != null, wellKnownName, clientId,
            BuildAdapterClientDisplayName(adapter), context,
            // AB#5114: no MayActAs list on the adapter path. The adapter's own OAuth client IS this
            // account's client (AB#5072 projects exactly these credentials into the adapter's Helm
            // values), so the only edge this path could declare is actor == target — a client
            // impersonating itself, which authorizes nothing the client cannot already do. The
            // meaningful edges (adapter-own client → per-pipeline override client) are materialised
            // by the standalone branch of ReconcileConfigurationAsync.
            mayActAsClientIds: null,
            (entity, isNewEntity) => communicationRepository.SavePipelineServiceAccountAsync(tenantId,
                adapter.ToRtEntityId(), entity, isNewEntity));

        if (result.Outcome != PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned)
        {
            Logger.Info(
                "[{TenantId}] Pipeline service account '{WellKnownName}' (client '{ClientId}') is linked to adapter '{AdapterName}' ({AdapterRtId})",
                tenantId, wellKnownName, clientId, adapter.Name, adapter.RtId);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ServiceAccountReconcileResult> ReconcileConfigurationAsync(string tenantId,
        RtServiceAccountConfiguration configuration, ServiceAccountReconcileContext context)
    {
        // A configuration owned by an adapter reconciles through the adapter path: the adapter's
        // rtId defines the deterministic well-known name / client id, so both entry points can
        // never produce diverging names for the same account.
        var owningAdapter = await communicationRepository.GetAdapterForServiceAccountAsync(tenantId,
            configuration.RtId);
        if (owningAdapter != null)
        {
            return await ReconcileAdapterAsync(tenantId, owningAdapter, context);
        }

        // Standalone (e.g. a per-pipeline override linked only via Uses): the configuration's own
        // identifiers win; the deterministic fallbacks key on the configuration's rtId — same
        // format as the adapter-bound names, and rtIds are unique across entities. A custom
        // ClientId outside the octo-pipeline-sa- prefix is honoured, but note that the identity
        // side only applies declarative role *removal* to prefixed clients (see
        // CreateIdentityDataCommandRequestConsumer) — a custom-named client syncs additively.
        var wellKnownName = configuration.RtWellKnownName ?? BuildWellKnownName(configuration.RtId);
        var existingClientId = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.ClientId));
        var clientId = string.IsNullOrWhiteSpace(existingClientId)
            ? BuildClientId(configuration.RtId)
            : existingClientId!;

        // AB#5114: the actor clients that may impersonate this account — the OWN clients (AB#5072)
        // of every adapter whose pipelines link this configuration via Uses. When none can be
        // resolved (nothing links the configuration yet, or the adapters have no provisioned own
        // account), null goes onto the wire — the identity side changes no edges — and the next
        // reconcile pass after the linkage exists materialises them.
        var mayActAsClientIds = await ResolveImpersonationActorClientIdsAsync(tenantId, configuration.RtId,
            wellKnownName, clientId);

        var result = await ReconcileCoreAsync(tenantId, configuration, isLinked: true, wellKnownName, clientId,
            BuildStandaloneClientDisplayName(wellKnownName), context, mayActAsClientIds,
            (entity, _) => communicationRepository.UpdateServiceAccountAsync(tenantId, entity));

        if (result.Outcome != PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned)
        {
            Logger.Info(
                "[{TenantId}] Standalone pipeline service account '{WellKnownName}' (client '{ClientId}') was reconciled",
                tenantId, wellKnownName, clientId);
        }

        return result;
    }

    /// <summary>
    /// The shared reconcile core (AB#5027 provisioning, generalised by AB#5111): converge the
    /// identity client onto the declaration, then converge the configuration entity — in that
    /// order, so a failed entity write leaves a client the next pass adopts rather than an entity
    /// whose client does not exist.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="existing">The current configuration entity, or <c>null</c> when none exists yet.</param>
    /// <param name="isLinked">
    ///     Adapter-bound: whether the <c>PipelineServiceAccount</c> edge currently points at
    ///     <paramref name="existing" /> (false forces a repairing save that restores it).
    ///     Standalone configurations pass <c>true</c> — there is no edge to repair.
    /// </param>
    /// <param name="wellKnownName">The deterministic <c>RtWellKnownName</c> of the account.</param>
    /// <param name="clientId">The identity client id the declaration materialises into.</param>
    /// <param name="clientDisplayName">Human-readable client name for the identity side.</param>
    /// <param name="context">Who triggered the pass and whether roles may be materialised.</param>
    /// <param name="mayActAsClientIds">
    ///     AB#5114: actor client ids that may impersonate this account, materialised identity-side
    ///     as additive <c>MayActAs</c> edges. <c>null</c> = no edge changes (the adapter path, and
    ///     standalone accounts whose using adapters could not be resolved).
    /// </param>
    /// <param name="saveAsync">Writes the (new or repaired) entity; receives <c>isNewEntity</c>.</param>
    private async Task<ServiceAccountReconcileResult> ReconcileCoreAsync(string tenantId,
        RtServiceAccountConfiguration? existing, bool isLinked, string wellKnownName, string clientId,
        string clientDisplayName, ServiceAccountReconcileContext context, IList<string>? mayActAsClientIds,
        Func<RtServiceAccountConfiguration, bool, Task> saveAsync)
    {
        // Read through GetAttributeValueOrDefault, never through the generated properties — kept
        // even though 3.33.0 made IssuerUri/ClientSecret/TenantId optional (their getters no
        // longer throw): ClientId is still mandatory, and the defensive read documents that a
        // half-written entity is exactly what this method exists to repair. The two declaration
        // attributes are optional and read through their (OrDefault-based) generated properties
        // below.
        var existingSecret = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.ClientSecret));
        var existingClientId = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.ClientId));
        var existingIssuerUri = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.IssuerUri));
        var existingTenantId = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.TenantId));

        // AB#5115: IssuerUri and TenantId are no longer part of "complete" — empty is the
        // canonical value ("the adapter's own installation / the tenant the adapter runs for"),
        // and a concrete foreign value is a deliberate author choice. What still makes an entity
        // incomplete: no secret (the dual-path transition keeps issuing one — impersonation-only
        // accounts are authored, never generated), no client id, no well-known name.
        var isComplete = existing != null &&
                         !string.IsNullOrWhiteSpace(existingSecret) &&
                         !string.IsNullOrWhiteSpace(existingClientId) &&
                         // The projection keys GlobalConfiguration on RtWellKnownName and throws on
                         // null — an unnamed entity is unusable and gets the deterministic name.
                         !string.IsNullOrWhiteSpace(existing.RtWellKnownName);

        // AB#5115 convergence: the two historic spellings of "this installation" — the AB#5111
        // token and the concrete pre-AB#5111 authority URL — become empty; likewise a TenantId
        // naming the tenant the entity already lives in. Anything else is a deliberate foreign
        // target and is preserved verbatim (needsConvergence stays false for it).
        // ConvergeX returns either null ("empty" — converged or already empty) or the existing
        // value verbatim (foreign target, preserved), so "this pass must write" is exactly "the
        // result is null while the entity still carries a value".
        var issuerUriToPersist = ConvergeIssuerUri(existingIssuerUri, options.Value.AuthorityUrl);
        var tenantIdToPersist = ConvergeTenantId(existingTenantId, tenantId);
        var needsConvergence =
            (issuerUriToPersist == null && !string.IsNullOrWhiteSpace(existingIssuerUri)) ||
            (tenantIdToPersist == null && !string.IsNullOrWhiteSpace(existingTenantId));

        var isNewEntity = existing == null;

        // AB#5111 declaration. An absent AssignedRoleNames attribute selects LEGACY mode: the
        // identity client's roles are left completely untouched (null below), because a pre-3.32.0
        // account may carry role edges granted by hand or by a blueprint — the documented
        // delegation setup — and a declarative sync would silently remove them on the next tenant
        // load. Declarative role management is opted into by setting the attribute; a freshly
        // created account is declarative from birth (defaults persisted below).
        var declaredRoleNames = existing?.AssignedRoleNames;
        var allowDelegation = existing?.AllowDelegation ?? true;

        string[]? rolesToMaterialize = null;
        var roleChangesSkipped = false;
        if (declaredRoleNames != null || isNewEntity)
        {
            var declared = declaredRoleNames?.ToArray() ?? DefaultAssignedRoleNames;
            if (context.MaterializeRoles)
            {
                rolesToMaterialize = declared;
            }
            else
            {
                // The security gate (AB#5111): a user-initiated reconcile may only materialise
                // roles its caller could have granted directly. The client itself is still
                // created/converged — a service account without its declared roles is degraded,
                // not dangerous; materialised roles the caller could not grant would be.
                roleChangesSkipped = true;
                Logger.Warn(
                    "[{TenantId}] Reconcile of pipeline service account '{WellKnownName}' (client '{ClientId}') skipped the declared role assignment: the calling user lacks the '{Role}' role. The client was converged without role changes; a caller with the role (or the next system-initiated reconcile) materialises them.",
                    tenantId, wellKnownName, clientId, CommonConstants.UserManagementRole);
                try
                {
                    await eventService.StoreWarningEventAsync(tenantId,
                        $"Reconcile of pipeline service account '{wellKnownName}' (client '{clientId}') skipped the declared roles " +
                        $"(source: {context.Source}): the caller lacks the '{CommonConstants.UserManagementRole}' role. " +
                        "The next system-initiated reconcile (tenant start, workload deploy) materialises the declaration.");
                }
                catch (Exception eventException)
                {
                    Logger.Warn(eventException,
                        "[{TenantId}] Could not store the role-materialisation warning event", tenantId);
                }
            }
        }

        // 🔴 The secret never leaves this method except through the two sanctioned sinks (the bus
        // command to the identity service, and the tenant-scoped configuration entity). It is not
        // logged at any level, not truncated, and not carried in any exception message.
        //
        // Re-sending the SAME plaintext is what makes a second run a no-op: the identity service
        // hashes it to the same value, so nothing rotates — while still healing a client that was
        // deleted underneath us. A fresh secret is generated ONLY when none exists (AB#5111
        // tightened this: AB#5027 also re-issued when some OTHER attribute was missing, which
        // rotated a live credential as a side effect of an unrelated repair).
        var secret = !string.IsNullOrWhiteSpace(existingSecret)
            ? existingSecret!
            : GenerateSecret();

        await SendIdentityClientAsync(tenantId, clientDisplayName, clientId, secret, rolesToMaterialize,
            allowDelegation, mayActAsClientIds);

        if (isComplete && isLinked && existingClientId == clientId && !needsConvergence)
        {
            // Healthy and current: entity complete, linked to its owner, and its issuer/tenant are
            // either empty (the AB#5115 installation default) or a deliberate foreign target.
            // Leave it completely untouched.
            return new ServiceAccountReconcileResult(PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned,
                clientId, wellKnownName, roleChangesSkipped);
        }

        var entity = BuildServiceAccount(existing?.RtId, wellKnownName, clientId, secret,
            // AB#5115: a new entity is born with issuer and tenant EMPTY (the adapter's own
            // installation and tenant); a repaired entity carries the converged values — empty for
            // the installation spellings, verbatim for a deliberate foreign target.
            issuerUriToPersist, tenantIdToPersist,
            // A new entity is declarative from birth (the AB#5111 defaults); a repaired legacy
            // entity keeps its declaration state as-is — repairs must not flip it into
            // declarative mode behind the operator's back.
            isNewEntity ? new AttributeStringValueList(DefaultAssignedRoleNames.ToList()) : declaredRoleNames,
            isNewEntity ? true : existing!.AllowDelegation);

        await saveAsync(entity, isNewEntity);

        return new ServiceAccountReconcileResult(
            isNewEntity
                ? PipelineServiceAccountProvisioningOutcome.Provisioned
                : PipelineServiceAccountProvisioningOutcome.Repaired,
            clientId, wellKnownName, roleChangesSkipped);
    }

    /// <inheritdoc />
    public async Task<PipelineServiceAccountRotationResult> RotateAdapterSecretAsync(string tenantId,
        RtAdapter adapter)
    {
        var wellKnownName = BuildWellKnownName(adapter.RtId);
        var clientId = BuildClientId(adapter.RtId);

        var linked = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);
        var existing = linked ?? await communicationRepository
            .GetServiceAccountByWellKnownNameAsync(tenantId, wellKnownName);

        var result = await RotateCoreAsync(tenantId, existing, wellKnownName, clientId,
            BuildAdapterClientDisplayName(adapter),
            (entity, isNew) => communicationRepository.SavePipelineServiceAccountAsync(tenantId,
                adapter.ToRtEntityId(), entity, isNew),
            adapterNameForLog: adapter.Name ?? adapter.RtId.ToString());

        Logger.Info(
            "[{TenantId}] Rotated the secret of pipeline service account '{WellKnownName}' (client '{ClientId}') of adapter '{AdapterName}' ({AdapterRtId})",
            tenantId, wellKnownName, clientId, adapter.Name, adapter.RtId);

        return result;
    }

    /// <inheritdoc />
    public async Task<PipelineServiceAccountRotationResult> RotateConfigurationSecretAsync(string tenantId,
        RtServiceAccountConfiguration configuration)
    {
        // Same routing rule as ReconcileConfigurationAsync: an adapter-owned configuration rotates
        // through the adapter path so the deterministic naming stays with the adapter.
        var owningAdapter = await communicationRepository.GetAdapterForServiceAccountAsync(tenantId,
            configuration.RtId);
        if (owningAdapter != null)
        {
            return await RotateAdapterSecretAsync(tenantId, owningAdapter);
        }

        var wellKnownName = configuration.RtWellKnownName ?? BuildWellKnownName(configuration.RtId);
        var existingClientId = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.ClientId));
        var clientId = string.IsNullOrWhiteSpace(existingClientId)
            ? BuildClientId(configuration.RtId)
            : existingClientId!;

        var result = await RotateCoreAsync(tenantId, configuration, wellKnownName, clientId,
            BuildStandaloneClientDisplayName(wellKnownName),
            (entity, _) => communicationRepository.UpdateServiceAccountAsync(tenantId, entity),
            adapterNameForLog: null);

        Logger.Info(
            "[{TenantId}] Rotated the secret of standalone pipeline service account '{WellKnownName}' (client '{ClientId}')",
            tenantId, wellKnownName, clientId);

        // WasCreated is always false here: a standalone rotation requires the caller to hold the
        // configuration entity, so it exists by construction.
        return result;
    }

    /// <summary>
    /// The shared rotation core (AB#5032, generalised by AB#5111). Roles are deliberately never
    /// touched by a rotation (<c>AssignedRoleNames = null</c> on the wire — the identity side
    /// leaves the edges alone); the delegation grant follows the entity's declaration, so a
    /// rotation cannot re-add a grant the declaration removed.
    /// </summary>
    private async Task<PipelineServiceAccountRotationResult> RotateCoreAsync(string tenantId,
        RtServiceAccountConfiguration? existing, string wellKnownName, string clientId, string clientDisplayName,
        Func<RtServiceAccountConfiguration, bool, Task> saveAsync, string? adapterNameForLog)
    {
        // The plaintext currently in the configuration, i.e. the one every running pipeline of this
        // account is presenting. Kept only to be able to put it back if the write below fails.
        var previousSecret = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.ClientSecret));
        var isNewEntity = existing == null;
        var allowDelegation = existing?.AllowDelegation ?? true;

        // 🔴 Same secret hygiene as the provisioning path: neither the old nor the new plaintext is
        // logged, truncated or carried in an exception message.
        var secret = GenerateSecret();

        // Identity first. A failure here leaves BOTH sides on the old secret — the rotation simply
        // did not happen, which is a consistent state, and the exception tells the caller so.
        // MayActAs edges are — like roles — never touched by a rotation (null on the wire).
        await SendIdentityClientAsync(tenantId, clientDisplayName, clientId, secret,
            assignedRoleNames: null, allowDelegation, mayActAsClientIds: null);

        try
        {
            await saveAsync(BuildServiceAccount(existing?.RtId, wellKnownName, clientId, secret,
                // AB#5115: same convergence as the reconcile — the installation spellings become
                // empty, a deliberate foreign issuer/tenant survives the rotation verbatim.
                ConvergeIssuerUri(ReadAttribute(existing, nameof(RtServiceAccountConfiguration.IssuerUri)),
                    options.Value.AuthorityUrl),
                ConvergeTenantId(ReadAttribute(existing, nameof(RtServiceAccountConfiguration.TenantId)), tenantId),
                existing?.AssignedRoleNames, existing?.AllowDelegation), isNewEntity);
        }
        catch (Exception e)
        {
            // The only genuinely inconsistent window: the client already carries the new hash while
            // the configuration still holds the old plaintext. Push the old one back so the adapters
            // running on it keep working, then let the failure surface.
            if (!string.IsNullOrWhiteSpace(previousSecret))
            {
                try
                {
                    await SendIdentityClientAsync(tenantId, clientDisplayName, clientId, previousSecret!,
                        assignedRoleNames: null, allowDelegation, mayActAsClientIds: null);
                    Logger.Warn(e,
                        "[{TenantId}] Rotating pipeline service account '{WellKnownName}' (adapter '{AdapterName}') failed while writing the configuration; the previous secret was restored at the identity service, so nothing changed",
                        tenantId, wellKnownName, adapterNameForLog ?? "-");
                }
                catch (Exception rollbackException)
                {
                    // Both sides now disagree. The next convergence pass re-sends the plaintext the
                    // configuration still holds and heals it, but an operator must know now.
                    Logger.Error(rollbackException,
                        "[{TenantId}] Could not restore the previous secret of pipeline service account '{WellKnownName}' (adapter '{AdapterName}') after a failed rotation. The identity client and the tenant configuration disagree until the next provisioning pass converges them",
                        tenantId, wellKnownName, adapterNameForLog ?? "-");
                }
            }

            throw;
        }

        return new PipelineServiceAccountRotationResult(clientId, wellKnownName, isNewEntity);
    }

    /// <summary>
    /// The configuration entity every convergence and rotation path writes. Kept in one place so a
    /// rotated entity can never drift from a provisioned one.
    /// </summary>
    /// <param name="existingRtId">The entity to update in place, or <c>null</c> to mint a new id.</param>
    /// <param name="wellKnownName">The deterministic <c>RtWellKnownName</c>.</param>
    /// <param name="clientId">The identity client id.</param>
    /// <param name="secret">The plaintext secret. 🔴 Never log the returned entity.</param>
    /// <param name="issuerUri">
    ///     AB#5115: <c>null</c> keeps the attribute absent — the canonical "this adapter's own
    ///     installation", which the adapter resolves against its own authority configuration. A
    ///     value is only ever the preserved deliberate foreign target (see
    ///     <see cref="ConvergeIssuerUri" />); nothing writes the <see cref="IssuerUriToken" /> or a
    ///     concrete own-installation URL any more.
    /// </param>
    /// <param name="tenantId">
    ///     AB#5115: <c>null</c> keeps the attribute absent — "the tenant the adapter runs for".
    ///     A value is only ever a preserved foreign target (see <see cref="ConvergeTenantId" />).
    /// </param>
    /// <param name="assignedRoleNames">
    ///     The declaration to persist, or <c>null</c> to keep the entity in legacy mode (no
    ///     <c>AssignedRoleNames</c> attribute). Copied defensively — the list instance of a read
    ///     entity is never re-attached to a new one.
    /// </param>
    /// <param name="allowDelegation"><c>null</c> keeps the attribute absent (legacy = true).</param>
    private static RtServiceAccountConfiguration BuildServiceAccount(OctoObjectId? existingRtId,
        string wellKnownName, string clientId, string secret, string? issuerUri, string? tenantId,
        IAttributeValueList<string>? assignedRoleNames, bool? allowDelegation)
    {
        var serviceAccount = new RtServiceAccountConfiguration
        {
            RtId = existingRtId ?? OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = wellKnownName,
            ClientId = clientId,
            // `ClientSecret` is marked isRuntimeState (AB#5027), which only stops a blueprint
            // re-apply from overwriting it — the runtime write path used here is unaffected, and it
            // is exactly why rotation has to go through this path rather than through a seed.
            ClientSecret = secret
        };

        if (issuerUri != null)
        {
            serviceAccount.IssuerUri = issuerUri;
        }

        if (tenantId != null)
        {
            serviceAccount.TenantId = tenantId;
        }

        if (assignedRoleNames != null)
        {
            serviceAccount.AssignedRoleNames = new AttributeStringValueList(assignedRoleNames.ToList());
        }

        if (allowDelegation != null)
        {
            serviceAccount.AllowDelegation = allowDelegation;
        }

        return serviceAccount;
    }

    /// <summary>
    /// Creates or converges the identity client over the distribution event hub — the only channel
    /// this service has to the identity service.
    ///
    /// <para>
    /// Why not the identity REST API (<c>ClientsController</c> already accepts a plaintext secret and
    /// hashes it): calling it needs an <c>octo_api</c> bearer token, i.e. a client-credentials
    /// identity — which is exactly what this method is trying to create. No such bootstrap client is
    /// seeded anywhere, so the REST route is circular. It would also add a
    /// <c>Meshmakers.Octo.Sdk.ServiceClient</c> package reference and a second identity transport to
    /// a service that has one.
    /// </para>
    ///
    /// <para>
    /// The command is re-sent on every pass on purpose: it is the only way grants, scope and role
    /// assignment converge for a client that was created before this code (or edited by hand), and
    /// the consumer treats a re-send of the same secret as a no-op.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant identifier the command is addressed to.</param>
    /// <param name="clientDisplayName">Human-readable client name.</param>
    /// <param name="clientId">The (deterministic) client id the consumer upserts on.</param>
    /// <param name="secret">The plaintext secret; hashed identity-side, never logged.</param>
    /// <param name="assignedRoleNames">
    ///     The roles the client should carry, or <c>null</c> to leave its role edges completely
    ///     untouched (legacy accounts, rotation, and the role-gated user reconcile). For a
    ///     declarative account (client id prefix <c>octo-pipeline-sa-</c>) the identity side syncs
    ///     to exactly this list — add missing, remove superfluous (AB#5111); for any other client
    ///     the list stays additive.
    /// </param>
    /// <param name="allowDelegation">
    ///     Whether the client carries the on-behalf-of grant. The consumer replaces
    ///     <c>AllowedGrantTypes</c> wholesale, so sending the list without the URN is what removes
    ///     an existing grant.
    /// </param>
    /// <param name="mayActAsClientIds">
    ///     AB#5114: actor client ids that may impersonate this client — materialised identity-side
    ///     as additive <c>System.Identity/MayActAs</c> edges (actor → this client); unknown actors
    ///     are skipped with a warning, existing edges are never removed (v1). <c>null</c> changes
    ///     no edges.
    /// </param>
    private async Task SendIdentityClientAsync(string tenantId, string clientDisplayName, string clientId,
        string secret, string[]? assignedRoleNames, bool allowDelegation, IList<string>? mayActAsClientIds)
    {
        var allowedGrantTypes = new List<string> { OidcConstants.GrantTypes.ClientCredentials };
        // AB#5114: every pipeline service account may be an ACTOR for another one (an adapter's
        // default account impersonating a standalone/override account). The grant permission is
        // harmless without a MayActAs edge — the edge authorizes the concrete pairing — so it is
        // part of the reconciled baseline rather than a separate declarative switch.
        allowedGrantTypes.Add(Constants.ImpersonationGrantType);
        if (allowDelegation)
        {
            // Precondition for AB#5031: Duende binds one extension-grant validator per grant type
            // AND gates it on the client's own AllowedGrantTypes, so without this URN a delegation
            // request is rejected before OnBehalfOfGrantValidator ever runs. AB#5111 makes it
            // declarative: AllowDelegation=false (explicitly set) produces a strictly
            // client_credentials client.
            allowedGrantTypes.Add(Constants.OnBehalfOfGrantType);
        }

        var request = new CreateIdentityDataCommandRequest(tenantId)
        {
            Clients = new List<DistClientDto>
            {
                new(clientId, clientDisplayName, options.Value.PublicUrl)
                {
                    AllowedGrantTypes = allowedGrantTypes.ToArray(),
                    RequireConsent = false,
                    RedirectUris = [],
                    PostLogoutRedirectUris = [],
                    AllowedCorsOrigins = [],
                    // Exactly the API scope, nothing else. The consumer writes AllowedScopes verbatim
                    // (unlike ClientsController, which unions in OctoDefaultScopes), and the delegation
                    // grant deliberately refuses offline_access — the role intersection is computed at
                    // issuance, so a refresh token would freeze it.
                    AllowedScopes = [CommonConstants.OctoApiFullAccess],
                    AllowOfflineAccess = false,
                    ClientSecret = secret,
                    RequireClientSecret = true,
                    // The controller's own endpoints authorize on the octo_api SCOPE, not on a role
                    // (Program.cs: every policy is a RequireClaim on the scope claim), so the deploy
                    // calls a pipeline makes back into this service already work with the scope alone.
                    // The default declaration grants CommunicationManagement because it is the role
                    // the platform uses for communication management, and — far more important —
                    // because the AB#5031 delegated token carries the INTERSECTION of service-account
                    // and user roles. A role the service account lacks can never appear in a
                    // delegated token.
                    AssignedRoleNames = assignedRoleNames,
                    // AB#5114: which clients may obtain this account's identity via the
                    // impersonation grant / the on-behalf-of requested_client_id extension —
                    // additive and idempotent identity-side, exactly like the role semantics.
                    MayActAsClientIds = mayActAsClientIds
                }
            }
        };

        using var scope = serviceProvider.CreateScope();
        var commandClient = scope.ServiceProvider
            .GetRequiredService<ICommandClient<CreateIdentityDataCommandRequest>>();

        var response = await commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(request, CancellationToken.None,
                IdentityCommandTimeout);

        switch (response.Response)
        {
            case CreateIdentityDataResult.Success:
                return;
            case CreateIdentityDataResult.SuccessIdentityDataSeedPending:
                // The client exists; only the tenant's role seed has not landed yet, so the role
                // assignment was skipped identity-side. The entity is still written so pipelines can
                // deploy — the next sweep re-sends and the roles converge then.
                Logger.Warn(
                    "[{TenantId}] Pipeline service account client '{ClientId}' was created, but the tenant's identity role seed is not in place yet — roles will be assigned on a later pass",
                    tenantId, clientId);
                return;
            default:
                throw new InvalidOperationException(
                    $"[{tenantId}] The identity service refused to create the pipeline service account client " +
                    $"'{clientId}': {response.Response}.");
        }
    }

    /// <summary>
    /// The one shared definition of "this IssuerUri means this installation" — the AB#5111 token
    /// (which the projection still resolves for legacy entities) or the concrete authority URL
    /// this instance serves (every entity provisioned before AB#5111). Trailing-slash-insensitive:
    /// 'https://localhost:5003/' and 'https://localhost:5003' are the same authority, and both
    /// spellings occur in the wild (seeds write the slash, options usually don't). Shared with the
    /// AB#5112 health aggregate (<see cref="ServiceAccountHealthService" />) so the convergence
    /// sweep and the health endpoint can never disagree about the same value.
    /// </summary>
    internal static bool IsInstallationIssuer(string? issuerUri, string authorityUrl)
    {
        return !string.IsNullOrWhiteSpace(issuerUri) &&
               (IssuerUriTokenPattern.IsMatch(issuerUri) ||
                issuerUri.TrimEnd('/') == authorityUrl.TrimEnd('/'));
    }

    /// <summary>
    /// AB#5115 convergence of <c>IssuerUri</c>: <c>null</c> (keep/leave the attribute absent) when
    /// the value is already empty or one of the installation spellings
    /// (<see cref="IsInstallationIssuer" />); any other concrete value — a deliberate foreign
    /// identity target — comes back verbatim and is preserved.
    /// </summary>
    internal static string? ConvergeIssuerUri(string? issuerUri, string authorityUrl)
    {
        if (string.IsNullOrWhiteSpace(issuerUri) || IsInstallationIssuer(issuerUri, authorityUrl))
        {
            return null;
        }

        return issuerUri;
    }

    /// <summary>
    /// AB#5115 convergence of <c>TenantId</c>: <c>null</c> when the value is already empty or names
    /// the tenant the entity lives in ("the tenant the adapter runs for" — the installation
    /// default); a different value is a deliberate foreign target (paired with a foreign
    /// <c>IssuerUri</c>) and comes back verbatim. This replaces the AB#5111 "always rewrite the
    /// current tenant id" behaviour.
    /// </summary>
    internal static string? ConvergeTenantId(string? existingTenantId, string currentTenantId)
    {
        if (string.IsNullOrWhiteSpace(existingTenantId) || existingTenantId == currentTenantId)
        {
            return null;
        }

        return existingTenantId;
    }

    /// <summary>
    /// AB#5114: whether a persisted <c>ClientSecret</c> is an actual credential. Mirrors the mesh
    /// adapter's <c>ServiceAccountTokenService.IsSecretUsable</c> byte for byte — empty and the
    /// angle-bracket seed placeholder (<c>&lt;insert secret here&gt;</c>) both mean "no secret",
    /// i.e. the account is used via impersonation, and guard/health must judge it the same way the
    /// adapter will.
    /// </summary>
    internal static bool IsSecretUsable(string? clientSecret)
    {
        return !string.IsNullOrWhiteSpace(clientSecret) && !clientSecret.TrimStart().StartsWith('<');
    }

    /// <summary>
    /// AB#5114: resolves the actor client ids for a standalone (per-pipeline override) account —
    /// the OWN clients of the adapters whose pipelines link the configuration via <c>Uses</c>. The
    /// adapter's own client is its default pipeline service account's <c>ClientId</c>: exactly the
    /// credentials AB#5072 projects into the adapter's Helm values, i.e. what the adapter presents
    /// on the impersonation path. Best-effort by design: an adapter without a provisioned own
    /// account (a local host adapter, or one the sweep has not reached) simply contributes no
    /// actor — the reconcile must never fail over it — and <c>null</c> is returned when no actor
    /// could be resolved at all, which leaves the identity edges untouched.
    /// </summary>
    private async Task<IList<string>?> ResolveImpersonationActorClientIdsAsync(string tenantId,
        OctoObjectId configurationRtId, string wellKnownName, string targetClientId)
    {
        try
        {
            var pipelines = await communicationRepository
                .GetPipelinesUsingServiceAccountAsync(tenantId, configurationRtId);

            // Sorted for a deterministic wire order (and log line) across pods and passes.
            var actorClientIds = new SortedSet<string>(StringComparer.Ordinal);
            var seenAdapterRtIds = new HashSet<OctoObjectId>();
            foreach (var pipeline in pipelines ?? [])
            {
                RtAdapter? adapter;
                try
                {
                    adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
                        pipeline.ToRtEntityId());
                }
                catch (CommunicationRepositoryException)
                {
                    // A pipeline without an Executes edge cannot tell us its adapter — the deploy
                    // paths own that error; for the edge materialisation it contributes nothing.
                    continue;
                }

                if (adapter == null || !seenAdapterRtIds.Add(adapter.RtId))
                {
                    continue;
                }

                var adapterDefault = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);
                var actorClientId = ReadAttribute(adapterDefault, nameof(RtServiceAccountConfiguration.ClientId));
                // A self-edge (actor == target) authorizes nothing and is never declared.
                if (!string.IsNullOrWhiteSpace(actorClientId) && actorClientId != targetClientId)
                {
                    actorClientIds.Add(actorClientId!);
                }
            }

            if (actorClientIds.Count == 0)
            {
                Logger.Info(
                    "[{TenantId}] Reconcile of standalone pipeline service account '{WellKnownName}' (client '{ClientId}') declares no MayActAs actors: no adapter with an own client could be resolved from the account's Uses linkage (AB#5114). Impersonation of this account stays unauthorized until a later pass finds one.",
                    tenantId, wellKnownName, targetClientId);
                return null;
            }

            Logger.Debug(
                "[{TenantId}] Reconcile of standalone pipeline service account '{WellKnownName}' (client '{ClientId}') declares MayActAs actors: {Actors} (AB#5114)",
                tenantId, wellKnownName, targetClientId, string.Join(", ", actorClientIds));
            return actorClientIds.ToList();
        }
        catch (Exception e)
        {
            // The edge materialisation is additive convenience — a lookup hiccup must not fail the
            // reconcile that keeps the client and secret converged. The next pass retries.
            Logger.Warn(e,
                "[{TenantId}] Could not resolve the MayActAs actors of standalone pipeline service account '{WellKnownName}' (client '{ClientId}'); the identity edges are left untouched this pass (AB#5114)",
                tenantId, wellKnownName, targetClientId);
            return null;
        }
    }

    /// <summary>
    /// Reads one attribute without triggering the generated mandatory-attribute guard (only
    /// <c>ClientId</c> still has one since 3.33.0). What makes a configuration usable (AB#5115):
    /// the client id — the adapter's <c>ServiceAccountTokenService</c> resolves an empty issuer
    /// and tenant against its own installation, and an empty secret selects the impersonation
    /// path (AB#5114). An entity missing the client id or well-known name is repaired rather than
    /// preserved: the "leave a working configuration untouched" rule is about *working* ones.
    /// </summary>
    private static string? ReadAttribute(RtServiceAccountConfiguration? configuration, string attributeName)
    {
        return configuration?.GetAttributeValueOrDefault(attributeName) as string;
    }

    private static string BuildAdapterClientDisplayName(RtAdapter adapter)
    {
        return $"Pipeline service account for adapter '{adapter.Name ?? adapter.RtId.ToString()}'";
    }

    private static string BuildStandaloneClientDisplayName(string wellKnownName)
    {
        return $"Pipeline service account '{wellKnownName}'";
    }

    /// <summary>
    /// Deterministic per owner, so a second provisioning run finds its own earlier entity instead
    /// of creating a duplicate — and so the name is stable across controller pods and restarts. The
    /// rtId (not a name) is the key: names are editable, rtIds are not. Adapter-bound accounts key
    /// on the adapter's rtId; standalone accounts (AB#5111) on the configuration's own rtId — both
    /// are ObjectIds and unique across entities, so the two families cannot collide.
    /// </summary>
    internal static string BuildWellKnownName(OctoObjectId ownerRtId)
    {
        return $"pipeline-service-account-{ownerRtId}";
    }

    /// <summary>
    /// Deterministic client id, same reasoning as the well-known name. It is also what makes the
    /// identity-side upsert idempotent — <c>CreateIdentityDataCommandRequestConsumer</c> keys on
    /// <c>ClientId</c> — and the <c>octo-pipeline-sa-</c> prefix is what opts the client into the
    /// identity side's declarative role sync (AB#5111).
    /// </summary>
    internal static string BuildClientId(OctoObjectId ownerRtId)
    {
        return $"octo-pipeline-sa-{ownerRtId}";
    }

    /// <summary>
    /// Cryptographically secure, URL-safe, 384 bits. URL-safe because the value travels as a form
    /// field in the OAuth token request the adapter sends.
    /// </summary>
    internal static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
