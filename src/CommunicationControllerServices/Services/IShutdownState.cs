namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Reports whether the host is in the middle of shutting down. Used by the
/// SignalR hubs to suppress <c>SetCommunicationStateOfflineAsync</c> calls
/// during a rolling-upgrade overlap: when the old pod's <c>OnDisconnectedAsync</c>
/// handlers fire (because the operator / adapter has reconnected to the new
/// pod), writing <c>Offline</c> would overwrite the <c>Online</c> state the new
/// pod has already written, leaving the UI stuck at Offline. The new pod is
/// the authoritative state holder once shutdown begins.
/// </summary>
public interface IShutdownState
{
    /// <summary>
    /// <c>true</c> once <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>
    /// has been signalled.
    /// </summary>
    bool IsShuttingDown { get; }
}
