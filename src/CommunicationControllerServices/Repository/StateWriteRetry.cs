using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// AB#6435: bounded retry for communication-state writes, since AB#9121 also for the deployment-state
/// writes of pipelines and pipeline triggers. Two writers can touch the same deployment site, adapter,
/// pipeline or trigger document in parallel (operator claim, disconnect handling, reconciliation sweep,
/// tenant setup racing an adapter re-registration). MongoDB then aborts one transaction with a WriteConflict
/// (TransientTransactionError). The write is idempotent, so the controller repeats it instead of leaving
/// the stored state stale until the next claim. For communication states the timestamp guard keeps stale
/// values out; the pipeline and trigger deployment-state writes have no guard and are last-writer-wins, as
/// the first attempt would have been.
/// Non-transient errors are never retried.
/// </summary>
internal static class StateWriteRetry
{
    /// <summary>Maximum number of repeats after the first attempt.</summary>
    internal const int MaxRetries = 2;

    private const int WriteConflictCode = 112;
    private const string TransientTransactionErrorLabel = "TransientTransactionError";

    /// <summary>
    /// Runs <paramref name="attempt"/>; on a transient MongoDB error repeats it up to
    /// <see cref="MaxRetries"/> times with a short jittered backoff. The last exception
    /// surfaces unchanged.
    /// </summary>
    /// <param name="attempt">One complete write attempt (own session and transaction).</param>
    /// <param name="logger">Logger for the warning on each retry.</param>
    /// <param name="description">What is written, for the log (tenant, entity, state).</param>
    /// <param name="delay">Delay function; tests replace it. Defaults to <see cref="Task.Delay(TimeSpan)"/>.</param>
    internal static async Task ExecuteAsync(Func<Task> attempt, ILogger logger, string description,
        Func<TimeSpan, Task>? delay = null)
    {
        delay ??= span => Task.Delay(span);
        for (var retry = 0;; retry++)
        {
            try
            {
                await attempt();
                return;
            }
            catch (Exception e) when (retry < MaxRetries && IsTransient(e))
            {
                var backoff = NextBackoff(retry);
                logger.LogWarning(e,
                    "Transient MongoDB error while writing {Description}; retry {Retry}/{MaxRetries} in {BackoffMs} ms",
                    description, retry + 1, MaxRetries, (int)backoff.TotalMilliseconds);
                await delay(backoff);
            }
        }
    }

    /// <summary>
    /// True when the exception or one of its causes is a MongoDB transient transaction error or
    /// a write conflict.
    /// </summary>
    internal static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsTransient))
            {
                return true;
            }

            switch (current)
            {
                case MongoException mongo when mongo.HasErrorLabel(TransientTransactionErrorLabel):
                case MongoCommandException { Code: WriteConflictCode }:
                case MongoWriteException { WriteError.Code: WriteConflictCode }:
                case MongoBulkWriteException bulk when bulk.WriteErrors.Any(w => w.Code == WriteConflictCode):
                    return true;
            }
        }

        return false;
    }

    // 50-100 ms for the first retry, 100-200 ms for the second: short, and different per writer.
    private static TimeSpan NextBackoff(int retry)
    {
        var baseMs = 50 * (retry + 1);
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, baseMs + 1));
    }
}
