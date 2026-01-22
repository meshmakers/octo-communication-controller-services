namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Request DTO for syncing buffered executions from adapter
/// </summary>
public record BufferedExecutionsSyncRequest
{
    /// <summary>
    /// List of buffered executions to sync
    /// </summary>
    public required IReadOnlyList<BufferedPipelineExecution> Executions { get; init; }
}
