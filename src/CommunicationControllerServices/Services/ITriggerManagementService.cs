using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Services that manage the triggers for the tenant
/// </summary>
public interface ITriggerManagementService
{
    /// <summary>
    /// Execute the pipeline for the tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="meshPipelineRtEntityId">The pipeline entity object id</param>
    /// <param name="pipelineInput">The input for the pipeline</param>
    /// <returns>The pipeline execution id, if the start of execution was successful</returns>
    Task<Guid> StartExecutePipelineAsync(string tenantId, RtEntityId meshPipelineRtEntityId, string? pipelineInput);
    
    /// <summary>
    /// Remove the schedule for the triggers of the tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    Task RemoveScheduleAsync(string tenantId);
    
    /// <summary>
    /// Update the schedule for the triggers of the tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    Task UpdateScheduleAsync(string tenantId);
}