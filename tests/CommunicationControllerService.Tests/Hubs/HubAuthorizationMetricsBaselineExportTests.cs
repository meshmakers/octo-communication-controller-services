using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#6740 — the zero baseline of the hub decision counter must reach the <i>exporter</i>, not just a
///     <see cref="System.Diagnostics.Metrics.MeterListener" />. A measurement taken while no OpenTelemetry
///     <see cref="MeterProvider" /> listens is lost, and the MeterProvider is only built when the host starts
///     (<c>Program.cs</c> used to construct the counter right after <c>builder.Build()</c>). These tests use a real
///     MeterProvider with a capturing exporter in the real startup order, which a per-provider MeterListener
///     cannot reproduce.
/// </summary>
internal class HubAuthorizationMetricsBaselineExportTests
{
    /// <summary>
    ///     An OpenTelemetry MeterProvider listens by meter <i>name</i>, regardless of the factory scope, so the
    ///     MeterListener tests running in parallel would leak their measurements into these exports. This factory
    ///     publishes the meter under a name unique to the test.
    /// </summary>
    private sealed class IsolatedMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public string MeterName { get; } = "Test.AB6740." + Guid.NewGuid().ToString("N");

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(MeterName, options.Version, options.Tags, this);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose() => _meters.ForEach(m => m.Dispose());
    }

    private sealed record Point(Dictionary<string, string> Tags, long Value);

    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        public List<Point> Points { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.Name != HubAuthorizationMetrics.InstrumentName)
                {
                    continue;
                }

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, string>();
                    foreach (var tag in point.Tags)
                    {
                        tags[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                    }

                    Points.Add(new Point(tags, point.GetSumLong()));
                }
            }

            return ExportResult.Success;
        }
    }

    /// <summary>The production wiring: OTel in the host, the counter registered as Program.cs does.</summary>
    private static IHost BuildHost(CapturingExporter exporter)
    {
        var builder = Host.CreateApplicationBuilder();
        var factory = new IsolatedMeterFactory();
        builder.Services.AddSingleton<IMeterFactory>(factory);
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(factory.MeterName)
            .AddReader(new BaseExportingMetricReader(exporter)));
        builder.Services.AddHubAuthorizationMetrics();
        return builder.Build();
    }

    private static async Task AssertFullBaseline(CapturingExporter exporter)
    {
        await Assert.That(exporter.Points.Count).IsEqualTo(30);
        await Assert.That(exporter.Points.Sum(p => p.Value)).IsEqualTo(0);
        foreach (var hub in new[] { "adapter", "operator", "adapterPoolHub" })
        {
            await Assert.That(exporter.Points.Any(p => p.Tags["octo.hub.name"] == hub)).IsTrue();
        }

        await Assert.That(exporter.Points.Count(p => p.Tags["octo.hub.name"] == "adapterPoolHub")).IsEqualTo(10);
    }

    [Test]
    public async Task FreshlyStartedHost_ExportsFullZeroBaseline_BeforeAnyDecision()
    {
        var exporter = new CapturingExporter();
        using var host = BuildHost(exporter);

        // Program.cs used to resolve the counter right here, before the host (and the MeterProvider) started.
        host.Services.GetRequiredService<HubAuthorizationMetrics>();
        await host.StartAsync();
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();

        await AssertFullBaseline(exporter);
        await host.StopAsync();
    }

    [Test]
    public async Task ZeroBaselineTakenBeforeMeterProviderListens_IsLost()
    {
        // Documents the OpenTelemetry semantics the fix has to respect: Add(0) with no listener is dropped.
        var exporter = new CapturingExporter();
        var services = new ServiceCollection();
        var factory = new IsolatedMeterFactory();
        services.AddSingleton<IMeterFactory>(factory);
        services.AddSingleton<HubAuthorizationMetrics>();
        await using var provider = services.BuildServiceProvider();
        var metrics = provider.GetRequiredService<HubAuthorizationMetrics>();
        metrics.EmitBaseline();

        using var meterProvider = OpenTelemetry.Sdk.CreateMeterProviderBuilder()
            .AddMeter(factory.MeterName)
            .AddReader(new BaseExportingMetricReader(exporter))
            .Build();
        meterProvider.ForceFlush();

        await Assert.That(exporter.Points.Count).IsEqualTo(0);

        metrics.EmitBaseline();
        meterProvider.ForceFlush();
        await Assert.That(exporter.Points.Count).IsEqualTo(30);
    }

    [Test]
    public async Task RealDecisionAfterStart_KeepsBaselineSeriesAndCountsOnTop()
    {
        var exporter = new CapturingExporter();
        using var host = BuildHost(exporter);
        await host.StartAsync();

        host.Services.GetRequiredService<HubAuthorizationMetrics>().Record(
            HubAuthorizationHub.AdapterPool, false, HubAuthorizationOutcome.WouldRefuse,
            HubAuthorizationReason.Unauthenticated);
        host.Services.GetRequiredService<MeterProvider>().ForceFlush();

        await Assert.That(exporter.Points.Count).IsEqualTo(30);
        await Assert.That(exporter.Points.Sum(p => p.Value)).IsEqualTo(1);
        var hit = exporter.Points.Single(p => p.Value == 1);
        await Assert.That(hit.Tags["octo.hub.name"]).IsEqualTo("adapterPoolHub");
        await Assert.That(hit.Tags["octo.hub.authorization.reason"]).IsEqualTo("unauthenticated");
        await host.StopAsync();
    }
}
