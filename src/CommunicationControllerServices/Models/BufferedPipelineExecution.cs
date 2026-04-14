using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// DTO for a buffered pipeline execution (used for offline sync)
/// </summary>
public record BufferedPipelineExecution
{
    /// <summary>
    /// Sequence number for ordering and deduplication
    /// </summary>
    public required int SequenceNumber { get; init; }

    /// <summary>
    /// Unique identifier for this execution (GUID as string)
    /// </summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// Pipeline that was executed
    /// </summary>
    public required RtEntityId PipelineRtEntityId { get; init; }

    /// <summary>
    /// Execution status
    /// </summary>
    public required RtPipelineExecutionStatusEnum Status { get; init; }

    /// <summary>
    /// Trigger type
    /// </summary>
    public required RtPipelineTriggerTypeEnum TriggerType { get; init; }

    /// <summary>
    /// When the execution started
    /// </summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>
    /// When the execution completed (null if interrupted)
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Duration in milliseconds (null if interrupted)
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional input data for debugging (JSON string)
    /// </summary>
    public string? InputData { get; init; }

    /// <summary>
    /// Optional output data (JSON string) from pipeline execution result
    /// </summary>
    public string? OutputData { get; init; }
}
