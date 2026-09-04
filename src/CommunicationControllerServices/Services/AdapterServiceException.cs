using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
            "Deploy the adapter first via the 'Deploy Adapter' action (or 'Pool → Deploy Workload' on the API), " +
            "then retry 'Update Configuration'.");
    }

    internal static Exception TenantNotEnabled(string tenantId)
    {
        return new AdapterServiceException($"[{tenantId}] Tenant not enabled.");
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

    internal static AdapterServiceException PipelineNotOnDemandCompatible(string tenantId,
        RtEntityId pipelineRtEntityId, string? workloadName, IReadOnlyList<string> processBoundNodes) =>
        new($"[{tenantId}] Cannot deploy pipeline '{pipelineRtEntityId}' to workload '{workloadName}': " +
            $"the workload has LifecycleMode=OnDemand, but the pipeline uses the process-bound trigger(s) " +
            $"{string.Join(", ", processBoundNodes.Select(n => $"'{n}'"))} that would silently stop while the " +
            "workload is hibernated (AB#4984). Either set the workload back to AlwaysOn or migrate the pipeline " +
            "to a wake-capable trigger (cron PipelineTrigger, FromHttpRequest, FromPipelineDataEvent).");

    /// <summary>
    /// AB#5027 mandatory-identity guard. Deliberately an <see cref="AdapterServiceException" />
    /// (→ HTTP 404 in <c>PipelineController</c>) rather than a <c>PoolServiceException</c> (→ 400):
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
