using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ILeaseSchedulerWakeSignal" />
internal sealed class LeaseSchedulerWakeSignal : ILeaseSchedulerWakeSignal, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    ///     🔴 Capacity ONE. That single number is what makes a burst coalesce into one extra round:
    ///     the second and every following request inside the same window find the semaphore full and
    ///     are dropped, because a round that has not run yet will see their work anyway. A larger
    ///     capacity — or a counter — would queue one round per work item and turn a burst of enqueues
    ///     into a burst of queue reads across every borrower of the pool.
    /// </summary>
    private readonly SemaphoreSlim _requested = new(0, 1);

    /// <inheritdoc />
    public void RequestRound(string reason)
    {
        try
        {
            _requested.Release();
            Logger.Debug("Adapter pool lease scheduling round requested: {Reason}", reason);
        }
        catch (SemaphoreFullException)
        {
            // A round is already pending and will see this work too. Not a failure and not worth a
            // log line of its own — under load this is the common case, which is the point.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown raced the caller. The scheduler is gone; there is nothing to wake.
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForRoundAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // WaitAsync with a timeout rather than Task.WhenAny over a delay task: WhenAny leaves the
        // loser running, so a service that ticks every few seconds for weeks accumulates one
        // abandoned timer per iteration.
        return await _requested.WaitAsync(interval, cancellationToken);
    }

    public void Dispose()
    {
        _requested.Dispose();
    }
}
