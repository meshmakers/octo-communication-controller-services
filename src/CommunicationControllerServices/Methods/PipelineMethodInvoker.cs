using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     Runs a CK method as a pipeline and returns the response envelope (AB#6879). Transport independent: the bus
///     consumer of <see cref="PipelineMethodHost" /> and the tests call it.
///     It never throws for a failing request or pipeline: every outcome, including contract version mismatch,
///     deadline, invalid parameters and a failing pipeline, is a <see cref="CkMethodResponse" />. Only cancellation
///     through the <c>cancellationToken</c> argument surfaces as <see cref="OperationCanceledException" />.
/// </summary>
internal interface IPipelineMethodInvoker
{
    /// <summary>Executes the pipeline whose well-known name equals the method id and maps its result.</summary>
    Task<CkMethodResponse> InvokeAsync(CkMethodRequest request, CancellationToken cancellationToken = default);
}

internal sealed class PipelineMethodInvoker(
    IPipelineMethodCatalog catalog,
    ICommunicationRepository repository,
    IServiceProvider serviceProvider,
    PipelineMethodHandlerInstance instance,
    IOptions<PipelineMethodOptions> options,
    TimeProvider timeProvider,
    ICkMethodCallerTokenValidator tokenValidator,
    ILogger<PipelineMethodInvoker> logger) : IPipelineMethodInvoker
{
    // CancellationTokenSource rejects delays above int.MaxValue ms (~24.8 days)
    private static readonly TimeSpan MaxCancellationDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    // 2 = strong (CallerTrustLevel.Strong): the caller is built ONLY from the access token this host validated itself
    // (issuer, signature, expiry, tenant, scope), like a bearer request of the HTTP API. The Caller member of the
    // request (informational, unauthenticated) is never consulted.
    private const int ValidatedCallerTrustLevel = 2;

    public async Task<CkMethodResponse> InvokeAsync(CkMethodRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CkMethodContract.IsSupported(request.ContractVersion))
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: contract version {RequestedVersion} is newer than {SupportedVersion}",
                request.MethodId, request.InvocationId, request.TenantId, request.ContractVersion,
                CkMethodContract.CurrentVersion);
            return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
                CkMethodError.Create(CkMethodErrorCodes.MethodContractVersionUnsupported,
                    (CkMethodErrorParameters.RequestedVersion, request.ContractVersion.ToString()),
                    (CkMethodErrorParameters.SupportedVersion, CkMethodContract.CurrentVersion.ToString())));
        }

        var correlationId = request.CorrelationId ?? request.InvocationId;
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["MethodId"] = request.MethodId,
            ["InvocationId"] = request.InvocationId,
            ["CorrelationId"] = correlationId,
            ["TenantId"] = request.TenantId
        });

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (request.DeadlineUtc <= now)
        {
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId} dropped: deadline {DeadlineUtc:O} already passed",
                request.MethodId, request.InvocationId, request.TenantId, request.DeadlineUtc);
            return Timeout(request, null);
        }

        // 0. Who is calling? The bus is not authenticated per message: a request can be forged by anyone who can
        // publish to the method queue. The forwarded token is the only proof; request.Caller is never used.
        // Done before any lookup so a refused caller learns nothing about the methods of the tenant.
        CkMethodPrincipal principal;
        try
        {
            var validation = await tokenValidator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (validation.Principal is not { } validated)
            {
                return Forbidden(request, validation.Failure);
            }

            principal = validated;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the identity keys cannot be loaded: the caller did nothing wrong. Logged without the token.
            logger.LogError(e,
                "CK method {MethodId} invocation {InvocationId} tenant {TenantId}: the caller token could not be validated",
                request.MethodId, request.InvocationId, request.TenantId);
            return HandlerFailed(request, correlationId);
        }

        // 1. The method must be a pipeline method of the tenant, and one the controller may run.
        if (!catalog.IsTenantEnabled(request.TenantId))
        {
            return NotAvailable(request, PipelineMethodReasons.TenantNotEnabled, "tenant is not served");
        }

        var method = catalog.Find(request.TenantId, request.MethodId);
        if (method is null || !method.IsPipelineHandled)
        {
            return NotAvailable(request, PipelineMethodReasons.MethodNotPipelineHandled,
                method is null ? "unknown method" : "the method is not declared handler: pipeline");
        }

        if (method.RefusalReason is { } refusal)
        {
            // OD-3, OD-4, OD-9: a pipeline execution stores its input and output; sensitive parameters and a sensitive
            // result must not reach a pipeline until the retention is decided. Names of parameters only, never values.
            return NotAvailable(request, refusal, method.RefusalDetail);
        }

        if (ValidateParameters(request, method) is { } invalid)
        {
            return invalid;
        }

        // 2. The pipeline implementing the method: deployed, well-known name = method id.
        RtPipeline? pipeline;
        try
        {
            pipeline = await repository.GetPipelineByWellKnownNameAsync(request.TenantId,
                method.PipelineWellKnownName).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "CK method {MethodId} tenant {TenantId}: the pipeline lookup failed",
                request.MethodId, request.TenantId);
            return HandlerFailed(request, correlationId);
        }

        if (pipeline is null || pipeline.DeploymentState != RtDeploymentStateEnum.Deployed)
        {
            return NotAvailable(request, PipelineMethodReasons.PipelineNotDeployed,
                pipeline is null
                    ? $"no pipeline with the well-known name '{method.PipelineWellKnownName}'"
                    : $"pipeline '{method.PipelineWellKnownName}' is in state {pipeline.DeploymentState}");
        }

        // 3. Run it and wait for the outcome until the deadline.
        var remaining = request.DeadlineUtc - timeProvider.GetUtcNow().UtcDateTime;
        if (remaining <= TimeSpan.Zero)
        {
            // the deadline passed during the lookup: nothing is started
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId} dropped: deadline {DeadlineUtc:O} passed during the lookup",
                request.MethodId, request.InvocationId, request.DeadlineUtc);
            return Timeout(request, method);
        }

        using var deadline = new CancellationTokenSource(
            remaining > MaxCancellationDelay ? MaxCancellationDelay : remaining, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            string executionId;
            try
            {
                var trigger = serviceProvider.GetRequiredService<ITriggerManagementService>();
                var execution = await trigger.StartExecutePipelineAsync(request.TenantId, pipeline.RtId,
                        BuildPipelineInput(request), false, BuildCaller(request, principal), request.CallerAccessToken,
                        methodInvocation: BuildMethodInvocation(request, pipeline.RtId))
                    .WaitAsync(linked.Token).ConfigureAwait(false);
                executionId = execution.Id.ToString();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // nothing ran: the adapter is offline or refused. The exception text is logged without the token.
                logger.LogWarning(
                    "CK method {MethodId} invocation {InvocationId}: starting pipeline '{Pipeline}' failed: {Reason}",
                    request.MethodId, request.InvocationId, method.PipelineWellKnownName,
                    Mask(e.Message, request.CallerAccessToken));
                return NotAvailable(request, PipelineMethodReasons.PipelineStartFailed, null);
            }

            var poll = options.Value.PollInterval;
            while (true)
            {
                RtPipelineExecution? execution = null;
                try
                {
                    execution = await repository.GetPipelineExecutionAsync(request.TenantId, executionId)
                        .WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // a transient store problem must not turn into a failed call; the deadline bounds the wait
                    logger.LogWarning(e, "CK method {MethodId} invocation {InvocationId}: reading execution {ExecutionId} failed, retrying",
                        request.MethodId, request.InvocationId, executionId);
                }

                if (execution is not null && PipelineMethodResultMapper.IsTerminal(execution.Status))
                {
                    var response = PipelineMethodResultMapper.Map(request, method, instance.InstanceId,
                        execution.Status, execution.OutputData, correlationId, out var detail);
                    if (detail is not null)
                    {
                        logger.LogError(
                            "CK method {MethodId} invocation {InvocationId} failed (correlation {CorrelationId}): {Detail}; pipeline error: {PipelineError}",
                            request.MethodId, request.InvocationId, correlationId, detail,
                            Truncate(Mask(execution.ErrorMessage, request.CallerAccessToken)));
                    }
                    else
                    {
                        logger.LogDebug(
                            "CK method {MethodId} invocation {InvocationId} finished in {ElapsedMs} ms: {Outcome}",
                            request.MethodId, request.InvocationId, stopwatch.ElapsedMilliseconds,
                            response.Success ? "success" : response.Error?.Code);
                    }

                    return response;
                }

                await Task.Delay(poll, timeProvider, linked.Token).ConfigureAwait(false);
                poll = TimeSpan.FromTicks(Math.Min((long)(poll.Ticks * 1.5), options.Value.MaxPollInterval.Ticks));
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // The outcome is unknown: the pipeline may still finish. Its execution is not cancelled.
            logger.LogWarning(
                "CK method {MethodId} invocation {InvocationId}: deadline reached after {ElapsedMs} ms while pipeline '{Pipeline}' was running; the pipeline may still finish",
                request.MethodId, request.InvocationId, stopwatch.ElapsedMilliseconds, method.PipelineWellKnownName);
            return Timeout(request, method);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId} failed (correlation {CorrelationId})",
                request.MethodId, request.InvocationId, correlationId);
            return HandlerFailed(request, correlationId);
        }
    }

    /// <summary>
    ///     The pipeline input: <c>{"parameters": {...}, "target": {"ckTypeId", "rtId"}, "method": {"id",
    ///     "invocationId", "correlationId"}}</c>. The caller is not part of it; it reaches the pipeline as the verified
    ///     principal of the execution.
    /// </summary>
    internal static string BuildPipelineInput(CkMethodRequest request)
    {
        var parameters = string.IsNullOrWhiteSpace(request.ParametersJson)
            ? new JsonObject()
            : JsonNode.Parse(request.ParametersJson) ?? new JsonObject();
        var input = new JsonObject
        {
            ["parameters"] = parameters,
            ["target"] = new JsonObject { ["ckTypeId"] = request.CkTypeId, ["rtId"] = request.TargetRtId },
            ["method"] = new JsonObject
            {
                ["id"] = request.MethodId,
                ["invocationId"] = request.InvocationId.ToString(),
                ["correlationId"] = (request.CorrelationId ?? request.InvocationId).ToString()
            }
        };
        return input.ToJsonString();
    }

    /// <summary>
    ///     The caller of the pipeline, built from the validated token only. A service token (client credentials, no
    ///     <c>sub</c>) runs as its <c>client_id</c> (<see cref="CkMethodPrincipal.Subject" />).
    /// </summary>
    /// <summary>
    ///     The marker that tells the executing adapter the run is the pipeline of this very method invocation
    ///     (AB#7460, decision note AB#7120). The qualified method id, the invocation id and the CK type id are those of the
    ///     request this host accepted (token validated, method and pipeline resolved); <c>PipelineRtId</c> is the
    ///     bound pipeline. 🔴 This is the only producer of the marker: REST execute and cron leave it null. A run queued
    ///     for a leased adapter carries it sealed in the queued execution's envelope (AB#7461).
    /// </summary>
    internal static ExecutePipelineMethodInvocation BuildMethodInvocation(CkMethodRequest request,
        OctoObjectId pipelineRtId) =>
        new()
        {
            MethodId = request.MethodId,
            InvocationId = request.InvocationId,
            CkTypeId = request.CkTypeId,
            PipelineRtId = pipelineRtId.ToString()
        };

    internal static ExecutePipelineCaller BuildCaller(CkMethodRequest request, CkMethodPrincipal principal) =>
        new()
        {
            SubjectId = principal.Subject,
            TenantId = principal.TenantId ?? request.TenantId,
            Roles = principal.Roles.ToArray(),
            TrustLevel = ValidatedCallerTrustLevel
        };

    private CkMethodResponse Forbidden(CkMethodRequest request, CkMethodFailure? failure)
    {
        var reason = failure is not null && failure.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var r)
            ? r
            : CkMethodTokenReasons.TokenInvalid;
        // reason, tenant and method only: never the token, a claim value or the (forged) Caller
        logger.LogWarning(
            "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: caller token refused ({Reason})",
            request.MethodId, request.InvocationId, request.TenantId, reason);
        return Failed(request, CkMethodErrorCodes.MethodForbidden, (CkMethodErrorParameters.Reason, reason));
    }

    private CkMethodResponse? ValidateParameters(CkMethodRequest request, PipelineMethodDefinition method)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.ParametersJson)
                ? "{}"
                : request.ParametersJson);
        }
        catch (JsonException)
        {
            return Validation(request, "$", PipelineMethodReasons.InvalidParameters);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Validation(request, "$", PipelineMethodReasons.InvalidParameters);
            }

            var present = document.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind != JsonValueKind.Null)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = (method.Definition.Parameters ?? [])
                .FirstOrDefault(p => !p.IsOptional && !present.Contains(p.Name));
            return missing is null ? null : Validation(request, missing.Name, PipelineMethodReasons.MissingParameter);
        }
    }

    private CkMethodResponse Validation(CkMethodRequest request, string field, string reason)
    {
        logger.LogInformation(
            "CK method {MethodId} invocation {InvocationId} rejected: invalid parameter '{Field}' ({Reason})",
            request.MethodId, request.InvocationId, field, reason);
        return Failed(request, CkMethodErrorCodes.MethodValidationFailed,
            (CkMethodErrorParameters.Field, field), (CkMethodErrorParameters.Reason, reason));
    }

    private CkMethodResponse NotAvailable(CkMethodRequest request, string reason, string? logDetail)
    {
        logger.LogWarning(
            "CK method {MethodId} invocation {InvocationId} tenant {TenantId} NOT executed: {Reason}{Detail}",
            request.MethodId, request.InvocationId, request.TenantId, reason,
            logDetail is null ? string.Empty : $" ({logDetail})");
        return Failed(request, CkMethodErrorCodes.MethodHandlerNotAvailable,
            (CkMethodErrorParameters.Reason, reason));
    }

    private CkMethodResponse Timeout(CkMethodRequest request, PipelineMethodDefinition? method) =>
        method is null
            ? Failed(request, CkMethodErrorCodes.MethodTimeout)
            : Failed(request, CkMethodErrorCodes.MethodTimeout,
                (CkMethodErrorParameters.TimeoutSeconds, method.TimeoutSeconds.ToString()));

    private CkMethodResponse HandlerFailed(CkMethodRequest request, Guid correlationId) =>
        PipelineMethodResultMapper.HandlerFailed(request, instance.InstanceId, correlationId);

    private CkMethodResponse Failed(CkMethodRequest request, string code,
        params (string Name, string Value)[] parameters)
    {
        var all = new List<(string Name, string Value)> { (CkMethodErrorParameters.MethodId, request.MethodId) };
        all.AddRange(parameters);
        return CkMethodResponse.Failed(request.InvocationId, instance.InstanceId,
            CkMethodError.Create(code, all.ToArray()));
    }

    private static string Mask(string? text, string? token) =>
        string.IsNullOrEmpty(text) ? string.Empty
        : string.IsNullOrEmpty(token) ? text
        : text.Replace(token, "***", StringComparison.Ordinal);

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";
}
