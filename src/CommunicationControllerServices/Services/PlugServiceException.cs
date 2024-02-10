using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PlugServiceException : Exception
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
    
    internal static Exception CommonFailedSetPlugDeploymentState(string tenantId, OctoObjectId plugRtId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to set plug '{plugRtId}' deployment state to '{deploymentState}'", exception);
    }
    internal static Exception CommonFailedSetPlugCommunicationState(string tenantId, OctoObjectId plugRtId, RtCommunicationStateEnum communicationState, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to set plug '{plugRtId}' communication state to '{communicationState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadPlugConfiguration(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to load plug '{plugRtId}' configuration", exception);
    }
}

