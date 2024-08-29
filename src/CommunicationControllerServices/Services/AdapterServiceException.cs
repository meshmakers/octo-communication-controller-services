using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterServiceException : Exception
{
    private AdapterServiceException()
    {
    }

    private AdapterServiceException(string message) : base(message)
    {
    }

    private AdapterServiceException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static Exception CommonFailedSetAdapterDeploymentState(string tenantId, RtEntityId adapterRtEntityId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtEntityId}' deployment state to '{deploymentState}'", exception);
    }
    internal static Exception CommonFailedSetAdapterCommunicationState(string tenantId, RtEntityId adapterRtEntityId, RtCommunicationStateEnum communicationState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtEntityId}' communication state to '{communicationState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadAdapterConfiguration(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to load adapter '{adapterRtEntityId}' configuration", exception);
    }

    internal static Exception AdapterNotLoaded(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Adapter '{adapterRtEntityId}' not loaded.");
    }

    internal static Exception TenantNotEnabled(string tenantId)
    {
        return new AdapterServiceException($"[{tenantId}] Tenant not enabled.");
    }

    internal static Exception PipelineNotFound(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Pipeline '{pipelineRtEntityId}' not found."); 
    }

    public static Exception DataPipelineNotFound(string tenantId, RtEntityId rtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Data pipeline of pipeline '{rtEntityId}' not found.");
    }

    public static Exception TenantReloadFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to reload tenant", exception);
    }
}

