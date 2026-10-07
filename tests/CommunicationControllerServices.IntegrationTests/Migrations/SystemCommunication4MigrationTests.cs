using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.CkModelMigrations;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Migrations;

/// <summary>
///     AB#4924 (G3) — a tenant on any System.Communication 3.x that main has shipped reaches 4.x WITH
///     the data migration, against real MongoDB and the real 4.x model + migration-meta of this repo.
/// </summary>
/// <remarks>
///     <para>
///         🔴 The bug this pins was silent. The 4.x migration-meta listed entry points only for 3.35.0 and
///         3.36.0, so a tenant on 3.40.0 (every main cluster) found no path and rode the engine's post-chain
///         schema-only bridge: schema 4.x, data still 3.x — Pool entities of a type nothing declares,
///         Manages edges no role defines, the seeded site still called CommunicationPool, no
///         post-validation and no error. Nothing in a unit test could see it, because it is the
///         combination of the real meta, the real engine path finder and real stored data that fails.
///     </para>
///     <para>
///         How the 3.x tenant is built: the published 3.40.0 compiled model (embedded resource, byte for
///         byte from the private catalog) is imported through the same <c>ImportCkModelAsync</c> a service
///         uses, then seeded with the blueprint's 3.x shape (seeded Cloud site + an operator-created Edge
///         site, each with an adapter hanging off it by Manages). For the other versions the model id
///         inside the JSON is rewritten — Pool, the Manages role and the CommunicationPool name are
///         identical in every 3.x from 3.35.0 to 3.42.0 (checked against the YAML of each release), and
///         the migration only reads those, so what varies between the cases is exactly what the bug was
///         about: which installed version the engine has to find a path from.
///     </para>
///     <para>
///         Each case gets its own child tenant, so the cases are independent of each other and of the
///         shared test tenant (which is born on 4.x).
///     </para>
/// </remarks>
[Collection("CommunicationController")]
public class SystemCommunication4MigrationTests(CommunicationControllerFixture fixture)
{
    private const string ModelName = "System.Communication";
    private const string PublishedVersion = "3.40.0";
    private const string ResourceSuffix = "ck-system.communication-3.40.0.json";

    // The blueprint's rtIds (System.Communication.Release / .MainLatest seed) for the seeded site and
    // the Mesh Adapter, plus an operator-created Edge site with its own adapter.
    private static readonly OctoObjectId SeededSiteRtId = new("670000000000000000000001");
    private static readonly OctoObjectId MeshAdapterRtId = new("670000000000000000000002");
    private static readonly OctoObjectId EdgeSiteRtId = new("6700000000000000000000e1");
    private static readonly OctoObjectId EdgeAdapterRtId = new("6700000000000000000000e2");

    private static readonly RtCkId<CkTypeId> LegacyPoolTypeId = new("System.Communication/Pool");

    /// <summary>
    ///     Every System.Communication 3.x at or above the 4.x entry point that main has released or
    ///     announced — the same list as migration-meta.yaml. 3.37.1 and 3.39.1 never reached the private
    ///     catalog but exist on local tenants; 3.41.0/3.42.0 are announced on unmerged main branches.
    /// </summary>
    public static TheoryData<string> MainLineVersions =>
    [
        "3.35.0", "3.36.0", "3.37.0", "3.37.1", "3.38.0", "3.39.0", "3.39.1", "3.40.0", "3.41.0", "3.42.0"
    ];

    [Theory]
    [MemberData(nameof(MainLineVersions))]
    public async Task TenantOnMainLine3x_ReachesCurrent4x_WithTheDataMigration(string installedVersion)
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = await CreateTenantOn3xAsync(installedVersion);
        var targetModelId = SystemCommunicationCkIds.CkModelId;

        // Precondition — the 3.x shape is really there.
        (await CountLegacyPoolsAsync(tenantId)).Should().Be(2);

        // 1. The path the engine finds from this version contains the data migration, not a bridge.
        var migrationService = fixture.GetService<ICkModelMigrationService>();
        var installedModelId = new CkModelId(ModelName, installedVersion);
        var path = await migrationService.FindMigrationPathAsync(installedModelId, targetModelId, ct);
        path.Should().NotBeNull($"a 4.x path must exist from {installedVersion}");
        path!.Steps.Should().ContainSingle(s => s.Script != null,
            "exactly one step runs the Pool -> DeploymentSite script");
        path.Steps.Single(s => s.Script != null).ToVersion.Should().Be("4.0.0");

        // 2. The post-validation is part of that path and really reads the tenant: a dry run changes
        //    nothing, so the two legacy Pool entities must make it fail.
        var dryRun = await migrationService.MigrateAsync(tenantId, installedModelId, targetModelId,
            new CkMigrationOptions { DryRun = true }, ct);
        dryRun.Success.Should().BeFalse();
        dryRun.Errors.Should().Contain(e =>
            e.Contains("no-legacy-pools") && e.Contains("2 entities of type 'System.Communication/Pool'"));

