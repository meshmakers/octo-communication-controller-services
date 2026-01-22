using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// DTO for pipeline statistics
/// </summary>
public record PipelineStatisticsDto
{
    /// <summary>
    /// Pipeline this statistics is for
    /// </summary>
    public required RtEntityId PipelineRtEntityId { get; init; }

    /// <summary>
    /// Successful executions in the last hour
    /// </summary>
    public int LastHourSuccessCount { get; init; }

    /// <summary>
    /// Failed executions in the last hour
    /// </summary>
    public int LastHourFailureCount { get; init; }

    /// <summary>
    /// Average duration in the last hour (ms)
    /// </summary>
    public int LastHourAvgDurationMs { get; init; }

    /// <summary>
    /// Successful executions in the last 12 hours
    /// </summary>
    public int Last12HoursSuccessCount { get; init; }

    /// <summary>
    /// Failed executions in the last 12 hours
    /// </summary>
    public int Last12HoursFailureCount { get; init; }

    /// <summary>
    /// Average duration in the last 12 hours (ms)
    /// </summary>
    public int Last12HoursAvgDurationMs { get; init; }

    /// <summary>
    /// Successful executions in the last 24 hours
    /// </summary>
    public int Last24HoursSuccessCount { get; init; }

    /// <summary>
    /// Failed executions in the last 24 hours
    /// </summary>
    public int Last24HoursFailureCount { get; init; }

    /// <summary>
    /// Average duration in the last 24 hours (ms)
    /// </summary>
    public int Last24HoursAvgDurationMs { get; init; }

    /// <summary>
    /// Successful executions in the last 30 days
    /// </summary>
    public int Last30DaysSuccessCount { get; init; }

    /// <summary>
    /// Failed executions in the last 30 days
    /// </summary>
    public int Last30DaysFailureCount { get; init; }

    /// <summary>
    /// Average duration in the last 30 days (ms)
    /// </summary>
    public int Last30DaysAvgDurationMs { get; init; }

    /// <summary>
    /// When statistics were last updated
    /// </summary>
    public DateTime? LastUpdatedAt { get; init; }

    /// <summary>
    /// When the last execution occurred
    /// </summary>
    public DateTime? LastExecutionAt { get; init; }

    /// <summary>
    /// Creates a DTO from the entity
    /// </summary>
    public static PipelineStatisticsDto FromEntity(RtPipelineStatistics entity, RtEntityId pipelineRtEntityId)
    {
        return new PipelineStatisticsDto
        {
            PipelineRtEntityId = pipelineRtEntityId,
            LastHourSuccessCount = entity.LastHourSuccessCount,
            LastHourFailureCount = entity.LastHourFailureCount,
            LastHourAvgDurationMs = entity.LastHourAvgDurationMs,
            Last12HoursSuccessCount = entity.Last12HoursSuccessCount,
            Last12HoursFailureCount = entity.Last12HoursFailureCount,
            Last12HoursAvgDurationMs = entity.Last12HoursAvgDurationMs,
            Last24HoursSuccessCount = entity.Last24HoursSuccessCount,
            Last24HoursFailureCount = entity.Last24HoursFailureCount,
            Last24HoursAvgDurationMs = entity.Last24HoursAvgDurationMs,
            Last30DaysSuccessCount = entity.Last30DaysSuccessCount,
            Last30DaysFailureCount = entity.Last30DaysFailureCount,
            Last30DaysAvgDurationMs = entity.Last30DaysAvgDurationMs,
            LastUpdatedAt = entity.LastUpdatedAt,
            LastExecutionAt = entity.LastExecutionAt
        };
    }
}
