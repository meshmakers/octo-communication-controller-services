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

        // A path the pipeline computed is not caller-steered; a Caller node is not elevated, so this lint skips it (the
        // cross-tenant rules below judge a caller-steered target tenant of a Caller node).
        await Assert.That(findings.Count).IsEqualTo(0);
    }

    // AB#9767 decision 2: which identity acts in a caller-steered target tenant decides between refusal and warning.
    private static (IReadOnlyList<ConfusedDeputyFinding> Refused, IReadOnlyList<ConfusedDeputyFinding> Warned)
        ClassifyCallerSteeredTargets(string yaml)
    {
        Parser.TryGetAllNodes(yaml, out var nodes);
        Parser.TryGetTriggerNodes(yaml, out var triggers);
        return (PipelineElevationInspector.FindRefusedCallerSteeredTargetTenants(nodes, triggers),
            PipelineElevationInspector.FindCallerSteeredTargetTenantWarnings(nodes, triggers));
    }

    private static string CallerSteered(string triggers, string identityLine) =>
        triggers + "\n" +
        $"""
         transformations:
           - type: ApplyChanges@2
             {identityLine}
             entityUpdatesPath: $.updates
             targetTenant:
               relation: Child
               tenantIdPath: $.body.tenant
         """;

    private const string VerifiedTrigger =
        """
        triggers:
          - type: FromHttpRequest@2
            method: Post
            path: /x
            allowAnonymous: false
        """;

    [Test]
    public async Task CallerSteeredTargetTenant_ServiceAccount_IsRefused()
    {
        var (refused, warned) = ClassifyCallerSteeredTargets(CallerSteered(VerifiedTrigger, "identity: ServiceAccount"));

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].Identity).IsEqualTo("ServiceAccount");
        await Assert.That(refused[0].PropertyName).IsEqualTo("targetTenant.tenantIdPath");
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CallerSteeredTargetTenant_PureCaller_IsOnlyWarned_AlsoWithoutIdentityProperty()
    {
        var (refused, warned) = ClassifyCallerSteeredTargets(CallerSteered(VerifiedTrigger, "identity: Caller") + "\n" +
            """
              - type: ApplyChanges@2
                entityUpdatesPath: $.updates
                targetTenant:
                  relation: Sibling
                  tenantIdPath: $.headers.x-tenant
            """);

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(0);
        await Assert.That(warned.Count).IsEqualTo(2);
        await Assert.That(warned[0].Identity).IsEqualTo("Caller");
    }

    // Every trigger that can run without a verified caller makes a Caller node fall back to the service account.
    [Test]
    [Arguments("triggers:\n  - type: FromHttpRequest@2\n    method: Post\n    path: /x\n    allowAnonymous: true")]
    [Arguments("triggers:\n  - type: FromHttpRequest@2\n    method: Post\n    path: /x\n    allowAnonymous: yes-please")]
    [Arguments("triggers:\n  - type: FromHttpRequest@1\n    method: Post\n    path: /x")]
    [Arguments("triggers:\n  - type: FromHttpRequest@2\n    method: Post\n    path: /x\n  - type: FromPipelineTriggerEvent@1")]
    [Arguments("triggers: []")]
    public async Task CallerSteeredTargetTenant_CallerWithServiceAccountFallback_IsRefused(string triggers)
    {
        var (refused, warned) = ClassifyCallerSteeredTargets(CallerSteered(triggers, "identity: Caller"));

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].Identity).IsEqualTo("Caller, service-account fallback");
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CallerSteeredTargetTenant_SystemAndPipelineControlledPaths_AreNeitherRefusedNorWarnedHere()
    {
        var (refused, warned) = ClassifyCallerSteeredTargets(VerifiedTrigger + "\n" +
            """
            transformations:
              - type: GetRtEntitiesByType@1
                identity: System
                targetTenant:
                  relation: Child
                  tenantIdPath: $.body.tenant
              - type: ApplyChanges@2
                identity: ServiceAccount
                targetTenant:
                  relation: Child
                  tenantIdPath: $.invoice.tenant
            """);

        // System is refused by FindSystemCrossTenantNodes; a pipeline-controlled path is checked by the guard at run time.
        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(0);
        await Assert.That(warned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CallerSteeredTargetTenant_ListOfTargets_EveryEntryIsInspected()
    {
        var (refused, _) = ClassifyCallerSteeredTargets(VerifiedTrigger + "\n" +
            """
            transformations:
              - type: ApplyChanges@2
                identity: ServiceAccount
                targetTenants:
                  - relation: Child
                    tenantId: child-a
                  - relation: Child
                    tenantIdPath: $.full.query.tenant
            """);

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].PropertyName).IsEqualTo("targetTenants[1].tenantIdPath");
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
        var yaml = CallerSteered(VerifiedTrigger, $"identity: {identity}");
        var nodes = Parse(yaml);
        var (refused, warned) = ClassifyCallerSteeredTargets(yaml);

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
        var yaml = CallerSteered(VerifiedTrigger, $"identity: {identity}");
        var nodes = Parse(yaml);
        var (refused, warned) = ClassifyCallerSteeredTargets(yaml);

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
        var (refused, warned) = ClassifyCallerSteeredTargets(SwitchWithNestedNodes);

        using var _ = Assert.Multiple();
        await Assert.That(nodes.Count(n => n.NodeType == "ApplyChanges@2")).IsEqualTo(2);
        await Assert.That(nodes.Count(n => n.NodeType == "Switch@1")).IsEqualTo(2);
        await Assert.That(PipelineElevationInspector.FindElevatedNodes(nodes).Count).IsEqualTo(2);
        await Assert.That(PipelineElevationInspector.FindSystemCrossTenantNodes(nodes).Count).IsEqualTo(1);
        await Assert.That(PipelineElevationInspector.FindConfusedDeputyHazards(nodes)
            .Any(f => f.PropertyName == "rtIdsPath")).IsTrue();
        await Assert.That(refused.Count).IsEqualTo(1);
        await Assert.That(refused[0].CallerControlledPath).IsEqualTo("$.body.t");
        await Assert.That(warned.Count).IsEqualTo(1);
        await Assert.That(warned[0].CallerControlledPath).IsEqualTo("$.query.t");
    }

    [Test]
    public async Task GetNodeProperties_FindsANodeInsideASwitchCase_WithTheSameIndexAsTheWalk()
    {
        var nested = Parser.GetNodeProperties(SwitchWithNestedNodes, "ApplyChanges@2", 1);

        await Assert.That(nested?.Properties["targetTenant"]).IsNotNull();
        await Assert.That(nested!.Properties.ContainsKey("identity")).IsFalse();
    }

    // AB#9767 (review finding 4): caller-controlled = the request roots, every multi-match path, and what a trigger
    // other than FromHttpRequest@2 writes (a trigger whose output is not known: every path).
    private static string ServiceAccountTarget(string triggers, string tenantIdPath) =>
        triggers + "\n" +
        $"""
         transformations:
           - type: ApplyChanges@2
             identity: ServiceAccount
             entityUpdatesPath: $.updates
             targetTenant:
               relation: Child
               tenantIdPath: "{tenantIdPath}"
         """;

    [Test]
    [Arguments("$..t")]
    [Arguments("$.*.t")]
    [Arguments("$..body.t")]
    [Arguments("$[?(@.t)].t")]
    [Arguments("$['invoice','body'].t")]
    [Arguments("$[0].t")]
    [Arguments("$.full..t")]
    public async Task MultiMatchTenantIdPath_IsCallerControlled(string path)
    {
        var (refused, _) = ClassifyCallerSteeredTargets(ServiceAccountTarget(VerifiedTrigger, path));

        await Assert.That(refused.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("triggers:\n  - type: FromTeamsBot@1\n    serverConfiguration: bot", "$.Emails[0].Subject")]
    [Arguments("triggers:\n  - type: FromTeamsBot@1\n    serverConfiguration: bot", "$.conversation.tenant")]
    [Arguments("triggers:\n  - type: FromTeamsBot@1\n    serverConfiguration: bot", "$.body.tenant")]
    [Arguments("triggers:\n  - type: FromEmail@1\n    serverConfiguration: mail", "$.invoice.tenant")]
    [Arguments("triggers:\n  - type: FromSomeOtherAdapter@3", "$.invoice.tenant")]
    [Arguments("triggers: []", "$.invoice.tenant")]
    public async Task TriggerOutputOtherThanVerifiedHttp_IsCallerControlled(string triggers, string path)
    {
        var (refused, _) = ClassifyCallerSteeredTargets(ServiceAccountTarget(triggers, path));

        await Assert.That(refused.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("triggers:\n  - type: FromTeamsBot@1\n    serverConfiguration: bot")]
    [Arguments("triggers:\n  - type: FromPipelineTriggerEvent@1")]
    [Arguments("triggers:\n  - type: FromWatchRtEntity@1\n    updateTypes: Update\n    ckTypeId: Basic/Contact")]
    [Arguments("triggers:\n  - type: FromHttpRequest@2\n    method: Post\n    path: /x\n    allowAnonymous: true")]
    public async Task PipelineControlledPath_OfATriggerWithKnownOutput_StaysAllowed(string triggers)
    {
        var (refused, warned) = ClassifyCallerSteeredTargets(ServiceAccountTarget(triggers, "$.invoice.tenant"));

        using var _ = Assert.Multiple();
        await Assert.That(refused.Count).IsEqualTo(0);
        await Assert.That(warned.Count).IsEqualTo(0);
    }
}
