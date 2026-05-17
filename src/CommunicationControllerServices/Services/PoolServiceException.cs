using Meshmakers.Octo.ConstructionKit.Contracts;

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
    
    internal static Exception PoolNotFound(string tenantId, string poolName)
    {
        return new PoolServiceException($"[{tenantId}] Pool '{poolName}' not found");
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

    internal static Exception WorkloadMissingChartVersion(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        return new PoolServiceException(
            $"[{tenantId}] Cannot deploy workload '{workloadName ?? workloadRtId.ToString()}': " +
            "the 'Chart Version' field is empty. Set the version of the Helm chart to deploy (e.g. '0.1.2').");
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
}

