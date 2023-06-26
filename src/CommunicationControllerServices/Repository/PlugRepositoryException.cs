using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

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

    internal static Exception PlugNotAssociatedToPool(string tenantId, OctoObjectId plugRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug '{plugRtId}' is not associated with a pool");
    }

    internal static Exception CommonGettingPoolOfPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated pool for plug '{plugRtId}'", exception);
    }

    internal static Exception PoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated pool for plug '{poolRtId}'");
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

    internal static Exception CommonFailedGettingPoolByName(string tenantId, string poolName, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get pool with name '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingPlugs(string tenantId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plugs", exception);
    }

    internal static Exception CommonFailedGettingPlugs(string tenantId, OctoObjectId poolRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plugs of pool '{poolRtId}'", exception);
    }

    internal static Exception CommonFailedCreatePool(string tenantId, string poolName, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to create pool '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get plug '{plugRtId}'", exception);
    }

    internal static Exception CommonFailedSetPoolState(string tenantId, OctoObjectId poolRtId, PoolStates state,
        Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to set state of pool '{poolRtId}' to '{state}'", exception);
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

    internal static Exception CommonFailedIsTenantExisting(string tenantId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to check if tenant exists", exception);
    }
}