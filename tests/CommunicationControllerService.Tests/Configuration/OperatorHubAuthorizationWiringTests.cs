using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Configuration;

/// <summary>
///     AB#5059 — pins the wiring that makes the <c>/operatorHub</c> gate reach the hub at all, and the
///     migration mode it starts in. Both pieces fail silently: drop the filter registration and the
///     hub is unguarded again with nothing turning red; drop the query-string token and the gate reads
///     every standards-compliant SignalR client as anonymous, which produces a clean-looking but
///     worthless inventory.
/// </summary>
internal class OperatorHubAuthorizationWiringTests
{
    private static JwtBearerOptions Configure()
    {
        var options = new JwtBearerOptions();
        new ConfigureJwtBearerOptions(
                Options.Create(new CommunicationControllerOptions
                {
                    AuthorityUrl = "https://localhost:5003"
                }))
            .Configure(options);
        return options;
    }

    private static async Task<string?> TokenFor(string path, string? queryToken)
    {
        var options = Configure();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (queryToken != null)
        {
            httpContext.Request.QueryString = new QueryString($"?access_token={queryToken}");
        }

        var context = new MessageReceivedContext(httpContext,
            new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null,
                typeof(JwtBearerHandler)),
            options);

        await options.Events!.MessageReceived(context);
        return context.Token;
    }

    /// <summary>
    ///     SignalR cannot put a bearer token in a header on the WebSocket / SSE transports, so it
    ///     appends <c>?access_token=</c>. Without the server-side counterpart the hub principal is
    ///     always anonymous.
    /// </summary>
    [Test]
    [Arguments("/operatorHub")]
    [Arguments("/operatorHub/negotiate")]
    [Arguments("/meshtest/adapterHub")]
    [Arguments("/meshtest/adapterHub/negotiate")]
    public async Task QueryStringToken_IsAccepted_OnHubPaths(string path)
    {
        await Assert.That(await TokenFor(path, "a.b.c")).IsEqualTo("a.b.c");
    }

    /// <summary>
    ///     🔴 Deliberately narrowed to the hub paths. A token accepted as a query parameter on a REST
    ///     route would end up in access logs, proxy logs and browser history — the reason the practice
    ///     is confined to transports that cannot carry a header.
    /// </summary>
    [Test]
    [Arguments("/meshtest/system/v1/adapter")]
    [Arguments("/system/v1/diagnostics/reconfigureLogLevel")]
    [Arguments("/healthz")]
    public async Task QueryStringToken_IsIgnored_OnRestPaths(string path)
    {
        await Assert.That(await TokenFor(path, "a.b.c")).IsNull();
    }

    [Test]
    public async Task NoQueryStringToken_LeavesTheHeaderPathUntouched()
    {
        await Assert.That(await TokenFor("/operatorHub", null)).IsNull();
    }

    /// <summary>
    ///     The composed <c>AddSignalR()</c> chain cannot be resolved from a unit test (it lives in the
    ///     top-level statements of <c>Program.cs</c>), so the registration is pinned at the source —
    ///     the same technique <see cref="TenantAuthorizationWiringTests" /> uses for the bearer scheme.
    /// </summary>
    [Test]
    public async Task Program_RegistersTheFilterOnTheOperatorHub()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        await Assert.That(program)
            .Contains("AddHubOptions<OperatorHub>(o => o.AddFilter<OperatorHubAuthorizationFilter>())");
        await Assert.That(program).Contains("OperatorHubAuthorizationOptions.SectionName");
    }

    /// <summary>
    ///     The section must bind, otherwise the mode can only ever be changed by a release — which is
    ///     the opposite of what a staged gate is for.
    /// </summary>
    [Test]
    public async Task Mode_IsOperatorSettable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OperatorHubAuthorization:Mode"] = "Enforce"
            })
            .Build();

        var provider = new ServiceCollection()
            .Configure<OperatorHubAuthorizationOptions>(
                configuration.GetSection(OperatorHubAuthorizationOptions.SectionName))
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<OperatorHubAuthorizationOptions>>()
            .Value.Mode).IsEqualTo(OperatorHubAuthorizationMode.Enforce);
    }

    [Test]
    public async Task Mode_DefaultsToLogOnly_WhenTheSectionIsAbsent()
    {
        var provider = new ServiceCollection()
            .Configure<OperatorHubAuthorizationOptions>(
                new ConfigurationBuilder().Build()
                    .GetSection(OperatorHubAuthorizationOptions.SectionName))
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<OperatorHubAuthorizationOptions>>()
            .Value.Mode).IsEqualTo(OperatorHubAuthorizationMode.LogOnly);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
