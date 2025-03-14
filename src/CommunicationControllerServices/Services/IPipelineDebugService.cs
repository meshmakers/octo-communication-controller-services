using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Interface for the pipeline debug service
/// </summary>
public interface IPipelineDebugService
{
    /// <summary>
    /// Cache debug information
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <param name="pipelineExecutionId">Guid that identifies the pipeline execution instance</param>
    /// <param name="debugPoint">Debug information of a node execution</param>
    /// <returns></returns>
    Task CacheDebugPointAsync(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId,
        DebugPointDto debugPoint);

    /// <summary>
    /// Returns cached pipeline execution ids
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <param name="skip">Amount of pipeline executions to skip</param>
    /// <param name="take">Number of pipeline executions to take</param>
    /// <returns></returns>
    Task<IEnumerable<PipelineExecutionDataDto>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, int skip = 0, int take = 100);
    
    /// <summary>
    /// Returns the latest pipeline execution id
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <returns></returns>
    Task<PipelineExecutionDataDto> GetLatestPipelineExecutionAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Returns cached pipeline execution ids
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <param name="pipelineExecutionId">Guid that identifies the pipeline execution instance</param>
    /// <returns>A tree of debug point nodes that represent the execution debug information</returns>
    Task<IEnumerable<DebugPointNode>> GetPipelineExecutionDebugPointNodesAsync(string tenantId,
        RtEntityId pipelineRtEntityId, Guid pipelineExecutionId);

    /// <summary>
    /// Returns the debug information for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <param name="pipelineExecutionId">Guid that identifies the pipeline execution instance</param>
    /// <param name="nodeId">ID of the node</param>
    /// <returns></returns>
    Task<DebugPointDataDto?> GetDebugPointDataAsync(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId, string nodeId);
}