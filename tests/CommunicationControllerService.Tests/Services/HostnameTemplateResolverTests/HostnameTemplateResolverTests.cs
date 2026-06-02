using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.HostnameTemplateResolverTests;

internal class HostnameTemplateResolverTests
{
    private static HostnameTemplateResolver CreateSut(Dictionary<string, string>? domains = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<CommunicationControllerOptions>>();
        monitor.CurrentValue.Returns(new CommunicationControllerOptions
        {
            Domains = domains ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = "staging.octo-mesh.com",
                ["internal"] = "octo.internal",
            },
        });
        return new HostnameTemplateResolver(monitor);
    }

    [Test]
    public async Task TryResolve_Null_ReturnsNullAndSucceeds()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve(null, out var resolved, out var unknown);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsNull();
        await Assert.That(unknown).IsNull();
    }

    [Test]
    public async Task TryResolve_Empty_ReturnsEmptyAndSucceeds()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve(string.Empty, out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TryResolve_LiteralWithoutPlaceholder_PassesThrough()
    {
        // Workloads pre-dating the template feature ship with literal hostnames
        // (e.g. "adapter.acme.com"); they must keep working unchanged.
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.staging.octo-mesh.com", out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_SinglePlaceholder_Substitutes()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.{{domain.default}}", out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_MultiplePlaceholders_SubstitutesAll()
    {
        // Allowed but unusual — a workload that uses two different domain names
        // in one hostname. Don't optimise away by accident.
        var sut = CreateSut();

        var ok = sut.TryResolve("api.{{domain.default}}-{{domain.internal}}", out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("api.staging.octo-mesh.com-octo.internal");
    }

    [Test]
    public async Task TryResolve_CaseInsensitiveDomainName_Resolves()
    {
        // OCTO_…__DOMAINS__DEFAULT in env var → "DEFAULT" key; template author
        // writes {{domain.default}}. Both must match.
        var sut = CreateSut(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEFAULT"] = "staging.octo-mesh.com",
        });

        var ok = sut.TryResolve("adapter.{{domain.default}}", out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_UnknownDomain_FailsWithName()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.{{domain.does-not-exist}}", out var resolved, out var unknown);

        await Assert.That(ok).IsFalse();
        await Assert.That(resolved).IsNull();
        await Assert.That(unknown).IsEqualTo("does-not-exist");
    }

    [Test]
    public async Task TryResolve_MultiplePlaceholders_OneUnknown_ReportsFirstUnknown()
    {
        // Stable error message even when several names are wrong — caller
        // should see the FIRST offender so re-running after a fix progresses
        // through the list deterministically.
        var sut = CreateSut();

        var ok = sut.TryResolve("a.{{domain.unknown-a}}.{{domain.unknown-b}}", out _, out var unknown);

        await Assert.That(ok).IsFalse();
        await Assert.That(unknown).IsEqualTo("unknown-a");
    }

    [Test]
    public async Task TryResolve_MalformedPlaceholderWithSingleBrace_DoesNotMatch()
    {
        // Single-brace ${...} is the blueprint-var syntax, resolved at apply
        // time — the hostname resolver must NOT touch it. The literal flows
        // through unchanged and any leftover ${...} will fail later at k8s
        // admission (or stay as a deliberate literal).
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.${domain.default}", out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.${domain.default}");
    }

    [Test]
    public async Task TryResolve_PlaceholderWithSurroundingWhitespace_StillResolves()
    {
        // Tolerate accidental whitespace inside braces — same forgiveness as
        // the blueprint interpolator's \s* in the placeholder regex.
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.{{ domain.default }}", out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task AvailableDomains_ReflectsOptions()
    {
        var sut = CreateSut(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "staging.octo-mesh.com",
            ["internal"] = "octo.internal",
        });

        var available = sut.AvailableDomains;

        await Assert.That(available).Count().IsEqualTo(2);
        await Assert.That(available["default"]).IsEqualTo("staging.octo-mesh.com");
        await Assert.That(available["internal"]).IsEqualTo("octo.internal");
    }
}
