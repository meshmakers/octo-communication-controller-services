using System.Runtime.CompilerServices;
using System.Text.Json;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelCatalogs;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Methods;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.Service;

/// <summary>
///     AB#8595 — the contract of the System.Communication methods the controller handles: what the CK model declares for
///     each of them (roles, timeout, idempotency, no sensitive marker) and how the host is wired. The calls themselves run
///     through the real host in the integration tests (<c>CommunicationMethodHostTests</c> there).
/// </summary>
internal class CommunicationMethodHostTests
{
    /// <summary>Method id → (timeout in seconds, idempotent, instance method). The PR's REST → method table.</summary>
    private static readonly Dictionary<string, (int Timeout, bool Idempotent, bool Instance)> Expected = new()
    {
        [SystemCommunicationCkIds.AdapterDeployUpdateMethodId] = (90, true, true),
        [SystemCommunicationCkIds.DeployableWorkloadWakeMethodId] = (90, true, true),
        [SystemCommunicationCkIds.AdapterReconcileServiceAccountMethodId] = (60, true, true),
        [SystemCommunicationCkIds.AdapterRotateServiceAccountSecretMethodId] = (60, false, true),
        [SystemCommunicationCkIds.AdapterPoolRefreshMirrorsMethodId] = (120, true, false),
        [SystemCommunicationCkIds.AdapterPoolPublishMirrorsMethodId] = (120, true, false),
        [SystemCommunicationCkIds.AdapterPoolGrantLeaseMethodId] = (30, false, true),
        [SystemCommunicationCkIds.AdapterPoolCancelQueuedExecutionMethodId] = (15, false, true)
    };

