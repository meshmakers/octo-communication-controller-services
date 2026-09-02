using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#5063 — pins the wiring that makes the <c>/{tenantId}/adapterHub</c> gate reach the hub at
///     all, and the migration mode it starts in. Both pieces fail silently: drop the filter
///     registration and the hub is unguarded again with nothing turning red; drop the section binding
///     and the mode can only be changed by a release, which defeats the purpose of staging it.
///     <para>
///         The server-side half that lets a SignalR client authenticate at all — <c>?access_token=</c>
///         accepted on the hub paths, adapter path included — is pinned by
///         <see cref="OperatorHubAuthorizationWiringTests" />, which already parameterises over
///         <c>/meshtest/adapterHub</c> and its <c>/negotiate</c> sub-path. It is not duplicated here.
///     </para>
/// </summary>
internal class AdapterHubAuthorizationWiringTests
{
    /// <summary>
    ///     The composed <c>AddSignalR()</c> chain cannot be resolved from a unit test (it lives in the
    ///     top-level statements of <c>Program.cs</c>), so the registration is pinned at the source —
    ///     the same technique the operator and tenant-gate wiring tests use.
    /// </summary>
    [Test]
    public async Task Program_RegistersTheFilterOnTheAdapterHub()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        await Assert.That(program)
            .Contains("AddHubOptions<AdapterHub>(o => o.AddFilter<AdapterHubAuthorizationFilter>())");
        await Assert.That(program).Contains("AdapterHubAuthorizationOptions.SectionName");
    }

    [Test]
    public async Task Mode_IsOperatorSettable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdapterHubAuthorization:Mode"] = "Enforce"
            })
            .Build();

        var provider = new ServiceCollection()
            .Configure<AdapterHubAuthorizationOptions>(
                configuration.GetSection(AdapterHubAuthorizationOptions.SectionName))
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<AdapterHubAuthorizationOptions>>()
            .Value.Mode).IsEqualTo(AdapterHubAuthorizationMode.Enforce);
    }

    [Test]
    public async Task Mode_DefaultsToLogOnly_WhenTheSectionIsAbsent()
    {
        var provider = new ServiceCollection()
            .Configure<AdapterHubAuthorizationOptions>(
                new ConfigurationBuilder().Build()
                    .GetSection(AdapterHubAuthorizationOptions.SectionName))
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<AdapterHubAuthorizationOptions>>()
            .Value.Mode).IsEqualTo(AdapterHubAuthorizationMode.LogOnly);
    }

    /// <summary>
    ///     The two hub gates must stay separately settable: they evaluate different policies, only one
    ///     of them is tenant-addressed, and their consumer fleets authenticate through different
    ///     mechanisms. A shared section would arm both at once.
    /// </summary>
    [Test]
    public async Task TheTwoHubGates_HaveDistinctSections()
    {
        await Assert.That(AdapterHubAuthorizationOptions.SectionName)
            .IsNotEqualTo(OperatorHubAuthorizationOptions.SectionName);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
