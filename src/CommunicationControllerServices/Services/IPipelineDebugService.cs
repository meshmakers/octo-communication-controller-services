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
    /// <param name="adapterRtEntityId">Adapter runtime id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <param name="debugInfo">Debug information</param>
    /// <returns></returns>
    Task CacheDebugInfo(string tenantId, RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId, string debugInfo);

    /// <summary>
    /// Returns the debug information for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant id</param>
    /// <param name="pipelineRtEntityId">Pipeline runtime id</param>
    /// <returns></returns>
    Task<string?> GetDebugInformation(string tenantId, RtEntityId pipelineRtEntityId);
}