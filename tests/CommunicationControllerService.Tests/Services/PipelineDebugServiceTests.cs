using System.Text.Json;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     The adapter's debug capture substitutes plain text placeholders for snapshots that exceed a
///     size limit (AB#4272, AB#4662). A single such non-JSON snapshot must not cost the execution its
///     debug points: the placeholder is kept as a JSON string and every other snapshot is parsed.
/// </summary>
internal class PipelineDebugServiceTests
{
    private const string TenantId = "acme";
    private const string Placeholder = "<debug snapshot omitted: total debug capture budget exhausted>";

    private readonly PipelineDebugService _service = new();
    private readonly RtEntityId _pipelineRtId =
        new(SystemCommunicationCkIds.RtCkPipelineTypeId, OctoObjectId.GenerateNewId());
    private readonly Guid _executionId = Guid.NewGuid();

    private static DebugPointDto DebugPoint(string nodeId, uint sequenceNumber, string? input, string? output)
    {
        return new DebugPointDto(nodeId, nodeId, $"Node {nodeId}", sequenceNumber)
        {
            Input = input,
            Output = output
        };
    }

    private async Task CacheAsync(params DebugPointDto[] debugPoints)
    {
        foreach (var debugPoint in debugPoints)
        {
            await _service.CacheDebugPointAsync(TenantId, _pipelineRtId, _executionId, debugPoint);
        }
    }

    private Task<DebugPointDataDto?> GetAsync(string nodeId)
    {
        return _service.GetDebugPointDataAsync(TenantId, _pipelineRtId, _executionId, nodeId);
    }

    [Test]
    public async Task AllSnapshotsValidJson_AllDebugPointsKeptAndParsed()
    {
        // Arrange / Act
        await CacheAsync(
            DebugPoint("first", 0, """{"value":1}""", """{"value":2}"""),
            DebugPoint("second", 1, """[1,2,3]""", """{"nested":{"ok":true}}"""));

        // Assert
        var roots = (await _service.GetPipelineExecutionDebugPointNodesAsync(TenantId, _pipelineRtId,
            _executionId)).ToList();
        await Assert.That(roots.Select(r => r.NodeId)).IsEquivalentTo(["first", "second"]);

        var first = await GetAsync("first");
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Input!.Value.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(first.Input!.Value.GetProperty("value").GetInt32()).IsEqualTo(1);
        await Assert.That(first.Output!.Value.GetProperty("value").GetInt32()).IsEqualTo(2);

        var second = await GetAsync("second");
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Input!.Value.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(second.Input!.Value.GetArrayLength()).IsEqualTo(3);
        await Assert.That(second.Output!.Value.GetProperty("nested").GetProperty("ok").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task OneNonJsonSnapshotAmongValidOnes_KeepsAllDebugPoints()
    {
        // Arrange / Act — the placeholder sits between two valid debug points, as it does when the
        // capture budget runs out mid-execution.
        await CacheAsync(
            DebugPoint("first", 0, """{"value":1}""", """{"value":2}"""),
            DebugPoint("second", 1, """{"value":2}""", Placeholder),
            DebugPoint("third", 2, """{"value":3}""", """{"value":4}"""));

        // Assert
        var roots = (await _service.GetPipelineExecutionDebugPointNodesAsync(TenantId, _pipelineRtId,
            _executionId)).ToList();
        await Assert.That(roots.Select(r => r.NodeId)).IsEquivalentTo(["first", "second", "third"]);

        var first = await GetAsync("first");
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Output!.Value.GetProperty("value").GetInt32()).IsEqualTo(2);

        var third = await GetAsync("third");
        await Assert.That(third).IsNotNull();
        await Assert.That(third!.Input!.Value.GetProperty("value").GetInt32()).IsEqualTo(3);
        await Assert.That(third.Output!.Value.GetProperty("value").GetInt32()).IsEqualTo(4);

        var second = await GetAsync("second");
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.Input!.Value.GetProperty("value").GetInt32()).IsEqualTo(2);
        await Assert.That(second.Output!.Value.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(second.Output!.Value.GetString()).IsEqualTo(Placeholder);
    }

    [Test]
    public async Task NonJsonInputSnapshot_IsKeptAsJsonString()
    {
        // Arrange / Act
        await CacheAsync(DebugPoint("only", 0, Placeholder, """{"value":1}"""));

        // Assert
        var debugPoint = await GetAsync("only");
        await Assert.That(debugPoint).IsNotNull();
        await Assert.That(debugPoint!.Input!.Value.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(debugPoint.Input!.Value.GetString()).IsEqualTo(Placeholder);
        await Assert.That(debugPoint.Output!.Value.GetProperty("value").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task MissingSnapshots_StayNull()
    {
        // Arrange / Act
        await CacheAsync(DebugPoint("only", 0, null, null));

        // Assert
        var debugPoint = await GetAsync("only");
        await Assert.That(debugPoint).IsNotNull();
        await Assert.That(debugPoint!.Input).IsNull();
        await Assert.That(debugPoint.Output).IsNull();
    }
}
