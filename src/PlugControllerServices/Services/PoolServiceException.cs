using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

internal class PoolServiceException : Exception
{
    public PoolServiceException()
    {
    }

    public PoolServiceException(string message) : base(message)
    {
    }

    public PoolServiceException(string message, Exception inner) : base(message, inner)
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

    internal static Exception PlugPoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new PoolServiceException($"[{tenantId}] Plug Pool '{poolRtId}' not found");
    }
    
    internal static Exception PlugPoolNotFound(string tenantId, string poolName)
    {
        return new PoolServiceException($"[{tenantId}] Plug Pool '{poolName}' not found");
    }

    internal static Exception PlugNotFound(string tenantId, OctoObjectId plugRtId)
    {
        return new PoolServiceException($"[{tenantId}] Plug '{plugRtId}' not found");
    }

    internal static Exception CannotCreatePlugPool(string tenantId, string plugPoolName)
    {
        return new PoolServiceException($"[{tenantId}] Cannot create plug pool '{plugPoolName}'");
    }


}

