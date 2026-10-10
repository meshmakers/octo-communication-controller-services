namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// When may an offline-reconciliation sweep start judging "nobody owns this" (AB#6418 follow-up, AB#4699)?
///
/// A fixed delay from the controller start does not work: after a controller restart the operators
/// (and adapters) reconnect on their own SignalR reconnect schedule, which on test-2 took more than a
/// minute beyond the 5 minute grace, so the sweep wrote Offline for every Cloud site and Cloud adapter
/// shortly before the owners came back and claimed them again. The sweep therefore waits for a quiet
/// period: one grace after the <i>last</i> registration this pod has seen. While owners are still
/// arriving (several operators, staggered reconnect backoffs) the sweep keeps waiting; the grace only
/// has to cover the time one owner needs to claim everything it hosts.
///
/// Two bounds keep the original goal (an ownerless site ends up Offline) intact:
/// <list type="bullet">
/// <item>If nobody registers at all, the owners are not reconnecting (central operator down, edge
/// device gone) and the sites really are ownerless — the sweep judges after
/// <see cref="MaxWaitIntervals"/> intervals from the start.</item>
/// <item>The same bound is a hard cap for the wait: an owner that keeps reconnecting (flapping edge
/// device) cannot hold the sweep back beyond <see cref="MaxWaitIntervals"/> intervals from the start.</item>
/// </list>
/// </summary>
internal static class ReconciliationGrace
{
    /// <summary>
    /// Sweep intervals after the start at which the sweep judges in any case. Deliberately a constant,
    /// not an option: the value only bounds how long a site without any owner keeps its stale state
    /// after a controller restart.
    /// </summary>
    internal const int MaxWaitIntervals = 3;

    /// <summary>
    /// Returns <c>true</c> when the sweep may judge ownership.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <param name="startedAt">When this pod's sweep service was created (process start).</param>
    /// <param name="lastRegistrationAt">When the latest operator / adapter registered on this pod, if any.</param>
    /// <param name="grace">Quiet period owners get to claim what they host (the sweep interval).</param>
    internal static bool IsOver(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset? lastRegistrationAt,
        TimeSpan grace)
    {
        if (now - startedAt >= grace * MaxWaitIntervals)
        {
            return true;
        }

        return lastRegistrationAt is { } last && now - last >= grace;
    }
}