        // 3. The real upgrade, through the import a starting service runs.
        await ImportCurrentModelAsync(tenantId);

        // History: the tenant is on the target, i.e. the migration ran and succeeded.
        var upgradeService = fixture.GetService<ICkModelUpgradeService>();
        var installed = await upgradeService.GetInstalledVersionsAsync(tenantId, ct);
        installed.Should().ContainKey(ModelName).WhoseValue.Should().Be(targetModelId.Version.ToString());

        // Pool -> DeploymentSite, same rtIds, attribute values kept.
        (await CountLegacyPoolsAsync(tenantId)).Should().Be(0);
        var repository = fixture.GetService<ICommunicationRepository>();
        var sites = await repository.GetDeploymentSitesAsync(tenantId);
        sites.Select(s => s.RtId).Should().BeEquivalentTo([SeededSiteRtId, EdgeSiteRtId]);

        var seeded = sites.Single(s => s.RtId == SeededSiteRtId);
        seeded.Name.Should().Be("Default Cloud");
        seeded.Environment.Should().Be(RtEnvironmentEnum.Cloud);

        // WellKnownName: the seeded site is renamed, the operator-created one is left alone.
        seeded.RtWellKnownName.Should().Be("DeploymentSite");
        sites.Single(s => s.RtId == EdgeSiteRtId).RtWellKnownName.Should().BeNullOrEmpty();

        // Manages -> Hosts: each adapter resolves its site through the renamed role.
        var meshAdapterSite = await repository.GetDeploymentSiteForWorkloadAsync(tenantId, MeshAdapterRtId);
        meshAdapterSite.Should().NotBeNull();
        meshAdapterSite!.RtId.Should().Be(SeededSiteRtId);
        var edgeAdapterSite = await repository.GetDeploymentSiteForWorkloadAsync(tenantId, EdgeAdapterRtId);
        edgeAdapterSite.Should().NotBeNull();
        edgeAdapterSite!.RtId.Should().Be(EdgeSiteRtId);
        (await repository.GetWorkloadsForDeploymentSiteAsync(tenantId, SeededSiteRtId))
            .Select(w => w.RtId).Should().BeEquivalentTo([MeshAdapterRtId]);

