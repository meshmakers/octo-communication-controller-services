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

    // Cross-tenant access within a tenant subtree: the system identity never reaches another tenant (abuse case 5,
    // deploy-time half; the mesh adapter's guard refuses it again at run time).
    [Test]
    public async Task FindSystemCrossTenantNodes_SystemNodeWithTargetTenant_IsFound()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesByType@1
                identity: System
                ckTypeId: Basic/Contact
                targetTenant:
                  relation: Parent
              - type: ApplyChanges@2
                identity: System
                entityUpdatesPath: $.updates
                targetTenants:
                  relation: Child
            """);

        var found = PipelineElevationInspector.FindSystemCrossTenantNodes(nodes);

        using var _ = Assert.Multiple();
        await Assert.That(found.Count).IsEqualTo(2);
        await Assert.That(found[0].NodeType).IsEqualTo("GetRtEntitiesByType@1");
        await Assert.That(found[1].NodeType).IsEqualTo("ApplyChanges@2");
    }

    [Test]
    public async Task FindSystemCrossTenantNodes_NumericSystemIdentityAndEmptySetting_AreFoundFailClosed()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesByType@1
                identity: 2
                targetTenant:
            """);

        var found = PipelineElevationInspector.FindSystemCrossTenantNodes(nodes);

        await Assert.That(found.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FindSystemCrossTenantNodes_CallerOrServiceAccountWithTarget_OrSystemWithoutTarget_IsNotFound()
    {
        var nodes = Parse(
            """
            transformations:
              - type: GetRtEntitiesByType@1
                identity: Caller
                targetTenant:
                  relation: Parent
              - type: GetRtEntitiesByType@1
                identity: ServiceAccount
                targetTenant:
                  relation: Sibling
                  tenantId: companyb
              - type: GetRtEntitiesById@1
                identity: System
                rtIds:
                  - 507f1f77bcf86cd799439011
            """);

        var found = PipelineElevationInspector.FindSystemCrossTenantNodes(nodes);

        await Assert.That(found.Count).IsEqualTo(0);
    }

    // Abuse case 3 (deploy-time half): an elevated node whose target tenant comes from caller input.
    [Test]
    public async Task FindConfusedDeputyHazards_FlagsCallerControlledTargetTenantIdPath()
    {
        var nodes = Parse(
            """
            transformations:
              - type: ApplyChanges@2
                identity: ServiceAccount
                entityUpdatesPath: $.updates
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.body.recipientTenantId
            """);

        var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);

        using var _ = Assert.Multiple();
        await Assert.That(findings.Count).IsEqualTo(1);
        await Assert.That(findings[0].PropertyName).IsEqualTo("targetTenant.tenantIdPath");
        await Assert.That(findings[0].CallerControlledPath).IsEqualTo("$.body.recipientTenantId");
    }

    [Test]
    public async Task FindConfusedDeputyHazards_PipelineControlledTargetTenantIdPath_IsNotFlagged()
    {
        var nodes = Parse(
            """
            transformations:
              - type: ApplyChanges@2
                identity: ServiceAccount
                entityUpdatesPath: $.updates
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.invoice.recipientTenantId
              - type: ApplyChanges@2
                entityUpdatesPath: $.updates
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.body.recipientTenantId
            """);

        var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);

        // A path the pipeline computed is not caller-steered; a Caller node is never a confused deputy (it acts with
        // the caller's own rights in the target).
        await Assert.That(findings.Count).IsEqualTo(0);
    }
}
