using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>The error codes the System.Communication methods declare (AB#8595); the CK model is the contract.</summary>
internal static class CommunicationMethodErrors
{
    /// <summary><c>Adapter.DeployUpdate</c>: the configuration could not be pushed (REST 400).</summary>
    public const string AdapterConfigurationDeployFailed = "ADAPTER_CONFIGURATION_DEPLOY_FAILED";

    /// <summary><c>DeployableWorkload.Wake</c>: the wake failed or timed out (REST 400).</summary>
    public const string WorkloadWakeFailed = "WORKLOAD_WAKE_FAILED";

    /// <summary><c>Adapter.ReconcileServiceAccount</c>: the reconcile did not complete (REST 400).</summary>
    public const string ServiceAccountReconcileFailed = "SERVICE_ACCOUNT_RECONCILE_FAILED";

    /// <summary><c>Adapter.RotateServiceAccountSecret</c>: the rotation did not happen (REST 400).</summary>
    public const string ServiceAccountRotationFailed = "SERVICE_ACCOUNT_ROTATION_FAILED";

    /// <summary><c>AdapterPool.CancelQueuedExecution</c>: the execution already holds a lease (REST 409).</summary>
    public const string ExecutionAlreadyLeased = "EXECUTION_ALREADY_LEASED";

    /// <summary><c>AdapterPool.CancelQueuedExecution</c>: no such queued execution in the pool (REST 404).</summary>
    public const string QueuedExecutionNotFound = "QUEUED_EXECUTION_NOT_FOUND";

    /// <summary><c>Pipeline.Deploy</c>: no adapter given and the pipeline is assigned to none.</summary>
    public const string PipelineAdapterNotAssigned = "PIPELINE_ADAPTER_NOT_ASSIGNED";

    /// <summary><c>Pipeline.Deploy</c>: the adapter rejected the definition (REST 422).</summary>
    public const string PipelineDeployRejected = "PIPELINE_DEPLOY_REJECTED";

    /// <summary><c>Pipeline.Deploy</c>: the deploy did not happen (REST 404/400).</summary>
    public const string PipelineDeployFailed = "PIPELINE_DEPLOY_FAILED";

    /// <summary><c>Pipeline.Execute</c>: the execution could not be started (REST 400).</summary>
    public const string PipelineExecutionStartFailed = "PIPELINE_EXECUTION_START_FAILED";

    /// <summary><c>Pipeline.SetDebugging</c>: the debug state could not be changed (REST 422/404/400).</summary>
    public const string PipelineDebugChangeFailed = "PIPELINE_DEBUG_CHANGE_FAILED";

    /// <summary><c>Pipeline.MoveToAdapter</c>: the move did not happen (REST: result entry with success false).</summary>
    public const string PipelineMoveFailed = "PIPELINE_MOVE_FAILED";

    /// <summary><c>DataFlow.Deploy</c>: the data flow could not be deployed (REST 422/404/400).</summary>
    public const string DataFlowDeployFailed = "DATA_FLOW_DEPLOY_FAILED";

    /// <summary><c>DataFlow.Undeploy</c>: the data flow could not be undeployed (REST 422/404/400).</summary>
    public const string DataFlowUndeployFailed = "DATA_FLOW_UNDEPLOY_FAILED";

    /// <summary><c>DeployableWorkload.SetChartVersion</c>: the workload exists but the write failed (REST 404).</summary>
    public const string WorkloadChartVersionUpdateFailed = "WORKLOAD_CHART_VERSION_UPDATE_FAILED";

    /// <summary>Error parameter with the service's message, as the REST error body carries it. Never contains a secret.</summary>
    public const string MessageParameter = "message";
}

/// <summary>Steps the handlers share: the target id and the adapter lookup the REST endpoints use.</summary>
internal static class CommunicationMethodSupport
{
    /// <summary>The audit text of a change made through a method (the REST endpoints write <c>source: User</c>).</summary>
    public const string MethodSource = "source: User via CK method";

    /// <summary>The target as object id; <c>false</c> for a missing or malformed id (it cannot exist).</summary>
    public static bool TryGetTarget(CkMethodContext context, out OctoObjectId target)
    {
        target = default;
        return !string.IsNullOrWhiteSpace(context.TargetRtId) && OctoObjectId.TryParse(context.TargetRtId, out target);
    }

    /// <summary>
    ///     The adapter of the tenant with this id, resolved through the tenant's adapter list as the REST endpoints do
    ///     (Adapter is polymorphic: MeshAdapter, PlugAdapter, …); <c>null</c> when the tenant has none with this id.
    /// </summary>
    public static async Task<RtAdapter?> FindAdapterAsync(ICommunicationRepository repository, string tenantId,
        OctoObjectId adapterRtId)
    {
        var adapters = await repository.GetAdaptersAsync(tenantId).ConfigureAwait(false);
        return adapters.FirstOrDefault(a => a.RtId == adapterRtId);
    }

    /// <summary>
    ///     Writes the audit event of an operation that has already taken effect. A failure of the event store is logged as a
    ///     warning and never turns the operation into a reported failure.
    /// </summary>
    public static async Task StoreInformationEventQuietlyAsync(ICommunicationEventService eventService, ILogger logger,
        string tenantId, string message)
    {
        try
        {
            await eventService.StoreInformationEventAsync(tenantId, message).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not store the audit event of a completed CK method call");
        }
    }

    /// <summary>
    ///     The text of a service failure without the values of sensitive parameters and the caller token (AB#8596): every
    ///     value is replaced as a whole and line by line (a validation message may quote one line of a pipeline definition).
    ///     Lines shorter than <see cref="MinRedactedLineLength" /> are left alone: they are YAML punctuation or short keys that
    ///     would mask the useful part of the message without hiding anything.
    /// </summary>
    public static string Redact(string? text, params string?[] sensitiveValues)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        foreach (var value in sensitiveValues)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            text = text.Replace(value, Masked, StringComparison.Ordinal);
            foreach (var line in value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                         .Where(l => l.Length >= MinRedactedLineLength)
                         .OrderByDescending(l => l.Length))
            {
                text = text.Replace(line, Masked, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    ///     The message a method may return for a service failure (CodeRabbit on PR #105): the text of the controller's own
    ///     exceptions (<c>AdapterServiceException</c>, <c>AdapterHubCallbackException</c>, <c>CommunicationRepositoryException</c>,
    ///     <c>WorkloadLifecycleServiceException</c>, <c>TriggerManagementServiceException</c> - the texts REST answers and the
    ///     Studio shows, e.g. the adapter's validation errors of a definition the caller may read anyway), redacted of the
    ///     sensitive values and the token; <c>null</c> for any other exception, whose free text of unknown origin stays in the
    ///     log (as AB#8595 does for the service account reconcile).
    /// </summary>
    public static string? CallerSafeMessage(Exception exception, params string?[] sensitiveValues) =>
        exception is AdapterServiceException or Hubs.AdapterHubCallbackException or CommunicationRepositoryException
            or WorkloadLifecycleServiceException or TriggerManagementServiceException
            ? Redact(exception.Message, sensitiveValues)
            : null;

    /// <summary>
    ///     The <c>message</c> parameter of a declared failure (AB#8716): the caller-safe text of the exception, otherwise a fixed
    ///     text that names the operation and the invocation id, never the text of the exception. Every error the model declares
    ///     with "Parameter message" therefore always carries it.
    /// </summary>
    /// <param name="exception">The service failure.</param>
    /// <param name="operation">What did not happen, as a sentence start, e.g. "The execution could not be started".</param>
    /// <param name="context">The call (its invocation id points to the controller log entry).</param>
    /// <param name="sensitiveValues">Values that must not appear in the answer (sensitive parameters, the caller token).</param>
    public static string FailureMessage(Exception exception, string operation, CkMethodContext context,
        params string?[] sensitiveValues) =>
        CallerSafeMessage(exception, sensitiveValues) ?? UnexpectedFailureMessage(operation, context.InvocationId);

    /// <summary>The fixed text for an exception whose own text must not reach the caller (AB#8716).</summary>
    public static string UnexpectedFailureMessage(string operation, Guid invocationId) =>
        $"{operation}; see the communication controller log (invocation {invocationId}).";

    /// <summary>The failure parameters: the message parameter (AB#8716: always present).</summary>
    public static (string Name, string Value)[] MessageParameters(string message) =>
        [(CommunicationMethodErrors.MessageParameter, message)];

    /// <summary>The placeholder of a redacted value.</summary>
    public const string Masked = "***";

    /// <summary>Shortest line of a sensitive value that <see cref="Redact" /> masks on its own.</summary>
    public const int MinRedactedLineLength = 8;

    /// <summary><c>true</c> when the validated caller holds the role (case-insensitive, as the token's role claims).</summary>
    public static bool HasRole(CkMethodPrincipal? principal, string role) =>
        principal?.Roles.Contains(role, StringComparer.OrdinalIgnoreCase) == true;
}
