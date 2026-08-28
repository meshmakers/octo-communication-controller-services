using System.Net;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Middleware;

/// <summary>
///     Wake-on-request for hibernated OnDemand workloads (AB#4923).
///
///     Adapter Ingresses carry <c>nginx.ingress.kubernetes.io/default-backend</c> pointing at this
///     service. nginx uses that backend exactly when the primary Service has no ready endpoint —
///     which for an OnDemand workload means it is scaled to zero. So this middleware only ever
///     sees traffic the adapter could not have answered anyway; steady-state requests go straight
///     to the adapter and never reach the controller.
///
///     It makes no authorization decisions and does not read the body: the routes and the auth
///     rules live inside the adapter (<c>HttpRequestService</c> / <c>FromHttpRequest@2</c>), and
///     nothing in front of it can decide either. The request is held during the wake and then
///     forwarded unchanged, so the adapter sees exactly what the client sent.
///
///     Runs before authentication and routing on purpose: the request belongs to the adapter's URL
///     space, so neither the controller's auth policies nor its route table apply to it. Requests
///     for any other host fall through untouched, which is a single dictionary miss.
/// </summary>
internal sealed class WorkloadActivatorMiddleware(
    RequestDelegate next,
    ILogger<WorkloadActivatorMiddleware> logger,
    IWorkloadHostnameIndex hostnameIndex,
    IWorkloadLifecycleService workloadLifecycleService,
    IHttpClientFactory httpClientFactory,
    IOptions<CommunicationControllerOptions> options)
{
    /// <summary>
    ///     Marks a request this middleware already forwarded. If the forwarded request comes back
    ///     here, the workload still has no ready endpoint and forwarding again would loop until
    ///     something times out.
    /// </summary>
    internal const string HopHeader = "X-Octo-Activator";

    internal const string HttpClientName = "WorkloadActivator";

    /// <summary>
    ///     Backoff shape for the wait between "the workload says it is awake" and "its Service
    ///     endpoint accepts connections" — see
    ///     <see cref="CommunicationControllerOptions.ActivatorForwardRetrySeconds"/> for why that
    ///     gap exists. Steps are taken from the front until the configured budget is spent, so the
    ///     budget alone decides how long the activator holds on.
    /// </summary>
    private static readonly TimeSpan[] ConnectRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5)];

    private static TimeSpan[] BuildRetryLadder(int budgetSeconds)
    {
        var budget = TimeSpan.FromSeconds(Math.Max(0, budgetSeconds));
        var ladder = new List<TimeSpan>();
        var spent = TimeSpan.Zero;
        foreach (var delay in ConnectRetryDelays)
        {
            if (spent + delay > budget)
            {
                break;
            }

            ladder.Add(delay);
            spent += delay;
        }

        return ladder.ToArray();
    }

    private static readonly string[] HopByHopHeaders =
    [
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Value.ActivatorEnabled ||
            !hostnameIndex.TryResolve(context.Request.Host.Host, out var target))
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey(HopHeader))
        {
            logger.LogWarning(
                "[{TenantId}] Activator loop for workload '{WorkloadName}': the forwarded request came back, " +
                "so the workload still has no ready endpoint", target.TenantId, target.WorkloadName);
            await WriteUnavailableAsync(context, target,
                "The workload did not become reachable after waking.");
            return;
        }

        try
        {
            await workloadLifecycleService.EnsureWorkloadRunningAsync(target.TenantId, target.WorkloadRtId);
        }
        catch (WorkloadLifecycleServiceException e)
        {
            // Wake failed or exceeded the budget. The workload is left scaled up for diagnosis
            // (see WorkloadLifecycleService), so a retry has a good chance of succeeding.
            logger.LogWarning(e, "[{TenantId}] Could not wake workload '{WorkloadName}' for an inbound request",
                target.TenantId, target.WorkloadName);
            await WriteUnavailableAsync(context, target, "The workload could not be woken in time.");
            return;
        }

        // A workload that is AlwaysOn, or on a tenant without scale-to-zero, makes the wake a no-op —
        // it is down for some other reason (image pull, crash loop). Forwarding still gives the
        // clearest possible answer: either it works, or the connection failure below names the
        // workload instead of leaving nginx's bare error page.
        try
        {
            await ForwardAsync(context, target);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up mid-forward; there is nobody left to answer.
        }
        catch (Exception e)
        {
            // Anything unexpected here would otherwise surface as a 500 from a service the caller
            // never addressed. 503 is both truthful and actionable — the workload is what is
            // unavailable, and the client's own retry is the right next step.
            logger.LogError(e, "[{TenantId}] Unexpected failure forwarding to workload '{WorkloadName}'",
                target.TenantId, target.WorkloadName);
            await WriteUnavailableAsync(context, target, "The workload could not be reached.");
        }
    }

    private async Task ForwardAsync(HttpContext context, ActivatorTarget target)
    {
        var request = context.Request;

        // Only bodyless requests are retried: the request stream is consumed by the first attempt
        // and cannot be replayed, and silently forwarding a truncated body would be worse than a
        // 503.
        var hasBody = request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding");
        var retryDelays = BuildRetryLadder(options.Value.ActivatorForwardRetrySeconds);
        var maxAttempts = hasBody ? 1 : retryDelays.Length + 1;
        var client = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 0; ; attempt++)
        {
            // A fresh message per attempt: HttpClient refuses to send the same HttpRequestMessage
            // twice, so a retry that reuses it fails with an InvalidOperationException instead of
            // reaching the workload.
            using var forwarded = BuildForwardRequest(context, target, hasBody);
            try
            {
                using var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted);
                await CopyResponseAsync(context, response);
                return;
            }
            catch (HttpRequestException e) when (attempt + 1 < maxAttempts)
            {
                logger.LogDebug(e,
                    "[{TenantId}] Workload '{WorkloadName}' not reachable yet (attempt {Attempt}); retrying",
                    target.TenantId, target.WorkloadName, attempt + 1);
                await Task.Delay(retryDelays[attempt], context.RequestAborted);
            }
            catch (HttpRequestException e)
            {
                logger.LogWarning(e, "[{TenantId}] Forwarding to workload '{WorkloadName}' at '{Address}' failed",
                    target.TenantId, target.WorkloadName, target.Address);
                await WriteUnavailableAsync(context, target, "The workload is awake but not reachable.");
                return;
            }
        }
    }

    private static HttpRequestMessage BuildForwardRequest(HttpContext context, ActivatorTarget target, bool hasBody)
    {
        var request = context.Request;
        var forwarded = new HttpRequestMessage(new HttpMethod(request.Method),
            new Uri(target.Address, request.Path + request.QueryString));

        if (hasBody)
        {
            forwarded.Content = new StreamContent(request.Body);
        }

        foreach (var header in request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Content headers belong on the content, everything else on the request. Both adds are
            // unvalidated: a header the client sent has already been accepted by nginx and Kestrel,
            // and rejecting it here would lose the request over a formatting opinion.
            if (!forwarded.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
            {
                forwarded.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
            }
        }

        // Keep the original Host so the adapter sees the URL the client used, and mark the request
        // so a second pass through here is recognised as a loop rather than forwarded again.
        forwarded.Headers.Host = request.Host.Value;
        forwarded.Headers.TryAddWithoutValidation(HopHeader, "1");
        return forwarded;
    }

    private static async Task CopyResponseAsync(HttpContext context, HttpResponseMessage response)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.Headers[header.Key] = new StringValues(header.Value.ToArray());
        }

        // Kestrel sets this from the body it actually writes; a copied value would be wrong the
        // moment the response is chunked.
        context.Response.Headers.Remove("Content-Length");

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private async Task WriteUnavailableAsync(HttpContext context, ActivatorTarget target, string reason)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
        // The wake budget is how long the next attempt would have to wait in the worst case, so it
        // is the honest hint for a client that wants to retry.
        context.Response.Headers.RetryAfter = options.Value.LifecycleWakeBudgetSeconds.ToString();
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            $"{reason} Workload '{target.WorkloadName}' of tenant '{target.TenantId}' is not available right now.",
            context.RequestAborted);
    }
}
