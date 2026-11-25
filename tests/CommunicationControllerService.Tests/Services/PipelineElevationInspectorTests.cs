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

    // AB#9767 decision 2 (Odin, 2026-11-25): a service-account or system node may name its target tenant only
    // statically (tenantId/tenantIds), never from data; no path is classified as caller- or pipeline-controlled.
    private static (IReadOnlyList<TargetTenantFinding> Refused, IReadOnlyList<TargetTenantFinding> Warned)
        FindDataDrivenTargets(string yaml)
    {
        Parser.TryGetAllNodes(yaml, out var nodes);
        return (PipelineElevationInspector.FindDataDrivenTargetTenants(nodes, elevated: true),
            PipelineElevationInspector.FindDataDrivenTargetTenants(nodes, elevated: false));
    }

    private static string DataDrivenTarget(string identityLine, string tenantIdPath, string triggers = VerifiedTrigger) =>
        triggers + "\n" +
        $"""
         transformations:
           - type: ApplyChanges@2
             {identityLine}
             entityUpdatesPath: $.updates
             targetTenant:
               relation: Child
               tenantIdPath: "{tenantIdPath}"
         """;

    private const string VerifiedTrigger =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /x
            allowAnonymous: false
        """;

    private const string CronTrigger =
        """
        triggers:
          - type: FromPipelineTriggerEvent@1
        """;

    [Test]
    [Arguments("$.body.t")]
    [Arguments("$.formData.t")]
    [Arguments("body.t")]
    [Arguments("$.invoice.tenant")]
    [Arguments("$..t")]
    public async Task ServiceAccountNode_WithTenantIdPath_IsRefused_WhateverThePathReads(string path)
    {
        var (refused, warned) = FindDataDrivenTargets(DataDrivenTarget("identity: ServiceAccount", path));

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].Identity).IsEqualTo("ServiceAccount");
        await Assert.That(refused[0].PropertyName).IsEqualTo("targetTenant.tenantIdPath");
        await Assert.That(refused[0].Value).IsEqualTo(path);
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ServiceAccountNode_WithTenantIdPath_OnACronPipeline_IsRefused()
    {
        var (refused, _) = FindDataDrivenTargets(DataDrivenTarget("identity: ServiceAccount", "$.t", CronTrigger));

        await Assert.That(refused.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ServiceAccountNode_WithTenantIdPath_InsideForEach_IsRefused()
    {
        var (refused, _) = FindDataDrivenTargets(CronTrigger + "\n" +
            """
            transformations:
              - type: ForEach@1
                iterationPath: $.items
                transformations:
                  - type: ApplyChanges@2
                    identity: ServiceAccount
                    entityUpdatesPath: $.updates
                    targetTenant:
                      relation: Child
                      tenantIdPath: $.item.tenant
            """);

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].Value).IsEqualTo("$.item.tenant");
    }

    [Test]
    public async Task ServiceAccountNode_WithStaticTarget_IsNotRefused()
    {
        var (refused, warned) = FindDataDrivenTargets(CronTrigger + "\n" +
            """
            transformations:
              - type: GetRtEntitiesByType@1
                identity: ServiceAccount
                ckTypeId: Basic/Contact
                targetTenant:
                  relation: Sibling
                  tenantId: companyb
              - type: GetRtEntitiesByType@1
                identity: ServiceAccount
                ckTypeId: Basic/Contact
                targetTenant:
                  relation: Parent
              - type: ApplyChanges@2
                identity: ServiceAccount
                entityUpdatesPath: $.updates
                targetTenants:
                  relation: Child
                  tenantIds:
                    - child-a
                    - child-b
            """);

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(0);
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    // Fail closed: a target setting the controller cannot read as static counts as data-driven, and any member named
    // like a path does, also a later variant such as tenantIdsPath and a key that differs only in case.
    [Test]
    public async Task ServiceAccountNode_WithAnyNonStaticTargetSetting_IsRefused()
    {
        var (refused, _) = FindDataDrivenTargets(CronTrigger + "\n" +
            """
            transformations:
              - type: ApplyChanges@2
                identity: ServiceAccount
                targetTenants:
                  relation: Child
                  tenantIdsPath: $.tenants
              - type: ApplyChanges@2
                identity: ServiceAccount
                TargetTenant:
                  relation: Child
                  TenantIdPATH: $.t
              - type: ApplyChanges@2
                identity: ServiceAccount
                targetTenant: $.t
              - type: ApplyChanges@2
                identity: ServiceAccount
                targetTenants:
                  - relation: Child
                    tenantId: child-a
                  - relation: Child
                    tenantIdPath: $.full.t
            """);

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(4);
        await Assert.That(refused[0].PropertyName).IsEqualTo("targetTenants.tenantIdsPath");
        await Assert.That(refused[1].PropertyName).IsEqualTo("TargetTenant.TenantIdPATH");
        await Assert.That(refused[2].PropertyName).IsEqualTo("targetTenant");
        await Assert.That(refused[3].PropertyName).IsEqualTo("targetTenants[1].tenantIdPath");
    }

    [Test]
    public async Task CallerNode_WithTenantIdPath_IsOnlyWarned_AlsoWithoutIdentityProperty_AndOnACronPipeline()
    {
        var (refused, warned) = FindDataDrivenTargets(DataDrivenTarget("identity: Caller", "$.body.t", CronTrigger) +
            "\n" +
            """
              - type: ApplyChanges@2
                entityUpdatesPath: $.updates
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.formData.t
            """);

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(0);
        await Assert.That(warned.Count).IsEqualTo(2);
        await Assert.That(warned[0].Identity).IsEqualTo("Caller");
        await Assert.That(warned[1].Value).IsEqualTo("$.formData.t");
    }

    [Test]
    public async Task SystemNode_WithAnyTarget_IsElevatedForBothRules()
    {
        var nodes = Parse(DataDrivenTarget("identity: System", "$.invoice.tenant"));
        var (refused, warned) = FindDataDrivenTargets(DataDrivenTarget("identity: System", "$.invoice.tenant"));

        using var _ = Assert.Multiple();
        await Assert.That(PipelineElevationInspector.FindSystemCrossTenantNodes(nodes).Count).IsEqualTo(1);
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    // The AB#5128 confused-deputy lint stays about entity selectors; a target tenant is judged by the rule above.
    [Test]
    public async Task FindConfusedDeputyHazards_DoesNotReportTargetTenantPaths()
    {
        var nodes = Parse(DataDrivenTarget("identity: ServiceAccount", "$.body.t"));

        await Assert.That(PipelineElevationInspector.FindConfusedDeputyHazards(nodes).Count).IsEqualTo(0);
    }

    // AB#9767 (review finding 1): the adapter binds `identity` with YamlDotNet's Enum.Parse(ignoreCase), which also
    // accepts leading zeros, signs and comma-joined flag names. Only an absent value, `Caller` or `0` (trimmed, any
    // case) is the caller; every other value counts as elevated, fail closed.
    [Test]
    [Arguments("'01'")]
    [Arguments("'+2'")]
    [Arguments("'Caller, System'")]
    [Arguments("'Caller, ServiceAccount'")]
    [Arguments("Admin")]
    public async Task UnrecognisedIdentity_CountsAsElevated_FailClosed(string identity)
    {
        var yaml = DataDrivenTarget($"identity: {identity}", "$.body.tenant");
        var nodes = Parse(yaml);
        var (refused, warned) = FindDataDrivenTargets(yaml);

        using var _ = Assert.Multiple();
        await Assert.That(PipelineElevationInspector.FindElevatedNodes(nodes).Count).IsEqualTo(1);
        await Assert.That(PipelineElevationInspector.FindUnrecognisedIdentityNodes(nodes).Count).IsEqualTo(1);
        await Assert.That(PipelineElevationInspector.FindSystemCrossTenantNodes(nodes).Count).IsEqualTo(1);
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("Caller")]
    [Arguments("caller")]
    [Arguments("' CALLER '")]
    [Arguments("0")]
    [Arguments("' 0 '")]
    public async Task CallerSpellings_AreTheCaller(string identity)
    {
        var yaml = DataDrivenTarget($"identity: {identity}", "$.body.tenant");
        var nodes = Parse(yaml);
        var (refused, warned) = FindDataDrivenTargets(yaml);

        using var _ = Assert.Multiple();
        await Assert.That(PipelineElevationInspector.FindElevatedNodes(nodes).Count).IsEqualTo(0);
        await Assert.That(PipelineElevationInspector.FindUnrecognisedIdentityNodes(nodes).Count).IsEqualTo(0);
        await Assert.That(refused.Count).IsEqualTo(0);
        await Assert.That(warned.Count).IsEqualTo(1);
    }

    // AB#9767 (review finding 2): Switch@1 keeps its child nodes in cases[].transformations and default; every rule
    // sees them, also in a Switch nested in a case.
    private const string SwitchWithNestedNodes =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /x
        transformations:
          - type: Switch@1
            path: $.body.kind
            cases:
              - value: a
                transformations:
                  - type: ApplyChanges@2
                    identity: ServiceAccount
                    entityUpdatesPath: $.updates
                    targetTenant:
                      relation: Child
                      tenantIdPath: $.body.t
              - value: b
                transformations:
                  - type: Switch@1
                    path: $.body.sub
                    cases: []
                    default:
                      - type: GetRtEntitiesById@1
                        identity: System
                        rtIdsPath: $.body.ids
                        targetTenant:
                          relation: Parent
            default:
              - type: ApplyChanges@2
                entityUpdatesPath: $.updates
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.query.t
        """;

    [Test]
    public async Task SwitchCasesAndDefault_AreWalked_ByEveryRule()
    {
        var nodes = Parse(SwitchWithNestedNodes);
        var (refused, warned) = FindDataDrivenTargets(SwitchWithNestedNodes);

        using var _ = Assert.Multiple();
        await Assert.That(nodes.Count(n => n.NodeType == "ApplyChanges@2")).IsEqualTo(2);
        await Assert.That(nodes.Count(n => n.NodeType == "Switch@1")).IsEqualTo(2);
        await Assert.That(PipelineElevationInspector.FindElevatedNodes(nodes).Count).IsEqualTo(2);
        await Assert.That(PipelineElevationInspector.FindSystemCrossTenantNodes(nodes).Count).IsEqualTo(1);
        await Assert.That(PipelineElevationInspector.FindConfusedDeputyHazards(nodes)
            .Any(f => f.PropertyName == "rtIdsPath")).IsTrue();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].Value).IsEqualTo("$.body.t");
        await Assert.That(warned.Count).IsEqualTo(1);
        await Assert.That(warned[0].Value).IsEqualTo("$.query.t");
    }

    [Test]
    public async Task GetNodeProperties_FindsANodeInsideASwitchCase_WithTheSameIndexAsTheWalk()
    {
        var nested = Parser.GetNodeProperties(SwitchWithNestedNodes, "ApplyChanges@2", 1);

        await Assert.That(nested?.Properties["targetTenant"]).IsNotNull();
        await Assert.That(nested!.Properties.ContainsKey("identity")).IsFalse();
    }
}
