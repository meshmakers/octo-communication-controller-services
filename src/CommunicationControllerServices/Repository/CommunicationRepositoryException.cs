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

    internal static Exception AdapterNotFound(string tenantId, OctoObjectId adapterRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Adapter '{adapterRtId}' does not exist");
    }

    internal static Exception AdapterNotAssociatedToPool(string tenantId, OctoObjectId adapterRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Adapter '{adapterRtId}' is not associated with a pool");
    }

    internal static Exception CommonGettingPoolOfAdapter(string tenantId, OctoObjectId adapterRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated pool for adapter '{adapterRtId}'", exception);
    }

    internal static Exception PoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pool '{poolRtId}'");
    }

    internal static Exception DataPipelineNotFound(string tenantId, OctoObjectId dataPipelineRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Data pipeline '{dataPipelineRtId}' does not exist");
    }

    internal static Exception DataPipelineNotAssociatedToAdapter(string tenantId, OctoObjectId dataPipelineRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Data pipeline '{dataPipelineRtId}' is not associated with an adapter.");
    }

    internal static Exception CommonGettingAdapterByDataPipeline(string tenantId, OctoObjectId dataPipelineRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated adapter for data pipeline '{dataPipelineRtId}'", exception);
    }

    internal static Exception CommonFailedGettingPoolByName(string tenantId, string poolName, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pool with name '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingAdapters(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get adapters", exception);
    }

    internal static Exception CommonFailedGettingAdapters(string tenantId, OctoObjectId poolRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get adapters of pool '{poolRtId}'", exception);
    }
    
    internal static Exception CommonOperationFailed(OperationResult operationResult)
    {
        return new CommunicationRepositoryException($"Operation failed with with messages: " + operationResult.GetMessages() );
    }

    internal static Exception CommonFailedCreatePool(string tenantId, string poolName, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to create pool '{poolName}'", exception);
    }

    internal static Exception CommonFailedGettingAdapter(string tenantId, OctoObjectId adapterRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get adapter '{adapterRtId}'", exception);
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

    internal static Exception CommonFailedSetAdapterDeploymentState(string tenantId, OctoObjectId adapterRtId, RtDeploymentStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of adapter '{adapterRtId}' to '{state}'", exception);
    }
    
    internal static Exception CommonFailedSetAdapterCommunicationState(string tenantId, OctoObjectId adapterRtId, RtCommunicationStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set communication state of adapter '{adapterRtId}' to '{state}'", exception);
    }

    internal static Exception CommonFailedIsTenantExisting(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to check if tenant exists", exception);
    }
}