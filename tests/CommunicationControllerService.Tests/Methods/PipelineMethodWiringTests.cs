using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     AB#6879 — the pieces of the pipeline method handler that fail silently when a registration is dropped: the
///     controller would simply never announce a method.
/// </summary>
internal class PipelineMethodWiringTests
{
    [Test]
    public async Task Program_RegistersTheHandlerAndTheDiscoveryConsumer()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        await Assert.That(program).Contains("AddPipelineMethodHandler(builder.Configuration)");
        await Assert.That(program).Contains("c.AddPipelineMethodDiscovery()");
    }

    [Test]
    public async Task TheRegistrationAddsTheStartupStepAfterTheTenantsStartedAndTheHeartbeat()
    {
        var services = new ServiceCollection();

        services.AddPipelineMethodHandler(new ConfigurationBuilder().Build());

        var initializer = services.Single(d => d.ImplementationType == typeof(PipelineMethodHostInitializer));
        await Assert.That(initializer.ServiceType).IsEqualTo(typeof(IAsyncInitializationService));
        // TenantStartupInitializationService runs at 30: the CK caches must be loaded before the first scan
        var instance = new PipelineMethodHostInitializer(null!, Options.Create(new PipelineMethodOptions()),
            new CapturingLogger<PipelineMethodHostInitializer>());
        await Assert.That(instance.Order).IsGreaterThan(30);
        await Assert.That(services.Any(d => d.ImplementationType == typeof(PipelineMethodHeartbeatService))).IsTrue();
    }

    [Test]
    public async Task TheSettingsAreOperatorSettableAndSafeByDefault()
    {
        var defaults = new PipelineMethodOptions();
        var configured = new ServiceCollection()
            .AddPipelineMethodHandler(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["PipelineMethods:Enabled"] = "false",
                    ["PipelineMethods:FailStartupOnInvalidMethod"] = "true",
                    ["PipelineMethods:HeartbeatInterval"] = "00:00:05"
                }).Build())
            .BuildServiceProvider()
            .GetRequiredService<IOptions<PipelineMethodOptions>>().Value;

        // a misconfigured tenant must not take the controller down unless asked to
        await Assert.That(defaults.FailStartupOnInvalidMethod).IsFalse();
        await Assert.That(defaults.Enabled).IsTrue();
        await Assert.That(defaults.HeartbeatInterval).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(defaults.Ttl).IsEqualTo(TimeSpan.FromSeconds(45));
        await Assert.That(configured.Enabled).IsFalse();
        await Assert.That(configured.FailStartupOnInvalidMethod).IsTrue();
        await Assert.That(configured.HeartbeatInterval).IsEqualTo(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task TheCallerTokenValidatorUsesTheAuthorityOfTheHttpApi_UnlessMethodsTokenSaysOtherwise()
    {
        static ServiceProvider Build(Dictionary<string, string?> settings) => new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>())
            .Configure<Meshmakers.Octo.Backend.CommunicationControllerServices.Options.CommunicationControllerOptions>(
                o => o.AuthorityUrl = "https://identity.example/")
            .AddPipelineMethodHandler(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .BuildServiceProvider();

        using var byDefault = Build([]);
        using var overridden = Build(new Dictionary<string, string?>
        {
            ["Methods:Token:Authority"] = "https://other-identity.example/"
        });

        await Assert.That(byDefault.GetRequiredService<IOptions<Meshmakers.Octo.Services.Methods.Authorization.CkMethodTokenOptions>>()
            .Value.Authority).IsEqualTo("https://identity.example/");
        await Assert.That(overridden.GetRequiredService<IOptions<Meshmakers.Octo.Services.Methods.Authorization.CkMethodTokenOptions>>()
            .Value.Authority).IsEqualTo("https://other-identity.example/");
        await Assert.That(byDefault.GetService<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodCallerTokenValidator>())
            .IsNotNull();
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
