using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>Why a REST call of a facade endpoint does not run through its CK method (log field <c>Reason</c>, docs).</summary>
internal enum CommunicationFallbackReason
{
    /// <summary><c>Methods:Communication:Enabled=false</c>: no handler is hosted.</summary>
    SwitchOff,

    /// <summary>The tenant's CK model does not declare the method (System.Communication older than the method).</summary>
    MethodNotDeclared,

    /// <summary>The caller passed the REST policy (scope) but does not hold a role the method declares (AB#9048, decision B).</summary>
    RoleMissing,

    /// <summary>The caller passed the REST policy but lacks a scope the method requires (the read-only scope of <c>execute</c>).</summary>
    ScopeMissing,

    /// <summary>The REST input cannot be expressed as a method call (a malformed id); the direct path answers as before.</summary>
    InputNotRoutable
}

/// <summary>One REST call of a facade endpoint: the CK method it stands for and its target and parameters.</summary>
/// <param name="CkTypeId">The declaring CK type of the method, e.g. <c>System.Communication/Adapter</c>.</param>
/// <param name="MethodId">Qualified method id (<c>SystemCommunicationCkIds.…MethodId</c>).</param>
/// <param name="Operation">Name of the endpoint, for the log lines (never a target id).</param>
internal sealed record CommunicationMethodCall(string CkTypeId, string MethodId, string Operation)
{
    /// <summary>Runtime id of the target; <c>null</c> for a static method.</summary>
    public string? TargetRtId { get; init; }

    /// <summary>
    ///     The generated parameter object of the method; <c>null</c> when the REST input cannot be expressed as a method call
    ///     (<see cref="CommunicationFallbackReason.InputNotRoutable" />): the endpoint then keeps its direct path, behind the guard.
    /// </summary>
    public object? Parameters { get; init; }
}

/// <summary>
///     The one entry of the REST facades of the communication controller (AB#9048, AB#9049, Feature AB#5701 F5.3). An adapter,
///     pool, pipeline, data-flow or workload operation endpoint hands its call here, with a mapping of the method envelope to its
///     REST answer and its previous direct code:
///     <list type="bullet">
///         <item>
///             <b>Method path.</b> When the handlers are hosted, the tenant's CK model declares the method and the caller passes
///             the method's declared authorization (roles, scope), the call is one CK method call through
///             <see cref="ICkMethodDispatcher" />. The handler is hosted by this service, so it runs in this process (AB#8411),
///             detached from the HTTP request, through the invocation pipeline: caller tenant (<c>CkMethodCallerTenantFilter</c>),
///             declared authorization, redaction, exactly one Method audit event (transport <c>in-process</c>).
///         </item>
///         <item>
///             <b>Guarded direct path.</b> Otherwise (switch off, method not declared, caller without the declared role or
///             scope, input that is no method call) the endpoint's direct code runs behind the same checks the method path makes
///             before the handler: the caller tenant (<see cref="CkMethodCallerTenantCheck" />, the rule of the method
///             transports), then exactly one Method audit event (transport <see cref="CommunicationMethodFacade.FallbackTransport" />, also for a refusal or
///             a fault) and one WARN line with the method id and the reason - never the target id, parameters, user or token.
///         </item>
///     </list>
///     The REST policy (<c>octo_api</c> scope) is not changed: a caller with the scope but without the declared role keeps the
///     REST behaviour on the guarded direct path (decision B of AB#9048, 2026-11-18: no right widened, none narrowed). The
///     communication types have no data permissions of their own, so neither path checks one.
/// </summary>
internal interface ICommunicationMethodFacade
{
    /// <summary>
    ///     Serves one REST call. Never throws for a refused or failing method call; the guard's refusal and every method envelope go
    ///     through <paramref name="map" />. An exception of <paramref name="direct" /> is recorded and surfaces unchanged.
    /// </summary>
    /// <param name="tenantId">The route tenant.</param>
    /// <param name="call">The method the call stands for.</param>
    /// <param name="user">The authenticated caller of the request.</param>
    /// <param name="accessToken">The bearer token of the request, forwarded for delegation (<c>Pipeline.Execute</c>). Sensitive.</param>
    /// <param name="map">Maps a method envelope (or the guard's refusal) to the REST answer.</param>
    /// <param name="direct">The endpoint's previous direct code; runs only on the guarded direct path.</param>
    /// <param name="failureOf">The audit outcome of a direct answer that is a failure; <c>null</c> for a success.</param>
    Task<T> ServeAsync<T>(string tenantId, CommunicationMethodCall call, ClaimsPrincipal user, string? accessToken,
        Func<CkMethodResponse, T> map, Func<Task<T>> direct, Func<T, string?> failureOf);
}

