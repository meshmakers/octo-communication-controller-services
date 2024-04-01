using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;

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

    internal static Exception AdapterNotFound(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Adapter '{adapterRtEntityId}' does not exist");
    }

    internal static Exception AdapterNotAssociatedToPool(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Adapter '{adapterRtEntityId}' is not associated with a pool");
    }

    internal static Exception CommonGettingPoolOfAdapter(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated pool for adapter '{adapterRtEntityId}'", exception);
    }

    internal static Exception PoolNotFound(string tenantId, OctoObjectId poolRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pool '{poolRtId}'");
    }

    internal static Exception PipelineNotFound(string tenantId, OctoObjectId pipelineRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Pipeline '{pipelineRtId}' does not exist");
    }

    internal static Exception PipelineNotAssociatedToAdapter(string tenantId, OctoObjectId pipelineRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Pipeline '{pipelineRtId}' is not associated with an adapter.");
    }

    internal static Exception CommonGettingAdapterByDataPipeline(string tenantId, OctoObjectId pipelineRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get associated adapter for data pipeline '{pipelineRtId}'", exception);
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

    internal static Exception CommonFailedGettingAdapter(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get adapter '{adapterRtEntityId}'", exception);
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

    internal static Exception CommonFailedSetAdapterDeploymentState(string tenantId, RtEntityId adapterRtEntityId, RtDeploymentStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of adapter '{adapterRtEntityId}' to '{state}'", exception);
    }
    
    internal static Exception CommonFailedSetAdapterCommunicationState(string tenantId, RtEntityId adapterRtEntityId, RtCommunicationStateEnum state, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set communication state of adapter '{adapterRtEntityId}' to '{state}'", exception);
    }

    internal static Exception CommonFailedIsTenantExisting(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to check if tenant exists", exception);
    }

    internal static Exception CommonFailedGettingPools(string tenantId, Exception exception)
    {   
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pools", exception);
    }

    internal static Exception CommonFailedGettingPipeline(string tenantId, RtEntityId pipelineRtEntityId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pipeline '{pipelineRtEntityId}'", exception);
    }
    
    internal static Exception CommonFailedGettingPipeline(string tenantId, OctoObjectId pipelineRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get pipeline '{pipelineRtId}'", exception);
    }

    internal static Exception CommonFailedSetTriggerDeploymentState(string tenantId, OctoObjectId triggerRtId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of trigger '{triggerRtId}' to '{deploymentState}'", exception);
    }

    internal static Exception CommonFailedGettingTriggers(string tenantId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get triggers", exception);
    }

    internal static Exception CommonFailedSetPipelineDeploymentState(string tenantId, RtEntityId pipelineRtEntityId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to set deployment state of pipeline '{pipelineRtEntityId}' to '{deploymentState}'", exception);
    }

    internal static Exception DataPipelineNotFound(string tenantId, OctoObjectId dataPipelineRtId)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Data pipeline '{dataPipelineRtId}' does not exist");
    }

    internal static Exception CommonFailedGettingByDataPipeline(string tenantId, OctoObjectId dataPipelineRtId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get by data pipeline '{dataPipelineRtId}'", exception);
    }

    internal static Exception CommonFailedGettingAdapterByPipeline(string tenantId, RtEntityId pipelineRtEntityId, Exception exception)
    {
        return new CommunicationRepositoryException($"[{tenantId}] Failed to get adapter by pipeline '{pipelineRtEntityId}'", exception);
    }
}