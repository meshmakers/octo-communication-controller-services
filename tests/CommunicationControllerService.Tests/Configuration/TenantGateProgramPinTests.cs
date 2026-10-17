using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#6833 — pins that make the operator switch real. The registration lives in the top-level
///     statements of <c>Program.cs</c>, which a test cannot compose; so the order is pinned at the
///     source, and the operator paths (configuration key and environment variable) are driven through
///     the same two registrations in that order.
/// </summary>
[NotInParallel("process-environment")]
internal class TenantGateProgramPinTests
{
    private const string EnvironmentVariable = "OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT";

    private static async Task<string> ProgramCodeAsync()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));
        // Comments mention these very calls; judge code only.
        return Regex.Replace(program, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    [Test]
    public async Task Program_RegistersTheCodeDefaultBeforeTheConfigurationBinding()
    {
        var code = await ProgramCodeAsync();

        var codeDefault = Regex.Match(code,
            @"AddOctoTenantAuthorization\(\s*o\s*=>\s*o\.UserTokenEnforcement\s*=\s*UserTokenTenantEnforcementMode\.LogOnly\s*\)");
        var binding = code.IndexOf("AddOctoTenantAuthorization(builder.Configuration)", StringComparison.Ordinal);

        await Assert.That(codeDefault.Success).IsTrue();
        await Assert.That(binding).IsGreaterThan(-1);
        // Later Configure<> wins, so the default has to come first for the operator value to win.
        await Assert.That(codeDefault.Index).IsLessThan(binding);
    }

    [Test]
    public async Task Program_RunsTheGateAfterAuthenticationAndAuthorization_AndBeforeTheEndpoints()
    {
        var code = await ProgramCodeAsync();

        var authentication = code.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        var authorization = code.IndexOf("app.UseAuthorization()", StringComparison.Ordinal);
        var gate = code.IndexOf("app.UseOctoTenantAuthorization()", StringComparison.Ordinal);
        var firstEndpoint = code.IndexOf("app.MapHub<", StringComparison.Ordinal);
        var controllers = code.IndexOf("app.MapControllerRoute(", StringComparison.Ordinal);

        await Assert.That(authentication).IsGreaterThan(-1);
        await Assert.That(authentication).IsLessThan(authorization);
        await Assert.That(authorization).IsLessThan(gate);
        await Assert.That(gate).IsLessThan(firstEndpoint);
        await Assert.That(gate).IsLessThan(controllers);
        await Assert.That(code).Contains("AddOctoTenantIdRouteConstraint()");
    }

    /// <summary>No behaviour change in this lane: the service ships on LogOnly; the flip is configuration.</summary>
    [Test]
    public async Task Program_StillShipsTheMigrationModeAsCodeDefault()
    {
        var code = await ProgramCodeAsync();

        await Assert.That(Regex.Matches(code, @"UserTokenTenantEnforcementMode\.\w+").Count).IsEqualTo(1);
        await Assert.That(code).Contains("UserTokenTenantEnforcementMode.LogOnly");
    }

    [Test]
    public async Task Enforce_WinsFromTheConfigurationKey_OverTheLogOnlyCodeDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
            })
            .Build();

        await Assert.That(Resolve(configuration)).IsEqualTo(UserTokenTenantEnforcementMode.Enforce);
    }

    [Test]
    public async Task Enforce_WinsFromTheEnvironmentVariable_OverTheLogOnlyCodeDefault()
    {
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, "Enforce");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables("OCTO_").Build();

            await Assert.That(Resolve(configuration)).IsEqualTo(UserTokenTenantEnforcementMode.Enforce);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, previous);
        }
    }

    /// <summary>
    ///     The environment variable only reaches the options because <c>Program.cs</c> adds the
    ///     environment provider with the <c>OCTO_</c> prefix; the test above mirrors that call.
    /// </summary>
    [Test]
    public async Task Program_ReadsTheEnvironmentWithTheOctoPrefix()
    {
        await Assert.That(await ProgramCodeAsync()).Contains("AddEnvironmentVariables(\"OCTO_\")");
    }

    [Test]
    public async Task WithoutOperatorSetting_TheCodeDefaultLogOnlyApplies()
    {
        await Assert.That(Resolve(new ConfigurationBuilder().Build()))
            .IsEqualTo(UserTokenTenantEnforcementMode.LogOnly);
    }

    /// <summary>
    ///     🔴 A marker on any endpoint of this service would let a parent tenant's user token address
    ///     a child's data routes. Neither a type nor a method in the service assembly carries it, and
    ///     the source does not mention it in code.
    /// </summary>
    [Test]
    public async Task NoEndpointCarriesTheParentTenantAdministrationMarker()
    {
        var assembly = typeof(AdapterHubAuthorizationFilter).Assembly;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;

        var marked = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttributes(true).OfType<IAllowParentTenantAdministration>().Any())
            {
                marked.Add(type.FullName!);
            }

            foreach (var method in type.GetMethods(all))
            {
                if (method.GetCustomAttributes(true).OfType<IAllowParentTenantAdministration>().Any())
                {
                    marked.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        await Assert.That(marked).IsEmpty();

        var sourceHits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs",
                     SearchOption.AllDirectories))
        {
            var code = Regex.Replace(await File.ReadAllTextAsync(file), @"//.*?$", string.Empty,
                RegexOptions.Multiline);
            if (code.Contains("AllowParentTenantAdministration", StringComparison.Ordinal))
            {
                sourceHits.Add(Path.GetFileName(file));
            }
        }

        await Assert.That(sourceHits).IsEmpty();
    }

    private static UserTokenTenantEnforcementMode Resolve(IConfiguration configuration)
    {
        // The two registrations of Program.cs, in its order (pinned above).
        using var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value.UserTokenEnforcement;
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
