using System.Diagnostics;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#9667: the deploy-time schema validation reads a caller-supplied definition, so it must stay within bounded
///     time and memory whatever the definition looks like, and refuse what is too large with a finding (never an
///     exception, never a crash of the controller). The schema is generator-shaped: a <c>oneOf</c> over every node
///     schema, with nested <c>transformations</c> referring back to it (the shape that exhausted the MCP process in
///     AB#9666).
/// </summary>
internal class PipelineSchemaValidatorBoundsTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private const long AllocationBudget = 256L * 1024 * 1024;

    private readonly PipelineSchemaValidator _validator = new();
    private readonly string _schema = GeneratorShapedPipelineSchema.Build();

    private (IReadOnlyList<string> Findings, TimeSpan Elapsed, long Allocated) Measure(string definition)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var findings = _validator.Validate(definition, _schema);
        stopwatch.Stop();
        return (findings, stopwatch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    private static async Task AssertBounded(TimeSpan elapsed, long allocated)
    {
        await Assert.That(elapsed).IsLessThan(Generous);
        await Assert.That(allocated).IsLessThan(AllocationBudget);
    }

    private static async Task AssertRefusedWith(IReadOnlyList<string> findings, string limitText)
    {
        await Assert.That(findings).Count().IsEqualTo(1);
        await Assert.That(findings[0]).StartsWith(PipelineDefinitionLimits.RefusedCode);
        await Assert.That(findings[0]).Contains(limitText);
    }

    // ===== nested If@1, the shape of AB#9666 ====================================

    [Test]
    [Arguments(4)]
    [Arguments(8)]
    public async Task NestedIf_ValidDraft_IsValidatedWithinBounds(int depth)
    {
        var (findings, elapsed, allocated) = Measure(GeneratorShapedPipelineSchema.NestedIfDraft(depth));

        await Assert.That(findings).IsEmpty();
        await AssertBounded(elapsed, allocated);
    }

    [Test]
    [Arguments(4)]
    [Arguments(8)]
    public async Task NestedIf_DraftInvalidAtTheDeepestLevel_IsValidatedWithinBounds(int depth)
    {
        // Bounded is what this pins. NJsonSchema 11 does not follow the nested `transformations` reference of the
        // generator's schema, so an error below the root level is not reported today (separate Bug); a validator
        // that does follow it must stay within the same bounds.
        var (findings, elapsed, allocated) =
            Measure(GeneratorShapedPipelineSchema.NestedIfDraft(depth, breakInnermost: true));

        await Assert.That(findings.Any(f => f.StartsWith(PipelineDefinitionLimits.RefusedCode))).IsFalse();
        await AssertBounded(elapsed, allocated);
    }

    [Test]
    public async Task InvalidRootNode_IsStillReported()
    {
        var (findings, _, _) = Measure(GeneratorShapedPipelineSchema.NestedIfDraft(1, breakInnermost: true));

        await Assert.That(findings).Count().IsEqualTo(1);
        await Assert.That(findings[0]).Contains("valueText");
    }

    // ===== limits before the evaluation =========================================

    [Test]
    public async Task NestingAboveTheLimit_IsRefusedBeforeEvaluation()
    {
        var draft = GeneratorShapedPipelineSchema.NestedIfDraft(PipelineDefinitionLimits.MaxNestingDepth + 1,
            siblings: 0);

        var (findings, elapsed, allocated) = Measure(draft);

        await AssertRefusedWith(findings, $"node lists more than {PipelineDefinitionLimits.MaxNestingDepth} levels");
        await AssertBounded(elapsed, allocated);
    }

    [Test]
    public async Task NestingAtTheLimit_IsEvaluated()
    {
        // The If nodes fill levels 1 to n-1, the leaf of the innermost one is at level n.
        var draft = GeneratorShapedPipelineSchema.NestedIfDraft(PipelineDefinitionLimits.MaxNestingDepth - 1,
            siblings: 0);

        var (findings, _, _) = Measure(draft);

        await Assert.That(findings).IsEmpty();
    }

    [Test]
    public async Task NodeMeasurement_FollowsSwitchCasesAndDefault()
    {
        var definition = System.Text.Json.Nodes.JsonNode.Parse("""
            {"transformations":[{"type":"Switch@1",
              "cases":[{"transformations":[{"type":"A@1","transformations":[{"type":"B@1"}]}]}],
              "default":[{"type":"C@1"}]}]}
            """);

        var (count, depth) = PipelineDefinitionLimits.MeasureNodes(definition, int.MaxValue, int.MaxValue);

        await Assert.That(count).IsEqualTo(4);
        await Assert.That(depth).IsEqualTo(3);
    }

    [Test]
    public async Task MoreNodesThanTheLimit_AreRefused()
    {
        var yaml = new StringBuilder("triggers:\n  - type: FromHttpRequest@2\n    path: /x\ntransformations:\n");
        for (var i = 0; i < PipelineDefinitionLimits.MaxPipelineNodes; i++)
        {
            yaml.Append("  - type: SetPrimitiveValue@1\n    valueText: v\n");
        }

        var (findings, elapsed, allocated) = Measure(yaml.ToString());

        await AssertRefusedWith(findings, $"more than {PipelineDefinitionLimits.MaxPipelineNodes} nodes");
        await AssertBounded(elapsed, allocated);
    }

    [Test]
    public async Task DeepJsonOutsideTheNodeLists_IsRefusedAsAFinding()
    {
        // Deeper than Newtonsoft's reader accepts: without the check NJsonSchema threw a JsonReaderException into the
        // deploy path.
        var yaml = "transformations:\n  - type: SetPrimitiveValue@1\n    valueText: v\n    extra: " +
                   new string('[', 70) + new string(']', 70) + "\n";

        var (findings, _, _) = Measure(yaml);

        await AssertRefusedWith(findings, $"more than {PipelineDefinitionLimits.MaxJsonDepth} levels deep");
    }

    [Test]
    public async Task DefinitionLongerThanTheLimit_IsRefusedBeforeParsing()
    {
        var definition = "description: " + new string('x', PipelineDefinitionLimits.MaxDefinitionLength) + "\n";

        var (findings, _, _) = Measure(definition);

        await AssertRefusedWith(findings, $"the limit is {PipelineDefinitionLimits.MaxDefinitionLength}");
    }

    // ===== the YAML side (shared converter) =====================================

    [Test]
    public async Task BillionLaughs_IsRefusedWithoutExpansion()
    {
        var yaml = new StringBuilder("l0: &l0 [ lol ]\n");
        for (var level = 1; level <= 10; level++)
        {
            yaml.Append($"l{level}: &l{level} [ ");
            yaml.Append(string.Join(" , ", Enumerable.Repeat($"*l{level - 1}", 10)));
            yaml.Append(" ]\n");
        }

        yaml.Append("password: do-not-echo\n");

        var (findings, elapsed, allocated) = Measure(yaml.ToString());

        await AssertRefusedWith(findings, "aliases are resolved");
        await Assert.That(findings[0]).DoesNotContain("do-not-echo");
        await AssertBounded(elapsed, allocated);
    }

    [Test]
    public async Task DeepYaml_IsRefusedWithoutOverflowingTheStack()
    {
        var (findings, elapsed, allocated) = Measure(new string('[', 200_000) + new string(']', 200_000));

        await AssertRefusedWith(findings, "levels deep");
        await AssertBounded(elapsed, allocated);
    }

    // ===== bounded output =======================================================

    [Test]
    public async Task ManyInvalidNodes_YieldACappedListOfFindings()
    {
        var yaml = new StringBuilder("transformations:\n");
        for (var i = 0; i < 500; i++)
        {
            yaml.Append("  - type: SetPrimitiveValue@1\n    targetPath: $.x\n");
        }

        var (findings, elapsed, allocated) = Measure(yaml.ToString());

        await Assert.That(findings).Count().IsEqualTo(PipelineDefinitionLimits.MaxFindings + 1);
        await Assert.That(findings[^1]).Contains($"and {500 - PipelineDefinitionLimits.MaxFindings} more findings");
        await AssertBounded(elapsed, allocated);
    }
}
