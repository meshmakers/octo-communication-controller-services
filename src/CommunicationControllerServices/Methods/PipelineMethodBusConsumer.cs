using System.Collections.Concurrent;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     The bus transport of the pipeline method handler: consumes <see cref="CkMethodRequest" /> from the method
///     queue, guards against a possible duplicate delivery and answers with the response of
///     <see cref="IPipelineMethodInvoker" />. It never throws: a thrown consumer would make the bus retry and start
///     the pipeline repeatedly. Same duplicate rules as the handler SDK (a redelivered request is not executed, the
///     caller gets <c>METHOD_OUTCOME_UNKNOWN</c>; no method is known to be idempotent here).
/// </summary>
internal sealed class PipelineMethodBusConsumer(
    IPipelineMethodInvoker invoker,
    PipelineMethodHandlerInstance instance,
    TimeProvider timeProvider,
    ILogger<PipelineMethodBusConsumer> logger)
{
    private static readonly TimeSpan SeenRetentionAfterDeadline = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxSeenRetention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<Guid, DateTime> _seenInvocations = new();
    private long _seenAdds;

    internal int SeenInvocationCount => _seenInvocations.Count;

    public ExecuteDeliveredCommandHandler<CkMethodRequest> CreateHandler()
    {
        return async (request, delivery, respond) =>
        {
            var response = await HandleAsync(request, delivery).ConfigureAwait(false);
            try
            {
                await respond(response).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Swallowed on purpose: rethrowing would let the bus retry and run the pipeline again. The caller
                // runs into METHOD_TIMEOUT instead (at-most-once).
                logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: sending the response failed",
                    request.MethodId, request.InvocationId);
            }
        };
    }

    internal async Task<CkMethodResponse> HandleAsync(CkMethodRequest request, CommandDeliveryInfo? delivery = null)
    {
        var firstSight = TryMarkSeen(request);
        if (delivery is { IsPossibleDuplicate: true })
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: possible duplicate delivery " +
                "(redelivered {Redelivered}, retry attempt {RetryAttempt}, redelivery count {RedeliveryCount}, message {MessageId})",
                request.MethodId, request.InvocationId, request.TenantId, delivery.Redelivered, delivery.RetryAttempt,
                delivery.RedeliveryCount, delivery.MessageId);
            return OutcomeUnknown(request);
        }

        if (!firstSight)
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: invocation id already seen",
                request.MethodId, request.InvocationId, request.TenantId);
            return OutcomeUnknown(request);
        }

        try
        {
            return await invoker.InvokeAsync(request).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // the invoker does not throw for a failing request; this is the last line before the bus
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId} failed unexpectedly",
                request.MethodId, request.InvocationId);
            return PipelineMethodResultMapper.HandlerFailed(request, instance.InstanceId,
                request.CorrelationId ?? request.InvocationId);
        }
    }

    private CkMethodResponse OutcomeUnknown(CkMethodRequest request) =>
        CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown,
                (CkMethodErrorParameters.MethodId, request.MethodId)));

    private bool TryMarkSeen(CkMethodRequest request)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var keepUntil = request.DeadlineUtc + SeenRetentionAfterDeadline;
        if (keepUntil > now + MaxSeenRetention)
        {
            keepUntil = now + MaxSeenRetention;
        }

        if (!_seenInvocations.TryAdd(request.InvocationId, keepUntil))
        {
            return false;
        }

        if (Interlocked.Increment(ref _seenAdds) % 256 == 0)
        {
            foreach (var entry in _seenInvocations.Where(e => e.Value < now))
            {
                _seenInvocations.TryRemove(entry.Key, out _);
            }
        }

        return true;
    }
}
