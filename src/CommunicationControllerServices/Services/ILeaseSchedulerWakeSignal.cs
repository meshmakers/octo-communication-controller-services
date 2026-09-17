namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Lets the two events that can make a lease grantable pull the next scheduling round forward
///     instead of waiting for the tick (AB#4924 §9.6).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Without this, every grant is exactly <c>LeaseSchedulerIntervalSeconds</c> after the
///         previous one</b> — measured 5.11–5.14 s apart for runs that took 0.43–1.36 s, so the member
///         was idle 75–92 % of the time and throughput per member was capped near twelve executions a
///         minute no matter how small the work was.
///     </para>
///     <para>
///         <b>Two events, not one.</b> A <i>release</i> frees a member that queued work could go to.
///         An <i>enqueue</i> puts work in front of a member that may be idle <i>already</i> — and that
///         is the half that decides whether the <c>Interactive</c> execution class means anything,
///         because a Studio Execute otherwise waits a full tick before anything starts at all. The
///         first walk only measured the release side, which is why the enqueue side was missing from
///         the original write-up: with two borrowers enqueuing continuously, somebody was always
///         working and the idle-member case never occurred.
///     </para>
///     <para>
///         🔴 <b>The periodic tick stays, and this never replaces it.</b> It covers everything no
///         signal reaches: a lease reaching its TTL, a topology change (a newly deployed
///         <c>Leased</c> adapter), and — the one that would otherwise bite in production — the
///         multi-pod case, where the release or the enqueue happened on a <i>different</i> controller
///         instance and this one heard nothing. Same reasoning as
///         <c>AdapterLeasingMetrics.SweepStaleDeploymentSites</c>: several controllers act independently and
///         each sees a different subset, so nothing may depend on having observed an event.
///     </para>
/// </remarks>
internal interface ILeaseSchedulerWakeSignal
{
    /// <summary>
    ///     Asks for a scheduling round as soon as the minimum gap allows. Cheap, non-blocking, and
    ///     safe to call from a hub callback or a request path.
    /// </summary>
    /// <remarks>
    ///     <b>Coalescing:</b> at most one round is ever pending. Six executions enqueued in 320 ms —
    ///     the shape the round-robin walk produced — must cause one extra round, not six: a round
    ///     reads the queue of <i>every</i> borrower of the pool, so one per work item would turn a
    ///     burst of work into a burst of database reads.
    /// </remarks>
    /// <param name="reason">What made a grant possible. Logged at debug level.</param>
    void RequestRound(string reason);

    /// <summary>
    ///     Waits for either a requested round or the periodic interval, whichever comes first.
    /// </summary>
    /// <returns><c>true</c> when a round was requested, <c>false</c> when the interval elapsed.</returns>
    Task<bool> WaitForRoundAsync(TimeSpan interval, CancellationToken cancellationToken);
}
