namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     Settings of the pipeline method handler host (AB#6879), bound from the configuration section
///     <c>PipelineMethods</c> (environment variable prefix <c>OCTO_PIPELINEMETHODS__</c>).
/// </summary>
public sealed class PipelineMethodOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "PipelineMethods";

    /// <summary>
    ///     Switches the handler host off. When off, the controller consumes no method queue and announces nothing;
    ///     methods declared <c>handler: pipeline</c> then answer <c>METHOD_HANDLER_NOT_AVAILABLE</c> at the dispatcher.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the host re-announces itself and re-checks the pipelines (default 15 s, like every handler).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Time to live of an announcement (default 45 s, three heartbeats).</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How long the host waits until a method endpoint accepts messages before it gives up on that method.</summary>
    public TimeSpan EndpointReadyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How often the host looks at the execution of a started pipeline while it waits for the outcome
    ///     (the execution is written by whichever controller node the adapter is connected to, so the host reads
    ///     the store instead of relying on an in-process signal).
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Upper bound of the poll interval; the interval grows by 50 % per round up to this value.</summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    ///     When true, the controller fails its startup (with a message that names every offending method) if a
    ///     method declared <c>handler: pipeline</c> has no deployed pipeline with the method id as well-known name
    ///     or declares sensitive parameters. Default false: such a method is not announced, an ERROR is logged
    ///     and the check repeats on every heartbeat, so one misconfigured tenant never takes the controller down.
    /// </summary>
    public bool FailStartupOnInvalidMethod { get; set; }
}
