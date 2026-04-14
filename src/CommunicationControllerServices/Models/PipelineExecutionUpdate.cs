using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Represents a single pipeline execution update for bulk operations
/// </summary>
public record PipelineExecutionUpdate
{
    /// <summary>
    /// Execution ID to update
    /// </summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// New status
    /// </summary>
    public required RtPipelineExecutionStatusEnum Status { get; init; }

    /// <summary>
    /// Completion timestamp
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Duration in milliseconds
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional output data (JSON string) from pipeline execution result
    /// </summary>
    public string? OutputData { get; init; }
}
