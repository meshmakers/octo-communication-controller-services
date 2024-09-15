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
    /// <returns></returns>
    Task<IEnumerable<Guid>> GetPipelineExecutionsAsync(string tenantId, RtEntityId pipelineRtEntityId);
    
    /// <summary>
    /// Returns the latest pipeline execution id
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <returns></returns>
    Task<Guid> GetLatestPipelineExecutionAsync(string tenantId, RtEntityId pipelineRtEntityId);

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
    /// <param name="nodePath">The path of the node</param>
    /// <returns></returns>
    Task<DebugPointDto?> GetDebugPointAsync(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId, NodePath nodePath);
}