using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#9667 — <see cref="PipelineDefinitionService" /> parses a caller-supplied definition with YamlDotNet and walks
///     its nodes recursively; it is behind the deploy gates and the REST endpoints <c>parse-node</c> and
///     <c>parse-all-nodes</c> (read-only policy). It parses only a
///     definition within <see cref="PipelineDefinitionLimits" />, in every configuration.
/// </summary>
internal class PipelineDefinitionServiceLimitsTests
{
    private readonly PipelineDefinitionService _service = new();

    /// <summary>
    ///     Ten levels of ten aliases: YamlDotNet binds each alias to the shared list, so the document parses quickly
    ///     and a recursive walk would visit 10^10 nodes.
    /// </summary>
    private static string NestedAliasDefinition()
    {
        var sb = new StringBuilder();
        sb.AppendLine("l0: &l0 [{type: Map@1}]");
        for (var level = 1; level <= 10; level++)
        {
            sb.Append($"l{level}: &l{level} [");
            sb.Append(string.Join(", ",
                Enumerable.Range(0, 10).Select(_ => $"{{type: If@1, transformations: *l{level - 1}}}")));
            sb.AppendLine("]");
        }

        sb.AppendLine("triggers: *l10");
        sb.AppendLine("transformations: *l10");
        return sb.ToString();
    }

    private static string DeepFlowDefinition(int depth) =>
        "transformations: " + new string('[', depth) + new string(']', depth) + "\n";

    [Test]
    [Timeout(30_000)]
    public async Task TryGetAllNodes_NestedAliases_AnswersUnparsableWithoutWalkingThem(CancellationToken token)
    {
        var parsed = _service.TryGetAllNodes(NestedAliasDefinition(), out var nodes);

        using var _ = Assert.Multiple();
        await Assert.That(parsed).IsFalse();
        await Assert.That(nodes).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task GetAllNodes_NestedAliases_IsEmpty(CancellationToken token)
    {
        // parse-all-nodes (REST, read-only policy)
        await Assert.That(_service.GetAllNodes(NestedAliasDefinition())).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task GetNodeProperties_NestedAliases_IsNull(CancellationToken token)
    {
        // parse-node (REST, read-only policy): the search for a missing index would walk every node.
        await Assert.That(_service.GetNodeProperties(NestedAliasDefinition(), "Missing@1", 0)).IsNull();
    }

    [Test]
    [Timeout(30_000)]
    public async Task TryGetTriggerNodes_NestedAliases_AnswersUnparsable(CancellationToken token)
    {
        var parsed = _service.TryGetTriggerNodes(NestedAliasDefinition(), out var triggers);

        using var _ = Assert.Multiple();
        await Assert.That(parsed).IsFalse();
        await Assert.That(triggers).IsEmpty();
    }

    [Test]
    [Timeout(30_000)]
    public async Task TryGetAllNodes_DeepFlowNesting_AnswersUnparsableWithoutOverflowingTheStack(
        CancellationToken token)
    {
        var parsed = _service.TryGetAllNodes(DeepFlowDefinition(200_000), out var nodes);

        using var _ = Assert.Multiple();
        await Assert.That(parsed).IsFalse();
        await Assert.That(nodes).IsEmpty();
    }

    [Test]
    public async Task TryGetAllNodes_MoreNodesThanTheLimit_AnswersUnparsable()
    {
        var nodes = string.Join("\n",
            Enumerable.Range(0, PipelineDefinitionLimits.MaxPipelineNodes + 1).Select(_ => "  - type: Map@1"));

        var parsed = _service.TryGetAllNodes($"transformations:\n{nodes}\n", out _);

        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task TryGetAllNodes_ShallowAliasesWithinTheLimits_AreStillParsed()
    {
        // Aliases themselves stay allowed: only their expansion is budgeted.
        const string definition = """
            shared: &shared {type: Map@1}
            transformations:
              - *shared
              - *shared
            """;

        var parsed = _service.TryGetAllNodes(definition, out var nodes);

        using var _ = Assert.Multiple();
        await Assert.That(parsed).IsTrue();
        await Assert.That(nodes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CheckDefinition_EmptyOrMalformed_IsLeftToTheCaller()
    {
        using var _ = Assert.Multiple();
        await Assert.That(PipelineDefinitionLimits.CheckDefinition(null)).IsNull();
        await Assert.That(PipelineDefinitionLimits.CheckDefinition("")).IsNull();
        await Assert.That(PipelineDefinitionLimits.CheckDefinition("transformations: [unclosed")).IsNull();
    }

    [Test]
    [Timeout(30_000)]
    public async Task CheckDefinition_NestedAliases_NamesTheLimitAndQuotesNothing(CancellationToken token)
    {
        var refusal = PipelineDefinitionLimits.CheckDefinition(NestedAliasDefinition());

        using var _ = Assert.Multiple();
        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal!).StartsWith(PipelineDefinitionLimits.RefusedCode);
        await Assert.That(refusal).DoesNotContain("If@1");
    }
}
