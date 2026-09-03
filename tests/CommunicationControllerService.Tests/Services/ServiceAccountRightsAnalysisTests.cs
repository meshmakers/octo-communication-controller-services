using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5113 — the rights analysis (<see cref="ServiceAccountRightsAnalysisService" />): type
/// extraction from pipeline YAML (nested containers, dynamic-path reporting), the join with the
/// tenant's data policies/permissions/roles (unprotected types dropped, owner-scoped flagged,
/// version-insensitive matching), the delta against the declaration (missing / superfluous /
/// baseline, legacy opt-out), and the robustness contract (unparsable YAML → warning, empty
/// pipeline set → valid empty result). YAML parsing runs through the real
/// <see cref="PipelineDefinitionService" /> — the same machinery the debugger endpoints use.
/// </summary>
internal class ServiceAccountRightsAnalysisTests
{
    private const string TenantId = "tenantId";
    private const string DocumentType = "Meshmakers.Accounting/Document";
    private const string TransactionType = "Meshmakers.Accounting/Transaction";
    private const string CategoryType = "Meshmakers.Accounting/Category";

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private readonly IPipelineServiceAccountResolver _resolver =
        Substitute.For<IPipelineServiceAccountResolver>();

    private ServiceAccountRightsAnalysisService CreateSut()
    {
        return new ServiceAccountRightsAnalysisService(_repo, _resolver, new PipelineDefinitionService());
    }

    // ---------------------------------------------------------------- arrangement helpers

