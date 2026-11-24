using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterServiceException : Exception
{
    private AdapterServiceException()
    {
    }

    private AdapterServiceException(string message) : base(message)
    {
    }

    private AdapterServiceException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static Exception CommonFailedSetAdapterDeploymentState(string tenantId, RtEntityId adapterRtEntityId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtEntityId}' deployment state to '{deploymentState}'", exception);
    }
    internal static Exception CommonFailedSetAdapterCommunicationState(string tenantId, RtEntityId adapterRtEntityId, RtCommunicationStateEnum communicationState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtEntityId}' communication state to '{communicationState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadAdapterConfiguration(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to load adapter '{adapterRtEntityId}' configuration", exception);
    }

    internal static Exception AdapterNotLoaded(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new AdapterServiceException(
            $"[{tenantId}] Adapter '{adapterRtEntityId}' has no live SignalR connection. " +
            "The adapter pod must be deployed and online before its pipeline configuration can be pushed. " +
            "Deploy the adapter first via the 'Deploy Adapter' action (or 'DeploymentSite → Deploy Workload' on the API), " +
            "then retry 'Update Configuration'.");
    }

    internal static Exception TenantNotEnabled(string tenantId)
    {
        return new AdapterServiceException($"[{tenantId}] Tenant not enabled.");
    }

    /// <summary>
    /// AB#5827: the tenant's adapter cache was re-initialised (tenant pre/post-update) again and
    /// again while the registration was reading the adapter configuration. Thrown instead of
    /// writing the adapter into a cache instance nothing reads any more; the adapter retries.
    /// </summary>
    internal static Exception RegistrationRaceLost(string tenantId, RtEntityId adapterRtEntityId, int attempts)
    {
        return new AdapterServiceException(
            $"[{tenantId}] Registration of adapter '{adapterRtEntityId}' gave up after {attempts} attempts: " +
            "the tenant's adapter cache was re-initialised by a tenant update during every attempt. Register again.");
    }

    /// <summary>
    /// AB#5827: the registration completed but the adapter is no longer in the tenant's adapter
    /// cache (e.g. a tenant update flushed it right after). Reporting success here would leave the
    /// adapter connected but deaf, so the registration fails and the adapter registers again.
    /// </summary>
    internal static Exception RegistrationLost(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new AdapterServiceException(
            $"[{tenantId}] Registration of adapter '{adapterRtEntityId}' was lost right after it completed: " +
            "the adapter is no longer in the tenant's adapter cache (tenant update in progress). Register again.");
    }

    internal static Exception PipelineNotFound(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Pipeline '{pipelineRtEntityId}' not found."); 
    }

    public static Exception DataFlowNotFound(string tenantId, RtEntityId rtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Data flow of pipeline '{rtEntityId}' not found.");
    }

    public static Exception PreUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Pre update tenant failed.", exception);
    }
    
    public static Exception PosUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Pos update tenant failed.", exception);
    }

    public static Exception CkModelChangedNotificationFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] CK model change notification to adapters failed.", exception);
    }

    public static Exception CkTypeIdUndefined()
    {
        return new AdapterServiceException("CkTypeId is undefined.");
    }

    public static Exception RtWellKnownNameUndefined()
    {
        return new AdapterServiceException("RtWellKnownName is undefined.");
    }

    public static Exception DeploymentStateNotSupported(RtDeploymentStateEnum rDeploymentState)
    {
        return new AdapterServiceException($"Deployment state '{rDeploymentState}' is not supported.");
    }
    
    public static Exception DataFlowHasNoPipelines(string tenantId, OctoObjectId dataFlowRtId)
    {
        return new AdapterServiceException($"[{tenantId}] Data flow '{dataFlowRtId}' has no pipelines assigned.");
    }

    public static Exception PipelineAdapterNotAssigned(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Pipeline '{pipelineRtEntityId}' has no adapter assigned. An adapter must be assigned when a pipeline exists.");
    }

    internal static AdapterServiceException DeploymentTimedOut(string tenantId, RtEntityId adapterRtEntityId,
        TimeSpan timeout) =>
        new($"[{tenantId}] Adapter '{adapterRtEntityId}' deployment timed out after {timeout.TotalSeconds}s");

    internal static AdapterServiceException DeploymentFailed(string tenantId, RtEntityId adapterRtEntityId,
        string? message) =>
        new($"[{tenantId}] Adapter '{adapterRtEntityId}' deployment failed: {message}");

    internal static AdapterServiceException PipelineSchemaValidationFailed(string tenantId,
        RtEntityId adapterRtEntityId, IReadOnlyList<string> errors) =>
        new($"[{tenantId}] Pipeline schema validation failed for adapter '{adapterRtEntityId}': {string.Join("; ", errors)}");

    internal static AdapterServiceException PipelineDefinitionLimitExceeded(string tenantId,
        RtEntityId pipelineRtEntityId, string refusal) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': {refusal}");

    internal static AdapterServiceException PipelineNotOnDemandCompatible(string tenantId,
        RtEntityId pipelineRtEntityId, string? workloadName, IReadOnlyList<string> processBoundNodes) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}' to workload '{workloadName}': " +
            $"the workload has LifecycleMode=OnDemand, but the pipeline uses the process-bound trigger(s) " +
            $"{string.Join(", ", processBoundNodes.Select(n => $"'{n}'"))} that would silently stop while the " +
            "workload is hibernated (AB#4984). Either set the workload back to AlwaysOn or migrate the pipeline " +
            "to a wake-capable trigger (cron PipelineTrigger, FromHttpRequest, FromPipelineDataEvent).");

    /// <summary>
    /// AB#4924: the <c>Leased</c> twin of <see cref="PipelineNotOnDemandCompatible" />. A separate
    /// message rather than a shared one, because the remedy is different: an OnDemand workload can
    /// be set back to AlwaysOn, whereas a Leased adapter has no process of its own to switch on —
    /// it either stops borrowing or stops using that trigger.
    /// </summary>
    internal static AdapterServiceException PipelineNotLeasable(string tenantId,
        RtEntityId pipelineRtEntityId, string? workloadName, IReadOnlyList<string> blockingNodes) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}' to workload '{workloadName}': " +
            "the workload has LifecycleMode=Leased, but the pipeline uses the trigger(s) " +
            $"{string.Join(", ", blockingNodes.Select(n => $"'{n}'"))} that a leased adapter cannot serve " +
            "(AB#4924, AB#5278). A leased adapter has no process of its own at all — it borrows one from an " +
            "adapter pool between work items — so a process-bound trigger never fires, and only work that " +
            "reaches the controller can be leased: a cron PipelineTrigger or an explicit ExecutePipeline. " +
            "FromHttpRequest (AB#5258) and FromPipelineDataEvent (AB#5231) are not routed through a lease yet. " +
            "Either give the adapter a process of its own (LifecycleMode AlwaysOn) or migrate the pipeline to " +
            "a cron PipelineTrigger.");

    /// <summary>
    /// AB#5027 mandatory-identity guard. Deliberately an <see cref="AdapterServiceException" />
    /// (→ HTTP 404 in <c>PipelineController</c>) rather than a <c>DeploymentSiteServiceException</c> (→ 400):
    /// it is thrown on the same deploy paths as the AB#4984 gate and must surface identically in
    /// the Studio, which renders <c>ErrorResponse.ErrorMessage</c> regardless of the status code.
    /// The message carries the whole remedy, so the status code is not the diagnostic here.
    /// </summary>
    internal static AdapterServiceException PipelineHasNoServiceAccount(string tenantId,
        RtEntityId pipelineRtEntityId, OctoObjectId adapterRtId, string? adapterName) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': no pipeline service account could be " +
            "resolved. Since AB#5027 (Epic AB#4979) every pipeline executes under a service-account identity, so " +
            "a pipeline without one would run unauthenticated and is refused before anything is written. " +
            $"Fix it in one of two ways: (1) link a ServiceAccountConfiguration to adapter '{adapterName ?? "?"}' " +
            $"({adapterRtId}) through its 'PipelineServiceAccount' association — this becomes the default identity " +
            "for every pipeline that adapter executes; or (2) set a per-pipeline override by linking a " +
            "ServiceAccountConfiguration to this pipeline through its 'Uses' association.");

    /// <summary>
    /// AB#9776 (own issuer only): the pipeline's service account names an identity service other than this
    /// installation's. The mesh adapter accepts service-account tokens only from its own issuer and refuses such a
    /// configuration on every execution, so the deploy is refused with the repair instead.
    /// </summary>
    internal static AdapterServiceException PipelineServiceAccountForeignIssuer(string tenantId,
        RtEntityId pipelineRtEntityId, string wellKnownName, string issuerUri) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': its service account '{wellKnownName}' " +
            $"names the issuer '{issuerUri}', which is not this installation's identity service. Service accounts " +
            "are accepted only from the own issuer (AB#9776). Leave IssuerUri empty (= this installation, AB#5115) " +
            "or set it to {{service.authority}}.");

    /// <summary>
    /// AB#9776: the pipeline's service account names a tenant other than the pipeline's. Its roles would be applied
    /// to the pipeline's repository, so the mesh adapter refuses it; until a cross-tenant exchange exists (AB#9777)
    /// the deploy is refused as well.
    /// </summary>
    internal static AdapterServiceException PipelineServiceAccountForeignTenant(string tenantId,
        RtEntityId pipelineRtEntityId, string wellKnownName, string configuredTenantId) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': its service account '{wellKnownName}' " +
            $"names the tenant '{configuredTenantId}', but the pipeline runs in tenant '{tenantId}'. A service " +
            "account acts only in the pipeline's own tenant (AB#9776). Leave TenantId empty (= the pipeline's " +
            "tenant, AB#5115) or set it to the pipeline's tenant.");

    /// <summary>
    /// AB#5112 hardened guard, first check: the account resolves but holds no client secret — the
    /// pipeline could never authenticate, so deploying it would only defer the failure to a place
    /// with no error message. Same exception type as the AB#5027 guard for the same Studio-surface
    /// reason (see <see cref="PipelineHasNoServiceAccount" />).
    /// </summary>
    internal static AdapterServiceException PipelineServiceAccountSecretMissing(string tenantId,
        RtEntityId pipelineRtEntityId, string wellKnownName, OctoObjectId adapterRtId, string? adapterName) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': its service account '{wellKnownName}' " +
            "holds no usable client secret, and the adapter has no own client to impersonate the account with " +
            "(AB#5112/AB#5114, Epic AB#4979) — the pipeline could not authenticate either way. " +
            $"Run the service-account reconcile for adapter '{adapterName ?? "?"}' ({adapterRtId}) — " +
            "POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile — which provisions the adapter's " +
            "own credentials AND issues this account a secret where one is missing (never rotating an existing " +
            "one); or reconcile the configuration itself " +
            "(POST {tenantId}/v1/serviceAccount/reconcile?configurationRtId=...), or open the adapter in Studio. " +
            "A deliberately secretless account additionally needs the MayActAs edge the reconcile materialises " +
            "(AB#5114).");

    /// <summary>
    /// AB#5112 hardened guard, second check: the identity client behind the account does not exist
    /// (or the configuration names none), so every token request of the deployed pipeline would be
    /// refused. Only thrown on an authoritative identity answer —
    /// an <b>unreachable</b> identity service is non-blocking by design (warning + deploy), see
    /// <c>AdapterService.EnsurePipelineHasServiceAccountAsync</c> — and the whole check can be
    /// disabled per environment via <c>OCTO_SERVICEACCOUNTGUARD__CHECKIDENTITYCLIENT=false</c>.
    /// </summary>
    /// <summary>
    /// AB#5128 (Epic AB#4979) elevation-authorization refusal. A pipeline that runs one or more
    /// data nodes under an elevated identity (AB#5127 <c>Identity == ServiceAccount</c> or
    /// <c>System</c>) escalates beyond the invoking caller's own rights — the node executes with
    /// the service account's full roles, or unfiltered as the system context, even when a caller
    /// principal is present. Deploying such a pipeline is therefore itself a privileged act and is
    /// refused unless the caller carries the elevation role. Same exception type as the sibling
    /// service-account guards for the same Studio-surface reason (see
    /// <see cref="PipelineHasNoServiceAccount" />): the message names every offending node and the
    /// required authorization, so it reads identically wherever it surfaces.
    /// </summary>
    internal static AdapterServiceException PipelineElevationNotAuthorized(string tenantId,
        RtEntityId pipelineRtEntityId, IReadOnlyList<string> elevatedNodes, string requiredRole) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': it elevates privilege in " +
            $"the node(s) {string.Join(", ", elevatedNodes.Select(n => $"'{n}'"))} — each runs under an " +
            "elevated identity (Identity=ServiceAccount or System, AB#5127/AB#5128, Epic AB#4979) that " +
            "executes with the service account's full roles or unfiltered as the system context, beyond " +
            "the rights of the caller who triggers the pipeline. Deploying an elevated pipeline is itself " +
            $"a privileged operation and requires the '{requiredRole}' role, which the caller does not " +
            "hold. Either have an authorized operator deploy it, or set the offending node(s) back to " +
            "Identity=Caller (the safe default) so the pipeline runs within the caller's own permissions.");

    /// <summary>
    /// AB#9767 (decision 3 of AB#8673): a node runs as the system context and names a target tenant. The system context
    /// is never accepted for another tenant, there is no exception; the mesh adapter's guard refuses it again at run time.
    /// </summary>
    internal static AdapterServiceException PipelineSystemCrossTenantAccess(string tenantId,
        RtEntityId pipelineRtEntityId, IReadOnlyList<string> nodes) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': the node(s) " +
            $"{string.Join(", ", nodes.Select(n => $"'{n}'"))} run as the system context (Identity=System) and name " +
            "a target tenant (targetTenant/targetTenants). The system context never acts in another tenant (AB#9767). " +
            "Set the node(s) to Identity=Caller or Identity=ServiceAccount, whose right in the target tenant is " +
            "proven by the identity service, or remove the target setting.");

    /// <summary>
    /// AB#9767 (decision 2 of AB#8673): a node takes its target tenant from caller input and would act there with the
    /// pipeline's service account (Identity=ServiceAccount, or Caller in a pipeline that can run without a verified
    /// caller): the caller would choose the tenant a service account acts in.
    /// </summary>
    internal static AdapterServiceException PipelineCallerSteeredTargetTenant(string tenantId,
        RtEntityId pipelineRtEntityId, IReadOnlyList<string> findings) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': {string.Join("; ", findings)}. The caller " +
            "of the pipeline would choose the tenant the pipeline's service account acts in (AB#9767). Take the " +
            "target tenant from data the pipeline controls (not $.body, $.query, $.files or $.headers), or run the " +
            "node as the verified caller: Identity=Caller with only FromHttpRequest@2 triggers that do not allow " +
            "anonymous callers.");

    internal static AdapterServiceException PipelineServiceAccountClientMissing(string tenantId,
        RtEntityId pipelineRtEntityId, string wellKnownName, string? clientId, OctoObjectId adapterRtId,
        string? adapterName) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}': " +
            (clientId == null
                ? $"its service account '{wellKnownName}' declares no identity client at all"
                : $"the identity client '{clientId}' of its service account '{wellKnownName}' does not exist in " +
                  "this tenant") +
            " (AB#5112, Epic AB#4979) — every token request of the running pipeline would be refused. " +
            $"Run the service-account reconcile for adapter '{adapterName ?? "?"}' ({adapterRtId}) — " +
            "POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile — or for the configuration " +
            "(POST {tenantId}/v1/serviceAccount/reconcile?configurationRtId=...), or open the adapter in Studio; " +
            "the reconcile materialises the client from the declaration.");
}
