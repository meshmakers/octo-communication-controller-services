using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.CkModels;

/// <summary>
///     AB#8594 (Feature AB#5697 F4.3): System.Communication 4.16.0 is the first ckLanguage 2 release of the model and a
///     MINOR against the published 4.15.0. The tests read the compiled model the service embeds (the one it imports into
///     every tenant) and guard what the Minor depends on: explicit <c>derivable: Any</c>, the System interfaces whose
///     members the types already carried, and the single access tightening (row T7a).
/// </summary>
internal class SystemCommunicationCkLanguage2Tests
{
    private static async Task<CkCompiledModelRoot> LoadEmbeddedSystemCommunicationAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.AddCkModelSystemCommunicationV4();
        await using var provider = services.BuildServiceProvider();

        var embedded = provider.GetServices<ICkEmbeddedModel>()
            .Single(m => m.ModelId.Name == "System.Communication");
        var operationResult = new OperationResult();
        var root = await embedded.GetCompiledModelRootAsync(operationResult);
        await Assert.That(operationResult.HasErrors).IsFalse();
        return root;
    }

    [Test]
    public async Task EmbeddedModel_IsSystemCommunication_4_17_0_OnCkLanguage2_WithTheCkV2MinEngineVersion()
    {
        var root = await LoadEmbeddedSystemCommunicationAsync();

        // AB#8595: 4.17.0 adds the Adapter / DeployableWorkload / AdapterPool methods (Minor, no sensitive marker, so the
        // minimum engine version stays).
        await Assert.That(root.ModelId.ToString()).IsEqualTo("System.Communication-4.17.0");
        await Assert.That(root.CkLanguage).IsEqualTo(2);
        // The compiler stamps minEngineVersion for a ckLanguage 2 model; the sources never set it. Pinned exactly: a
        // higher value means every service below it can no longer import System.Communication. A feature that raises it
        // (e.g. a method with execution.mode) is a deliberate rollout decision and updates this assertion.
        await Assert.That(root.MinEngineVersion).IsNotNull();
        await Assert.That(Version.Parse(root.MinEngineVersion!)).IsEqualTo(new Version(3, 5, 1));
    }

    /// <summary>
    ///     ckLanguage 2 changes the default of <c>derivable</c> from Any to Model (row T6 of ck-semver-rules.md). Without
    ///     the explicit <c>derivable: Any</c> every pre-existing type and record would be a MAJOR change (OCTO-CK200), and
    ///     the adapter, AI and energy models that derive from them would stop compiling (message 113).
    /// </summary>
    /// <summary>The result records of the System.Communication methods (AB#8595, 4.17.0).</summary>
    private static readonly string[] MethodResultRecords =
    [
        "ServiceAccountReconcileResult", "ServiceAccountSecretRotationResult", "AdapterPoolMirrorSyncResult",
        "AdapterPoolLeaseResult"
    ];

    [Test]
    public async Task EveryTypeAndRecord_StaysDerivableEverywhere()
    {
        var root = await LoadEmbeddedSystemCommunicationAsync();
        var types = root.Types ?? [];
        var records = root.Records ?? [];

        await Assert.That(types.Count).IsEqualTo(32);
        // AB#8595 (4.17.0) adds four method result records; they are answers, not extension points, and keep the
        // ckLanguage 2 default derivable: Model. Every record that existed before 4.17.0 stays derivable everywhere.
        await Assert.That(records.Count).IsEqualTo(8 + MethodResultRecords.Length);
        var newRecords = records.Where(r => MethodResultRecords.Contains(r.RecordId.Name)).ToList();
        await Assert.That(newRecords.Count).IsEqualTo(MethodResultRecords.Length);
        await Assert.That(newRecords.All(r => r.Derivable is null or CkDerivableDto.Model)).IsTrue();
        var notDerivable = types.Where(t => t.Derivable != CkDerivableDto.Any).Select(t => t.TypeId.Name)
            .Concat(records.Where(r => !MethodResultRecords.Contains(r.RecordId.Name) && r.Derivable != CkDerivableDto.Any)
                .Select(r => r.RecordId.Name))
            .ToList();
        await Assert.That(notDerivable).IsEmpty();
    }

    /// <summary>
    ///     Types implement a System interface only where they already carried its members with the required multiplicity
    ///     (row T2, Minor). Left out on purpose: Pipeline (Enabled optional), DeployableEntity/EnergyCommunityConfiguration/
    ///     DataPointMapping for Named-1 (Name optional), SecretDefault (platform-only entity, kept out of cross-type
    ///     interface queries) and Hierarchical-1 (DataFlow has no ParentChild role).
    /// </summary>
    [Test]
    public async Task TypesImplement_TheSystemInterfacesTheyAlreadySatisfied()
    {
        var root = await LoadEmbeddedSystemCommunicationAsync();

        var implemented = (root.Types ?? [])
            .Where(t => t.Implements is { Count: > 0 })
            .ToDictionary(t => t.TypeId.Name,
                t => t.Implements!.Select(i => $"{i.ModelId.Name}/{i.ElementId.Name}").OrderBy(n => n, StringComparer.Ordinal).ToList());

        var expected = new Dictionary<string, List<string>>
        {
            ["DataFlow"] = ["System/Describable", "System/Named"],
            ["LentAdapterPool"] = ["System/Describable", "System/Named"],
            ["DeployableEntity"] = ["System/Describable"],
            ["PipelineTrigger"] = ["System/Enableable"],
            ["DataPointMapping"] = ["System/Enableable"]
        };

        await Assert.That(implemented.Keys.OrderBy(k => k, StringComparer.Ordinal))
            .IsEquivalentTo(expected.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (typeName, interfaces) in expected)
        {
            await Assert.That(implemented[typeName]).IsEquivalentTo(interfaces).Because(typeName);
        }
    }

    /// <summary>
    ///     The only tightened attribute is <c>SecretDefault.Value</c> (Hidden, row T7a, acknowledged in ckModel.yaml): the
    ///     controller writes it through its repository only. The configuration credentials stay ReadWrite because Refinery
    ///     Studio writes them through the generic GraphQL update, and service-written state stays ReadWrite because a
    ///     ReadOnly on an attribute that is not securitySensitive is a MAJOR (row T7). Any further tightening is a
    ///     deliberate, versioned decision that must update this test.
    /// </summary>
    [Test]
    public async Task OnlySecretDefaultValue_IsTightened_AndItsDefinitionIsSecuritySensitive()
    {
        var root = await LoadEmbeddedSystemCommunicationAsync();

        var tightened = (root.Types ?? [])
            .SelectMany(t => (t.Attributes ?? []).Select(a => (Type: t.TypeId.Name, Attribute: a)))
            .Concat((root.Records ?? []).SelectMany(r =>
                (r.Attributes ?? []).Select(a => (Type: r.RecordId.Name, Attribute: a))))
            .Where(x => x.Attribute.Access is not null && x.Attribute.Access != CkAttributeAccessDto.ReadWrite)
            .Select(x => $"{x.Type}.{x.Attribute.AttributeName}:{x.Attribute.Access}")
            .ToList();
        await Assert.That(tightened).IsEquivalentTo(new[] { "SecretDefault.Value:Hidden" });

        var sensitive = (root.Attributes ?? []).Where(a => a.SecuritySensitive == true)
            .Select(a => a.AttributeId.Name).ToList();
        await Assert.That(sensitive).IsEquivalentTo(new[] { "SecretDefaultValue" });
    }

    /// <summary>
    ///     The access tightening of SecretDefault.Value stays acknowledged in 4.17.0 (AB#8595): 4.16.0 was never published
    ///     (publishSystemCkModels), so the published baseline is still 4.15.0 and the tightening is part of this release's
    ///     diff. The entry goes with the first bump after a version carrying it has been published (OCTO-CK204).
    /// </summary>
    [Test]
    public async Task TheAccessTightening_IsAcknowledged()
    {
        var root = await LoadEmbeddedSystemCommunicationAsync();

        var acknowledged = root.Compatibility?.Acknowledge?.Select(a => a.Change).ToList() ?? [];
        await Assert.That(acknowledged)
            .IsEquivalentTo(new[] { "TypeAttribute:SecretDefault-1/Value#Modified:access" });
    }
}
