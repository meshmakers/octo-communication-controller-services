using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterServiceException : Exception
{
    public AdapterServiceException()
    {
    }

    public AdapterServiceException(string message) : base(message)
    {
    }

    public AdapterServiceException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static Exception CommonFailedSetAdapterDeploymentState(string tenantId, OctoObjectId adapterRtId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtId}' deployment state to '{deploymentState}'", exception);
    }
    internal static Exception CommonFailedSetAdapterCommunicationState(string tenantId, OctoObjectId adapterRtId, RtCommunicationStateEnum communicationState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtId}' communication state to '{communicationState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadAdapterConfiguration(string tenantId, OctoObjectId adapterRtId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to load adapter '{adapterRtId}' configuration", exception);
    }

    internal static Exception AdapterNotLoaded(string tenantId, OctoObjectId adapterRtId)
    {
        return new AdapterServiceException($"[{tenantId}] Adapter '{adapterRtId}' not loaded.");
    }
}

