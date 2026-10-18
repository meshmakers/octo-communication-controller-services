using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     AB#6879: the sample pipeline of the guide "Pipeline Method Handlers" has the shape a pipeline handler needs: it
///     starts with the execute command trigger (binding the caller) and ends with the node that stores the result.
/// </summary>
internal class SamplePipelineTests
{
    private static readonly string Yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Methods",
        "SamplePipelines", "Device.UpdateFirmware-1.pipeline.yaml"));

    [Test]
    public async Task TheSamplePipelineIsTriggeredByTheExecuteCommandAndRequiresACaller()
    {
        var parsed = new PipelineDefinitionService().TryGetTriggerNodes(Yaml, out var triggers);

        await Assert.That(parsed).IsTrue();
        await Assert.That(triggers.Count).IsEqualTo(1);
        await Assert.That(triggers[0].NodeType).IsEqualTo("FromExecutePipelineCommand@1");
        await Assert.That(triggers[0].Properties["callerBinding"]?.ToString()).IsEqualTo("BindingRequired");
    }

    [Test]
    public async Task TheSamplePipelineEndsWithTheNodeThatStoresTheResult()
    {
        var lastTransformation = Yaml.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("  - type:", StringComparison.Ordinal))
            .Last();

        await Assert.That(lastTransformation).IsEqualTo("  - type: SetPipelineExecutionResult@1");
    }
}
