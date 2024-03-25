using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Implementation of the pipeline debug service
/// </summary>
internal class PipelineDebugService : IPipelineDebugService
{
    private readonly ConcurrentDictionary<Tuple<string, RtEntityId>, string> _debugInfo = new();
    public Task CacheDebugInfo(string tenantId, RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId, string debugInfo)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        _debugInfo.AddOrUpdate(key, debugInfo, (_, _) => debugInfo);
        return Task.CompletedTask;
    }

    public Task<string?> GetDebugInformation(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        _debugInfo.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }
}