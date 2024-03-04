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

    internal static Exception ImageNameNotSet()
    {
        return new PoolServiceException("Image name not set");
    }

    internal static Exception ImageVersionNotSet()
    {
        return new PoolServiceException("Image version not set");
    }

    internal static Exception TenantNotFound(string tenantId)
    {
        return new PoolServiceException($"Tenant {tenantId} not found");
    }

    internal static Exception PoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new PoolServiceException($"[{tenantId}] Pool '{poolRtId}' not found");
    }
    
    internal static Exception PoolNotFound(string tenantId, string poolName)
    {
        return new PoolServiceException($"[{tenantId}] Pool '{poolName}' not found");
    }

    internal static Exception AdapterNotFound(string tenantId, OctoObjectId adapterRtId)
    {
        return new PoolServiceException($"[{tenantId}] Adapter '{adapterRtId}' not found");
    }

    internal static Exception CannotCreatePool(string tenantId, string poolName)
    {
        return new PoolServiceException($"[{tenantId}] Cannot create pool '{poolName}'");
    }

    internal static Exception TenantReloadFailed(string tenantId, Exception exception)
    {
        return new PoolServiceException($"[{tenantId}] Failed to reload tenant", exception);
    }

    public static Exception CkTypeIdNotSet()
    {
        return new PoolServiceException("CkTypeId not set");
    }
}

