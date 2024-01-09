using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class SocketServiceException : Exception
{
    public SocketServiceException()
    {
    }

    public SocketServiceException(string message) : base(message)
    {
    }

    public SocketServiceException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static Exception CommonFailedCannotLoadSocketConfiguration(string tenantId, OctoObjectId socketRtId, Exception exception)
    {
        return new SocketServiceException($"[{tenantId}] Failed to load socket '{socketRtId}' configuration", exception);
    }

    internal static Exception CommonFailedSetSocketState(string tenantId, OctoObjectId socketRtId, RtAdapterStateEnum adapterState, Exception exception)
    {
        return new SocketServiceException($"[{tenantId}] Failed to set socket '{socketRtId}' state to '{adapterState}'", exception);
    }
}
