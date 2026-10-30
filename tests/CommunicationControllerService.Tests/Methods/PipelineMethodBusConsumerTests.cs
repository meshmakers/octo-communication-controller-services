using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.PipelineMethodTestSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     AB#6879: a redelivered request may have started the pipeline before: it is not started again. Same rule as the
///     handler SDK of the bus transport.
/// </summary>
internal class PipelineMethodBusConsumerTests
{
    private readonly IPipelineMethodInvoker _invoker = Substitute.For<IPipelineMethodInvoker>();
    private readonly PipelineMethodBusConsumer _consumer;
    private readonly PipelineMethodHandlerInstance _instance;

    public PipelineMethodBusConsumerTests()
    {
        _instance = new PipelineMethodHandlerInstance(Options.Create(new PipelineMethodOptions()), TimeProvider.System);
        _consumer = new PipelineMethodBusConsumer(_invoker, _instance, TimeProvider.System,
            new CapturingLogger<PipelineMethodBusConsumer>());
        _invoker.InvokeAsync(Arg.Any<CkMethodRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            CkMethodResponse.Succeeded(call.Arg<CkMethodRequest>().InvocationId, _instance.InstanceId, "{\"value\":1}"));
    }

    [Test]
    public async Task AFreshRequestIsExecuted()
    {
        var response = await _consumer.HandleAsync(Request());

        await Assert.That(response.Success).IsTrue();
    }

    [Test]
    public async Task ABrokerRedeliveryIsNotExecutedAndAnswersOutcomeUnknown()
    {
        var delivery = new CommandDeliveryInfo(true, 0, 1, Guid.NewGuid());

        var response = await _consumer.HandleAsync(Request(), delivery);

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodOutcomeUnknown);
        await Assert.That(response.Error.Parameters["methodId"]).IsEqualTo(MethodId);
        await _invoker.DidNotReceiveWithAnyArgs().InvokeAsync(default!);
    }

    [Test]
    public async Task ARepeatedInvocationIdIsExecutedOnlyOnce()
    {
        var request = Request();

        var first = await _consumer.HandleAsync(request);
        var second = await _consumer.HandleAsync(request);

        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodOutcomeUnknown);
        await _invoker.Received(1).InvokeAsync(Arg.Any<CkMethodRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFarFutureDeadlineDoesNotThrowAndTheSecondDeliveryIsRefused()
    {
        // A non-dispatcher sender may put DateTime.MaxValue on the wire; deadline + retention used to overflow.
        var request = Request() with { DeadlineUtc = DateTime.MaxValue };

        var first = await _consumer.HandleAsync(request);
        var second = await _consumer.HandleAsync(request);

        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodOutcomeUnknown);
        await _invoker.Received(1).InvokeAsync(Arg.Any<CkMethodRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AFarFutureDeadlineWithABrokerDeliveryIsExecutedOnlyOnce()
    {
        // the production handler always passes the bus delivery info
        var request = Request() with { DeadlineUtc = DateTime.MaxValue };
        var delivery = new CommandDeliveryInfo(false, 0, 0, null);

        var first = await _consumer.HandleAsync(request, delivery);
        var second = await _consumer.HandleAsync(request, delivery);

        await Assert.That(first.Success).IsTrue();
        await Assert.That(second.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodOutcomeUnknown);
        await _invoker.Received(1).InvokeAsync(Arg.Any<CkMethodRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnInvokerThatThrowsNeverReachesTheBus()
    {
        // a thrown consumer makes the bus retry, i.e. start the pipeline again
        _invoker.InvokeAsync(Arg.Any<CkMethodRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CkMethodResponse>>(_ => throw new InvalidOperationException("boom"));

        var response = await _consumer.HandleAsync(Request());

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
    }

    [Test]
    public async Task AFailingResponseSendIsSwallowedSoTheBusDoesNotRetry()
    {
        var handler = _consumer.CreateHandler();

        await handler(Request(), new CommandDeliveryInfo(false, 0, 0, null),
            _ => throw new InvalidOperationException("channel closed"));
    }
}
