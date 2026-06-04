using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadTemplateResolverTests;

internal class WorkloadTemplateResolverTests
{
    private const string DefaultTenantId = "tenant-a";

    private static WorkloadTemplateResolver CreateSut(
        Dictionary<string, string>? domains = null,
        Dictionary<string, string>? serviceUrls = null)
    {
        var monitor = Substitute.For<IOptionsMonitor<CommunicationControllerOptions>>();
        monitor.CurrentValue.Returns(new CommunicationControllerOptions
        {
            Domains = domains ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = "staging.octo-mesh.com",
                ["internal"] = "octo.internal",
            },
            ServiceUrls = serviceUrls ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["authority"] = "https://identity.staging.octo-mesh.com",
                ["assetRepository"] = "https://assets.staging.octo-mesh.com",
            },
        });
        return new WorkloadTemplateResolver(monitor);
    }

    private static WorkloadTemplateContext Ctx(string? tenantId = DefaultTenantId) =>
        new(tenantId!);

    [Test]
    public async Task TryResolve_Null_ReturnsNullAndSucceeds()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve(null, Ctx(), out var resolved, out var unknown);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsNull();
        await Assert.That(unknown).IsNull();
    }

    [Test]
    public async Task TryResolve_Empty_ReturnsEmptyAndSucceeds()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve(string.Empty, Ctx(), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TryResolve_LiteralWithoutPlaceholder_PassesThrough()
    {
        // Workloads pre-dating the template feature ship with literal values
        // (e.g. "adapter.acme.com"); they must keep working unchanged.
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.staging.octo-mesh.com", Ctx(), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_SingleDomainPlaceholder_Substitutes()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.{{domain.default}}", Ctx(), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_SingleServicePlaceholder_Substitutes()
    {
        // {{service.authority}} resolves against ServiceUrls. Same shape as
        // {{domain.NAME}} so callers don't have to special-case the family.
        var sut = CreateSut();

        var ok = sut.TryResolve("oauth.authority={{service.authority}}", Ctx(), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("oauth.authority=https://identity.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_TenantIdPlaceholder_Substitutes()
    {
        // {{context.tenantId}} is the only context.* placeholder for now;
        // the namespace is kept so future per-deploy values (workloadRtId,
        // poolName) can land without touching templates already in the field.
        var sut = CreateSut();

        var ok = sut.TryResolve("https://api/{{context.tenantId}}/callback", Ctx("acme"),
            out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("https://api/acme/callback");
    }

    [Test]
    public async Task TryResolve_MixedFamilies_SubstitutesAll()
    {
        // Realistic case: a per-tenant URL stitched together with the cluster's
        // public Identity authority and the deploying tenant id.
        var sut = CreateSut();

        var ok = sut.TryResolve(
            "https://{{context.tenantId}}.{{domain.default}}?authority={{service.authority}}",
            Ctx("acme"), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo(
            "https://acme.staging.octo-mesh.com?authority=https://identity.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_CaseInsensitiveLookup_Resolves()
    {
        // OCTO_…__DOMAINS__DEFAULT / OCTO_…__SERVICEURLS__AUTHORITY env vars
        // land as uppercase keys; templates use lowercase. Both must match.
        var sut = CreateSut(
            domains: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DEFAULT"] = "staging.octo-mesh.com",
            },
            serviceUrls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AUTHORITY"] = "https://identity.staging.octo-mesh.com",
            });

        var ok = sut.TryResolve("a.{{domain.default}}-{{service.authority}}", Ctx(),
            out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("a.staging.octo-mesh.com-https://identity.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_UnknownDomain_ReportsFullyQualifiedPlaceholder()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.{{domain.does-not-exist}}", Ctx(),
            out var resolved, out var unknown);

        await Assert.That(ok).IsFalse();
        await Assert.That(resolved).IsNull();
        await Assert.That(unknown).IsEqualTo("domain.does-not-exist");
    }

    [Test]
    public async Task TryResolve_UnknownService_ReportsFullyQualifiedPlaceholder()
    {
        var sut = CreateSut();

        var ok = sut.TryResolve("{{service.nope}}", Ctx(), out _, out var unknown);

        await Assert.That(ok).IsFalse();
        await Assert.That(unknown).IsEqualTo("service.nope");
    }

    [Test]
    public async Task TryResolve_EmptyTenantIdInContext_ReportsContextPlaceholder()
    {
        // Defensive: an empty TenantId on the context (shouldn't happen in
        // production — the caller always passes the real tenant) is reported
        // as an unknown placeholder rather than silently substituted to empty.
        var sut = CreateSut();

        var ok = sut.TryResolve("{{context.tenantId}}", Ctx(string.Empty),
            out _, out var unknown);

        await Assert.That(ok).IsFalse();
        await Assert.That(unknown).IsEqualTo("context.tenantId");
    }

    [Test]
    public async Task TryResolve_MultiplePlaceholders_OneUnknown_ReportsFirstUnknown()
    {
        // Stable error message even when several placeholders are wrong —
        // caller should see the FIRST offender so re-running after a fix
        // progresses through the list deterministically.
        var sut = CreateSut();

        var ok = sut.TryResolve("a.{{domain.unknown-a}}.{{service.unknown-b}}", Ctx(),
            out _, out var unknown);

        await Assert.That(ok).IsFalse();
        await Assert.That(unknown).IsEqualTo("domain.unknown-a");
    }

    [Test]
    public async Task TryResolve_MalformedPlaceholderWithSingleBrace_DoesNotMatch()
    {
        // Single-brace ${...} is the blueprint-var syntax, resolved at apply
        // time — the workload template resolver must NOT touch it.
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.${domain.default}", Ctx(), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.${domain.default}");
    }

    [Test]
    public async Task TryResolve_PlaceholderWithSurroundingWhitespace_StillResolves()
    {
        // Tolerate accidental whitespace inside braces — same forgiveness as
        // the blueprint interpolator's \s* in the placeholder regex.
        var sut = CreateSut();

        var ok = sut.TryResolve("adapter.{{ domain.default }}", Ctx(), out var resolved, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("adapter.staging.octo-mesh.com");
    }

    [Test]
    public async Task TryResolve_UnknownNamespaceLikeFlow_StaysLiteral()
    {
        // {{foo.bar}} doesn't match any of the three known families and is
        // returned unchanged. This is the contract that lets a ValuesYaml
        // block carry literal Go-template-looking strings (e.g. chart-side
        // {{ .Values.x }} samples) without the resolver tripping.
        var sut = CreateSut();

        var ok = sut.TryResolve("foo={{foo.bar}}", Ctx(), out var resolved, out var unknown);

        await Assert.That(ok).IsTrue();
        await Assert.That(resolved).IsEqualTo("foo={{foo.bar}}");
        await Assert.That(unknown).IsNull();
    }

    [Test]
    public async Task AvailableDomains_ReflectsOptions()
    {
        var sut = CreateSut(domains: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = "staging.octo-mesh.com",
            ["internal"] = "octo.internal",
        });

        var available = sut.AvailableDomains;

        await Assert.That(available).Count().IsEqualTo(2);
        await Assert.That(available["default"]).IsEqualTo("staging.octo-mesh.com");
        await Assert.That(available["internal"]).IsEqualTo("octo.internal");
    }

    [Test]
    public async Task AvailableServiceUrls_ReflectsOptions()
    {
        var sut = CreateSut(serviceUrls: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["authority"] = "https://identity.staging.octo-mesh.com",
            ["bot"] = "https://bot.staging.octo-mesh.com",
        });

        var available = sut.AvailableServiceUrls;

        await Assert.That(available).Count().IsEqualTo(2);
        await Assert.That(available["authority"]).IsEqualTo("https://identity.staging.octo-mesh.com");
        await Assert.That(available["bot"]).IsEqualTo("https://bot.staging.octo-mesh.com");
    }
}
