using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Aggregated execution status of a data flow, computed from its child pipeline executions
/// </summary>
public record DataFlowStatusDto
{
    /// <summary>
    /// Runtime identifier of the data flow
    /// </summary>
    public required OctoObjectId DataFlowRtId { get; init; }

    /// <summary>
    /// Aggregated execution state of the data flow
    /// </summary>
    public required DataFlowExecutionState State { get; init; }

    /// <summary>
    /// Per-pipeline execution status details
    /// </summary>
    public required IReadOnlyList<PipelineStatusDto> Pipelines { get; init; }
}

/// <summary>
/// Execution status of an individual pipeline within a data flow
/// </summary>
public record PipelineStatusDto
{
    /// <summary>
    /// Runtime entity identifier of the pipeline
    /// </summary>
    public required RtEntityId PipelineRtEntityId { get; init; }

    /// <summary>
    /// CK type of the pipeline (e.g. "System.Communication/MeshPipeline" or "System.Communication/EdgePipeline")
    /// </summary>
    public required string PipelineType { get; init; }

    /// <summary>
    /// Current execution state of the pipeline
    /// </summary>
    public required PipelineExecutionState State { get; init; }

    /// <summary>
    /// When the last execution occurred (null if no recent executions)
    /// </summary>
    public DateTime? LastExecutionAt { get; init; }

    /// <summary>
    /// Summary statistics for the last hour
    /// </summary>
    public PipelineStatisticsSummaryDto? Statistics { get; init; }
}

/// <summary>
/// Summary statistics for a pipeline within the last hour
/// </summary>
public record PipelineStatisticsSummaryDto
{
    /// <summary>
    /// Number of successful executions in the last hour
    /// </summary>
    public int LastHourSuccessCount { get; init; }

    /// <summary>
    /// Number of failed executions in the last hour
    /// </summary>
    public int LastHourFailureCount { get; init; }

    /// <summary>
    /// Average duration in milliseconds in the last hour
    /// </summary>
    public int LastHourAvgDurationMs { get; init; }
}

/// <summary>
/// Aggregated execution state of a data flow
/// </summary>
public enum DataFlowExecutionState
{
    /// <summary>
    /// No recent executions for any pipeline
    /// </summary>
    Idle,

    /// <summary>
    /// At least one pipeline is currently running
    /// </summary>
    Running,

    /// <summary>
    /// All recent executions completed successfully (none running)
    /// </summary>
    Completed,

    /// <summary>
    /// At least one pipeline failed in the last hour (none running)
    /// </summary>
    Failed
}

/// <summary>
/// Execution state of an individual pipeline
/// </summary>
public enum PipelineExecutionState
{
    /// <summary>
    /// No recent executions
    /// </summary>
    Idle,

    /// <summary>
    /// Pipeline is currently running
    /// </summary>
    Running,

    /// <summary>
    /// Most recent execution completed successfully
    /// </summary>
    Completed,

    /// <summary>
    /// Most recent execution failed
    /// </summary>
    Failed
}
