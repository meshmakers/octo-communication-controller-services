using System.Collections.Concurrent;
using System.Globalization;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Implementation of the pipeline debug service
/// </summary>
internal class PipelineDebugService : IPipelineDebugService
{
    private class PipelineExecutionRecord(DateTime dateTime)
    {
        public DateTime DateTime { get; } = dateTime;
        private readonly ConcurrentDictionary<NodePath, DebugPointDto> _debugInfo = new();

        public void Add(NodePath nodePath, DebugPointDto debugPoint)
        {
            _debugInfo.AddOrUpdate(nodePath, debugPoint, (_, _) => debugPoint);
        }

        public DebugPointDto? Get(NodePath nodePath)
        {
            var correctedNodePath = nodePath.ToString(CultureInfo.InvariantCulture).Replace("PipelineExecution/", "");
            _debugInfo.TryGetValue(correctedNodePath, out var value);
            return value;
        }

        public IEnumerable<DebugPointNode> GetRoots()
        {
            var debugPoints = _debugInfo.Select(x => x.Value).ToList();

            Dictionary<NodePath, DebugPointNode> mergedResultsMap = new();
            var roots = new List<DebugPointNode>();

            // Create entries
            foreach (var debugPoint in debugPoints)
            {
                var fullPath = debugPoint.NodePath.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(fullPath))
                {
                    fullPath = "PipelineExecution";
                }
                else
                {
                    fullPath = "PipelineExecution/" + fullPath;
                }

                var lastIndex = fullPath.LastIndexOf("/", StringComparison.Ordinal);
                var nodeName = fullPath.Substring(lastIndex + 1);

                mergedResultsMap[fullPath] = new DebugPointNode
                    { SequenceNumber = debugPoint.SequenceNumber, Name = nodeName, FullPath = fullPath };
            }

            foreach (var debugPoint in debugPoints)
            {
                var fullPath = debugPoint.NodePath.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(fullPath))
                {
                    fullPath = "PipelineExecution";
                }
                else
                {
                    fullPath = "PipelineExecution/" + fullPath;
                }

                var lastIndex = fullPath.LastIndexOf("/", StringComparison.Ordinal);
                var nodePath = "";
                if (lastIndex > -1)
                {
                    nodePath = fullPath.Substring(0, lastIndex);
                }

                if (mergedResultsMap.TryGetValue(nodePath, out var debugPointNode))
                {
                    debugPointNode.Children ??= new List<DebugPointNode>();
                    debugPointNode.Children.Add(mergedResultsMap[fullPath]);
                }

                if (string.IsNullOrWhiteSpace(nodePath))
                {
                    roots.Add(mergedResultsMap[fullPath]);
                }
            }

            return roots;
        }
    }

    private class PipelineExecutionListRecord
    {
        private readonly ConcurrentDictionary<Guid, PipelineExecutionRecord> _debugInfo = new();

        public void Add(Guid pipelineExecutionId, DebugPointDto debugPoint)
        {
            _debugInfo.AddOrUpdate(pipelineExecutionId, _ => new PipelineExecutionRecord(DateTime.UtcNow),
                (_, record) =>
                {
                    record.Add(debugPoint.NodePath, debugPoint);
                    return record;
                });
        }

        public PipelineExecutionRecord? Get(Guid pipelineExecutionId)
        {
            _debugInfo.TryGetValue(pipelineExecutionId, out var value);
            return value;
        }

        public IEnumerable<Guid> GetPipelineExecutionIds()
        {
            return _debugInfo.Keys;
        }

        public Guid GetLatestPipelineExecutionId()
        {
            return _debugInfo.OrderByDescending(x => x.Value.DateTime).First().Key;
        }
    }

    private readonly ConcurrentDictionary<Tuple<string, RtEntityId>, PipelineExecutionListRecord> _debugInfo = new();


    public Task CacheDebugPointAsync(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId,
        DebugPointDto debugPoint)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        var debugInfo = _debugInfo.GetOrAdd(key, _ => new PipelineExecutionListRecord());
        debugInfo.Add(pipelineExecutionId, debugPoint);

        return Task.CompletedTask;
    }

    public Task<IEnumerable<Guid>> GetPipelineExecutionsAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            return Task.FromResult(value.GetPipelineExecutionIds());
        }

        throw PipelineDebugInformationNotFoundException.NotFound(tenantId, pipelineRtEntityId);
    }

    public Task<Guid> GetLatestPipelineExecutionAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            return Task.FromResult(value.GetLatestPipelineExecutionId());
        }

        throw PipelineDebugInformationNotFoundException.NotFound(tenantId, pipelineRtEntityId);
    }

    public Task<IEnumerable<DebugPointNode>> GetPipelineExecutionDebugPointNodesAsync(string tenantId,
        RtEntityId pipelineRtEntityId, Guid pipelineExecutionId)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            var pipelineExecutionRecord = value.Get(pipelineExecutionId);
            if (pipelineExecutionRecord != null)
            {
                return Task.FromResult(pipelineExecutionRecord.GetRoots());
            }

            throw PipelineDebugInformationNotFoundException.ExecutionNotFound(tenantId, pipelineRtEntityId,
                pipelineExecutionId);
        }

        throw PipelineDebugInformationNotFoundException.NotFound(tenantId, pipelineRtEntityId);
    }

    public Task<DebugPointDto?> GetDebugPointAsync(string tenantId, RtEntityId pipelineRtEntityId,
        Guid pipelineExecutionId, NodePath nodePath)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);

        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            var pipelineExecutionRecord = value.Get(pipelineExecutionId);
            if (pipelineExecutionRecord != null)
            {
                return Task.FromResult(pipelineExecutionRecord.Get(nodePath));
            }

            throw PipelineDebugInformationNotFoundException.ExecutionNotFound(tenantId, pipelineRtEntityId,
                pipelineExecutionId);
        }

        throw PipelineDebugInformationNotFoundException.NotFound(tenantId, pipelineRtEntityId);
    }
}