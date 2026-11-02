using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#8145 — when identity restarts together with the controller, the startup sweep's single
/// identity request times out and nothing used to retry. The service now remembers such adapters and
/// retries them with backoff. Transient (identity does not answer) is retried; a refusal is not.
/// </summary>
internal class PipelineServiceAccountProvisioningRetryTests
{
    private const string TenantId = "tenantId";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();

    private readonly ICommandClient<CreateIdentityDataCommandRequest> _commandClient =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();
    private readonly MutableTimeProvider _time = new();
    private readonly CommunicationControllerOptions _options = new()
    {
        AuthorityUrl = "https://identity.example.com",
        PublicUrl = "https://communication.example.com",
        PipelineServiceAccountRetryInitialDelaySeconds = 30,
        PipelineServiceAccountRetryMaxDelayMinutes = 5,
        PipelineServiceAccountRetryMaxAttempts = 4
    };

    private readonly PipelineServiceAccountProvisioningService _sut;

    public PipelineServiceAccountProvisioningRetryTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_commandClient);

        _sut = new PipelineServiceAccountProvisioningService(
            _repository,
            new PipelineServiceAccountResolver(_repository),
            services.BuildServiceProvider(),
            _eventService,
            Options.Create(_options),
            TestSecretEncryption.Create().EncryptionService,
            _time);
    }

    private int IdentityCalls => _commandClient.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse));

    private void IdentityTimesOut() =>
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns<EnumCommandResponse<CreateIdentityDataResult>>(
                _ => throw new TimeoutException("identity did not answer within 30 seconds"));

    private void IdentityAnswers(CreateIdentityDataResult result) =>
        _commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult> { Response = result });

    private void AdapterExists(RtAdapter adapter) =>
        _repository.GetAdaptersAsync(TenantId).Returns(new[] { adapter });

    [Test]
    public async Task TransientFailure_IsRememberedAndRetriedUntilIdentityIsBack()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        AdapterExists(adapter);
        IdentityTimesOut();

        // Startup sweep: single shot, fails, adapter is remembered.
        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Failed);
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(1);
        await Assert.That(IdentityCalls).IsEqualTo(1);

        // Before the backoff elapsed nothing is sent.
        _time.Advance(TimeSpan.FromSeconds(29));
        await Assert.That(await _sut.RetryPendingAsync(CancellationToken.None)).IsEqualTo(0);
        await Assert.That(IdentityCalls).IsEqualTo(1);

        // Retry 1 (30 s): identity still down.
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await _sut.RetryPendingAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That(IdentityCalls).IsEqualTo(2);
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(1);

        // Identity is back; the next retry is due after the doubled delay (60 s).
        IdentityAnswers(CreateIdentityDataResult.Success);
        _time.Advance(TimeSpan.FromSeconds(60));
        await Assert.That(await _sut.RetryPendingAsync(CancellationToken.None)).IsEqualTo(1);

        using var _ = Assert.Multiple();
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(0);
        await _repository.ReceivedWithAnyArgs(1).SavePipelineServiceAccountAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtServiceAccountConfiguration>(), Arg.Any<bool>());
        // Recovery is recorded once as an information event.
        await _eventService.ReceivedWithAnyArgs(1).StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task Backoff_DoublesAndIsCappedAtTheMaximum()
    {
        await Assert.That(_sut.GetRetryDelay(1)).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(_sut.GetRetryDelay(2)).IsEqualTo(TimeSpan.FromSeconds(60));
        await Assert.That(_sut.GetRetryDelay(3)).IsEqualTo(TimeSpan.FromSeconds(120));
        await Assert.That(_sut.GetRetryDelay(4)).IsEqualTo(TimeSpan.FromSeconds(240));
        await Assert.That(_sut.GetRetryDelay(5)).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(_sut.GetRetryDelay(500)).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task PermanentRefusal_IsNotRetriedAndStillReportsOnce()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        AdapterExists(adapter);
        IdentityAnswers(CreateIdentityDataResult.FailedTenantHasNoIdentityCk);

        var outcome = await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        _time.Advance(TimeSpan.FromHours(1));
        var retried = await _sut.RetryPendingAsync(CancellationToken.None);

        using var _ = Assert.Multiple();
        await Assert.That(outcome).IsEqualTo(PipelineServiceAccountProvisioningOutcome.Failed);
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(0);
        await Assert.That(retried).IsEqualTo(0);
        await Assert.That(IdentityCalls).IsEqualTo(1);
        await _eventService.ReceivedWithAnyArgs(1).StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeletedAdapter_StopsTheRetry()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        IdentityTimesOut();
        await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        _repository.GetAdaptersAsync(TenantId).Returns(Array.Empty<RtAdapter>());
        _time.Advance(TimeSpan.FromMinutes(1));
        await _sut.RetryPendingAsync(CancellationToken.None);

        using var _ = Assert.Multiple();
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(0);
        await Assert.That(IdentityCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DeletedTenant_AdapterLookupFailureCountsAsAttemptAndEventuallyStops()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        IdentityTimesOut();
        await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        _repository.GetAdaptersAsync(TenantId)
            .Returns<IReadOnlyCollection<RtAdapter>>(_ => throw new InvalidOperationException("tenant unloaded"));

        for (var i = 0; i < 10 && _sut.PendingRetryCount > 0; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(6));
            await _sut.RetryPendingAsync(CancellationToken.None);
        }

        await Assert.That(_sut.PendingRetryCount).IsEqualTo(0);
        await Assert.That(IdentityCalls).IsEqualTo(1);
    }

    [Test]
    public async Task RetriesAreBounded_AfterTheAttemptBudgetTheAdapterIsDropped()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        AdapterExists(adapter);
        IdentityTimesOut();
        await _sut.EnsureAdapterProvisionedAsync(TenantId, adapter);

        for (var i = 0; i < 20 && _sut.PendingRetryCount > 0; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(6));
            await _sut.RetryPendingAsync(CancellationToken.None);
        }

        using var _ = Assert.Multiple();
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(0);
        // 1 startup attempt + MaxAttempts (4) retries.
        await Assert.That(IdentityCalls).IsEqualTo(1 + _options.PipelineServiceAccountRetryMaxAttempts);
    }

    [Test]
    public async Task TenantSweep_StaysSingleShot_AndRemembersTheFailedAdapter()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        AdapterExists(adapter);
        IdentityTimesOut();

        var report = await _sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.Failures.Count).IsEqualTo(1);
        // No in-line retry: exactly one identity request during the sweep.
        await Assert.That(IdentityCalls).IsEqualTo(1);
        await Assert.That(_sut.PendingRetryCount).IsEqualTo(1);
    }

    [Test]
    public async Task BackgroundService_TicksTheServiceOnlyWhenSomethingIsPending()
    {
        var service = Substitute.For<IPipelineServiceAccountProvisioningService>();
        service.PendingRetryCount.Returns(0, 1);
        service.RetryPendingAsync(Arg.Any<CancellationToken>()).Returns(1);
        var sut = new PipelineServiceAccountProvisioningRetryBackgroundService(service);

        using var cts = new CancellationTokenSource();
        await sut.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline &&
               service.ReceivedCalls().All(c => c.GetMethodInfo().Name != nameof(service.RetryPendingAsync)))
        {
            await Task.Delay(200);
        }

        await sut.StopAsync(CancellationToken.None);

        await service.Received().RetryPendingAsync(Arg.Any<CancellationToken>());
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
