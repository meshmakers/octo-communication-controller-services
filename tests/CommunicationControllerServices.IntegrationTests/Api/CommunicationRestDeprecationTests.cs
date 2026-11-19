using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1;
using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Api;

/// <summary>
///     AB#9051 (Feature AB#5701 F5.3): the 22 communication endpoints that are facades of System.Communication CK methods carry
///     the deprecation headers of wave <c>CommunicationWave3</c> on every response (also a refusal: the headers depend on the
///     endpoint, not on the status code), never a <c>Sunset</c> header, are counted per endpoint template and client id, and
///     exactly these endpoints carry the attribute (the OpenAPI flag is set by the transformer of octo-common-services from
///     this metadata and tested there).
/// </summary>
[Collection("ZWebFactory")]
public sealed class CommunicationRestDeprecationTests(CustomWebApplicationFactory factory)
    : IntegrationTestBase(factory), IDisposable
{
    private const string Wave = CommunicationRestDeprecation.Wave3;
    private const string Since = "2026-12-01";

    // Pinned literals: the test is about the wire format.
    private const string DeprecationHeader = "@1796083200";

    private const string LinkHeader =
        "<https://docs.meshmakers.cloud/docs/technologyGuide/communication/methods>; rel=\"deprecation\"; type=\"text/html\"";

    private const string Tenant = "deprecationtests";
    private const string Id = "65f0c0ffee65f0c0ffee65f0";

    private readonly List<WebApplicationFactory<Program>> _hosts = [];

    /// <summary>The 22 endpoints: HTTP method and path below <c>{tenant}/v1/</c>.</summary>
    public static TheoryData<string, string> Endpoints => new()
    {
        { "POST", $"Adapter/deployUpdate?adapterRtEntityId=System.Communication/MeshAdapter@{Id}" },
        { "POST", $"Adapter/{Id}/wake" },
        { "POST", $"Adapter/{Id}/serviceAccount/reconcile" },
        { "POST", $"Adapter/{Id}/serviceAccount/rotateSecret" },
        { "POST", "AdapterPool/mirrors/refresh" },
        { "POST", "AdapterPool/mirrors/publish" },
        { "POST", $"AdapterPool/{Id}/lease" },
        { "DELETE", $"AdapterPool/{Id}/queue/exec-1" },
        { "POST", $"Pipeline/deploy?adapterRtEntityId=a&pipelineRtEntityId=b" },
        { "POST", $"Pipeline/execute?pipelineRtId={Id}" },
        { "PATCH", "Pipeline/move-to-adapter" },
        { "PATCH", $"Pipeline/{Id}/debug" },
        { "POST", $"DataFlow/deploy?dataFlowRtId={Id}" },
        { "POST", $"DataFlow/undeploy?dataFlowRtId={Id}" },
        { "PATCH", $"Workload/{Id}/chart-version" },
        { "POST", $"DeploymentSite/deploy?deploymentSiteRtId={Id}" },
        { "POST", $"DeploymentSite/undeploy?deploymentSiteRtId={Id}" },
        { "POST", $"DeploymentSite/workloads/deploy?workloadRtId={Id}" },
        { "POST", $"DeploymentSite/workloads/undeploy?workloadRtId={Id}" },
        { "POST", $"DeploymentSite/workloads/adapter-pool/scale?workloadRtId={Id}&replicas=1" },
        { "POST", "PipelineTrigger/deploy" },
        { "POST", "PipelineTrigger/undeploy" }
    };

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    /// <remarks>
    ///     The shape the clusters run (Gerald 2026-11-11): <c>since</c> is configured at the delivery of the release, the sunset
    ///     date is to be announced and stays empty, so no <c>Sunset</c> header is sent.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task AMigratedEndpoint_CarriesDeprecationAndLink_ButNoSunset(string method, string path)
    {
        using var client = Host(Since).CreateClient();

        // No token: the bearer scheme refuses the call after routing selected the endpoint; the headers depend on the
        // endpoint, not on the status code.
        var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        response.Headers.GetValues("Deprecation").Should().Equal(DeprecationHeader);
        response.Headers.GetValues("Link").Should().Equal(LinkHeader);
        response.Headers.Contains("Sunset").Should().BeFalse("the sunset date is to be announced and not configured");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task WithoutTheWaveDate_OnlyTheLinkIsSent(string method, string path)
    {
        using var client = Host(null).CreateClient();

        var response = await SendAsync(client, method, path);

        response.Headers.Contains("Deprecation").Should().BeFalse("no date is invented while Since is not configured");
        response.Headers.Contains("Sunset").Should().BeFalse();
        response.Headers.GetValues("Link").Should().Equal(LinkHeader);
    }

    /// <summary>The endpoints that stay REST (reads, stateless tools, tenant-level endpoints) carry no deprecation header.</summary>
    [Theory]
    [InlineData("GET", "DeploymentSite")]
    [InlineData("GET", "Workload?chartName=octo-mesh-adapter")]
    [InlineData("GET", "Pipeline/status")]
    [InlineData("POST", "Communication/validate-expression")]
    [InlineData("POST", "ServiceAccount/reconcile")]
    [InlineData("PUT", "PipelineDefinition/update-node")]
    public async Task AnEndpointThatStaysRest_CarriesNoDeprecationHeader(string method, string path)
    {
        using var client = Host(Since).CreateClient();

        var response = await SendAsync(client, method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path} exists and is refused without a token");
        response.Headers.Contains("Deprecation").Should().BeFalse(path);
        response.Headers.Contains("Sunset").Should().BeFalse(path);
        response.Headers.Contains("Link").Should().BeFalse(path);
    }

    [Fact]
    public async Task ACall_IsCounted_PerEndpointTemplateAndClientIdOnly()
    {
        using var collector = new DeprecatedCallCollector();
        using var client = Host(Since).CreateClient();

        await SendAsync(client, "POST", $"DeploymentSite/workloads/deploy?workloadRtId={Id}");

        // The listener sees the whole process; other test classes may run in parallel.
        var measurement = collector.Snapshot()
            .Where(m => m.Tags.TryGetValue("endpoint", out var e) && e.EndsWith("DeploymentSite/workloads/deploy"))
            .Should().NotBeEmpty().And.Subject.First();
        measurement.Tags.Keys.Should().BeEquivalentTo("endpoint", "client_id", "status_class");
        measurement.Tags["endpoint"].Should().StartWith("POST ").And.NotContain(Tenant).And.NotContain(Id);
        measurement.Tags["status_class"].Should().Be("4xx");
    }

    [Fact]
    public void ExactlyTheFacadeEndpoints_CarryTheAttribute_OfWave3_WithTheMethodsLink()
    {
        var marked = Host(Since).Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Attribute: e.Metadata.GetMetadata<OctoRestDeprecatedAttribute>()))
            .Where(e => e.Attribute != null)
            .ToList();

        marked.Should().OnlyContain(e => e.Attribute!.Wave == Wave &&
                                         e.Attribute.Link == CommunicationRestDeprecation.MethodsLink &&
                                         string.IsNullOrEmpty(e.Attribute.Since) &&
                                         string.IsNullOrEmpty(e.Attribute.Sunset));
        const string prefix = "/v{version:apiVersion}/";
        marked.Select(e =>
            {
                var template = e.Endpoint.RoutePattern.RawText!;
                return $"{string.Join(",", e.Endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)} " +
                       template[(template.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..];
            })
            .Should().BeEquivalentTo(
                "POST Adapter/deployUpdate", "POST Adapter/{workloadRtId}/wake",
                "POST Adapter/{adapterRtId}/serviceAccount/reconcile",
                "POST Adapter/{adapterRtId}/serviceAccount/rotateSecret",
                "POST AdapterPool/mirrors/refresh", "POST AdapterPool/mirrors/publish",
                "POST AdapterPool/{adapterPoolRtId}/lease", "DELETE AdapterPool/{adapterPoolRtId}/queue/{executionId}",
                "POST Pipeline/deploy", "POST Pipeline/execute", "PATCH Pipeline/move-to-adapter",
                "PATCH Pipeline/{pipelineRtId}/debug", "POST DataFlow/deploy", "POST DataFlow/undeploy",
                "PATCH Workload/{workloadRtId}/chart-version", "POST DeploymentSite/deploy", "POST DeploymentSite/undeploy",
                "POST DeploymentSite/workloads/deploy", "POST DeploymentSite/workloads/undeploy",
                "POST DeploymentSite/workloads/adapter-pool/scale", "POST PipelineTrigger/deploy",
                "POST PipelineTrigger/undeploy");
    }

    private WebApplicationFactory<Program> Host(string? since)
    {
        var host = Factory.WithWebHostBuilder(b =>
        {
            // Every key is set explicitly (empty = no date), so that dates configured in the environment cannot reach
            // a host of this test.
            b.UseSetting($"RestDeprecation:Waves:{Wave}:Since", since ?? string.Empty);
            b.UseSetting($"RestDeprecation:Waves:{Wave}:Sunset", string.Empty);
        });
        _hosts.Add(host);
        return host;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/{Tenant}/v1/{path}");
        if (method is "POST" or "PATCH")
        {
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Collects the measurements of <c>octo.rest.deprecated.calls</c> while it lives.</summary>
    private sealed class DeprecatedCallCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<DeprecatedCallMeasurement> _measurements = new();

        public DeprecatedCallCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Meshmakers.Octo.RestDeprecation" &&
                    instrument.Name == "octo.rest.deprecated.calls")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var dictionary = new Dictionary<string, string>();
                foreach (var tag in tags)
                {
                    dictionary[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                }

                _measurements.Enqueue(new DeprecatedCallMeasurement(value, dictionary));
            });
            _listener.Start();
        }

        public IReadOnlyList<DeprecatedCallMeasurement> Snapshot() => _measurements.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private sealed record DeprecatedCallMeasurement(long Value, IReadOnlyDictionary<string, string> Tags);
}
