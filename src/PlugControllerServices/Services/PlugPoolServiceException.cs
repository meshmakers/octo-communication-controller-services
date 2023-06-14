using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

public class PlugPoolServiceException : Exception
{
    public PlugPoolServiceException()
    {
    }

    public PlugPoolServiceException(string message) : base(message)
    {
    }

    public PlugPoolServiceException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception PlugPoolNameNotSet()
    {
        return new PlugPoolServiceException("Plug pool name not set");
    }

    internal static Exception ImageNameNotSet()
    {
        return new PlugPoolServiceException("Image name not set");
    }

    internal static Exception ImageVersionNotSet()
    {
        return new PlugPoolServiceException("Image version not set");
    }

    internal static Exception TenantNotFound(string tenantId)
    {
        return new PlugPoolServiceException($"Tenant {tenantId} not found");
    }

    internal static Exception PlugPoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new PlugPoolServiceException($"[{tenantId}] Plug Pool '{poolRtId}' not found");
    }
    
    internal static Exception PlugPoolNotFound(string tenantId, string poolName)
    {
        return new PlugPoolServiceException($"[{tenantId}] Plug Pool '{poolName}' not found");
    }

    internal static Exception PlugNotFound(string tenantId, OctoObjectId plugRtId)
    {
        return new PlugPoolServiceException($"[{tenantId}] Plug '{plugRtId}' not found");
    }

    internal static Exception CannotCreatePlugPool(string tenantId, string plugPoolName)
    {
        return new PlugPoolServiceException($"[{tenantId}] Cannot create plug pool '{plugPoolName}'");
    }

    internal static Exception CommonFailedSetPlugState(string tenantId, OctoObjectId plugRtId, PlugStates plugState, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to set plug '{plugRtId}' state to '{plugState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadPlugConfiguration(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to load plug '{plugRtId}' configuration", exception);
    }
}

