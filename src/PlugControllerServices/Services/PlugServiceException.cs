using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

public class PlugServiceException : Exception
{
    public PlugServiceException()
    {
    }

    public PlugServiceException(string message) : base(message)
    {
    }

    public PlugServiceException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static Exception CommonFailedSetPlugState(string tenantId, OctoObjectId plugRtId, PlugStates plugState, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to set plug '{plugRtId}' state to '{plugState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadPlugConfiguration(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to load plug '{plugRtId}' configuration", exception);
    }

    internal static Exception TenantNotFound(string tenantId)
    {
        return new PoolServiceException($"Tenant {tenantId} not found");
    }
}

