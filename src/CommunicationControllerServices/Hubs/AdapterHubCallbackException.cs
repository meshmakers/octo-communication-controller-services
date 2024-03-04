using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Exception for adapter hub callback
/// </summary>
public class AdapterHubCallbackException : Exception
{
    private AdapterHubCallbackException()
    {
    }

    private AdapterHubCallbackException(string message) : base(message)
    {
    }

    private AdapterHubCallbackException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception AdapterNotOnline(string tenantId, OctoObjectId adapterRtId)
    {
        throw new AdapterHubCallbackException($"[{tenantId}] Adapter '{adapterRtId}' not online.");
    }
}