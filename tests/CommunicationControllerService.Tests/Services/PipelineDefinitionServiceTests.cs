using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

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
}
