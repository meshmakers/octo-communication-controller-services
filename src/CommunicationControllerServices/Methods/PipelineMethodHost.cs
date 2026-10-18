using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     Hosts the controller as the handler of every CK method declared <c>handler: pipeline</c> (AB#6879): connects one
///     shared command endpoint per such method, waits until it accepts messages, announces the instance and keeps
///     the set current.
/// </summary>
/// <remarks>
///     <para>
///         The handler SDK of common-services registers handlers statically, at service configuration time. The
///         methods of the controller are only known at run time: they are declared by the CK models of the tenants,
///         and pipelines are deployed later. The host therefore reconciles (at startup and on every heartbeat): a
///         method becomes announced when it is declared <c>handler: pipeline</c>, has no sensitive parameter and a
///         deployed pipeline with the method id as well-known name exists in a tenant; it is withdrawn when none is
///         left. It uses the same wire contract, queue names, shared endpoints and announcement events as the SDK.
///     </para>
///     <para>
///         A method that cannot be announced is logged as ERROR with the method, the tenant and the reason (once per
///         change, not every heartbeat); with <see cref="PipelineMethodOptions.FailStartupOnInvalidMethod" /> the
///         startup fails with a message that names all of them. A call to such a method is answered
///         <c>METHOD_HANDLER_NOT_AVAILABLE</c> either way.
///     </para>
/// </remarks>
internal sealed class PipelineMethodHost(
    IEventHubControl eventHubControl,
    IDistributionEventHubService eventHub,
    IPipelineMethodCatalog catalog,
    ICommunicationRepository repository,
    PipelineMethodHandlerInstance instance,
    PipelineMethodBusConsumer consumer,
    IOptions<PipelineMethodOptions> options,
    ILogger<PipelineMethodHost> logger)
{
    // Method queues carry caller tokens: no retry, no Fault<T> with the request, no _error/_skipped copies.
    private static readonly CommandConsumerOptions EndpointOptions = new() { SuppressFaults = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _loggedProblems = new(StringComparer.Ordinal);

    /// <summary>
    ///     Looks at the pipeline methods of all tenants, connects and announces the ones that can be served and
    ///     withdraws the ones that cannot any longer.
    /// </summary>
    /// <param name="startup">True for the run at service startup (strict mode may fail the startup).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task ReconcileAsync(bool startup, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (instance.Withdrawn)
            {
                return;
            }

            var problems = new List<string>();
            var ready = new HashSet<string>(StringComparer.Ordinal);
            var seenProblems = new HashSet<string>(StringComparer.Ordinal);

            foreach (var method in catalog.GetPipelineMethods())
            {
                var problem = await CheckAsync(method, cancellationToken).ConfigureAwait(false);
                if (problem is null)
                {
                    ready.Add(method.MethodId);
                    continue;
                }

                var key = $"{method.TenantId}|{method.MethodId}";
                seenProblems.Add(key);
                var text = $"CK method '{method.MethodId}' in tenant '{method.TenantId}' is declared handler: pipeline but " +
                           $"cannot be served: {problem}";
                problems.Add(text);
                if (!_loggedProblems.TryGetValue(key, out var logged) || logged != problem)
                {
                    _loggedProblems[key] = problem;
                    logger.LogError("{Problem}. It is not announced; calls are answered METHOD_HANDLER_NOT_AVAILABLE.",
                        text);
                }
            }

            foreach (var stale in _loggedProblems.Keys.Where(k => !seenProblems.Contains(k)).ToList())
            {
                _loggedProblems.Remove(stale);
            }

            if (startup && options.Value.FailStartupOnInvalidMethod && problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "Pipeline method handler cannot start: " + string.Join("; ", problems));
            }

            await ConnectAsync(ready, startup, cancellationToken).ConfigureAwait(false);
            await WithdrawGoneAsync(ready, cancellationToken).ConfigureAwait(false);

            var announced = instance.MethodIds;
            if (announced.Count > 0)
            {
                await eventHub.PublishAsync(instance.CreateAnnouncement(announced), cancellationToken)
                    .ConfigureAwait(false);
                logger.LogDebug("CK pipeline method handler {InstanceId} announced {MethodCount} method(s)",
                    instance.InstanceId, announced.Count);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publishes the announcement now (reply to a dispatcher's discovery request).</summary>
    public async Task AnnounceNowAsync(CancellationToken cancellationToken = default)
    {
        // under the gate: an announcement must never follow the withdrawal of a shutdown
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var announced = instance.MethodIds;
            if (instance.Withdrawn || announced.Count == 0)
            {
                return;
            }

            await eventHub.PublishAsync(instance.CreateAnnouncement(announced), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publishes the withdrawal and removes all endpoints (graceful shutdown).</summary>
    public async Task WithdrawAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var methodIds = instance.MethodIds;
            var endpoints = instance.TakeAllEndpoints();
            if (methodIds.Count > 0)
            {
                try
                {
                    await eventHub.PublishAsync(instance.CreateWithdrawal(methodIds), cancellationToken)
                        .ConfigureAwait(false);
                    logger.LogInformation("CK pipeline method handler {InstanceId} withdrawn", instance.InstanceId);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Publishing the CK pipeline method handler withdrawal failed");
                }
            }

            foreach (var (_, handle) in endpoints)
            {
                await DisposeQuietlyAsync(handle).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> CheckAsync(PipelineMethodDefinition method, CancellationToken cancellationToken)
    {
        if (method.RefusalReason is { } refusal)
        {
            return $"{refusal} (parameters: {string.Join(", ", method.SensitiveParameters)})";
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipeline = await repository.GetPipelineByWellKnownNameAsync(method.TenantId,
                method.PipelineWellKnownName).ConfigureAwait(false);
            if (pipeline is null)
            {
                return $"{PipelineMethodReasons.PipelineNotDeployed} (no pipeline with the well-known name " +
                       $"'{method.PipelineWellKnownName}')";
            }

            return pipeline.DeploymentState == Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated
                .System.Communication.v4.RtDeploymentStateEnum.Deployed
                ? null
                : $"{PipelineMethodReasons.PipelineNotDeployed} (pipeline '{method.PipelineWellKnownName}' is in " +
                  $"state {pipeline.DeploymentState})";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // a store problem is not a statement about the pipeline: keep the current state, try again next time
            logger.LogWarning(e, "Checking the pipeline of CK method {MethodId} in tenant {TenantId} failed",
                method.MethodId, method.TenantId);
            return instance.HasEndpoint(method.MethodId) ? null : "pipeline-lookup-failed";
        }
    }

    private async Task ConnectAsync(HashSet<string> ready, bool startup, CancellationToken cancellationToken)
    {
        var added = new List<(string MethodId, string QueueName, EndpointHandle Handle)>();
        foreach (var methodId in ready.Where(id => !instance.HasEndpoint(id)).Order(StringComparer.Ordinal))
        {
            var queueName = CkMethodQueueNames.For(methodId);
            added.Add((methodId, queueName,
                eventHubControl.RegisterCommandConsumer(queueName, consumer.CreateHandler(), EndpointOptions)));
        }

        // one budget for all new endpoints: N slow queues must not stretch the startup to N times the timeout
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.EndpointReadyTimeout);
        foreach (var (methodId, queueName, handle) in added)
        {
            try
            {
                await handle.WaitUntilReadyAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(handle).ConfigureAwait(false);
                var message =
                    $"CK method {methodId}: endpoint {queueName} did not accept messages within " +
                    $"{options.Value.EndpointReadyTimeout}; the method is not announced.";
                if (startup && options.Value.FailStartupOnInvalidMethod)
                {
                    throw new TimeoutException(message);
                }

                logger.LogError("{Message} The next heartbeat tries again.", message);
                continue;
            }

            instance.AddEndpoint(methodId, handle);
            logger.LogInformation("CK method {MethodId} pipeline handler endpoint {QueueName} ready", methodId,
                queueName);
        }
    }

    private async Task WithdrawGoneAsync(HashSet<string> ready, CancellationToken cancellationToken)
    {
        var gone = instance.MethodIds.Where(id => !ready.Contains(id)).ToList();
        if (gone.Count == 0)
        {
            return;
        }

        try
        {
            await eventHub.PublishAsync(instance.CreateWithdrawal(gone), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Publishing the withdrawal of {Count} CK pipeline method(s) failed", gone.Count);
        }

        foreach (var methodId in gone)
        {
            logger.LogWarning("CK method {MethodId} is no longer served by a pipeline: withdrawn", methodId);
            if (instance.RemoveEndpoint(methodId) is { } handle)
            {
                await DisposeQuietlyAsync(handle).ConfigureAwait(false);
            }
        }
    }

    private async Task DisposeQuietlyAsync(EndpointHandle handle)
    {
        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Removing a CK pipeline method endpoint failed");
        }
    }
}
