using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5128: pure coverage of the elevation detection + confused-deputy path matching, driven
/// through the real YAML parser so the property casing and nested-shape assumptions are exercised.
/// </summary>
internal class PipelineElevationInspectorTests
{
    private static readonly PipelineDefinitionService Parser = new();

    private static IReadOnlyList<PipelineNodeProperties> Parse(string yaml)
    {
        Parser.TryGetAllNodes(yaml, out var nodes);
        return nodes;
    }

    [Test]
    public async Task FindElevatedNodes_DetectsServiceAccountAndSystem_IgnoresCallerAndAbsent()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesById@1
                identity: System
              - type: GetRtEntitiesByType@1
                identity: ServiceAccount
              - type: GetQueryById@1
                identity: Caller
              - type: ApplyChanges@2
            """);

        var elevated = PipelineElevationInspector.FindElevatedNodes(nodes);

        using var _ = Assert.Multiple();
        await Assert.That(elevated.Count).IsEqualTo(2);
        await Assert.That(elevated[0].NodeType).IsEqualTo("GetRtEntitiesById@1");
        await Assert.That(elevated[0].Identity).IsEqualTo("System");
        await Assert.That(elevated[1].Identity).IsEqualTo("ServiceAccount");
    }

    [Test]
    public async Task FindConfusedDeputyHazards_FlagsCallerControlledRoots_AndForEachFullPrefix()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesById@1
                identity: System
                rtIdsPath: $.body.rtId
                ckTypeIdPath: $.full.query.type
            """);

        var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);

        using var _ = Assert.Multiple();
        await Assert.That(findings.Count).IsEqualTo(2);
        await Assert.That(findings.Any(f => f.PropertyName == "rtIdsPath")).IsTrue();
        await Assert.That(findings.Any(f => f.PropertyName == "ckTypeIdPath")).IsTrue();
    }

    [Test]
    public async Task FindConfusedDeputyHazards_FieldFilterComparisonValuePath_IsFlagged()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesByType@1
                identity: ServiceAccount
                fieldFilters:
                  - attributePath: $.name
                    comparisonValuePath: $.query.name
            """);

        var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);

        await Assert.That(findings.Any(f => f.PropertyName == "fieldFilters[0].comparisonValuePath"
                                            && f.CallerControlledPath == "$.query.name")).IsTrue();
    }

    [Test]
    public async Task FindConfusedDeputyHazards_ConstantAndPrincipalTargets_AreSilent()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesById@1
                identity: System
                rtIds:
                  - 507f1f77bcf86cd799439011
                ckTypeIdPath: $.principal.tenant
            """);

        var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);

        // A constant id list and the verified $.principal subset are not caller-steerable.
        await Assert.That(findings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FindConfusedDeputyHazards_NonElevatedNode_IsNeverFlagged()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesById@1
                rtIdsPath: $.body.rtId
            """);

        var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);

        await Assert.That(findings.Count).IsEqualTo(0);
    }
}
