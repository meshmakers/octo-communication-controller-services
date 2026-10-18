using MassTransit;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.PipelineMethodTestSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     AB#6879: the controller announces itself as handler of the pipeline-handled methods that it can really serve,
///     only after the endpoint accepts messages, and says so loudly for the ones it cannot.
/// </summary>
internal class PipelineMethodHostTests
{
    private readonly IEventHubControl _control = Substitute.For<IEventHubControl>();
    private readonly IDistributionEventHubService _eventHub = Substitute.For<IDistributionEventHubService>();
    private readonly IPipelineMethodCatalog _catalog = Substitute.For<IPipelineMethodCatalog>();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly CapturingLogger<PipelineMethodHost> _logger = new();
    private readonly List<string> _events = [];
    private readonly List<CkMethodHandlerAnnounced> _announced = [];
    private readonly List<CkMethodHandlerWithdrawn> _withdrawn = [];
    private readonly Dictionary<string, HostReceiveEndpointHandle> _handles = new();
    private readonly PipelineMethodOptions _options = new() { EndpointReadyTimeout = TimeSpan.FromMilliseconds(300) };
    private readonly PipelineMethodHandlerInstance _instance;
    private readonly PipelineMethodHost _host;
    private TaskCompletionSource<ReceiveEndpointReady>? _readyGate;

