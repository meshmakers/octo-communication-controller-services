namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Represents the data of a pipeline execution
/// </summary>
public record PipelineExecutionDataDto
{
    /// <summary>
    /// ID of the pipeline execution
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Execution start date and time
    /// </summary>
    public required DateTime DateTime { get; init; }

    /// <summary>
    /// Execution status (Running, Completed, Failed, Interrupted, Cancelled).
    /// Null if status is unknown (e.g. from in-memory debug cache).
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Duration of the execution in milliseconds. Null if still running or unknown.
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Error message if the execution failed. Null otherwise.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether debug point data is available for this execution.
    /// </summary>
    public bool HasDebugData { get; init; }
}
