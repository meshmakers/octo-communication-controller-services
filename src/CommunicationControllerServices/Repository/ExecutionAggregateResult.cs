namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Result of an execution aggregation query
/// </summary>
/// <param name="SuccessCount">Number of successful executions</param>
/// <param name="FailureCount">Number of failed executions</param>
/// <param name="TotalDurationMs">Total duration of all executions in milliseconds</param>
/// <param name="ExecutionCount">Total number of executions with duration (for avg calculation)</param>
public record ExecutionAggregateResult(
    int SuccessCount,
    int FailureCount,
    long TotalDurationMs,
    int ExecutionCount
)
{
    /// <summary>
    /// Average execution duration in milliseconds
    /// </summary>
    public long AvgDurationMs => ExecutionCount > 0 ? TotalDurationMs / ExecutionCount : 0;
}