/// <inheritdoc />
internal sealed class CommunicationMethodFacade(
    IServiceProvider services,
    ICkMethodAuthorizationDefinitionSource definitions,
    ICkMethodAuthorizationEvaluator evaluator,
    ICkMethodAuditRecorder auditRecorder,
    IOptions<CommunicationMethodsOptions> methodOptions,
    IOptions<TenantAuthorizationOptions> tenantOptions,
    IHostApplicationLifetime lifetime,
    ILogger<CommunicationMethodFacade> logger) : ICommunicationMethodFacade
{
    /// <summary>The audit transport of a call on the guarded direct path (the method path writes <c>in-process</c>).</summary>
    public const string FallbackTransport = "fallback";

    /// <inheritdoc />
    public async Task<T> ServeAsync<T>(string tenantId, CommunicationMethodCall call, ClaimsPrincipal user,
        string? accessToken, Func<CkMethodResponse, T> map, Func<Task<T>> direct, Func<T, string?> failureOf)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(failureOf);

        var principal = user.Identity?.IsAuthenticated == true ? CkMethodPrincipal.FromClaimsPrincipal(user) : null;
        var reason = RouteOf(tenantId, call, principal);
        if (reason == null)
        {
            return map(await DispatchAsync(tenantId, call, principal!, accessToken).ConfigureAwait(false));
        }

        return await GuardedDirectAsync(tenantId, call, user, principal, reason.Value, map, direct, failureOf)
            .ConfigureAwait(false);
    }

    /// <summary><c>null</c> when the call goes through the method, otherwise why it does not.</summary>
    internal CommunicationFallbackReason? RouteOf(string tenantId, CommunicationMethodCall call, CkMethodPrincipal? principal)
    {
        if (!methodOptions.Value.Enabled)
        {
            return CommunicationFallbackReason.SwitchOff;
        }

        if (call.Parameters == null)
        {
            return CommunicationFallbackReason.InputNotRoutable;
        }

        var definition = definitions.Find(tenantId, call.CkTypeId, call.MethodId);
        if (definition == null)
        {
            return CommunicationFallbackReason.MethodNotDeclared;
        }

        // The declared authorization, judged exactly as the invocation pipeline judges it, only to choose the path: a caller who
        // would be refused there keeps the REST behaviour (decision B). The pipeline judges again on the method path.
        var decision = evaluator.Evaluate(definition.Authorization, definition.Kind, call.TargetRtId, principal);
        return decision.Allowed
            ? null
            : decision.Denial is CkMethodDenial.MissingApiScope or CkMethodDenial.MissingScope
                ? CommunicationFallbackReason.ScopeMissing
                : CommunicationFallbackReason.RoleMissing;
    }

    private async Task<CkMethodResponse> DispatchAsync(string tenantId, CommunicationMethodCall call,
        CkMethodPrincipal caller, string? accessToken)
    {
        var dispatcher = services.GetRequiredService<ICkMethodDispatcher>();
        var timeouts = services.GetRequiredService<ICkMethodTimeoutResolver>();
        var request = new CkMethodRequest(tenantId)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = call.MethodId,
            CkTypeId = call.CkTypeId,
            TargetRtId = call.TargetRtId,
            ParametersJson = JsonSerializer.Serialize(call.Parameters, call.Parameters!.GetType(), CkMethodJson.Options),
            CallerAccessToken = accessToken,
            DeadlineUtc = timeouts.GetDeadlineUtc(tenantId, call.CkTypeId, call.MethodId)
        };

        // The host's lifetime, not the request: like the REST endpoints before, a client that disconnects never stops an
        // operation that has started (a pipeline execution keeps running, a deploy completes). The deadline of the method
        // (its declared timeout) bounds the call.
        using (CkMethodPrincipalScope.Begin(caller))
        {
            return await dispatcher.InvokeAsync(request, lifetime.ApplicationStopping).ConfigureAwait(false);
        }
    }

    private async Task<T> GuardedDirectAsync<T>(string tenantId, CommunicationMethodCall call, ClaimsPrincipal user,
        CkMethodPrincipal? principal, CommunicationFallbackReason reason, Func<CkMethodResponse, T> map,
        Func<Task<T>> direct, Func<T, string?> failureOf)
    {
        var invocationId = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        T value;
        try
        {
            var refusal = await CheckAsync(tenantId, call, principal).ConfigureAwait(false);
            if (refusal != null)
            {
                // a refusal did not run: it stays the refusal, also when the audit write fails
                await FinishAsync(refusal.Code).ConfigureAwait(false);
                return map(CkMethodResponse.Failed(invocationId, string.Empty, refusal));
            }

            value = await direct().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(CkMethodAuditOutcomes.CallerCancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // a fault of the checks or of the direct code: recorded, the exception surfaces unchanged
            await FinishAsync(CkMethodErrorCodes.MethodHandlerFailed).ConfigureAwait(false);
            throw;
        }

        // The direct code ran. Like the audit filter of the method pipeline: when the tenant fails calls on an audit failure and
        // the event could not be written, the answer is METHOD_OUTCOME_UNKNOWN (the change may have taken effect).
        if (!await FinishAsync(failureOf(value) ?? CkMethodAuditOutcomes.Success).ConfigureAwait(false))
        {
            return map(CkMethodResponse.Failed(invocationId, string.Empty,
                CkMethodError.Create(CkMethodErrorCodes.MethodOutcomeUnknown, (CkMethodErrorParameters.MethodId, call.MethodId))));
        }

        return value;

        // exactly one WARN line and one audit event per call, whatever the outcome; false when a strict audit refused the event
        async Task<bool> FinishAsync(string outcome)
        {
            logger.LogWarning(
                "{Operation} in tenant {TenantId} ran on the fallback of {MethodId} (Reason={Reason}), invocation {InvocationId}: {Outcome}",
                call.Operation, tenantId, call.MethodId, ReasonText(reason), invocationId, outcome);
            return await RecordAsync(new CkMethodAuditRecord
            {
                TenantId = tenantId,
                InvocationId = invocationId,
                MethodId = call.MethodId,
                CkTypeId = call.CkTypeId,
                TargetRtId = call.TargetRtId,
                Transport = FallbackTransport,
                Outcome = outcome,
                CallerSubject = principal?.SubjectId ?? user.FindFirst("client_id")?.Value,
                CallerName = user.FindFirst("name")?.Value,
                Duration = Stopwatch.GetElapsedTime(started)
            }).ConfigureAwait(false);
        }
    }

    /// <summary>The caller tenant (rule of the method transports); <c>null</c> when the caller may proceed.</summary>
    private async Task<CkMethodError?> CheckAsync(string tenantId, CommunicationMethodCall call, CkMethodPrincipal? principal)
    {
        // fail closed: the REST policy admits authenticated callers only, so no principal is never a legitimate call
        if (principal == null)
        {
            return CkMethodAuthorizationErrors.Forbidden(call.MethodId);
        }

        var check = await CkMethodCallerTenantCheck.CheckAsync(principal, tenantId, call.MethodId, tenantOptions.Value,
            services.GetService<ITenantHierarchyReader>(), logger, FallbackTransport).ConfigureAwait(false);
        return check.IsRefused ? check.Error : null;
    }

    /// <summary>Writes the audit event; a failing recorder never changes the answer. <c>false</c> when a strict audit refused it.</summary>
    private async Task<bool> RecordAsync(CkMethodAuditRecord record)
    {
        try
        {
            await auditRecorder.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (CkMethodAuditFailedException)
        {
            // logged and counted by the recorder; the caller answers METHOD_OUTCOME_UNKNOWN
            return false;
        }
        catch (Exception e)
        {
            logger.LogError(e, "CK method {MethodId} invocation {InvocationId}: the audit recorder threw", record.MethodId,
                record.InvocationId);
            return true;
        }
    }

    /// <summary>The fixed token of a reason in the log line and the docs.</summary>
    public static string ReasonText(CommunicationFallbackReason reason) => reason switch
    {
        CommunicationFallbackReason.SwitchOff => "switch-off",
        CommunicationFallbackReason.MethodNotDeclared => "method-not-declared",
        CommunicationFallbackReason.RoleMissing => "role-missing",
        CommunicationFallbackReason.ScopeMissing => "scope-missing",
        _ => "input-not-routable"
    };
}
