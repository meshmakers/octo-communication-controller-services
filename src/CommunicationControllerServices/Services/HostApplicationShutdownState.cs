using Microsoft.Extensions.Hosting;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Production <see cref="IShutdownState"/> backed by
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>. Registered as
/// a singleton — the underlying token never resets, so reading the property
/// is allocation-free.
/// </summary>
public sealed class HostApplicationShutdownState : IShutdownState
{
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Captures the host application lifetime so <see cref="IsShuttingDown"/>
    /// can poll its <see cref="IHostApplicationLifetime.ApplicationStopping"/>
    /// cancellation token without allocating.
    /// </summary>
    public HostApplicationShutdownState(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public bool IsShuttingDown => _lifetime.ApplicationStopping.IsCancellationRequested;
}