        // 4. The same post-validation now passes on the migrated data.
        var afterDryRun = await migrationService.MigrateAsync(tenantId, installedModelId, targetModelId,
            new CkMigrationOptions { DryRun = true }, ct);
        afterDryRun.Success.Should().BeTrue(string.Join("; ", afterDryRun.Errors));
    }

    /// <summary>
    ///     The engine half of G3, end to end: a 3.x above the last entry point (the next main bump that
    ///     nobody added to the meta) must fail LOUDLY and leave the data where it is — not be lifted to 4.x
    ///     by the schema-only bridge, not now and not on the next start.
    /// </summary>
    [Fact]
    public async Task TenantAboveTheLastEntryPoint_IsRefused_AndStaysOn3x()
    {
        var ct = TestContext.Current.CancellationToken;
        const string unlistedVersion = "3.99.0";
        var tenantId = await CreateTenantOn3xAsync(unlistedVersion);
        var targetModelId = SystemCommunicationCkIds.CkModelId;

        var migrationService = fixture.GetService<ICkModelMigrationService>();
        (await migrationService.FindMigrationPathAsync(new CkModelId(ModelName, unlistedVersion), targetModelId, ct))
            .Should().BeNull();

        await ImportCurrentModelAsync(tenantId);

        var upgradeService = fixture.GetService<ICkModelUpgradeService>();
        (await upgradeService.GetInstalledVersionsAsync(tenantId, ct))
            .Should().ContainKey(ModelName).WhoseValue.Should().Be(unlistedVersion);
        (await CountLegacyPoolsAsync(tenantId)).Should().Be(2, "nothing was migrated");

        // A second start retries from the history — and must refuse again rather than record 4.x.
        await ImportCurrentModelAsync(tenantId);
        (await upgradeService.GetInstalledVersionsAsync(tenantId, ct))
            .Should().ContainKey(ModelName).WhoseValue.Should().Be(unlistedVersion);
        (await CountLegacyPoolsAsync(tenantId)).Should().Be(2);

        var result = await upgradeService.UpgradeModelsAsync(tenantId, [targetModelId.ToVersionRange()],
            cancellationToken: ct);
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should()
            .Contain("crosses a major version (3.x -> 4.x)").And.Contain($"add a migration entry for {unlistedVersion}");
    }

    private async Task<string> CreateTenantOn3xAsync(string version)
    {
        var tenantId = $"g3mig{version.Replace(".", "")}{Guid.NewGuid():N}"[..20];
        var systemContext = fixture.GetSystemContext();

        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        foreach (var dependency in new[] { SystemCkIds.CkModelId, SystemBotCkIds.CkModelId })
        {
            if (await tenantContext.IsCkModelExistingAsync(dependency))
            {
                continue;
            }

            var dependencyResult = new OperationResult();
            await tenantContext.ImportCkModelAsync(dependency, dependencyResult);
            dependencyResult.HasErrors.Should().BeFalse(dependencyResult.GetMessages());
        }

        // The published 3.40.0, re-labelled to the version under test.
        var json = (await ReadResourceAsync(ResourceSuffix))
            .Replace($"{ModelName}-{PublishedVersion}", $"{ModelName}-{version}", StringComparison.Ordinal);
        var deserializeResult = new OperationResult();
        var model = await fixture.GetService<ICkJsonSerializer>()
            .DeserializeCompiledModelRootAsync(json, $"ck-system.communication-{version}.json", deserializeResult);
        deserializeResult.HasErrors.Should().BeFalse(deserializeResult.GetMessages());
        model.ModelId.Should().Be(new CkModelId(ModelName, version));

        await tenantContext.ImportCkModelAsync(model);
        (await tenantContext.IsCkModelExistingAsync(model.ModelId)).Should().BeTrue();

        // Seed the 3.x shape the blueprint left on every main tenant.
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        await tenantRepository.GetCkTypeGraphAsync(LegacyPoolTypeId);
        await fixture.GetService<IImportRtModelCommand>()
            .ImportTextAsync(tenantRepository, SeedOf3xTenant, ImportStrategy.Insert);

        return tenantId;
    }

    private async Task ImportCurrentModelAsync(string tenantId)
    {
        var tenantContext = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
        var importResult = new OperationResult();
        await tenantContext.ImportCkModelAsync(SystemCommunicationCkIds.CkModelId, importResult);
        importResult.HasErrors.Should().BeFalse(importResult.GetMessages());
    }

    private async Task<int> CountLegacyPoolsAsync(string tenantId)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(tenantId);
        using var session = await tenantRepository.GetSessionAsync();
        var (entities, _) = await tenantRepository.GetRtEntitiesByTypeForMigrationAsync(session, LegacyPoolTypeId);
        return entities.Count;
    }

    private static async Task<string> ReadResourceAsync(string suffix)
    {
        var assembly = typeof(SystemCommunication4MigrationTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    ///     The blueprint's seeded Cloud site + Mesh Adapter (System.Communication.Release seed on main),
    ///     and an Edge site an operator created by hand (no well-known name) with an adapter of its own.
    /// </summary>
    private static string SeedOf3xTenant => $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - System.Communication-[3.0,4.0)
        entities:
          - rtId: '{{SeededSiteRtId}}'
            ckTypeId: System.Communication/Pool
            rtWellKnownName: CommunicationPool
            attributes:
              - id: System/Name
                value: Default Cloud
              - id: System.Communication/Environment
                value: 1
              - id: System.Communication/DeploymentState
                value: 0
              - id: System.Communication/CommunicationState
                value: 0
              - id: System.Communication/ConfigurationState
                value: 0
          - rtId: '{{EdgeSiteRtId}}'
            ckTypeId: System.Communication/Pool
            attributes:
              - id: System/Name
                value: k8sfirmianstrasse
              - id: System.Communication/Environment
                value: 0
              - id: System.Communication/DeploymentState
                value: 1
              - id: System.Communication/CommunicationState
                value: 0
              - id: System.Communication/ConfigurationState
                value: 0
          - rtId: '{{MeshAdapterRtId}}'
            ckTypeId: System.Communication/Adapter
            rtWellKnownName: MeshAdapter
            associations:
              - roleId: System.Communication/Manages
                targetRtId: '{{SeededSiteRtId}}'
                targetCkTypeId: System.Communication/Pool
            attributes:
              - id: System/Name
                value: Mesh Adapter
              - id: System.Communication/ChartName
                value: octo-mesh-adapter
              - id: System.Communication/DeploymentState
                value: 0
              - id: System.Communication/CommunicationState
                value: 0
              - id: System.Communication/ConfigurationState
                value: 0
              - id: System.Communication/LastSyncedSequenceNumber
                value: 0
              - id: System.Communication/ReceivesClusterSecrets
                value: true
          - rtId: '{{EdgeAdapterRtId}}'
            ckTypeId: System.Communication/Adapter
            associations:
              - roleId: System.Communication/Manages
                targetRtId: '{{EdgeSiteRtId}}'
                targetCkTypeId: System.Communication/Pool
            attributes:
              - id: System/Name
                value: Edge Adapter
              - id: System.Communication/ChartName
                value: octo-mesh-adapter
              - id: System.Communication/DeploymentState
                value: 1
              - id: System.Communication/CommunicationState
                value: 0
              - id: System.Communication/ConfigurationState
                value: 0
              - id: System.Communication/LastSyncedSequenceNumber
                value: 0
              - id: System.Communication/ReceivesClusterSecrets
                value: false
        """;
}
