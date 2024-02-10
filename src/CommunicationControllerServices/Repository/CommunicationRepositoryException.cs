using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

internal class CommunicationRepositoryException : Exception
{
    public CommunicationRepositoryException()
    {
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public CommunicationRepositoryException(string message) : base(message)
    {
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public CommunicationRepositoryException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception PlugNotFound(string tenantId, OctoObjectId plugRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Plug '{plugRtId}' does not exist");
    }

    internal static Exception PlugNotAssociatedToPool(string tenantId, OctoObjectId plugRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Plug '{plugRtId}' is not associated with a pool");
    }

    internal static Exception CommonGettingPoolOfPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated pool for plug '{plugRtId}'", exception);
    }

    internal static Exception PoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated pool for plug '{poolRtId}'");
    }

    internal static Exception PlugMappingNotFound(string tenantId, OctoObjectId plugMappingRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Plug mapping '{plugMappingRtId}' does not exist");
    }

    internal static Exception PlugMappingNotAssociatedToPlug(string tenantId, OctoObjectId plugMappingRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Plug mapping '{plugMappingRtId}' is not associated with a plug");
    }

    internal static Exception CommonGettingPlugByMapping(string tenantId, OctoObjectId plugMappingRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated plug for plug mapping '{plugMappingRtId}'", exception);
    }

    internal static Exception CommonFailedGettingPoolByName(string tenantId, string poolName, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pool with name '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingPlugs(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get plugs", exception);
    }

    internal static Exception CommonFailedGettingPlugs(string tenantId, OctoObjectId poolRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get plugs of pool '{poolRtId}'", exception);
    }
    
    internal static Exception CommonOperationFailed(OperationResult operationResult)
    {
        return new CommunicationRepositoryException($"Operation failed with with messages: " + operationResult.GetMessages() );
    }

    internal static Exception CommonFailedCreatePool(string tenantId, string poolName, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to create pool '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get plug '{plugRtId}'", exception);
    }

    internal static Exception CommonFailedSetPoolDeploymentState(string tenantId, OctoObjectId poolRtId, RtDeploymentStateEnum state,
        Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of pool '{poolRtId}' to '{state}'", exception);
    }
    
    internal static Exception CommonFailedSetPoolCommunicationState(string tenantId, OctoObjectId poolRtId, RtCommunicationStateEnum state,
        Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set communication state of pool '{poolRtId}' to '{state}'", exception);
    }

    internal static Exception CommonFailedSetPlugDeploymentState(string tenantId, OctoObjectId plugRtId, RtDeploymentStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of plug '{plugRtId}' to '{state}'", exception);
    }
    
    internal static Exception CommonFailedSetPlugCommunicationState(string tenantId, OctoObjectId plugRtId, RtCommunicationStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set communication state of plug '{plugRtId}' to '{state}'", exception);
    }
    
    internal static Exception CommonFailedSetSocketDeploymentState(string tenantId, OctoObjectId socketRtId, RtDeploymentStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of socket '{socketRtId}' to '{state}'", exception);
    }
    
    internal static Exception CommonFailedSetSocketCommunicationState(string tenantId, OctoObjectId socketRtId, RtCommunicationStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set communication state of socket '{socketRtId}' to '{state}'", exception);
    }

    internal static Exception CommonGettingPlugGroupsOfPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get plug groups of plug '{plugRtId}'", exception);
    }

    internal static Exception PlugGroupNotAssociatedToPlug(string tenantId, OctoObjectId plugGroupRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Plug group '{plugGroupRtId}' is not associated with a plug");
    }

    internal static Exception PlugGroupNotFound(string tenantId, OctoObjectId plugGroupRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Plug group '{plugGroupRtId}' does not exist");
    }

    internal static Exception CommonGettingPlugByGroup(string tenantId, OctoObjectId plugGroupRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get plug by group '{plugGroupRtId}'", exception);
    }

    internal static Exception CommonFailedIsTenantExisting(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to check if tenant exists", exception);
    }

    internal static Exception CommonFailedGettingSockets(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get sockets", exception);
    }

    internal static Exception SocketNotFound(string tenantId, OctoObjectId socketRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Socket '{socketRtId}' does not exist");
    }

    public static Exception CommonFailedGettingSocket(string tenantId, OctoObjectId socketRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get socket '{socketRtId}'", exception);
    }
}