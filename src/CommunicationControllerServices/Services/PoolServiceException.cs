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

    internal static Exception WorkloadIncomplete(string tenantId, OctoObjectId workloadRtId)
    {
        return new PoolServiceException(
            $"[{tenantId}] Workload '{workloadRtId}' is incomplete (missing chart name / chart version / helm repository) — fix the entity and try again");
    }
}

