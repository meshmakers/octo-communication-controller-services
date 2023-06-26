using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

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
    
    internal static Exception CommonFailedSetPlugState(string tenantId, OctoObjectId plugRtId, AdapterStates adapterState, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to set plug '{plugRtId}' state to '{adapterState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadPlugConfiguration(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugServiceException($"[{tenantId}] Failed to load plug '{plugRtId}' configuration", exception);
    }
}

