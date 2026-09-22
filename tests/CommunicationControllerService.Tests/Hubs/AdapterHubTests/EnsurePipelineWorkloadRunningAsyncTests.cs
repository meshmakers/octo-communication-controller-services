using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterHubTests;

/// <summary>
///     AB#5231: the send-path wake gate an adapter calls before publishing a pipeline data event to a
///     pipeline on another workload. Tenant-bound: the gate runs for the connection's own tenant.
/// </summary>
internal class EnsurePipelineWorkloadRunningAsyncTests : IDisposable
{
    private const string TenantId = "acme";

    private readonly IWorkloadLifecycleService _lifecycle = Substitute.For<IWorkloadLifecycleService>();
    private readonly AdapterHub _hub;

    public EnsurePipelineWorkloadRunningAsyncTests()
    {
        _hub = new AdapterHub(Substitute.For<IAdapterService>(), Substitute.For<IPipelineDebugService>(),
            Substitute.For<ICommunicationEventService>(), Substitute.For<IPipelineExecutionService>(),
            Substitute.For<IPipelineExecutionReportQueue>(), Substitute.For<IShutdownState>(), _lifecycle);
    }

    private void GivenConnectionOfTenant(string tenantId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = tenantId;
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new HttpContextFeature(httpContext));
        var context = Substitute.For<HubCallerContext>();
        context.Features.Returns(features);
        _hub.Context = context;
    }

    public void Dispose() => _hub.Dispose();

    /// <summary>What <c>HubCallerContext.GetHttpContext()</c> reads: the request the connection came in on.</summary>
    private sealed class HttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    [Test]
    public async Task WakesTheWorkloadOfTheNamedPipeline_ForTheConnectionsOwnTenant()
    {
        GivenConnectionOfTenant(TenantId);
        var pipelineRtId = OctoObjectId.GenerateNewId();

        await _hub.EnsurePipelineWorkloadRunningAsync(new RtEntityId("System.Communication/Pipeline", pipelineRtId));

        await _lifecycle.Received(1).EnsureWorkloadRunningForPipelineAsync(TenantId, pipelineRtId);
    }
}
