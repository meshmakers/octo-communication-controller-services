using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Repository;

public class PlugRepositoryException : Exception
{
    public PlugRepositoryException()
    {
    }

    public PlugRepositoryException(string message) : base(message)
    {
    }

    public PlugRepositoryException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception PlugNotFound(string tenantId, OctoObjectId plugRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug '{plugRtId}' does not exist");
    }

    internal static Exception PlugNotAssociatedToPlugPool(string tenantId, OctoObjectId plugRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug '{plugRtId}' is not associated with a plug pool");
    }

    internal static Exception CommonGettingPlugPoolOfPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated plug pool for plug '{plugRtId}'", exception);
    }

    internal static Exception PlugPoolNotFound(string tenantId, OctoObjectId plugPoolId)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated plug pool for plug '{plugPoolId}'");
    }

    internal static Exception PlugMappingNotFound(string tenantId, OctoObjectId plugMappingRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug mapping '{plugMappingRtId}' does not exist");
    }

    internal static Exception PlugMappingNotAssociatedToPlug(string tenantId, OctoObjectId plugMappingRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug mapping '{plugMappingRtId}' is not associated with a plug");
    }

    internal static Exception CommonGettingPlugByMapping(string tenantId, OctoObjectId plugMappingRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated plug for plug mapping '{plugMappingRtId}'", exception);
    }

    internal static Exception CommonFailedGettingPlugPoolByName(string tenantId, string plugPoolName, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plug pool with name '{plugPoolName}'", exception);
    }

    internal static Exception CommonFailedGettingPlugs(string tenantId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plugs", exception);
    }

    internal static Exception CommonFailedGettingPlugs(string tenantId, OctoObjectId poolRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plugs of pool '{poolRtId}'", exception);
    }

    internal static Exception CommonFailedCreatePlugPool(string tenantId, string poolName, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to create plug pool '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plug '{plugRtId}'", exception);
    }

    internal static Exception CommonFailedSetPlugPoolState(string tenantId, OctoObjectId plugPoolId, PlugPoolStates state,
        Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to set state of plug pool '{plugPoolId}' to '{state}'", exception);
    }

    internal static Exception CommonFailedSetPlugState(string tenantId, OctoObjectId plugRtId, PlugStates state, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to set state of plug '{plugRtId}' to '{state}'", exception);
    }

    internal static Exception CommonGettingPlugGroupsOfPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plug groups of plug '{plugRtId}'", exception);
    }

    internal static Exception PlugGroupNotAssociatedToPlug(string tenantId, OctoObjectId plugGroupRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug group '{plugGroupRtId}' is not associated with a plug");
    }

    internal static Exception PlugGroupNotFound(string tenantId, OctoObjectId plugGroupRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug group '{plugGroupRtId}' does not exist");
    }

    internal static Exception CommonGettingPlugByGroup(string tenantId, OctoObjectId plugGroupRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plug by group '{plugGroupRtId}'", exception);
    }
}