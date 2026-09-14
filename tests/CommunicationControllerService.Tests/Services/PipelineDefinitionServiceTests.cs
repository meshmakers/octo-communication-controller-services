using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

public class PipelineDefinitionServiceTests
{
    private readonly PipelineDefinitionService _service = new();

    private const string SampleDefinition = """
        triggers:
          - type: FromExecutePipelineCommand@1
        transformations:
          - type: CreateUpdateInfo@1
            description: Create entity
            targetPath: $.result
            updateKind: INSERT
            ckTypeId: Loxone/Room
          - type: ForEach@1
            iterationPath: $.items
            targetPath: $.output
            transformations:
              - type: CreateUpdateInfo@1
                description: Nested entity
                targetPath: $.nested
                updateKind: UPDATE
        """;

    [Test]
    public async Task GetNodeProperties_ShouldFindFirstNode()
    {
        var result = _service.GetNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.NodeType).IsEqualTo("CreateUpdateInfo@1");
        await Assert.That(result.Properties["description"]?.ToString()).IsEqualTo("Create entity");
        await Assert.That(result.Properties["targetPath"]?.ToString()).IsEqualTo("$.result");
    }

    [Test]
    public async Task GetNodeProperties_ShouldFindNestedNode()
    {
        var result = _service.GetNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 1);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Properties["description"]?.ToString()).IsEqualTo("Nested entity");
        await Assert.That(result.Properties["targetPath"]?.ToString()).IsEqualTo("$.nested");
    }

    [Test]
    public async Task GetNodeProperties_ShouldReturnNullForMissingNode()
    {
        var result = _service.GetNodeProperties(SampleDefinition, "NonExistent@1", 0);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetAllNodes_ShouldReturnAllNodes()
    {
        var result = _service.GetAllNodes(SampleDefinition);

        await Assert.That(result.Count).IsEqualTo(4);
        await Assert.That(result[0].NodeType).IsEqualTo("FromExecutePipelineCommand@1");
        await Assert.That(result[1].NodeType).IsEqualTo("CreateUpdateInfo@1");
        await Assert.That(result[2].NodeType).IsEqualTo("ForEach@1");
        await Assert.That(result[3].NodeType).IsEqualTo("CreateUpdateInfo@1");
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldUpdateExistingProperty()
    {
        var props = new Dictionary<string, object?> { ["description"] = "Updated entity" };

        var result = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0, props);

        await Assert.That(result).IsNotNull();

        var parsed = _service.GetNodeProperties(result!, "CreateUpdateInfo@1", 0);
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Properties["description"]?.ToString()).IsEqualTo("Updated entity");
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldAddNewProperty()
    {
        var props = new Dictionary<string, object?> { ["generateRtId"] = true };

        var result = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0, props);

        await Assert.That(result).IsNotNull();

        var parsed = _service.GetNodeProperties(result!, "CreateUpdateInfo@1", 0);
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Properties.ContainsKey("generateRtId")).IsTrue();
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldRemovePropertyWhenNull()
    {
        var props = new Dictionary<string, object?> { ["description"] = null };

        var result = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0, props);

        await Assert.That(result).IsNotNull();

        var parsed = _service.GetNodeProperties(result!, "CreateUpdateInfo@1", 0);
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Properties.ContainsKey("description")).IsFalse();
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldNotOverwriteTypeDiscriminator()
    {
        var props = new Dictionary<string, object?> { ["type"] = "SomethingElse@1" };

        var result = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0, props);

        await Assert.That(result).IsNotNull();

        var parsed = _service.GetNodeProperties(result!, "CreateUpdateInfo@1", 0);
        await Assert.That(parsed).IsNotNull();
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldUpdateNestedNode()
    {
        var props = new Dictionary<string, object?> { ["description"] = "Updated nested" };

        var result = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 1, props);

        await Assert.That(result).IsNotNull();

        var first = _service.GetNodeProperties(result!, "CreateUpdateInfo@1", 0);
        await Assert.That(first!.Properties["description"]?.ToString()).IsEqualTo("Create entity");

        var second = _service.GetNodeProperties(result!, "CreateUpdateInfo@1", 1);
        await Assert.That(second!.Properties["description"]?.ToString()).IsEqualTo("Updated nested");
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldReturnNullForMissingNode()
    {
        var props = new Dictionary<string, object?> { ["description"] = "test" };

        var result = _service.UpdateNodeProperties(SampleDefinition, "NonExistent@1", 0, props);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task UpdateNodeProperties_ShouldPreserveOtherNodes()
    {
        var props = new Dictionary<string, object?> { ["description"] = "Changed" };

        var result = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0, props);

        await Assert.That(result).IsNotNull();

        var forEach = _service.GetNodeProperties(result!, "ForEach@1", 0);
        await Assert.That(forEach).IsNotNull();
        await Assert.That(forEach!.Properties["iterationPath"]?.ToString()).IsEqualTo("$.items");

        var trigger = _service.GetNodeProperties(result!, "FromExecutePipelineCommand@1", 0);
        await Assert.That(trigger).IsNotNull();
    }
    // ===== scalar quoting on write ==============================================
    // UpdateNodeProperties used to emit every string unquoted unless it carried a `:`, `#`,
    // a quote or surrounding spaces. A string that merely LOOKED like another YAML type
    // therefore changed type on the way back in, and one opening with a block-sequence
    // indicator broke the whole document. The emitted scalar is checked by reading it back
    // through the shared YamlToJsonConverter — the reader the deploy-time schema validation
    // and the MCP server use.

    private static JsonNode ReadBack(string definition) =>
        YamlToJsonConverter.ToJsonNode(definition)
        ?? throw new InvalidOperationException("the updated definition parsed to nothing");

    private string UpdateValue(string value)
    {
        var updated = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0,
            new Dictionary<string, object?> { ["description"] = value });
        return updated!;
    }

    [Test]
    [Arguments("true")]
    [Arguments("false")]
    [Arguments("True")]
    [Arguments("3")]
    [Arguments("-7")]
    [Arguments("1.5")]
    [Arguments("1e3")]
    [Arguments("null")]
    [Arguments("Null")]
    [Arguments("~")]
    [Arguments("007700")]
    [Arguments("670000000000000000000002")]
    public async Task UpdateNodeProperties_StringLookingLikeAnotherYamlType_StaysAString(string value)
    {
        var updated = UpdateValue(value);

        var readBack = ReadBack(updated)["transformations"]![0]!["description"];
        await Assert.That(readBack).IsNotNull();
        await Assert.That(readBack!.GetValueKind()).IsEqualTo(JsonValueKind.String);
        await Assert.That(readBack.GetValue<string>()).IsEqualTo(value);
    }

    [Test]
    [Arguments("- dash")]
    [Arguments("? question")]
    [Arguments("[bracket")]
    [Arguments("{brace")]
    [Arguments("&anchor")]
    [Arguments("*alias")]
    [Arguments("!tag")]
    [Arguments("|pipe")]
    [Arguments(">fold")]
    [Arguments("%directive")]
    [Arguments("@at")]
    [Arguments("`backtick")]
    [Arguments(",comma")]
    public async Task UpdateNodeProperties_StringOpeningWithAYamlIndicator_KeepsTheDefinitionParseable(string value)
    {
        var updated = UpdateValue(value);

        // Before the fix several of these made the whole definition unparseable.
        var readBack = ReadBack(updated)["transformations"]![0]!["description"];
        await Assert.That(readBack!.GetValueKind()).IsEqualTo(JsonValueKind.String);
        await Assert.That(readBack.GetValue<string>()).IsEqualTo(value);
    }

    [Test]
    [Arguments("plain text")]
    [Arguments("$.some.json.path")]
    [Arguments("Loxone/Room")]
    [Arguments("FromHttpRequest@1")]
    [Arguments("a#b")]
    public async Task UpdateNodeProperties_OrdinaryString_IsWrittenWithoutQuotes(string value)
    {
        var updated = UpdateValue(value);

        var line = updated.Split('\n').First(l => l.TrimStart().StartsWith("description:"));
        await Assert.That(line.Trim()).IsEqualTo($"description: {value}");
        await Assert.That(ReadBack(updated)["transformations"]![0]!["description"]!.GetValue<string>())
            .IsEqualTo(value);
    }

    [Test]
    [Arguments("has: colon")]
    [Arguments("trailing space ")]
    [Arguments(" leading space")]
    [Arguments("says \"hi\"")]
    [Arguments("says 'hi'")]
    [Arguments("back\\slash")]
    [Arguments("C:\\temp\\file")]
    [Arguments("line\nbreak")]
    [Arguments("tab\there")]
    [Arguments("trailing # comment")]
    public async Task UpdateNodeProperties_StringNeedingEscapes_RoundTripsExactly(string value)
    {
        var updated = UpdateValue(value);

        await Assert.That(ReadBack(updated)["transformations"]![0]!["description"]!.GetValue<string>())
            .IsEqualTo(value);
    }

    [Test]
    public async Task UpdateNodeProperties_NumericValue_UsesInvariantCulture()
    {
        // A German-locale server would otherwise write `1,5`, which reads back as a string.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var updated = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0,
                new Dictionary<string, object?> { ["description"] = 1.5d })!;

            var readBack = ReadBack(updated)["transformations"]![0]!["description"]!;
            await Assert.That(readBack.GetValueKind()).IsEqualTo(JsonValueKind.Number);
            await Assert.That(readBack.GetValue<double>()).IsEqualTo(1.5d);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task UpdateNodeProperties_GenuineBooleanAndNumber_StayTyped()
    {
        // The counterpart: the fix must not start quoting values that really are scalars.
        var updated = _service.UpdateNodeProperties(SampleDefinition, "CreateUpdateInfo@1", 0,
            new Dictionary<string, object?> { ["description"] = true, ["targetPath"] = 42L })!;

        var node = ReadBack(updated)["transformations"]![0]!;
        await Assert.That(node["description"]!.GetValue<bool>()).IsTrue();
        await Assert.That(node["targetPath"]!.GetValue<long>()).IsEqualTo(42L);
    }
}
