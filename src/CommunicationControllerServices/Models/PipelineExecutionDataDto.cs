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
}