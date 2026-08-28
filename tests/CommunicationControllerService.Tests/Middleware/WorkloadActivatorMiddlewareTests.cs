using System.Net;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Middleware;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Middleware;

/// <summary>
///     AB#4923. The activator only ever sees requests the workload could not answer itself, so every
///     path through it is a path the client is currently blocked on.
/// </summary>
internal class WorkloadActivatorMiddlewareTests
{
    private const string TenantId = "acme";
    private const string Host = "adapter-acme.test-2.mm.cloud";

    private readonly IWorkloadHostnameIndex _index = Substitute.For<IWorkloadHostnameIndex>();
    private readonly IWorkloadLifecycleService _lifecycle = Substitute.For<IWorkloadLifecycleService>();
    private readonly ActivatorTarget _target = new(TenantId, OctoObjectId.GenerateNewId(), "Mesh Adapter",
        new Uri("http://acme-workload"));

    /// <summary>Replays a scripted sequence of outcomes, one per attempt.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var step = steps[Math.Min(Calls, steps.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }
    }

    private (WorkloadActivatorMiddleware Middleware, DefaultHttpContext Context, bool[] NextCalled) Build(
        HttpMessageHandler handler, bool indexHit = true, bool activatorEnabled = true)
    {
        _index.TryResolve(Arg.Any<string?>(), out Arg.Any<ActivatorTarget?>())
            .Returns(x =>
            {
                x[1] = indexHit ? _target : null;
                return indexHit;
            });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(WorkloadActivatorMiddleware.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var nextCalled = new[] { false };
        var middleware = new WorkloadActivatorMiddleware(
            _ =>
            {
                nextCalled[0] = true;
                return Task.CompletedTask;
            },
            NullLogger<WorkloadActivatorMiddleware>.Instance, _index, _lifecycle, factory,
            Options.Create(new CommunicationControllerOptions
            {
                ActivatorEnabled = activatorEnabled, LifecycleWakeBudgetSeconds = 42,
            }));

        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(Host);
        context.Request.Method = "GET";
        context.Request.Path = "/acme/route";
        context.Response.Body = new MemoryStream();
        return (middleware, context, nextCalled);
    }

    private static HttpResponseMessage Ok(string body = "from the adapter") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    [Test]
    public async Task RequestForAHibernatedWorkload_IsWokenAndForwarded()
    {
        // Arrange
        var handler = new ScriptedHandler(() => Ok());
        var (middleware, context, nextCalled) = Build(handler);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await _lifecycle.Received(1).EnsureWorkloadRunningAsync(TenantId, _target.WorkloadRtId);
        await Assert.That(context.Response.StatusCode).IsEqualTo(200);
        await Assert.That(nextCalled[0]).IsFalse();
    }

    /// <summary>
    ///     Endpoints appear in kube-proxy a moment after the adapter reports itself configured, so the
    ///     first forward can still be refused. This is also the regression guard for reusing one
    ///     <c>HttpRequestMessage</c> across attempts, which fails with "the request message was
    ///     already sent" and surfaced as a 500 on the very first live request.
    /// </summary>
    [Test]
    public async Task ConnectionRefusedOnTheFirstAttempt_IsRetriedWithAFreshRequest()
    {
        // Arrange
        var handler = new ScriptedHandler(
            () => throw new HttpRequestException("connection refused"),
            () => Ok());
        var (middleware, context, _) = Build(handler);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await Assert.That(handler.Calls).IsEqualTo(2);
        await Assert.That(context.Response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task WorkloadThatStaysUnreachable_Answers503WithRetryAfter()
    {
        // Arrange
        var handler = new ScriptedHandler(() => throw new HttpRequestException("connection refused"));
        var (middleware, context, _) = Build(handler);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await Assert.That(context.Response.StatusCode).IsEqualTo(503);
        await Assert.That(context.Response.Headers.RetryAfter.ToString()).IsEqualTo("42");
    }

    [Test]
    public async Task FailedWake_Answers503AndNeverForwards()
    {
        // Arrange
        var handler = new ScriptedHandler(() => Ok());
        var (middleware, context, _) = Build(handler);
        _lifecycle.EnsureWorkloadRunningAsync(TenantId, _target.WorkloadRtId)
            .Returns(_ => throw WorkloadLifecycleServiceException.WakeTimedOut(TenantId, _target.WorkloadRtId, "Mesh Adapter", TimeSpan.FromSeconds(42)));

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await Assert.That(context.Response.StatusCode).IsEqualTo(503);
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    /// <summary>
    ///     A forwarded request that comes back means the workload still has no ready endpoint.
    ///     Forwarding again would loop until something times out.
    /// </summary>
    [Test]
    public async Task ForwardedRequestComingBack_IsAnswered503RatherThanForwardedAgain()
    {
        // Arrange
        var handler = new ScriptedHandler(() => Ok());
        var (middleware, context, _) = Build(handler);
        context.Request.Headers[WorkloadActivatorMiddleware.HopHeader] = "1";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await Assert.That(context.Response.StatusCode).IsEqualTo(503);
        await Assert.That(handler.Calls).IsEqualTo(0);
        await _lifecycle.DidNotReceive().EnsureWorkloadRunningAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task RequestForAnyOtherHost_FallsThroughUntouched()
    {
        // Arrange — the controller's own API is the common case and must not pay for this feature.
        var handler = new ScriptedHandler(() => Ok());
        var (middleware, context, nextCalled) = Build(handler, indexHit: false);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await Assert.That(nextCalled[0]).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task DisabledActivator_FallsThroughWithoutEvenResolvingTheHost()
    {
        // Arrange
        var handler = new ScriptedHandler(() => Ok());
        var (middleware, context, nextCalled) = Build(handler, activatorEnabled: false);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        await Assert.That(nextCalled[0]).IsTrue();
        await _lifecycle.DidNotReceive().EnsureWorkloadRunningAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }
}
