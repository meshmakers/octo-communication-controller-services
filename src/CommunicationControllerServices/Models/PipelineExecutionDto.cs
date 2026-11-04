using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// DTO for pipeline execution query results
/// </summary>
public record PipelineExecutionDto
{
    /// <summary>
    /// Unique identifier for this execution (GUID as string)
    /// </summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// Pipeline that was executed
    /// </summary>
    public required RtEntityId PipelineRtEntityId { get; init; }

    /// <summary>
    /// Adapter that executed the pipeline
    /// </summary>
    public required RtEntityId AdapterRtEntityId { get; init; }

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
    /// AB#4924: nullable since System.Communication 4.0.0. A Queued execution has not
    /// started yet — see QueuedAt for when it entered the pool queue.
    public required DateTime? StartedAt { get; init; }

    /// <summary>
    /// When the execution completed (null if still running)
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Duration in milliseconds (null if still running)
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// What a failed execution says about its writes (AB#8156): <c>OutcomeUnknown</c> when a write was not
    /// acknowledged and may have taken effect, <c>Failed</c> for an ordinary failure. Null for an execution that
    /// did not fail, for an adapter that reported no kind, and for interrupted or offline-buffered executions.
    /// </summary>
    public RtPipelineExecutionFailureKindEnum? FailureKind { get; init; }

    /// <summary>
    /// Creates a DTO from the entity
    /// </summary>
    public static PipelineExecutionDto FromEntity(RtPipelineExecution entity, RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId)
    {
        return new PipelineExecutionDto
        {
            ExecutionId = entity.ExecutionId ?? string.Empty,
            PipelineRtEntityId = pipelineRtEntityId,
            AdapterRtEntityId = adapterRtEntityId,
            Status = entity.Status,
            TriggerType = entity.TriggerType,
            StartedAt = entity.StartedAt,
            CompletedAt = entity.CompletedAt,
            DurationMs = (int?)entity.DurationMs,
            ErrorMessage = entity.ErrorMessage,
            FailureKind = entity.Status == RtPipelineExecutionStatusEnum.Failed ? entity.FailureKind : null
        };
    }
}
