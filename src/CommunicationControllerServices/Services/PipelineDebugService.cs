using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Implementation of the pipeline debug service
/// </summary>
internal class PipelineDebugService : IPipelineDebugService
{
    private class PipelineExecutionRecord(Guid id, DateTime dateTime)
    {
        public Guid Id { get; } = id;
        public DateTime DateTime { get; } = dateTime;
        private readonly ConcurrentDictionary<string, DebugPointDataDto> _debugInfo = new();

        public void Add(NodePath nodePath, DebugPointDto debugPoint)
        {
            var debugInfo = new DebugPointDataDto(debugPoint.NodeId, debugPoint.NodePath, debugPoint.Description,
                debugPoint.SequenceNumber)
            {
                Messages = debugPoint.Messages,
                Input = debugPoint.Input != null ? JsonDocument.Parse(debugPoint.Input).RootElement : null,
                Output = debugPoint.Output != null ? JsonDocument.Parse(debugPoint.Output).RootElement : null
            };

            _debugInfo.AddOrUpdate(nodePath, debugInfo, (_, _) => debugInfo);
        }

        public DebugPointDataDto? Get(string nodeId)
        {
            _debugInfo.TryGetValue(nodeId, out var value);
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

                var lastIndex = fullPath.LastIndexOf("/", StringComparison.Ordinal);
                var nodeName = fullPath.Substring(lastIndex + 1);

                mergedResultsMap[debugPoint.NodeId] = new DebugPointNode
                {
                    NodeId = debugPoint.NodeId,
                    SequenceNumber = debugPoint.SequenceNumber,
                    Description = debugPoint.Description,
                    Name = nodeName,
                    FullPath = debugPoint.NodePath
                };
            }

            foreach (var debugPoint in debugPoints)
            {
                var fullPath = debugPoint.NodeId.ToString(CultureInfo.InvariantCulture);

                var lastIndex = fullPath.LastIndexOf("/", StringComparison.Ordinal);
                var nodeId = "";
                if (lastIndex > -1)
                {
                    nodeId = fullPath.Substring(0, lastIndex);
                }

                if (mergedResultsMap.TryGetValue(nodeId, out var debugPointNode))
                {
                    debugPointNode.Children ??= new List<DebugPointNode>();
                    debugPointNode.Children.Add(mergedResultsMap[fullPath]);
                }

                if (string.IsNullOrWhiteSpace(nodeId))
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
            _debugInfo.AddOrUpdate(pipelineExecutionId, _ =>
                {
                    var record = new PipelineExecutionRecord(pipelineExecutionId, DateTime.UtcNow);
                    record.Add(debugPoint.NodeId, debugPoint);
                    return record;
                },
                (_, record) =>
                {
                    record.Add(debugPoint.NodeId, debugPoint);
                    return record;
                });

            // We leave only the last 10 records
            if (_debugInfo.Count > 10)
            {
                var oldest = _debugInfo.OrderBy(x => x.Value.DateTime).First().Key;
                _debugInfo.TryRemove(oldest, out _);
            }
        }

        public PipelineExecutionRecord? Get(Guid pipelineExecutionId)
        {
            _debugInfo.TryGetValue(pipelineExecutionId, out var value);
            return value;
        }

        public IEnumerable<PipelineExecutionDataDto> GetPipelineExecutionIds()
        {
            return _debugInfo.Values.Select(p => new PipelineExecutionDataDto
                { Id = p.Id, DateTime = p.DateTime });
        }

        public PipelineExecutionDataDto? GetLatestPipelineExecutionId()
        {
            if (_debugInfo.Count == 0)
            {
                return null;
            }

            var latest = _debugInfo.OrderByDescending(x => x.Value.DateTime).First();

            return new PipelineExecutionDataDto
            {
                Id = latest.Key,
                DateTime = latest.Value.DateTime
            };
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

    public Task<IEnumerable<PipelineExecutionDataDto>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId,
        int skip = 0, int take = 100)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            return Task.FromResult(value.GetPipelineExecutionIds());
        }

        throw PipelineDebugInformationNotFoundException.NotFound(tenantId, pipelineRtEntityId);
    }

    public Task<PipelineExecutionDataDto> GetLatestPipelineExecutionAsync(string tenantId,
        RtEntityId pipelineRtEntityId)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);
        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            var executionData = value.GetLatestPipelineExecutionId();
            if (executionData != null)
            {
                return Task.FromResult(executionData);
            }
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

    public Task<DebugPointDataDto?> GetDebugPointDataAsync(string tenantId, RtEntityId pipelineRtEntityId,
        Guid pipelineExecutionId, string nodeId)
    {
        var key = new Tuple<string, RtEntityId>(tenantId, pipelineRtEntityId);

        if (_debugInfo.TryGetValue(key, out PipelineExecutionListRecord? value))
        {
            var pipelineExecutionRecord = value.Get(pipelineExecutionId);
            if (pipelineExecutionRecord != null)
            {
                return Task.FromResult(pipelineExecutionRecord.Get(nodeId));
            }

            throw PipelineDebugInformationNotFoundException.ExecutionNotFound(tenantId, pipelineRtEntityId,
                pipelineExecutionId);
        }

        throw PipelineDebugInformationNotFoundException.NotFound(tenantId, pipelineRtEntityId);
    }
}