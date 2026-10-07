using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

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
    /// <param name="pipelineRtId">The runtime id of the pipeline</param>
    /// <param name="pipelineInput">The input for the pipeline</param>
    /// <param name="isDryRun">When true (M4-B.2), the adapter runs the pipeline with all
    /// dry-run-honouring Load nodes suppressing their real side effect; would-be payloads
    /// are recorded on the debug stream instead. Default false preserves classic semantics.</param>
    /// <param name="caller">The invoker of this manual execution, carried through so the pipeline
    /// can run as them (AB#5126). Null for an internal invocation — the pipeline then runs
    /// anonymously exactly as before.</param>
    /// <param name="callerAccessToken">The invoker's raw access token, for a node that must act as
    /// the invoker against another service (delegation, AB#5031). Null when none is available; never
    /// logged.</param>
    /// <param name="triggerType">What caused this execution, recorded on the execution entity when the
    /// work item is queued for a leased adapter. <see cref="RtPipelineTriggerTypeEnum.Scheduled"/> for a
    /// lease cron tick (AB#5863); everything else is a manual execute. Ignored on the dedicated path,
    /// where the adapter reports the execution and its trigger type itself.</param>
    /// <returns>The pipeline execution id, if the start of execution was successful</returns>
    Task<PipelineExecutionDataDto> StartExecutePipelineAsync(string tenantId, OctoObjectId pipelineRtId,
        string? pipelineInput, bool isDryRun = false,
        Meshmakers.Octo.Communication.Contracts.MessageObjects.ExecutePipelineCaller? caller = null,
        string? callerAccessToken = null,
        RtPipelineTriggerTypeEnum triggerType = RtPipelineTriggerTypeEnum.Manual);
    
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