    /// <summary>An adapter whose default account runs the given pipelines (no overrides).</summary>
    private RtAdapter ArrangeAdapterWithPipelines(RtServiceAccountConfiguration? configuration,
        params RtPipeline[] pipelines)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(configuration);
        _repo.GetPipelinesAsync(TenantId, Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId))
            .Returns(pipelines);
        foreach (var pipeline in pipelines)
        {
            _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId)
                .Returns(configuration != null
                    ? new PipelineServiceAccountResolution(configuration, PipelineServiceAccountSource.AdapterDefault)
                    : PipelineServiceAccountResolution.Unresolved);
        }

        return adapter;
    }

    /// <summary>
    /// One DataPolicy entity as the untyped repository read returns it (attribute names in
    /// PascalCase, exactly like the MongoDB deserialization shape).
    /// </summary>
    private static RtEntity CreatePolicy(string wellKnownName, IEnumerable<string> targetCkTypeIds,
        int scope = 0, int enforcementMode = 0)
    {
        var policy = new RtEntity("System.Identity/DataPolicy", OctoObjectId.GenerateNewId())
        {
            RtWellKnownName = wellKnownName
        };
        policy.SetAttributeRawValue("TargetCkTypeIds", targetCkTypeIds.ToList());
        policy.SetAttributeRawValue("Scope", scope);
        policy.SetAttributeRawValue("EnforcementMode", enforcementMode);
        return policy;
    }

    private static RtEntity CreatePermission(string permissionId)
    {
        var permission = new RtEntity("System.Identity/DataPermission", OctoObjectId.GenerateNewId());
        permission.SetAttributeRawValue("PermissionId", permissionId);
        return permission;
    }

    private static RtEntity CreateRole(string name)
    {
        var role = new RtEntity("System.Identity/Role", OctoObjectId.GenerateNewId());
        role.SetAttributeRawValue("Name", name);
        return role;
    }

    /// <summary>Wires policy → permission → roles through the three repository reads.</summary>
    private void ArrangeIdentityGraph(params (RtEntity Policy, RtEntity Permission, string[] RoleNames)[] chains)
    {
        _repo.GetDataPoliciesAsync(TenantId).Returns(chains.Select(c => c.Policy).Distinct().ToList());
        _repo.GetDataPermissionsForPoliciesAsync(TenantId, Arg.Any<IReadOnlyCollection<OctoObjectId>>())
            .Returns(chains.ToDictionary(c => c.Policy.RtId,
                c => (IReadOnlyList<RtEntity>)[c.Permission]));
        _repo.GetGrantingRolesForDataPermissionsAsync(TenantId, Arg.Any<IReadOnlyCollection<OctoObjectId>>())
            .Returns(chains.ToDictionary(c => c.Permission.RtId,
                c => (IReadOnlyList<RtEntity>)c.RoleNames.Select(CreateRole).ToList()));
    }

    private static RtServiceAccountConfiguration CreateDeclaredConfiguration(params string[] roleNames)
    {
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        configuration.AssignedRoleNames = new AttributeStringValueList(roleNames.ToList());
        return configuration;
    }

    private static RtPipeline CreateNamedPipeline(string name, string definition)
    {
        var pipeline = RtEntityCreator.CreatePipeline(definition);
        pipeline.Name = name;
        return pipeline;
    }

    // ---------------------------------------------------------------- type extraction

    [Test]
    public async Task AnalyzeAdapter_NestedContainersAndAllReferenceProperties_ExtractsEveryTouchedType()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration(CommonConstants.CommunicationManagementRole, "DataAccess");
        // Every reference shape at once: top-level ckTypeId, a node nested two containers deep,
        // and target/origin ids inside a nested update record (list-of-dict recursion).
        var pipeline = CreateNamedPipeline("import", $"""
            triggers:
              - type: FromDataFlow@1
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
              - type: ForEach@1
                itemsPath: $.items
                transformations:
                  - type: If@1
                    transformations:
                      - type: CreateUpdateInfo@2
                        ckTypeId: {TransactionType}
                  - type: CreateAssociationUpdate@1
                    updates:
                      - targetCkTypeId: {CategoryType}
                        originCkTypeId: {TransactionType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        var policy = CreatePolicy("AccountingPolicy", [DocumentType, TransactionType, CategoryType]);
        ArrangeIdentityGraph((policy, CreatePermission("accounting.data"), ["DataAccess"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.ProtectedTypes.Select(e => e.CkTypeId).Order().ToList())
            .IsEquivalentTo(new List<string> { CategoryType, DocumentType, TransactionType });
        await Assert.That(dto.ProtectedTypes.All(e => e.TouchedByPipelines.SequenceEqual(["import"]))).IsTrue();
        await Assert.That(dto.DynamicTypeUsages).IsEmpty();
        await Assert.That(dto.Warnings).IsEmpty();
        await Assert.That(dto.MissingRoles).IsEmpty();
        await Assert.That(dto.SuperfluousRoles).IsEmpty();
        await Assert.That(dto.BaselineRoles).IsEquivalentTo(
            new List<string> { CommonConstants.CommunicationManagementRole });
    }

    [Test]
    public async Task AnalyzeAdapter_CkTypeIdsListAndDeduplication_KeepsPerTypeProvenance()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("DataAccess");
        var first = CreateNamedPipeline("first", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeIds:
                  - {DocumentType}
                  - {TransactionType}
            """);
        var second = CreateNamedPipeline("second", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, first, second);

        var policy = CreatePolicy("AccountingPolicy", [DocumentType, TransactionType]);
        ArrangeIdentityGraph((policy, CreatePermission("accounting.data"), ["DataAccess"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        var documentEntry = dto.ProtectedTypes.Single(e => e.CkTypeId == DocumentType);
        var transactionEntry = dto.ProtectedTypes.Single(e => e.CkTypeId == TransactionType);
        await Assert.That(documentEntry.TouchedByPipelines).IsEquivalentTo(new List<string> { "first", "second" });
        await Assert.That(transactionEntry.TouchedByPipelines).IsEquivalentTo(new List<string> { "first" });
    }

    [Test]
    public async Task AnalyzeAdapter_DynamicPathAndTemplatedId_ReportedNotSilentlyIgnored()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("DataAccess");
        var pipeline = CreateNamedPipeline("dynamic", """
            transformations:
              - type: SetPrimitiveValue@1
                ckTypeIdPath: $.typeId
              - type: GetRtEntities@1
                ckTypeId: "{{settings.entityType}}"
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);
        ArrangeIdentityGraph();

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.DynamicTypeUsages.Count).IsEqualTo(2);
        var pathUsage = dto.DynamicTypeUsages.Single(u => u.PropertyName == "ckTypeIdPath");
        await Assert.That(pathUsage.PipelineName).IsEqualTo("dynamic");
        await Assert.That(pathUsage.NodeType).IsEqualTo("SetPrimitiveValue@1");
        var templatedUsage = dto.DynamicTypeUsages.Single(u => u.PropertyName == "ckTypeId");
        await Assert.That(templatedUsage.NodeType).IsEqualTo("GetRtEntities@1");
        await Assert.That(dto.ProtectedTypes).IsEmpty();
        await Assert.That(dto.Message).Contains("dynamic");
    }

    // ---------------------------------------------------------------- policy join

    [Test]
    public async Task AnalyzeAdapter_TouchedTypeWithoutPolicy_IsDropped()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("DataAccess");
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
              - type: GetRtEntities@1
                ckTypeId: {TransactionType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        // Only Document is protected — Transaction must not appear anywhere.
        var policy = CreatePolicy("DocumentPolicy", [DocumentType]);
        ArrangeIdentityGraph((policy, CreatePermission("document.read"), ["DataAccess"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.ProtectedTypes.Select(e => e.CkTypeId).ToList())
            .IsEquivalentTo(new List<string> { DocumentType });
        await Assert.That(dto.MissingRoles).IsEmpty();
    }

    [Test]
    public async Task AnalyzeAdapter_VersionedPolicyTarget_MatchesUnversionedPipelineReference()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("DataAccess");
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        // Canonical policy target with an element version (SemanticVersionedFullName) — the match
        // is on the Model/Type name part, version-insensitively.
        var policy = CreatePolicy("DocumentPolicy", [$"{DocumentType}-3"]);
        ArrangeIdentityGraph((policy, CreatePermission("document.read"), ["DataAccess"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        var entry = dto.ProtectedTypes.Single();
        using var _ = Assert.Multiple();
        await Assert.That(entry.CkTypeId).IsEqualTo(DocumentType);
        await Assert.That(entry.Policy.Name).IsEqualTo("DocumentPolicy");
        await Assert.That(entry.Policy.Mode).IsEqualTo("Enforce");
    }

    [Test]
    public async Task AnalyzeAdapter_PermissionGrantedToSeveralRoles_JoinsAllGrantingRoles()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("Reader");
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        var policy = CreatePolicy("DocumentPolicy", [DocumentType], enforcementMode: 1);
        ArrangeIdentityGraph((policy, CreatePermission("document.read"), ["Reader", "Auditor"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        var entry = dto.ProtectedTypes.Single();
        using var _ = Assert.Multiple();
        await Assert.That(entry.GrantingRoles).IsEquivalentTo(new List<string> { "Auditor", "Reader" });
        await Assert.That(entry.Policy.Mode).IsEqualTo("AuditOnly");
        await Assert.That(dto.MissingRoles).IsEmpty(); // Reader is declared and grants the type.
    }

    // ---------------------------------------------------------------- owner scope (D7)

    [Test]
    public async Task AnalyzeAdapter_OwnerScopedPolicyOnly_FlagsEntryAndDoesNotCountAsCoverage()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("Owner");
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        // OwnedOnly scope: even the declared 'Owner' role does not help a service account (D7) —
        // the entry is flagged and the summary names the blind type.
        var policy = CreatePolicy("OwnedDocumentPolicy", [DocumentType], scope: 1);
        ArrangeIdentityGraph((policy, CreatePermission("document.owned"), ["Owner"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        var entry = dto.ProtectedTypes.Single();
        using var _ = Assert.Multiple();
        await Assert.That(entry.OwnerScoped).IsTrue();
        await Assert.That(entry.Message).IsNotNull();
        await Assert.That(entry.Message!).Contains("full-scope");
        // No full-scope grant exists, so no role is 'missing' — a role cannot fix this.
        await Assert.That(dto.MissingRoles).IsEmpty();
        await Assert.That(dto.Message).Contains(DocumentType);
        await Assert.That(dto.Message).Contains("owner-scoped");
    }

    [Test]
    public async Task AnalyzeAdapter_OwnerScopedBesideFullScopePolicy_FullScopeGrantCounts()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration(CommonConstants.CommunicationManagementRole);
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        var ownedPolicy = CreatePolicy("OwnedDocumentPolicy", [DocumentType], scope: 1);
        var fullPolicy = CreatePolicy("FullDocumentPolicy", [DocumentType]);
        ArrangeIdentityGraph(
            (ownedPolicy, CreatePermission("document.owned"), ["Owner"]),
            (fullPolicy, CreatePermission("document.read"), ["Reader"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        // One entry per (type × policy); the type is not blind because a full-scope grant exists.
        await Assert.That(dto.ProtectedTypes.Count).IsEqualTo(2);
        await Assert.That(dto.Message.Contains("owner-scoped grants exist")).IsFalse();
        // The full-scope grant's role is missing from the declaration.
        await Assert.That(dto.MissingRoles).IsEquivalentTo(new List<string> { "Reader" });
    }

    // ---------------------------------------------------------------- declaration delta

    [Test]
    public async Task AnalyzeAdapter_DeclarationDelta_MissingSuperfluousAndBaseline()
    {
        var sut = CreateSut();
        // CommunicationManagement is the operational baseline (grants no data access, but every
        // pipeline SA needs it) — it must land in baselineRoles, never in superfluousRoles.
        var configuration = CreateDeclaredConfiguration(CommonConstants.CommunicationManagementRole, "Stale");
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        var policy = CreatePolicy("DocumentPolicy", [DocumentType]);
        ArrangeIdentityGraph((policy, CreatePermission("document.read"), ["Reader"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.DeclaredRoles!).IsEquivalentTo(
            new List<string> { CommonConstants.CommunicationManagementRole, "Stale" });
        await Assert.That(dto.MissingRoles).IsEquivalentTo(new List<string> { "Reader" });
        await Assert.That(dto.SuperfluousRoles).IsEquivalentTo(new List<string> { "Stale" });
        await Assert.That(dto.BaselineRoles).IsEquivalentTo(
            new List<string> { CommonConstants.CommunicationManagementRole });
        await Assert.That(dto.Message).Contains("Reader");
    }

    [Test]
    public async Task AnalyzeAdapter_LegacyConfigurationWithoutDeclaration_ReportsRecommendationWithoutDelta()
    {
        var sut = CreateSut();
        // No AssignedRoleNames — the pre-3.32.0 legacy shape.
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        var pipeline = CreateNamedPipeline("import", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, pipeline);

        var policy = CreatePolicy("DocumentPolicy", [DocumentType]);
        ArrangeIdentityGraph((policy, CreatePermission("document.read"), ["Reader"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.DeclaredRoles).IsNull();
        await Assert.That(dto.MissingRoles).IsEmpty();
        await Assert.That(dto.SuperfluousRoles).IsEmpty();
        await Assert.That(dto.BaselineRoles).IsEmpty();
        // The recommendation is still there: the per-type granting roles.
        await Assert.That(dto.ProtectedTypes.Single().GrantingRoles)
            .IsEquivalentTo(new List<string> { "Reader" });
        await Assert.That(dto.Message).Contains("legacy");
    }

    // ---------------------------------------------------------------- robustness

    [Test]
    public async Task AnalyzeAdapter_UnparsablePipeline_WarnsAndAnalyzesTheRest()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("Reader");
        var broken = CreateNamedPipeline("broken", "an: unclosed: mapping: [");
        var healthy = CreateNamedPipeline("healthy", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var adapter = ArrangeAdapterWithPipelines(configuration, broken, healthy);

        var policy = CreatePolicy("DocumentPolicy", [DocumentType]);
        ArrangeIdentityGraph((policy, CreatePermission("document.read"), ["Reader"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        var warning = dto.Warnings.Single();
        await Assert.That(warning.PipelineName).IsEqualTo("broken");
        await Assert.That(warning.Message).Contains("YAML");
        await Assert.That(dto.ProtectedTypes.Single().CkTypeId).IsEqualTo(DocumentType);
        await Assert.That(dto.AnalyzedPipelines).IsEquivalentTo(new List<string> { "broken", "healthy" });
    }

    [Test]
    public async Task AnalyzeAdapter_EmptyPipelineSet_ReturnsEmptyButValidResult()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("Reader");
        var adapter = ArrangeAdapterWithPipelines(configuration);

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.AnalyzedPipelines).IsEmpty();
        await Assert.That(dto.ProtectedTypes).IsEmpty();
        await Assert.That(dto.DynamicTypeUsages).IsEmpty();
        await Assert.That(dto.Warnings).IsEmpty();
        await Assert.That(dto.MissingRoles).IsEmpty();
        await Assert.That(dto.Message).Contains("No pipelines");
        // The identity join is skipped entirely — nothing was touched.
        await _repo.DidNotReceiveWithAnyArgs().GetDataPoliciesAsync(default!);
    }

    // ---------------------------------------------------------------- pipeline-set resolution

    [Test]
    public async Task AnalyzeAdapter_PipelineWithOwnOverride_IsExcluded()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("Reader");
        var adapter = RtEntityCreator.CreateAdapter();
        var inherited = CreateNamedPipeline("inherited", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var overriding = CreateNamedPipeline("overriding", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {TransactionType}
            """);

        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(configuration);
        _repo.GetPipelinesAsync(TenantId, Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId))
            .Returns([inherited, overriding]);
        _resolver.ResolveAsync(TenantId, inherited.RtId, adapter.RtId)
            .Returns(new PipelineServiceAccountResolution(configuration,
                PipelineServiceAccountSource.AdapterDefault));
        _resolver.ResolveAsync(TenantId, overriding.RtId, adapter.RtId)
            .Returns(new PipelineServiceAccountResolution(RtEntityCreator.CreateServiceAccountConfiguration("other"),
                PipelineServiceAccountSource.PipelineOverride));

        var policy = CreatePolicy("AccountingPolicy", [DocumentType, TransactionType]);
        ArrangeIdentityGraph((policy, CreatePermission("accounting.data"), ["Reader"]));

        var dto = await sut.AnalyzeAdapterAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        // Only the inherited pipeline is analyzed — the overriding one executes as someone else.
        await Assert.That(dto.AnalyzedPipelines).IsEquivalentTo(new List<string> { "inherited" });
        await Assert.That(dto.ProtectedTypes.Select(e => e.CkTypeId).ToList())
            .IsEquivalentTo(new List<string> { DocumentType });
    }

    [Test]
    public async Task AnalyzeConfiguration_AdapterDefaultsPlusOverridesToIt_MinusOverriddenAway()
    {
        var sut = CreateSut();
        var configuration = CreateDeclaredConfiguration("Reader");
        var adapter = RtEntityCreator.CreateAdapter();
        var inherited = CreateNamedPipeline("inherited", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {DocumentType}
            """);
        var overriddenAway = CreateNamedPipeline("overriddenAway", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {TransactionType}
            """);
        var overridingToIt = CreateNamedPipeline("overridingToIt", $"""
            transformations:
              - type: GetRtEntities@1
                ckTypeId: {CategoryType}
            """);

        // The configuration is the adapter's default; one adapter pipeline overrides away.
        _repo.GetAdapterForServiceAccountAsync(TenantId, configuration.RtId).Returns(adapter);
        _repo.GetPipelinesAsync(TenantId, Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId))
            .Returns([inherited, overriddenAway]);
        _resolver.ResolveAsync(TenantId, inherited.RtId, adapter.RtId)
            .Returns(new PipelineServiceAccountResolution(configuration,
                PipelineServiceAccountSource.AdapterDefault));
        _resolver.ResolveAsync(TenantId, overriddenAway.RtId, adapter.RtId)
            .Returns(new PipelineServiceAccountResolution(RtEntityCreator.CreateServiceAccountConfiguration("other"),
                PipelineServiceAccountSource.PipelineOverride));

        // A pipeline on another adapter overrides TO this configuration via its Uses edge.
        _repo.GetPipelinesUsingServiceAccountAsync(TenantId, configuration.RtId)
            .Returns([overridingToIt]);
        _resolver.ResolveForPipelineAsync(TenantId, Arg.Is<RtEntityId>(id => id.RtId == overridingToIt.RtId))
            .Returns(new PipelineServiceAccountResolution(configuration,
                PipelineServiceAccountSource.PipelineOverride));

        var policy = CreatePolicy("AccountingPolicy", [DocumentType, TransactionType, CategoryType]);
        ArrangeIdentityGraph((policy, CreatePermission("accounting.data"), ["Reader"]));

        var dto = await sut.AnalyzeConfigurationAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.AnalyzedPipelines)
            .IsEquivalentTo(new List<string> { "inherited", "overridingToIt" });
        await Assert.That(dto.ProtectedTypes.Select(e => e.CkTypeId).Order().ToList())
            .IsEquivalentTo(new List<string> { CategoryType, DocumentType });
        await Assert.That(dto.ConfigurationRtId).IsEqualTo(configuration.RtId.ToString());
    }
}
