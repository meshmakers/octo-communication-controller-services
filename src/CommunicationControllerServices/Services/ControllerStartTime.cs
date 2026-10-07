namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     The instant this controller process started, captured explicitly as the first statement of
///     <c>Program</c> and registered as a singleton (AB#5866). Broadcast consumers drop messages
///     whose timestamp lies before it (<see cref="IsBeforeStart" />).
/// </summary>
/// <remarks>
///     Replaces <c>Constants.StartTime</c>, a <c>static readonly … = DateTime.UtcNow</c> field in a
///     class without a static constructor (beforefieldinit). Only the consumers read it, so the
///     runtime initialised it on the FIRST CONSUMED MESSAGE — which made exactly that message
///     "older than the start" and dropped it. After every controller start the first tenant update
///     lost its Pre half: no <c>CkModelChanged</c> relay, no adapter restart relay, and the Pos
///     stayed unpaired in the static pairing dictionary. Capturing the value eagerly and injecting
///     it removes the lazy-initialisation trap and makes the filter testable without far-future
///     timestamps.
/// </remarks>
internal sealed class ControllerStartTime
{
    public ControllerStartTime(DateTime startedAtUtc)
    {
        StartedAtUtc = startedAtUtc.Kind == DateTimeKind.Utc
            ? startedAtUtc
            : DateTime.SpecifyKind(startedAtUtc.ToUniversalTime(), DateTimeKind.Utc);
    }

    /// <summary>The UTC instant the controller process started.</summary>
    public DateTime StartedAtUtc { get; }

    /// <summary>
    ///     True when a message stamped <paramref name="messageTimestamp" /> was published before this
    ///     controller process started and must therefore be ignored.
    /// </summary>
    public bool IsBeforeStart(DateTime messageTimestamp) => messageTimestamp < StartedAtUtc;
}