    public PipelineMethodHostTests()
    {
        var options = Options.Create(_options);
        _instance = new PipelineMethodHandlerInstance(options, TimeProvider.System);
        var consumer = new PipelineMethodBusConsumer(Substitute.For<IPipelineMethodInvoker>(), _instance,
            TimeProvider.System, new CapturingLogger<PipelineMethodBusConsumer>());
        _host = new PipelineMethodHost(_control, _eventHub, _catalog, _repository, _instance, consumer, options, _logger);

        _control.RegisterCommandConsumer(Arg.Any<string>(), Arg.Any<ExecuteDeliveredCommandHandler<CkMethodRequest>>(),
            Arg.Any<CommandConsumerOptions>()).Returns(call =>
        {
            var queue = call.ArgAt<string>(0);
            _events.Add($"register {queue}");
            var handle = Substitute.For<HostReceiveEndpointHandle>();
            handle.Ready.Returns(_ =>
            {
                if (_readyGate is not null)
                {
                    return _readyGate.Task;
                }

                _events.Add($"ready {queue}");
                return Task.FromResult(default(ReceiveEndpointReady)!);
            });
            _handles[queue] = handle;
            return new EndpointHandle(handle);
        });
        _eventHub.PublishAsync(Arg.Any<CkMethodHandlerAnnounced>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _events.Add("announce");
            _announced.Add(call.Arg<CkMethodHandlerAnnounced>());
            return Task.CompletedTask;
        });
        _eventHub.PublishAsync(Arg.Any<CkMethodHandlerWithdrawn>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _events.Add("withdraw");
            _withdrawn.Add(call.Arg<CkMethodHandlerWithdrawn>());
            return Task.CompletedTask;
        });
    }

    private void StrictMode() => _options.FailStartupOnInvalidMethod = true;

    private void BlockEndpoints() => _readyGate = new TaskCompletionSource<ReceiveEndpointReady>();

    private void ArrangeCatalog(params PipelineMethodDefinition[] methods) =>
        _catalog.GetPipelineMethods().Returns(methods);

    private void ArrangePipeline(string methodId, RtDeploymentStateEnum state = RtDeploymentStateEnum.Deployed)
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        pipeline.DeploymentState = state;
        _repository.GetPipelineByWellKnownNameAsync(TenantId, methodId).Returns(pipeline);
    }

    [Test]
    public async Task AMethodWithADeployedPipelineIsAnnouncedAfterItsEndpointIsReady()
    {
        ArrangeCatalog(Method());
        ArrangePipeline(MethodId);

        await _host.ReconcileAsync(true);

        var queue = CkMethodQueueNames.For(MethodId);
        await Assert.That(_events).IsEquivalentTo([$"register {queue}", $"ready {queue}", "announce"]);
        await Assert.That(_announced.Single().MethodIds).IsEquivalentTo([MethodId]);
        await Assert.That(_announced.Single().ServiceName).IsEqualTo("communication-controller-services");
        await Assert.That(_announced.Single().InstanceId).IsEqualTo(_instance.InstanceId);
    }

    [Test]
    public async Task NothingIsAnnouncedWhileTheEndpointDoesNotAcceptMessages()
    {
        BlockEndpoints();
        ArrangeCatalog(Method());
        ArrangePipeline(MethodId);

        await _host.ReconcileAsync(false);

        await Assert.That(_announced).IsEmpty();
        await Assert.That(_instance.MethodIds).IsEmpty();
        await Assert.That(_logger.All).Contains("did not accept messages");
    }

    [Test]
    public async Task AMethodWithoutItsPipelineIsNotAnnouncedAndTheErrorNamesMethodTenantAndPipeline()
    {
        ArrangeCatalog(Method());
        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns((RtPipeline?)null);

        await _host.ReconcileAsync(false);

        await Assert.That(_announced).IsEmpty();
        await Assert.That(_events).IsEmpty();
        await Assert.That(_logger.All).Contains("[Error]");
        await Assert.That(_logger.All).Contains(MethodId);
        await Assert.That(_logger.All).Contains(TenantId);
        await Assert.That(_logger.All).Contains("pipeline-not-deployed");
    }

    [Test]
    public async Task AnUndeployedPipelineDoesNotCount()
    {
        ArrangeCatalog(Method());
        ArrangePipeline(MethodId, RtDeploymentStateEnum.Undeployed);

        await _host.ReconcileAsync(false);

        await Assert.That(_announced).IsEmpty();
    }

    [Test]
    public async Task AMethodWithSensitiveParametersIsNeverAnnounced()
    {
        ArrangeCatalog(Method(sensitiveParameter: true));
        ArrangePipeline(MethodId);

        await _host.ReconcileAsync(false);

        await Assert.That(_announced).IsEmpty();
        await Assert.That(_events).IsEmpty();
        await Assert.That(_logger.All).Contains("sensitive-parameters-not-supported");
        await Assert.That(_logger.All).Contains("signingKey");
        await _repository.DidNotReceiveWithAnyArgs().GetPipelineByWellKnownNameAsync(default!, default!);
    }

    [Test]
    public async Task StrictModeFailsTheStartupAndNamesEveryOffendingMethod()
    {
        StrictMode();
        const string other = "Acme.Assets/Device.Reboot-1";
        ArrangeCatalog(Method(), Method(sensitiveParameter: true, methodId: other));
        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns((RtPipeline?)null);

        var exception = await Assert.That(async () => await _host.ReconcileAsync(true))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains(MethodId);
        await Assert.That(exception.Message).Contains(other);
        await Assert.That(exception.Message).Contains("pipeline-not-deployed");
        await Assert.That(exception.Message).Contains("sensitive-parameters-not-supported");
    }

    [Test]
    public async Task StrictModeDoesNotFailLaterHeartbeats()
    {
        StrictMode();
        ArrangeCatalog(Method());
        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns((RtPipeline?)null);

        await _host.ReconcileAsync(false);

        await Assert.That(_announced).IsEmpty();
    }

    [Test]
    public async Task EachHeartbeatReannouncesButLogsAProblemOnlyOnce()
    {
        ArrangeCatalog(Method(), Method(methodId: "Acme.Assets/Device.Reboot-1"));
        ArrangePipeline(MethodId);
        _repository.GetPipelineByWellKnownNameAsync(TenantId, "Acme.Assets/Device.Reboot-1")
            .Returns((RtPipeline?)null);

        await _host.ReconcileAsync(true);
        await _host.ReconcileAsync(false);
        await _host.ReconcileAsync(false);

        await Assert.That(_announced.Count).IsEqualTo(3);
        await Assert.That(_announced.All(a => a.MethodIds.Count == 1)).IsTrue();
        await Assert.That(_logger.Entries.Count(e => e.Contains("[Error]"))).IsEqualTo(1);
        // the endpoint is created once
        await Assert.That(_events.Count(e => e.StartsWith("register"))).IsEqualTo(1);
    }

    [Test]
    public async Task AMethodWhosePipelineDisappearsIsWithdrawnAndItsEndpointRemoved()
    {
        ArrangeCatalog(Method());
        ArrangePipeline(MethodId);
        await _host.ReconcileAsync(true);

        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId).Returns((RtPipeline?)null);
        await _host.ReconcileAsync(false);

        await Assert.That(_withdrawn.Single().MethodIds).IsEquivalentTo([MethodId]);
        await Assert.That(_instance.MethodIds).IsEmpty();
        await _handles[CkMethodQueueNames.For(MethodId)].Received(1).StopAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AStoreErrorKeepsTheCurrentStateInsteadOfWithdrawing()
    {
        ArrangeCatalog(Method());
        ArrangePipeline(MethodId);
        await _host.ReconcileAsync(true);

        _repository.GetPipelineByWellKnownNameAsync(TenantId, MethodId)
            .Returns<Task<RtPipeline?>>(_ => throw new InvalidOperationException("mongo down"));
        await _host.ReconcileAsync(false);

        await Assert.That(_withdrawn).IsEmpty();
        await Assert.That(_instance.MethodIds).IsEquivalentTo([MethodId]);
    }

    [Test]
    public async Task GracefulShutdownWithdrawsBeforeTheEndpointsAreRemoved()
    {
        ArrangeCatalog(Method());
        ArrangePipeline(MethodId);
        await _host.ReconcileAsync(true);

        await _host.WithdrawAllAsync(CancellationToken.None);

        var withdrawIndex = _events.IndexOf("withdraw");
        await Assert.That(withdrawIndex).IsGreaterThan(-1);
        await Assert.That(_withdrawn.Single().InstanceId).IsEqualTo(_instance.InstanceId);
        await _handles[CkMethodQueueNames.For(MethodId)].Received(1).StopAsync(Arg.Any<CancellationToken>());
        // no announcement after the withdrawal
        await _host.ReconcileAsync(false);
        await Assert.That(_announced.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ADiscoveryRequestIsAnsweredWithAnImmediateAnnouncementOnlyWhenSomethingIsServed()
    {
        await _host.AnnounceNowAsync();
        await Assert.That(_announced).IsEmpty();

        ArrangeCatalog(Method());
        ArrangePipeline(MethodId);
        await _host.ReconcileAsync(true);
        await _host.AnnounceNowAsync();

        await Assert.That(_announced.Count).IsEqualTo(2);
    }
}
