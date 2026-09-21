using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class DeploymentSiteServiceException : Exception
{
    private DeploymentSiteServiceException()
    {
    }

    private DeploymentSiteServiceException(string message) : base(message)
    {
    }

    private DeploymentSiteServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception TenantNotFoundOrNotEnabled(string tenantId)
    {
        return new DeploymentSiteServiceException($"Tenant {tenantId} not found or communication service not enabled");
    }

    internal static Exception DeploymentSiteNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] DeploymentSite '{poolRtId}' not found");
    }

    internal static Exception AdapterNotFound(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] Adapter '{adapterRtEntityId}' not found");
    }

    internal static Exception CannotCreatePool(string tenantId, string deploymentSiteName)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] Cannot create deploymentSite '{deploymentSiteName}'");
    }

    internal static Exception PreUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] Failed to pre update tenant", exception);
    }
    
    internal static Exception PosUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] Failed to pos update tenant", exception);
    }

    internal static Exception WorkloadNotFound(string tenantId, OctoObjectId workloadRtId)
    {
        return new DeploymentSiteServiceException($"[{tenantId}] Workload '{workloadRtId}' not found");
    }

    internal static Exception WorkloadNotInDeploymentSite(string tenantId, OctoObjectId workloadRtId)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Workload '{workloadRtId}' is not currently in any deploymentSite — assign it to a deploymentSite before deploying");
    }

    internal static Exception WorkloadMissingChartName(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "the 'Chart Name' field is empty. Open the workload in the Refinery Studio and set a Helm chart name " +
            "(e.g. 'octo-modbus-adapter') before deploying.");
    }

    internal static Exception WorkloadMissingHelmRepository(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "no Helm repository is linked and none could be resolved by purpose. Either associate the workload with a " +
            "HelmRepositoryConfiguration in the Studio (workload form → 'Helm Repository' field), or give the tenant " +
            "exactly one repository whose Purpose matches the workload (Adapters for adapters and pools, Applications " +
            "for applications) — the channel blueprints stamp that since System.Communication 4.1.0.");
    }

    internal static Exception WorkloadHelmRepositoryUrlEmpty(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "the linked Helm repository has an empty 'Repository URL'. Open the HelmRepositoryConfiguration " +
            "entity in the Studio and set a chart-repository URL (e.g. 'https://charts.meshmakers.cloud').");
    }

    internal static Exception WorkloadIngressEnabledButHostnameEmpty(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "'Ingress Enabled' is on but the 'Hostname' field is empty. Open the workload in the Refinery Studio and " +
            "set a public hostname (e.g. 'adapter.staging.octo-mesh.com') or disable 'Ingress Enabled' before deploying.");
    }

    internal static Exception WorkloadTemplateUnknownPlaceholder(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, string fieldName, string template, string unknownPlaceholder)
    {
        var hint = unknownPlaceholder.StartsWith("domain.", StringComparison.OrdinalIgnoreCase)
            ? $"Either pick one of the values exposed by GET /v1/communication/workload-variables, or extend the controller's Domains option (OCTO_COMMUNICATIONCONTROLLER__DOMAINS__{unknownPlaceholder["domain.".Length..].ToUpperInvariant()})."
            : unknownPlaceholder.StartsWith("service.", StringComparison.OrdinalIgnoreCase)
                ? $"Either pick one of the values exposed by GET /v1/communication/workload-variables, or extend the controller's ServiceUrls option (OCTO_COMMUNICATIONCONTROLLER__SERVICEURLS__{unknownPlaceholder["service.".Length..].ToUpperInvariant()})."
                : "Available placeholders: {{context.tenantId}}, {{domain.NAME}}, {{service.NAME}}; see GET /v1/communication/workload-variables for configured NAMEs.";
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            $"the '{fieldName}' template '{template}' references unknown placeholder '{{{{{unknownPlaceholder}}}}}'. {hint}");
    }

    internal static Exception EdgePoolNotDeployable(string tenantId, OctoObjectId poolRtId, string? deploymentSiteName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] DeploymentSite '{deploymentSiteName ?? poolRtId.ToString()}' has Environment=Edge — Deploy is not available. " +
            "Edge deploymentSites are installed and run by an external operator outside the central cluster; only Cloud deploymentSites " +
            "can be deployed from this controller.");
    }

    internal static Exception PoolAlreadyNotDeployed(string tenantId, OctoObjectId poolRtId, string? deploymentSiteName,
        RtDeploymentStateEnum currentState)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] DeploymentSite '{deploymentSiteName ?? poolRtId.ToString()}' is '{currentState}' — there is nothing to undeploy. " +
            "Undeploy is only valid when the deploymentSite is Deployed, Pending, or in Error.");
    }

    internal static Exception WorkloadAlreadyNotDeployed(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, RtDeploymentStateEnum currentState)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Workload '{workloadName ?? workloadRtId.ToString()}' is '{currentState}' — there is nothing to undeploy. " +
            "Undeploy is only valid when the workload is Deployed, Pending, or in Error.");
    }

    internal static Exception WorkloadLifecycleModeAutoNotImplemented(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': LifecycleMode 'Auto' " +
            "is reserved and not implemented yet (AB#4984). Set the workload to AlwaysOn or OnDemand in the Refinery Studio.");
    }

    internal static Exception WorkloadOnDemandNotSupportedForType(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': LifecycleMode 'OnDemand' " +
            "is currently supported for adapter workloads only — the idle watchdog and wake gates do not manage " +
            "Application workloads (AB#4984). Set the workload to AlwaysOn in the Refinery Studio.");
    }

    internal static Exception WorkloadNotOnDemandCapable(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, IReadOnlyList<string> blockingReasons)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}' with LifecycleMode 'OnDemand': " +
            $"{string.Join("; ", blockingReasons)}. Process-bound triggers stop silently while the workload is hibernated " +
            "(AB#4984). Either set the workload back to AlwaysOn or migrate the pipelines to wake-capable triggers " +
            "(cron PipelineTrigger, FromHttpRequest, FromPipelineDataEvent).");
    }

    // ---- AB#4924 shared adapter leasing -------------------------------------------------------
    //
    // Same enforcement rationale as the AB#4984 block above: LifecycleMode, SharingMode and the
    // LentFrom* pair are plain CK author configuration, writable via GraphQL and seedable by a
    // blueprint with no service-layer hook. The deploy is therefore the net, and every message
    // below names what to change and where, because the alternative is a workload that deploys
    // successfully and then never executes anything.

    internal static Exception WorkloadLeasedNotSupportedForType(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': LifecycleMode 'Leased' " +
            "is supported for adapter workloads only — only an Adapter runs pipelines, and only pipelines can be " +
            "executed on a borrowed process (AB#4924). Set the workload to AlwaysOn in the Refinery Studio.");
    }

    internal static Exception AdapterPoolCannotBeLeased(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy adapter deploymentSite '{workloadName ?? workloadRtId.ToString()}' with LifecycleMode " +
            "'Leased' (AB#4924). 'Leased' is the BORROWER's mode — it means the workload has no process of its own. " +
            "A deploymentSite is the opposite: it owns the processes that are lent out. Set the deploymentSite to AlwaysOn and control its " +
            "size with MinReplicas / MaxReplicas instead.");
    }

    internal static Exception WorkloadLeasedNotOnDemandCapable(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, IReadOnlyList<string> blockingReasons)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}' with LifecycleMode 'Leased': " +
            $"{string.Join("; ", blockingReasons)}. A lease can only be handed to a process whose triggers are " +
            "wake-capable — a process-bound trigger would need a process of its own, which is exactly what a leased " +
            "workload does not have (AB#4924). Same gate as LifecycleMode 'OnDemand'.");
    }

    internal static Exception LeasedWorkloadLenderIncomplete(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}' with LifecycleMode 'Leased': " +
            "the LentAdapterPool it is linked to names no lender (AB#5271). Its Lender record must carry both the " +
            "lending tenant and the AdapterPool's RtId inside that tenant; one without the other names no resolvable " +
            "pool. The controller writes that record as one unit, so a mirror in this state was edited by hand. There " +
            "is no referential integrity behind it either — it points into a different tenant's database, so nothing " +
            "but this check can catch a half-configured borrower. Re-run the adapter pool mirror sync to repair it.");
    }

    internal static Exception LeasedWorkloadWithoutLender(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}' with LifecycleMode 'Leased': " +
            "it names no adapter pool to borrow from. Link the adapter to the LentAdapterPool of the pool it should " +
            "borrow from (association 'LentFrom', AB#5271). Those mirrors are provisioned by the controller for every " +
            "pool this tenant may borrow; if the list is empty, no ancestor or sibling lends to this tenant.");
    }

    internal static Exception LentFromSetWithoutLeasedMode(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': it is linked to a " +
            "LentAdapterPool ('LentFrom', AB#5271) but LifecycleMode is not 'Leased'. The link would do nothing, and a " +
            "link that silently does nothing is worse than an error — it reads like the workload borrows a process " +
            "when it actually runs its own. Either set LifecycleMode to 'Leased' or remove the LentFrom link.");
    }

    internal static Exception LenderDoesNotLendToThisTenant(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, string lenderTenantId)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}' with LifecycleMode 'Leased': " +
            $"tenant '{lenderTenantId}' does not lend to tenant '{tenantId}' (AB#4924). Lending follows the tenant tree " +
            "and never flows upwards: a deploymentSite lends to its owner's descendants, and to siblings under a shared parent " +
            $"when its SharingMode is 'DescendantsAndSiblings'. Check the deploymentSite's SharingMode and its " +
            "LendingAllowedTenantIds allow-list in tenant '" + lenderTenantId + "'.");
    }

    internal static Exception AdapterPoolNotOnDemandCapable(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy adapter deploymentSite '{workloadName ?? workloadRtId.ToString()}' with a SharingMode " +
            "other than 'NotShared': the deploymentSite is not on-demand capable (AB#4924). A lease is handed to a process " +
            "between work items, so a deploymentSite whose own workload carries a process-bound trigger cannot serve one.");
    }

    internal static Exception AdapterPoolReplicaRangeInvalid(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, int minReplicas, int maxReplicas)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy adapter deploymentSite '{workloadName ?? workloadRtId.ToString()}': the replica range " +
            $"MinReplicas={minReplicas} / MaxReplicas={maxReplicas} is invalid (AB#4924). MaxReplicas must be at least 1 " +
            "and at least MinReplicas; MinReplicas must not be negative. MinReplicas=0 is permitted and turns the deploymentSite " +
            "into a scale-to-zero deploymentSite where every burst pays one cold start.");
    }

    internal static Exception WorkloadIsNotAnAdapterPool(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Workload '{workloadName ?? workloadRtId.ToString()}' is not an adapter deploymentSite and cannot be " +
            "scaled through the deploymentSite path (AB#4924). An Adapter or Application is scaled by the on-demand lifecycle " +
            "(hibernate / wake), which owns its replica count; only a deploymentSite has a replica RANGE to move within.");
    }

    internal static Exception AdapterPoolNotDeployed(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, RtDeploymentStateEnum deploymentState)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot scale adapter deploymentSite '{workloadName ?? workloadRtId.ToString()}': it is " +
            $"'{deploymentState}', so there is no Helm release whose members could be scaled (AB#4924). Deploy the deploymentSite " +
            "first.");
    }

    internal static Exception AdapterPoolLeaseCapInvalid(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, int cap)
    {
        return new DeploymentSiteServiceException(
            $"[{tenantId}] Cannot deploy adapter deploymentSite '{workloadName ?? workloadRtId.ToString()}': " +
            $"LendingMaxConcurrentLeasesPerTenant is {cap} (AB#4924). Leave it unset for no per-tenant cap — " +
            "MaxReplicas and the round-robin rotation are the real bounds — or set a value of at least 1. A cap of 0 " +
            "would let a borrower queue work that can never be leased.");
    }
}

