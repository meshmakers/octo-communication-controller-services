using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#5054 — pins the wiring that makes the shared transport tenant gate
///     (<c>UseOctoTenantAuthorization()</c> / <c>TenantAuthorizationMiddleware</c>) effective in this
///     service, and the migration mode it starts in. Every piece here fails <i>silently</i>: drop
///     one and the gate goes back to letting every request through, with no compile error and no
///     other test turning red.
/// </summary>
internal class TenantAuthorizationWiringTests
{
    private static JwtBearerOptions Configure(string authority = "https://localhost:5003")
    {
        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(
                Options.Create(new CommunicationControllerOptions { AuthorityUrl = authority }))
            .Configure(options);
        return options;
    }

    /// <summary>
    ///     🔴 The silent-no-op trap. The middleware skips any principal whose
    ///     <c>AuthenticationType</c> is not <c>Bearer</c> — a guard against false 403s on cookie
    ///     principals. The JWT handler's default label is <c>AuthenticationTypes.Federation</c>, so
    ///     without this the gate never fires on a bearer request and the AB#5032 service-token
    ///     audit log this service is supposed to produce stays empty.
    /// </summary>
    [Test]
    public async Task ConfigureJwtBearerOptions_LabelsTheIdentityBearer()
    {
        await Assert.That(Configure().TokenValidationParameters.AuthenticationType)
            .IsEqualTo(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>
    ///     The settings the configurator took over from the former <c>AddJwtBearer</c> delegate,
    ///     so consolidating the two did not change what a token has to satisfy.
    /// </summary>
    [Test]
    public async Task ConfigureJwtBearerOptions_KeepsAuthorityIssuerAndAudienceContract()
    {
        var options = Configure("https://identity.example.com");

        await Assert.That(options.Authority).IsEqualTo("https://identity.example.com/");
        // Trailing slash: IdentityServer stamps `iss` with one, so ValidIssuer must match exactly.
        await Assert.That(options.TokenValidationParameters.ValidIssuer)
            .IsEqualTo("https://identity.example.com/");
        await Assert.That(options.Audience).IsEqualTo("octoAPI");
    }

    /// <summary>
    ///     🔴 The test above proves nothing on its own — and that is not a figure of speech.
    ///     octo-ai-services had exactly that test, green, while the label was wiped at runtime: its
    ///     <c>Program.cs</c> configured the bearer scheme a <b>second</b> time via
    ///     <c>AddJwtBearer(jwt =&gt; { jwt.TokenValidationParameters = new TokenValidationParameters
    ///     { … }; })</c>. The options factory runs configurators in registration order, so the later
    ///     delegate replaced the whole instance — label and <c>ValidIssuer</c> gone — and the gate
    ///     was a no-op for a full release (AB#5051 → AB#5056). This service had the identical
    ///     double configuration until AB#5054.
    ///     <para>
    ///         The composed options cannot be resolved from a unit test (the registration lives in
    ///         top-level statements in <c>Program.cs</c>), so this guard pins the composition rule
    ///         at the source instead: exactly one configurator owns the scheme, and
    ///         <c>AddJwtBearer</c> is called without an argument.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ConfigureJwtBearerOptions_IsTheOnlyConfiguratorOfTheBearerScheme()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        await Assert.That(program).Contains("ConfigureOptions<ConfigureJwtBearerOptions>()");

        // Comments talk about the very pattern this guards against, so strip them first.
        var code = Regex.Replace(program, @"//.*?$", string.Empty, RegexOptions.Multiline);

        await Assert.That(Regex.IsMatch(code, @"AddJwtBearer\s*\(\s*[^)\s]"))
            .IsFalse();
    }

    /// <summary>
    ///     The user path is armed in stages (AB#5054): the gate has never run in this service, so
    ///     the consumer inventory it was supposed to be judged on does not exist yet. LogOnly writes
    ///     it without changing any outcome. The platform default is <c>Enforce</c>, so this opt-down
    ///     has to be explicit.
    /// </summary>
    [Test]
    public async Task UserTokenEnforcement_StartsInTheMigrationMode()
    {
        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value
            .UserTokenEnforcement).IsEqualTo(UserTokenTenantEnforcementMode.LogOnly);
    }

    /// <summary>
    ///     🔴 Registration order is load-bearing: the code default must come BEFORE the section
    ///     binding, otherwise <c>OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce</c> is inert
    ///     and this service is stuck in the migration mode while the estate moves on — the exact
    ///     class of silent outlier AB#5047 had to fix once already.
    /// </summary>
    [Test]
    public async Task UserTokenEnforcement_IsStillOperatorSettable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TenantAuthorization:UserTokenEnforcement"] = "Enforce"
            })
            .Build();

        var provider = new ServiceCollection()
            .AddOctoTenantAuthorization(o => o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly)
            .AddOctoTenantAuthorization(configuration)
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<TenantAuthorizationOptions>>().Value
            .UserTokenEnforcement).IsEqualTo(UserTokenTenantEnforcementMode.Enforce);
    }

    /// <summary>
    ///     Repository root, derived from this file's compile-time path so it is independent of the
    ///     build output directory.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