    private static async Task<Dictionary<string, CkMethodDto>> LoadDeclaredMethodsAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRuntimeEngine();
        services.AddCkModelSystemCommunicationV4();
        await using var provider = services.BuildServiceProvider();
        var embedded = provider.GetServices<ICkEmbeddedModel>().Single(m => m.ModelId.Name == "System.Communication");
        var root = await embedded.GetCompiledModelRootAsync(new OperationResult());
        return (root.Types ?? [])
            .SelectMany(t => (t.Methods ?? []).Select(m => (Id: $"System.Communication/{t.TypeId.Name}.{m.MethodId}", m)))
            .ToDictionary(x => x.Id, x => x.m);
    }

    [Test]
    public async Task EveryDeclaredMethod_HasOneHandlerRow_AndEveryRowADeclaredMethod()
    {
        var declared = await LoadDeclaredMethodsAsync();
        var rows = CommunicationMethodHost.Handlers.Select(h => h.MethodId).ToList();

        await Assert.That(rows).IsEquivalentTo(declared.Keys);
        await Assert.That(rows.Distinct().Count()).IsEqualTo(rows.Count);
        await Assert.That(rows).IsEquivalentTo(Expected.Keys);
        await Assert.That(CommunicationMethodHost.Handlers.All(h => !string.IsNullOrWhiteSpace(h.RestEndpoint))).IsTrue();
    }

    /// <summary>
    ///     The REST policy asks for the scope <c>octo_api</c> only; a method without roles would admit administrators only
    ///     (default-deny of the evaluator), so every method declares <c>CommunicationManagement</c>. Timeout and
    ///     idempotency are explicit, nothing is sensitive (the rotation result carries no secret), the handler is the service.
    /// </summary>
    [Test]
    public async Task EveryMethod_DeclaresTheRole_TheTimeout_TheIdempotency_AndNothingSensitive()
    {
        var declared = await LoadDeclaredMethodsAsync();

        using var _ = Assert.Multiple();
        foreach (var (methodId, (timeout, idempotent, instance)) in Expected)
        {
            var method = declared[methodId];
            await Assert.That(method.Authorization?.Roles).IsEquivalentTo(new[] { "CommunicationManagement" });
            await Assert.That(method.Authorization!.AllowSelf).IsFalse();
            await Assert.That(method.Execution?.TimeoutSeconds).IsEqualTo(timeout);
            await Assert.That(method.Execution!.Idempotent).IsEqualTo(idempotent);
            await Assert.That(method.Execution.Handler is null or CkMethodHandlers.Service).IsTrue();
            await Assert.That(method.Execution.Mode).IsNull();
            await Assert.That(method.Kind).IsEqualTo(instance ? CkMethodKindDto.Instance : CkMethodKindDto.Static);
            await Assert.That((method.Parameters ?? []).Any(p => p.Sensitive)).IsFalse();
            await Assert.That(method.Result?.Sensitive ?? false).IsFalse();
        }
    }

    [Test]
    public async Task Program_RegistersTheHostAndItsDiscovery()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        await Assert.That(program).Contains("AddCommunicationCkMethodHost(builder.Configuration)");
        await Assert.That(program).Contains("c.AddCommunicationMethodDiscovery()");
    }

    [Test]
    public async Task Enabled_IsTheDefault_AndRegistersEveryHandler()
    {
        var services = new ServiceCollection();

        var hosted = services.AddCommunicationCkMethodHost(new ConfigurationBuilder().Build());

        await Assert.That(hosted).IsTrue();
        await Assert.That(new CommunicationMethodsOptions().Enabled).IsTrue();
        foreach (var handler in CommunicationMethodHost.Handlers)
        {
            await Assert.That(services.Any(d => d.ServiceType == handler.HandlerType)).IsTrue();
        }

        await Assert.That(services.Any(d => d.ServiceType.FullName ==
                                            "Meshmakers.Octo.Services.Methods.Authorization.ICkMethodCallerTokenValidator"))
            .IsTrue();
    }

    [Test]
    public async Task Disabled_RegistersNoHandler()
    {
        var services = new ServiceCollection();

        var hosted = services.AddCommunicationCkMethodHost(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Methods:Communication:Enabled"] = "false" })
            .Build());

        await Assert.That(hosted).IsFalse();
        foreach (var handler in CommunicationMethodHost.Handlers)
        {
            await Assert.That(services.Any(d => d.ServiceType == handler.HandlerType)).IsFalse();
        }
    }

    [Test]
    public async Task TheCallerTokenValidation_IsRegisteredOnce_ForBothHostsOfTheController()
    {
        var services = new ServiceCollection();

        services.AddPipelineMethodHandlerTokenAndCommunicationHost();

        await Assert.That(services.Count(d => d.ServiceType.FullName ==
                                              "Meshmakers.Octo.Services.Methods.Authorization.ICkMethodCallerTokenValidator"))
            .IsEqualTo(1);
    }

    /// <summary>
    ///     The wire form of a record result is the bare camelCase JSON of the generated record (AB#8079); the gateway maps it
    ///     onto the CK record by attribute name. It carries the members of the REST answer.
    /// </summary>
    [Test]
    public async Task ARecordResult_GoesOnTheWireWithTheMembersOfTheRestAnswer()
    {
        var value = new RtAdapterPoolLeaseResultRecord
        {
            Granted = true, LeaseId = "lease-1", MemberId = "member-1", StatusMessage = null
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, CkMethodJson.Options));

        await Assert.That(json.RootElement.GetProperty("granted").GetBoolean()).IsTrue();
        await Assert.That(json.RootElement.GetProperty("leaseId").GetString()).IsEqualTo("lease-1");
        await Assert.That(json.RootElement.GetProperty("memberId").GetString()).IsEqualTo("member-1");
    }

    private static string RepositoryRoot([CallerFilePath] string path = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Octo.CommunicationController.sln")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}

internal static class CommunicationMethodHostTestExtensions
{
    /// <summary>The order of Program.cs: the pipeline method handler first, then the service-handled methods.</summary>
    public static IServiceCollection AddPipelineMethodHandlerTokenAndCommunicationHost(this IServiceCollection services)
    {
        var configuration = new ConfigurationBuilder().Build();
        services.AddPipelineMethodHandlerForTest(configuration);
        services.AddCommunicationCkMethodHost(configuration);
        return services;
    }

    private static void AddPipelineMethodHandlerForTest(this IServiceCollection services, IConfiguration configuration) =>
        CommunicationControllerServices.Methods.PipelineMethodServiceCollectionExtensions.AddPipelineMethodHandler(
            services, configuration);
}
