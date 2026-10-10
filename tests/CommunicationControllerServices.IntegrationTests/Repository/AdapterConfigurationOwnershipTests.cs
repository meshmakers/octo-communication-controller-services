using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#6312 (incident AB#6310) — the connection settings a tenant typed into an Adapter's
///     <c>configuration</c> (for the EDA adapter the Ponton host, user and password) survive a blueprint
///     update, against real MongoDB and the real System.Communication model of this repo.
/// </summary>
/// <remarks>
///     <para>
///         The incident: a blueprint seed carries the EMPTY skeleton of the adapter configuration. A blueprint
///         update imports its seed with <c>ImportStrategy.Upsert</c>, which is a full ReplaceOne in the
///         MongoDB layer, so the seed skeleton replaced what the tenant had entered — or cleared it outright
///         when the seed omitted the attribute. The engine keeps an attribute only when its ownership is not
///         seed-owned (<c>AttributeOwnership.IsPreservedOnUpsert</c>); <c>AdapterConfiguration</c> was.
///     </para>
///     <para>
///         Layer: the blueprint apply of the asset repository imports seed entities through
///         <c>IImportRtModelCommand</c> with <c>Upsert</c> (the same command as a plain <c>ImportRt -r</c>), and
///         that command is where the ownership is honoured. This test drives that command with the seed shapes
///         of the EdaIntegration blueprint. What it does NOT cover is the blueprint orchestration above it
///         (Update / Merge mode selection, the preview) - that belongs to the engine and asset-repository
///         stories of the same Feature and to the verification on test-2 (AB#6313, AB#6319).
///     </para>
///     <para>
///         The values below are made up; no real credential appears in the repository.
///     </para>
/// </remarks>
[Collection("CommunicationController")]
public class AdapterConfigurationOwnershipTests(CommunicationControllerFixture fixture)
{
    /// <summary>The empty skeleton the EdaIntegration seed ships (host, user and password empty).</summary>
    private const string SeedSkeleton =
        """{"EdaHttpAdapter":{"Host":"","User":"","Password":"","IsSimulationMode":true}}""";

    /// <summary>What the tenant entered after the install.</summary>
    private const string TenantEntered =
        """{"EdaHttpAdapter":{"Host":"http://ponton.example.invalid:8438","User":"test-user","Password":"test-not-a-real-secret","IsSimulationMode":false}}""";

    [Fact]
    public async Task AdapterConfiguration_IsSecretOwned_InTheCompiledModel()
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        var adapterType = await tenantRepository.GetCkTypeGraphAsync(SystemCommunicationCkIds.RtCkAdapterTypeId);

        var configuration = adapterType.AllAttributes.Values
            .Single(a => a.AttributeName.Equals(nameof(RtAdapter.Configuration), StringComparison.OrdinalIgnoreCase));

        configuration.Ownership.Should().Be(AttributeOwnershipDto.Secret);
        configuration.Ownership.IsPreservedOnUpsert().Should().BeTrue();
        configuration.Ownership.IsExcludedFromExport().Should().BeTrue("the JSON holds a password");
    }

    [Fact]
    public async Task Upsert_SeedWithEmptyConfiguration_KeepsTheTenantsConfiguration()
    {
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter", configuration: TenantEntered),
            ImportStrategy.Insert);

        // The blueprint update: same rtId, the seed's empty skeleton, and a changed seed-owned value.
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter-v2", configuration: SeedSkeleton),
            ImportStrategy.Upsert);

        var adapter = await LoadAsync(rtId);
        adapter.Configuration.Should().Be(TenantEntered);

        // Control: the import really replaced the entity - a seed-owned attribute moved with the seed.
        adapter.ChartName.Should().Be("octo-mesh-adapter-v2");
    }

    [Fact]
    public async Task Upsert_SeedOmittingConfiguration_KeepsTheTenantsConfiguration()
    {
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter", configuration: TenantEntered),
            ImportStrategy.Insert);

        // Omission is the other seed shape: a ReplaceOne would clear the attribute.
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter-v2", configuration: null),
            ImportStrategy.Upsert);

        var adapter = await LoadAsync(rtId);
        adapter.Configuration.Should().Be(TenantEntered);
        adapter.ChartName.Should().Be("octo-mesh-adapter-v2");
    }

    [Fact]
    public async Task Upsert_OnAnAdapterThatDoesNotExistYet_AppliesTheSeedConfiguration()
    {
        var rtId = OctoObjectId.GenerateNewId();

        // A fresh tenant: the seed still initialises the value.
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter", configuration: SeedSkeleton),
            ImportStrategy.Upsert);

        (await LoadAsync(rtId)).Configuration.Should().Be(SeedSkeleton);
    }

    [Fact]
    public async Task Upsert_OnAnAdapterWithoutAConfigurationYet_AppliesTheSeedConfiguration()
    {
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter", configuration: null),
            ImportStrategy.Insert);

        // Nothing stored to preserve: the seed value lands.
        await ImportAsync(AdapterSeed(rtId, "Mesh Adapter", "octo-mesh-adapter", configuration: SeedSkeleton),
            ImportStrategy.Upsert);

        (await LoadAsync(rtId)).Configuration.Should().Be(SeedSkeleton);
    }

    private async Task ImportAsync(string yaml, ImportStrategy strategy)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        await fixture.GetService<IImportRtModelCommand>().ImportTextAsync(tenantRepository, yaml, strategy);
    }

    private Task<RtAdapter> LoadAsync(OctoObjectId rtId) =>
        fixture.GetService<ICommunicationRepository>().GetAdapterAsync(fixture.TestTenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, rtId));

    /// <summary>
    ///     The Adapter of an EdaIntegration-shaped seed. <paramref name="configuration" /> null omits the
    ///     attribute entirely.
    /// </summary>
    private static string AdapterSeed(OctoObjectId rtId, string name, string chartName, string? configuration)
    {
        var configurationYaml = configuration == null
            ? string.Empty
            : $$"""

                  - id: System.Communication/AdapterConfiguration
                    value: '{{configuration}}'
            """;

        return $$"""
            $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
            dependencies:
              - System.Communication-[4.5,5.0)
            entities:
              - rtId: '{{rtId}}'
                ckTypeId: System.Communication/Adapter
                attributes:
                  - id: System/Name
                    value: {{name}}
                  - id: System.Communication/ChartName
                    value: {{chartName}}
                  - id: System.Communication/DeploymentState
                    value: 0
                  - id: System.Communication/CommunicationState
                    value: 0
                  - id: System.Communication/ConfigurationState
                    value: 0
                  - id: System.Communication/LastSyncedSequenceNumber
                    value: 0{{configurationYaml}}
            """;
    }
}
