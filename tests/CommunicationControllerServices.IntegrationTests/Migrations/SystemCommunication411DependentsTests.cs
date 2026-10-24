using System.Text.RegularExpressions;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Migrations;

/// <summary>
///     AB#7446 — System.Communication 4.11.0 (<c>DataPoint.ExternalId</c> is seed-owned again, a MINOR) must not
///     push the models that depend on it into <c>ResolveFailed</c>. The two published dependents are
///     range-retaining (<c>Loxone-5.4.0</c>: <c>System.Communication-[4.5,5.0)</c>, <c>System.Ai-4.6.0</c>:
///     <c>System.Communication-[4.0,5.0)</c>), so a tenant is judged by the range, not by the closure pin
///     (<c>System.Communication-4.9.0</c>) the compiler stamped into them.
/// </summary>
/// <remarks>
///     The starting state is the published world: System.Communication 4.10.0 (the latest in the catalog),
///     Loxone 5.4.0, System.Ai 4.6.0 and Basic 2.5.0, byte for byte from the public CK catalog (embedded resources;
///     only the System pin is set to the System this build embeds). Then the build's own 4.11.0 is imported
///     through the same <c>ImportCkModelAsync</c> a starting service uses.
/// </remarks>
[Collection("CommunicationController")]
public class SystemCommunication411DependentsTests(CommunicationControllerFixture fixture)
{
    private const int Available = 1;

    [Fact]
    public async Task ImportingTheCurrentModel_KeepsLoxoneAndSystemAiAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantId = $"c411{Guid.NewGuid():N}"[..20];
        var systemContext = fixture.GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        var tenant = await systemContext.FindTenantContextAsync(tenantId);
        await ImportEmbeddedAsync(tenant, SystemCkIds.CkModelId);
        await ImportEmbeddedAsync(tenant, SystemBotCkIds.CkModelId);
        await ImportPublishedAsync(tenant, "ck-basic-2.5.0.json", "Basic-2.5.0");
        await ImportPublishedAsync(tenant, "ck-system.communication-4.10.0.json", "System.Communication-4.10.0");
        await ImportPublishedAsync(tenant, "ck-loxone-5.4.0.json", "Loxone-5.4.0");
        await ImportPublishedAsync(tenant, "ck-system.ai-4.6.0.json", "System.Ai-4.6.0");

        (await StatesAsync(tenantId)).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Basic-2.5.0"] = Available,
            ["System.Communication-4.10.0"] = Available,
            ["Loxone-5.4.0"] = Available,
            ["System.Ai-4.6.0"] = Available
        }, o => o.Using<int>(c => c.Subject.Should().Be(c.Expectation)).WhenTypeIs<int>(),
            "precondition: the published world resolves");

        // The upgrade a starting comm-controller performs.
        await ImportEmbeddedAsync(tenant, SystemCommunicationCkIds.CkModelId);

        var states = await StatesAsync(tenantId);
        var communication = SystemCommunicationCkIds.CkModelId.FullName;
        states.Should().ContainKey(communication).WhoseValue.Should().Be(Available);
        states.Should().NotContainKey("System.Communication-4.10.0", "the minor replaced the previous version");
        states["Loxone-5.4.0"].Should().Be(Available, "Loxone declares System.Communication-[4.5,5.0)");
        states["System.Ai-4.6.0"].Should().Be(Available, "System.Ai declares System.Communication-[4.0,5.0)");
        states["Basic-2.5.0"].Should().Be(Available);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    ///     Negative control, and the reason the check above is worth anything: the same Loxone WITHOUT its persisted
    ///     ranges (what a classic, exact-pin build of it looks like) is judged by the closure pin
    ///     <c>System.Communication-4.9.0</c> and does not survive the minor. This is the failure mode the project
    ///     memory describes (AB#4696) - it is real for a classic dependent, and absent for the range-retaining ones
    ///     that are published today.
    /// </summary>
    [Fact]
    public async Task AClassicExactPinDependent_IsNotProtected_ByTheMinor()
    {
        var tenantId = $"c411n{Guid.NewGuid():N}"[..20];
        var systemContext = fixture.GetSystemContext();
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        var tenant = await systemContext.FindTenantContextAsync(tenantId);
        await ImportEmbeddedAsync(tenant, SystemCkIds.CkModelId);
        await ImportEmbeddedAsync(tenant, SystemBotCkIds.CkModelId);
        await ImportPublishedAsync(tenant, "ck-basic-2.5.0.json", "Basic-2.5.0");
        await ImportPublishedAsync(tenant, "ck-system.communication-4.10.0.json", "System.Communication-4.10.0");
        // The closure pin of the published Loxone is 4.9.0; widen it to the installed 4.10.0 so the classic copy resolves
        // before the upgrade, and drop the ranges so only that exact pin is left.
        await ImportPublishedAsync(tenant, "ck-loxone-5.4.0.json", "Loxone-5.4.0", json =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node.Remove("dependencyRanges");
            return node.ToJsonString().Replace("System.Communication-4.9.0", "System.Communication-4.10.0");
        });
        (await StatesAsync(tenantId))["Loxone-5.4.0"].Should().Be(Available, "precondition: resolves before the minor");

        await ImportEmbeddedAsync(tenant, SystemCommunicationCkIds.CkModelId);

        (await StatesAsync(tenantId))["Loxone-5.4.0"].Should().Be(2, "ResolveFailed: an exact pin does not follow a minor");
    }

    private async Task<Dictionary<string, int>> StatesAsync(string tenantId)
    {
        var tenant = await fixture.GetSystemContext().FindTenantContextAsync(tenantId);
        var repository = tenant.GetTenantRepository();
        using var session = await repository.GetSessionAsync();
        var models = await repository.GetCkModelsAsync(session, null, RtEntityQueryOptions.Create());
        return models.Items.Where(m => m.Id.Name is "Basic" or "System.Communication" or "Loxone" or "System.Ai")
            .ToDictionary(m => m.Id.FullName, m => (int)m.ModelState);
    }

    private static async Task ImportEmbeddedAsync(ITenantContext tenant, CkModelId id)
    {
        var result = new OperationResult();
        await tenant.ImportCkModelAsync(id, result);
        result.HasErrors.Should().BeFalse(result.GetMessages());
    }

    private async Task ImportPublishedAsync(ITenantContext tenant, string resource, string expectedId,
        Func<string, string>? transform = null)
    {
        var json = Regex.Replace(await ReadResourceAsync(resource), @"System-2\.\d+\.\d+", SystemCkIds.CkModelId.ToString());
        json = transform?.Invoke(json) ?? json;
        var result = new OperationResult();
        var model = await fixture.GetService<ICkJsonSerializer>()
            .DeserializeCompiledModelRootAsync(json, resource, result);
        result.HasErrors.Should().BeFalse(result.GetMessages());
        model.ModelId.FullName.Should().Be(expectedId);
        await tenant.ImportCkModelAsync(model);
    }

    private static async Task<string> ReadResourceAsync(string suffix)
    {
        var assembly = typeof(SystemCommunication411DependentsTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
