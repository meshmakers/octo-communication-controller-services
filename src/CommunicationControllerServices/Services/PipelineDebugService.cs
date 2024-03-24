using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Implementation of the pipeline debug service
/// </summary>
internal class PipelineDebugService : IPipelineDebugService
{
    private readonly ConcurrentDictionary<Tuple<string, OctoObjectId>, string> _debugInfo = new();
    public Task CacheDebugInfo(string tenantId, RtEntityId adapterRtEntityId, OctoObjectId pipelineRtId, string debugInfo)
    {
        var key = new Tuple<string, OctoObjectId>(tenantId, pipelineRtId);
        _debugInfo.AddOrUpdate(key, debugInfo, (_, _) => debugInfo);
        return Task.CompletedTask;
    }

    public Task<string?> GetDebugInformation(string tenantId, OctoObjectId pipelineRtId)
    {
        var key = new Tuple<string, OctoObjectId>(tenantId, pipelineRtId);
        _debugInfo.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }
}