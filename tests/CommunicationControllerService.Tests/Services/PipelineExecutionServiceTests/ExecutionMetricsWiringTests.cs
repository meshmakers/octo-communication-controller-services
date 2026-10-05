using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

/// <summary>
///     AB#5425. <see cref="PipelineExecutionMetricsTests" /> pins what the instruments publish; this
///     pins that the service actually feeds them. Without it a refactor could drop the calls and
///     every test would stay green while the alerting went blind again — which is precisely the
///     failure mode the work item exists to end.
///
///     Deliberately not derived from <see cref="PipelineExecutionServiceTestsBase" />: that base
///     pins a shared tenant id, and the instruments are process-wide while the suite runs
///     concurrently, so the measurements of two tests would be indistinguishable.
/// </summary>
[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class ExecutionMetricsWiringTests
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();
    private readonly IWorkloadLifecycleService _lifecycleService = Substitute.For<IWorkloadLifecycleService>();
    private readonly PipelineExecutionService _service;
    private readonly string _tenantId = $"tenant-{Guid.NewGuid():N}";

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    public ExecutionMetricsWiringTests()
    {
        _service = new PipelineExecutionService(_repository, _adapterCache, _eventService, _lifecycleService);

        // AB#5432: the tenant has opted into observability. Without it every instrument in this file
        // is silent by design — pinned by FoldAndPruneExecutionsAsync_OnATenantThatDidNotOptIn_….
        WorkloadObservabilityOptIn.Refresh(_tenantId, optedIn: true, TimeSpan.FromMinutes(5));

        var adapterCachePublish = Substitute.For<IAdapterCachePublish>();
        var adapterTenant = new AdapterTenant(adapterCachePublish, _tenantId);
        _adapterCache.TryGetTenant(_tenantId, out Arg.Any<AdapterTenant?>())
            .Returns(x =>
            {
                x[1] = adapterTenant;
                return true;
            });
    }

    private async Task<List<Recorded>> CollectAsync(Func<Task> act, bool observeGauges = false)
    {
        var recorded = new List<Recorded>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineExecutionMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.Start();

        await act();

        if (observeGauges)
        {
            listener.RecordObservableInstruments();
        }

        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.tenant.id") == _tenantId).ToList();
    }

    private static Dictionary<string, string> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return map;
    }

    [Test]
    public async Task CompleteExecutionAsync_CountsTheReportedOutcome()
    {
        // Arrange
        var execution = RtEntityCreator.CreatePipelineExecution("exec-1");
        _repository.GetPipelineExecutionAsync(_tenantId, "exec-1").Returns(execution);
        _repository.GetPipelineExecutionsAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int?>()).Returns([]);

        // Act
        var recorded = await CollectAsync(() => _service.CompleteExecutionAsync(_tenantId,
            RtEntityCreator.CreateAdapter().ToRtEntityId(),
            new PipelineExecutionEndDto
            {
                ExecutionId = "exec-1",
                Status = PipelineExecutionStatus.Failed,
                CompletedAt = DateTime.UtcNow,
                DurationMs = 42,
                ErrorMessage = "boom",
            }));

        // Assert
        var counted = recorded.Single(r => r.Instrument == "octo.pipeline.execution.count");
        await Assert.That(counted.Value).IsEqualTo(1);
        await Assert.That(counted.Tags["octo.pipeline.outcome"]).IsEqualTo("failed");
    }

    [Test]
    public async Task BatchCompleteExecutionsAsync_CountsOneMeasurementPerOutcome()
    {
        // Arrange
        _repository.BulkUpdatePipelineExecutionsAsync(_tenantId, Arg.Any<IReadOnlyList<PipelineExecutionUpdate>>())
            .Returns(3);

        PipelineExecutionEndDto End(string id, PipelineExecutionStatus status) => new()
        {
            ExecutionId = id,
            Status = status,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 1,
        };

        // Act
        var recorded = await CollectAsync(() => _service.BatchCompleteExecutionsAsync(_tenantId,
        [
            End("a", PipelineExecutionStatus.Completed),
            End("b", PipelineExecutionStatus.Completed),
            End("c", PipelineExecutionStatus.Failed),
        ]));

        // Assert
        var byOutcome = recorded
            .Where(r => r.Instrument == "octo.pipeline.execution.count")
            .ToDictionary(r => r.Tags["octo.pipeline.outcome"], r => r.Value);
        await Assert.That(byOutcome["completed"]).IsEqualTo(2);
        await Assert.That(byOutcome["failed"]).IsEqualTo(1);
    }

    /// <summary>
    ///     The sweep is the unconditional half of the contract: every pipeline it enumerates gets a
    ///     series, whether or not it ever ran. This is the test that would have caught the
    ///     <c>Monitoring.CkHealth</c> stall.
    /// </summary>
    [Test]
    public async Task FoldAndPruneExecutionsAsync_PublishesGaugesForEveryPipelineItSweeps()
    {
        // Arrange — one pipeline that ran four hours ago, one that never ran at all.
        var stalled = RtEntityCreator.CreatePipeline();
        stalled.Name = "Monitoring.CkHealth";
        var neverRan = RtEntityCreator.CreatePipeline();
        neverRan.Name = "Accounting.ImportMail";

        _repository.GetAllPipelinesAsync(_tenantId).Returns([stalled, neverRan]);
        _repository.GetTerminalExecutionsOlderThanAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime>(),
            Arg.Any<int>()).Returns([]);
        _repository.GetPipelineExecutionsAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>()).Returns([]);

        var stalledStatistics = RtEntityCreator.CreatePipelineStatistics();
        stalledStatistics.LastExecutionAt = DateTime.UtcNow.AddHours(-4);
        stalledStatistics.LastHourFailureCount = 0;
        stalledStatistics.LastHourSuccessCount = 0;
        _repository.GetPipelineStatisticsAsync(_tenantId, stalled.ToRtEntityId()).Returns(stalledStatistics);
        _repository.GetPipelineStatisticsAsync(_tenantId, neverRan.ToRtEntityId()).Returns((RtPipelineStatistics?)null);

        // Act
        var recorded = await CollectAsync(
            () => _service.FoldAndPruneExecutionsAsync(_tenantId, 1), observeGauges: true);

        // Assert — both pipelines reported, neither silently absent.
        var ages = recorded
            .Where(r => r.Instrument == "octo.pipeline.execution.age")
            .ToDictionary(r => r.Tags["octo.pipeline.rt_id"], r => r);

        await Assert.That(ages.ContainsKey(stalled.RtId.ToString())).IsTrue();
        await Assert.That(ages.ContainsKey(neverRan.RtId.ToString())).IsTrue();

        var stalledAge = ages[stalled.RtId.ToString()];
        await Assert.That(stalledAge.Value).IsBetween(4 * 3600 - 60, 4 * 3600 + 60);
        await Assert.That(stalledAge.Tags["octo.pipeline.name"]).IsEqualTo("Monitoring.CkHealth");
        await Assert.That(stalledAge.Tags["octo.pipeline.deployment_state"]).IsEqualTo("deployed");

        await Assert.That(ages[neverRan.RtId.ToString()].Value)
            .IsEqualTo(PipelineExecutionMetrics.NeverExecuted);

        // …and the failure gauge exists for the healthy pipeline too, reporting a zero.
        await Assert.That(recorded.Single(r =>
            r.Instrument == "octo.pipeline.execution.failures" &&
            r.Tags["octo.pipeline.rt_id"] == stalled.RtId.ToString()).Value).IsEqualTo(0);
    }

    /// <summary>
    ///     AB#5432. The statistics sweep keeps running for a tenant that did not opt in — it maintains
    ///     the persisted <c>RtPipelineStatistics</c>, which is not observability — but not one series
    ///     may reach the exporter. That is the whole reason the gate sits in the gauge callbacks
    ///     instead of at the <c>ObservePipeline</c> / <c>ObserveStatistics</c> call sites: the sweep
    ///     must not be skipped, only its publication.
    /// </summary>
    [Test]
    public async Task FoldAndPruneExecutionsAsync_OnATenantThatDidNotOptIn_SweepsButPublishesNothing()
    {
        // Arrange
        WorkloadObservabilityOptIn.Refresh(_tenantId, optedIn: false, TimeSpan.FromMinutes(5));

        var pipeline = RtEntityCreator.CreatePipeline();
        _repository.GetAllPipelinesAsync(_tenantId).Returns([pipeline]);
        _repository.GetTerminalExecutionsOlderThanAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime>(),
            Arg.Any<int>()).Returns([]);
        _repository.GetPipelineExecutionsAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>()).Returns([]);

        var statistics = RtEntityCreator.CreatePipelineStatistics();
        statistics.LastExecutionAt = DateTime.UtcNow.AddHours(-4);
        _repository.GetPipelineStatisticsAsync(_tenantId, pipeline.ToRtEntityId()).Returns(statistics);

        // Act
        var recorded = await CollectAsync(
            () => _service.FoldAndPruneExecutionsAsync(_tenantId, 1), observeGauges: true);

        // Assert — nothing on the wire, but the sweep did its persistence work.
        await Assert.That(recorded).IsEmpty();
        await _repository.Received().GetAllPipelinesAsync(_tenantId);
    }

    /// <summary>
    ///     AB#5492. The sweep is where the controller's knowledge of the schedule meets the
    ///     statistics: it reads the enabled triggers through the same query the scheduler uses and
    ///     hands each pipeline its own. The cron pipeline that stalled gets a count; the HTTP pipeline
    ///     next to it — idle for a month, which is fine for an HTTP pipeline — gets no cron series at
    ///     all, so the rule stays quiet for it.
    /// </summary>
    [Test]
    public async Task FoldAndPruneExecutionsAsync_PublishesMissedExecutionsOnlyForCronTriggeredPipelines()
    {
        // Arrange — the finAPI case: hourly cron, last execution three fire times ago.
        var cronPipeline = RtEntityCreator.CreatePipeline();
        cronPipeline.Name = "Accounting.FinApiImport";
        var httpPipeline = RtEntityCreator.CreatePipeline();
        httpPipeline.Name = "Accounting.UploadDocuments";

        var trigger = RtEntityCreator.CreatePipelineTrigger("0 * * * *");
        _repository.GetTriggersAndPipelinesAsync(_tenantId).Returns(
            new Dictionary<RtPipelineTrigger, IList<RtPipeline>> { [trigger] = [cronPipeline] });

        _repository.GetAllPipelinesAsync(_tenantId).Returns([cronPipeline, httpPipeline]);
        _repository.GetTerminalExecutionsOlderThanAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime>(),
            Arg.Any<int>()).Returns([]);
        _repository.GetPipelineExecutionsAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>()).Returns([]);

        var cronStatistics = RtEntityCreator.CreatePipelineStatistics();
        cronStatistics.LastExecutionAt = DateTime.UtcNow.Subtract(PipelineCronSchedule.Grace).AddHours(-3);
        _repository.GetPipelineStatisticsAsync(_tenantId, cronPipeline.ToRtEntityId()).Returns(cronStatistics);

        var httpStatistics = RtEntityCreator.CreatePipelineStatistics();
        httpStatistics.LastExecutionAt = DateTime.UtcNow.AddDays(-30);
        _repository.GetPipelineStatisticsAsync(_tenantId, httpPipeline.ToRtEntityId()).Returns(httpStatistics);

        // Act
        var recorded = await CollectAsync(
            () => _service.FoldAndPruneExecutionsAsync(_tenantId, 1), observeGauges: true);

        // Assert
        var missed = recorded.Where(r => r.Instrument == "octo.pipeline.cron.missed_executions").ToList();
        var cronSeries = missed.Single();
        await Assert.That(cronSeries.Value).IsEqualTo(3);
        await Assert.That(cronSeries.Tags["octo.pipeline.rt_id"]).IsEqualTo(cronPipeline.RtId.ToString());
        await Assert.That(cronSeries.Tags["octo.pipeline.name"]).IsEqualTo("Accounting.FinApiImport");
        await Assert.That(cronSeries.Tags["octo.cron.expression"]).IsEqualTo("0 * * * *");

        // …while the HTTP pipeline keeps its age (a month, legitimately) and nothing cron-shaped.
        await Assert.That(recorded.Any(r =>
            r.Instrument.StartsWith("octo.pipeline.cron.") &&
            r.Tags["octo.pipeline.rt_id"] == httpPipeline.RtId.ToString())).IsFalse();
        await Assert.That(recorded.Any(r =>
            r.Instrument == "octo.pipeline.execution.age" &&
            r.Tags["octo.pipeline.rt_id"] == httpPipeline.RtId.ToString())).IsTrue();
    }

    /// <summary>
    ///     A failing trigger read must neither stop the statistics sweep nor withdraw the schedule
    ///     published on the previous pass — a transient repository error is not "no triggers".
    /// </summary>
    [Test]
    public async Task FoldAndPruneExecutionsAsync_WhenTheTriggerReadFails_KeepsSweepingAndKeepsTheLastSchedule()
    {
        // Arrange — a pass that published the schedule, then a pass whose trigger read throws.
        var pipeline = RtEntityCreator.CreatePipeline();
        var trigger = RtEntityCreator.CreatePipelineTrigger("0 * * * *");
        _repository.GetTriggersAndPipelinesAsync(_tenantId).Returns(
            _ => new Dictionary<RtPipelineTrigger, IList<RtPipeline>> { [trigger] = [pipeline] },
            _ => throw new InvalidOperationException("mongo away"));

        _repository.GetAllPipelinesAsync(_tenantId).Returns([pipeline]);
        _repository.GetTerminalExecutionsOlderThanAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime>(),
            Arg.Any<int>()).Returns([]);
        _repository.GetPipelineExecutionsAsync(_tenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>()).Returns([]);

        var statistics = RtEntityCreator.CreatePipelineStatistics();
        statistics.LastExecutionAt = DateTime.UtcNow.Subtract(PipelineCronSchedule.Grace).AddHours(-2);
        _repository.GetPipelineStatisticsAsync(_tenantId, pipeline.ToRtEntityId()).Returns(statistics);

        await _service.FoldAndPruneExecutionsAsync(_tenantId, 1);

        // Act
        var recorded = await CollectAsync(
            () => _service.FoldAndPruneExecutionsAsync(_tenantId, 1), observeGauges: true);

        // Assert — second pass completed (statistics were read twice) and the series is still there.
        await _repository.Received(2).GetPipelineStatisticsAsync(_tenantId, pipeline.ToRtEntityId());
        await Assert.That(recorded.Single(r => r.Instrument == "octo.pipeline.cron.missed_executions").Value)
            .IsEqualTo(2);
    }
}
