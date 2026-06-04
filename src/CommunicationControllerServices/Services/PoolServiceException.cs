using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PoolServiceException : Exception
{
    private PoolServiceException()
    {
    }

    private PoolServiceException(string message) : base(message)
    {
    }

    private PoolServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception TenantNotFoundOrNotEnabled(string tenantId)
    {
        return new PoolServiceException($"Tenant {tenantId} not found or communication service not enabled");
    }

    internal static Exception PoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new PoolServiceException($"[{tenantId}] Pool '{poolRtId}' not found");
    }

    internal static Exception AdapterNotFound(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new PoolServiceException($"[{tenantId}] Adapter '{adapterRtEntityId}' not found");
    }

    internal static Exception CannotCreatePool(string tenantId, string poolName)
    {
        return new PoolServiceException($"[{tenantId}] Cannot create pool '{poolName}'");
    }

    internal static Exception PreUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new PoolServiceException($"[{tenantId}] Failed to pre update tenant", exception);
    }
    
    internal static Exception PosUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new PoolServiceException($"[{tenantId}] Failed to pos update tenant", exception);
    }

    internal static Exception WorkloadNotFound(string tenantId, OctoObjectId workloadRtId)
    {
        return new PoolServiceException($"[{tenantId}] Workload '{workloadRtId}' not found");
    }

    internal static Exception WorkloadNotInPool(string tenantId, OctoObjectId workloadRtId)
    {
        return new PoolServiceException(
            $"[{tenantId}] Workload '{workloadRtId}' is not currently in any pool — assign it to a pool before deploying");
    }

    internal static Exception WorkloadMissingChartName(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new PoolServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "the 'Chart Name' field is empty. Open the workload in the Refinery Studio and set a Helm chart name " +
            "(e.g. 'octo-modbus-adapter') before deploying.");
    }

    internal static Exception WorkloadMissingHelmRepository(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new PoolServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "no Helm repository is linked. Associate the workload with a HelmRepositoryConfiguration in the Studio " +
            "(workload form → 'Helm Repository' field) so the operator knows where to pull the chart from.");
    }

    internal static Exception WorkloadHelmRepositoryUrlEmpty(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new PoolServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "the linked Helm repository has an empty 'Repository URL'. Open the HelmRepositoryConfiguration " +
            "entity in the Studio and set a chart-repository URL (e.g. 'https://charts.meshmakers.cloud').");
    }

    internal static Exception WorkloadIngressEnabledButHostnameEmpty(string tenantId, OctoObjectId workloadRtId,
        string? workloadName)
    {
        return new PoolServiceException(
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
        return new PoolServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            $"the '{fieldName}' template '{template}' references unknown placeholder '{{{{{unknownPlaceholder}}}}}'. {hint}");
    }

    internal static Exception EdgePoolNotDeployable(string tenantId, OctoObjectId poolRtId, string? poolName)
    {
        return new PoolServiceException(
            $"[{tenantId}] Pool '{poolName ?? poolRtId.ToString()}' has Environment=Edge — Deploy is not available. " +
            "Edge pools are installed and run by an external operator outside the central cluster; only Cloud pools " +
            "can be deployed from this controller.");
    }

    internal static Exception PoolAlreadyNotDeployed(string tenantId, OctoObjectId poolRtId, string? poolName,
        RtDeploymentStateEnum currentState)
    {
        return new PoolServiceException(
            $"[{tenantId}] Pool '{poolName ?? poolRtId.ToString()}' is '{currentState}' — there is nothing to undeploy. " +
            "Undeploy is only valid when the pool is Deployed, Pending, or in Error.");
    }

    internal static Exception WorkloadAlreadyNotDeployed(string tenantId, OctoObjectId workloadRtId,
        string? workloadName, RtDeploymentStateEnum currentState)
    {
        return new PoolServiceException(
            $"[{tenantId}] Workload '{workloadName ?? workloadRtId.ToString()}' is '{currentState}' — there is nothing to undeploy. " +
            "Undeploy is only valid when the workload is Deployed, Pending, or in Error.");
    }
}

