using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#4924 increment 6 — pins the wiring of <c>/adapterPoolHub</c>.
/// </summary>
/// <remarks>
///     Three things here fail silently if they are dropped: without the <c>MapHub</c> the members
///     cannot connect at all (which at least shows up quickly), without the filter registration the
///     hub is unguarded with nothing turning red, and without the section binding the mode can only
///     be changed by a release — which defeats the point of staging it. Pinned at the source for the
///     same reason <see cref="AdapterHubAuthorizationWiringTests" /> is: the composed
///     <c>AddSignalR()</c> chain lives in the top-level statements of <c>Program.cs</c> and cannot be
///     resolved from a unit test.
/// </remarks>
internal class AdapterPoolHubWiringTests
{
    [Test]
    public async Task Program_MountsTheHubAndRegistersItsFilter()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        using var _ = Assert.Multiple();
        await Assert.That(program)
            .Contains("AddHubOptions<AdapterPoolHub>(o => o.AddFilter<AdapterPoolHubAuthorizationFilter>())");
        await Assert.That(program).Contains("AdapterPoolHubAuthorizationOptions.SectionName");
        await Assert.That(program).Contains("app.MapHub<AdapterPoolHub>(\"/adapterPoolHub\")");
    }

    /// <summary>
    ///     🔴 The route is tenant-free. A pool member belongs to no tenant, and mounting this hub
    ///     under <c>{tenantId:tenantId}</c> would make the member's connection tenant-addressed —
    ///     which is exactly the property that forced a new hub rather than a relaxed
    ///     <c>AdapterHub</c> (implementation plan §12.6).
    /// </summary>
    [Test]
    public async Task TheRouteCarriesNoTenant()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        using var _ = Assert.Multiple();
        await Assert.That(program).Contains("\"/adapterPoolHub\"");
        await Assert.That(program).DoesNotContain("{tenantId:tenantId}/adapterPoolHub");
    }

    /// <summary>
    ///     🔴 The adapter gate must not be weakened to accommodate pool members. A regression that
    ///     mounted <c>AdapterHub</c> without its filter, or dropped the filter's route-tenant check,
    ///     would give away the only tenant binding the adapter data plane has.
    /// </summary>
    [Test]
    public async Task TheAdapterHubKeepsItsOwnGateAndItsRouteTenant()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        using var _ = Assert.Multiple();
        await Assert.That(program)
            .Contains("AddHubOptions<AdapterHub>(o => o.AddFilter<AdapterHubAuthorizationFilter>())");
        await Assert.That(program).Contains("app.MapHub<AdapterHub>(\"/{tenantId:tenantId}/adapterHub\")");
    }

    [Test]
    public async Task Mode_IsOperatorSettable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdapterPoolHubAuthorization:Mode"] = "Enforce"
            })
            .Build();

        var provider = new ServiceCollection()
            .Configure<AdapterPoolHubAuthorizationOptions>(
                configuration.GetSection(AdapterPoolHubAuthorizationOptions.SectionName))
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<AdapterPoolHubAuthorizationOptions>>()
            .Value.Mode).IsEqualTo(AdapterPoolHubAuthorizationMode.Enforce);
    }

    [Test]
    public async Task Mode_DefaultsToLogOnly_WhenTheSectionIsAbsent()
    {
        var provider = new ServiceCollection()
            .Configure<AdapterPoolHubAuthorizationOptions>(
                new ConfigurationBuilder().Build()
                    .GetSection(AdapterPoolHubAuthorizationOptions.SectionName))
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<AdapterPoolHubAuthorizationOptions>>()
            .Value.Mode).IsEqualTo(AdapterPoolHubAuthorizationMode.LogOnly);
    }

    /// <summary>
    ///     The three hub gates must stay separately settable: they evaluate different policies, only
    ///     one of them is tenant-addressed, and their consumer fleets authenticate through different
    ///     mechanisms. A shared section would arm all three at once.
    /// </summary>
    [Test]
    public async Task TheThreeHubGates_HaveDistinctSections()
    {
        var sections = new[]
        {
            AdapterHubAuthorizationOptions.SectionName,
            OperatorHubAuthorizationOptions.SectionName,
            AdapterPoolHubAuthorizationOptions.SectionName
        };

        await Assert.That(sections.Distinct().Count()).IsEqualTo(sections.Length);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
