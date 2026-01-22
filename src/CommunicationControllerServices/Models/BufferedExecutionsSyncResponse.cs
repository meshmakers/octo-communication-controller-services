namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Response DTO for syncing buffered executions
/// </summary>
public record BufferedExecutionsSyncResponse
{
    /// <summary>
    /// Number of executions successfully synced
    /// </summary>
    public required int SyncedCount { get; init; }

    /// <summary>
    /// Number of executions skipped (already existed)
    /// </summary>
    public required int SkippedCount { get; init; }

    /// <summary>
    /// Last processed sequence number
    /// </summary>
    public required int LastSequenceNumber { get; init; }
}
