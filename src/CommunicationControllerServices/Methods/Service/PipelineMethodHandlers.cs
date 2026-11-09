using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationMethodSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>
///     Handler of <c>System.Communication/Pipeline.Deploy-1</c> (AB#8596). REST parity: <c>POST Pipeline/deploy</c>
///     (<c>PipelineController.DeployPipeline</c>): <see cref="IAdapterService.DeployPipelineAsync" /> with the adapter and the
///     optional definition (the REST request body).
/// </summary>
/// <remarks>
///     <para>
///         The adapter defaults to the one the pipeline is assigned to (REST requires it in the query); an explicit
///         <c>adapterRtId</c> is resolved through the tenant's adapter list like the adapter methods of AB#8595, so an id
///         that names no adapter is <c>METHOD_VALIDATION_FAILED</c>, a pipeline that does not exist is
///         <c>METHOD_TARGET_NOT_FOUND</c>.
///     </para>
///     <para>
///         🔴 <c>pipelineDefinition</c> is <c>sensitive</c> (a definition can carry credentials): it is never logged, and the
///         failure message is passed through <see cref="CommunicationMethodSupport.Redact" />, so a validation message that
///         quotes a line of it does not echo it.
///     </para>
/// </remarks>
internal sealed class PipelineDeployHandler(
    ICommunicationRepository repository,
    IAdapterService adapterService,
    ILogger<PipelineDeployHandler> logger) : ICkMethodHandler<PipelineDeployParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<PipelineDeployParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var pipelineRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);
        if (await repository.GetPipelineAsync(context.TenantId, pipelineRtEntityId).ConfigureAwait(false) is null)
        {
            return CkMethodOutcome.TargetNotFound();
        }

        RtAdapter? adapter;
        var parameters = context.Parameters;
        if (string.IsNullOrWhiteSpace(parameters.AdapterRtId))
        {
            adapter = await repository.GetAdapterByPipelineAsync(context.TenantId, pipelineRtEntityId)
                .ConfigureAwait(false);
            if (adapter is null)
            {
                return CkMethodOutcome.Fail(CommunicationMethodErrors.PipelineAdapterNotAssigned);
            }
        }
        else if (!OctoObjectId.TryParse(parameters.AdapterRtId, out var adapterRtId))
        {
            return CkMethodOutcome.ValidationFailed("adapterRtId", "invalid-object-id");
        }
        else if ((adapter = await FindAdapterAsync(repository, context.TenantId, adapterRtId).ConfigureAwait(false)) is null)
        {
            return CkMethodOutcome.ValidationFailed("adapterRtId", "not-found");
        }

        var definition = string.IsNullOrEmpty(parameters.PipelineDefinition) ? null : parameters.PipelineDefinition;
        try
        {
            await adapterService.DeployPipelineAsync(context.TenantId, adapter.ToRtEntityId(), pipelineRtEntityId,
                definition).ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = Redact(e.Message, definition, context.CallerAccessToken);
            logger.LogError(
                "Deploying pipeline '{PipelineRtId}' to adapter '{AdapterRtId}' failed (CK method, invocation {InvocationId}): {Reason}",
                pipelineRtId, adapter.RtId, context.InvocationId, message);
            return CkMethodOutcome.Fail(e is AdapterHubCallbackException
                    ? CommunicationMethodErrors.PipelineDeployRejected
                    : CommunicationMethodErrors.PipelineDeployFailed,
                (CommunicationMethodErrors.MessageParameter, message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/Pipeline.Execute-1</c> (AB#8596). REST parity: <c>POST Pipeline/execute</c>
///     (<c>PipelineController.ExecutePipeline</c>): <see cref="ITriggerManagementService.StartExecutePipelineAsync" /> with the
///     input, the dry-run flag, the caller and the caller's token for delegation (AB#5031, AB#5126). Starts the execution and
///     returns its id; it does not wait for the end.
/// </summary>
/// <remarks>
///     <para>
///         The caller is the validated principal (<see cref="PipelineMethodInvoker.BuildCaller" />, trust level 2 as for a
///         bearer-authenticated REST call). No method-invocation marker is passed: this is the REST execute as a method, not
///         a pipeline that implements a method (<c>handler: pipeline</c>, AB#6879).
///     </para>
///     <para>
///         🔴 <c>input</c> is <c>sensitive</c>: never logged; the failure message is redacted of the input and the token.
///     </para>
/// </remarks>
internal sealed class PipelineExecuteHandler(
    ICommunicationRepository repository,
    ITriggerManagementService triggerManagementService,
    ILogger<PipelineExecuteHandler> logger)
    : ICkMethodHandler<PipelineExecuteParameters, PipelineExecuteResult>
{
    public async Task<CkMethodOutcome<PipelineExecuteResult>> HandleAsync(
        CkMethodContext<PipelineExecuteParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var pipelineRtId) ||
            await repository.GetPipelineAsync(context.TenantId,
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId)).ConfigureAwait(false) is null)
        {
            return CkMethodOutcome<PipelineExecuteResult>.TargetNotFound();
        }

        var input = context.Parameters.Input;
        try
        {
            var caller = context.CallerPrincipal is { } principal
                ? PipelineMethodInvoker.BuildCaller(context.Request, principal)
                : null;
            var execution = await triggerManagementService.StartExecutePipelineAsync(context.TenantId, pipelineRtId,
                input, context.Parameters.IsDryRun ?? false, caller, context.CallerAccessToken).ConfigureAwait(false);

            return new PipelineExecuteResult
            {
                Value = new RtPipelineExecutionStartResultRecord
                {
                    ExecutionId = execution.Id.ToString(),
                    StartedAt = execution.DateTime
                }
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = Redact(e.Message, input, context.CallerAccessToken);
            logger.LogError(
                "Starting pipeline '{PipelineRtId}' failed (CK method, invocation {InvocationId}): {Reason}",
                pipelineRtId, context.InvocationId, message);
            return CkMethodOutcome<PipelineExecuteResult>.Fail(CommunicationMethodErrors.PipelineExecutionStartFailed,
                (CommunicationMethodErrors.MessageParameter, message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/Pipeline.SetDebugging-1</c> (AB#8596). REST parity:
///     <c>PATCH Pipeline/{pipelineRtId}/debug</c> (<c>PipelineController.SetPipelineDebugging</c>):
///     <see cref="IAdapterService.SetPipelineDebuggingAsync" />, the same answer and the same information event.
/// </summary>
internal sealed class PipelineSetDebuggingHandler(
    ICommunicationRepository repository,
    IAdapterService adapterService,
    ICommunicationEventService eventService,
    ILogger<PipelineSetDebuggingHandler> logger)
    : ICkMethodHandler<PipelineSetDebuggingParameters, PipelineSetDebuggingResult>
{
    public async Task<CkMethodOutcome<PipelineSetDebuggingResult>> HandleAsync(
        CkMethodContext<PipelineSetDebuggingParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var pipelineRtId))
        {
            return CkMethodOutcome<PipelineSetDebuggingResult>.TargetNotFound();
        }

        var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);
        if (await repository.GetPipelineAsync(context.TenantId, pipelineRtEntityId).ConfigureAwait(false) is null)
        {
            return CkMethodOutcome<PipelineSetDebuggingResult>.TargetNotFound();
        }

        var enabled = context.Parameters.Enabled;
        bool appliedToRunningAdapter;
        try
        {
            appliedToRunningAdapter = await adapterService
                .SetPipelineDebuggingAsync(context.TenantId, pipelineRtEntityId, enabled).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Setting debugging of pipeline '{PipelineRtId}' failed (CK method, invocation {InvocationId})",
                pipelineRtId, context.InvocationId);
            return CkMethodOutcome<PipelineSetDebuggingResult>.Fail(CommunicationMethodErrors.PipelineDebugChangeFailed,
                (CommunicationMethodErrors.MessageParameter, e.Message));
        }

        // The flag is persisted: a failing event store must not report the change as failed.
        await StoreInformationEventQuietlyAsync(eventService, logger, context.TenantId,
            PipelineController.BuildDebugAuditMessage(pipelineRtId, enabled, appliedToRunningAdapter, MethodSource))
            .ConfigureAwait(false);

        return new PipelineSetDebuggingResult
        {
            Value = new RtPipelineDebugResultRecord
            {
                Enabled = enabled,
                AppliedToRunningAdapter = appliedToRunningAdapter
            }
        };
    }
}

/// <summary>
///     Handler of <c>System.Communication/Pipeline.MoveToAdapter-1</c> (AB#8596). REST parity:
///     <c>PATCH Pipeline/move-to-adapter</c> (<c>PipelineController.MovePipelinesToAdapter</c>) for one pipeline:
///     <see cref="ICommunicationRepository.MovePipelineToAdapterAsync" />, the optional best-effort redeploy on the new
///     adapter (its failure is a warning, the move stays committed) and the same information event.
/// </summary>
/// <remarks>
///     The REST endpoint takes a list; the method is an instance method on one pipeline, so a client that moves several
///     pipelines calls it once per pipeline (each move is its own repository call in REST too).
/// </remarks>
internal sealed class PipelineMoveToAdapterHandler(
    ICommunicationRepository repository,
    IAdapterService adapterService,
    ICommunicationEventService eventService,
    ILogger<PipelineMoveToAdapterHandler> logger)
    : ICkMethodHandler<PipelineMoveToAdapterParameters, PipelineMoveToAdapterResult>
{
    public async Task<CkMethodOutcome<PipelineMoveToAdapterResult>> HandleAsync(
        CkMethodContext<PipelineMoveToAdapterParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var pipelineRtId))
        {
            return CkMethodOutcome<PipelineMoveToAdapterResult>.TargetNotFound();
        }

        if (!OctoObjectId.TryParse(context.Parameters.TargetAdapterRtId, out var targetAdapterRtId))
        {
            return CkMethodOutcome<PipelineMoveToAdapterResult>.ValidationFailed("targetAdapterRtId",
                "invalid-object-id");
        }

        Models.PipelineMoveResult moveResult;
        try
        {
            moveResult = await repository.MovePipelineToAdapterAsync(context.TenantId, pipelineRtId, targetAdapterRtId)
                .ConfigureAwait(false);
        }
        catch (CommunicationRepositoryException e)
        {
            logger.LogWarning(e,
                "Moving pipeline '{PipelineRtId}' to adapter '{TargetAdapterRtId}' failed (CK method, invocation {InvocationId})",
                pipelineRtId, targetAdapterRtId, context.InvocationId);
            return CkMethodOutcome<PipelineMoveToAdapterResult>.Fail(CommunicationMethodErrors.PipelineMoveFailed,
                (CommunicationMethodErrors.MessageParameter, e.Message));
        }

        string? warning = null;
        if (context.Parameters.Redeploy == true &&
            !moveResult.OldAdapterRtEntityId.Equals(moveResult.NewAdapterRtEntityId))
        {
            try
            {
                await adapterService.DeployPipelineAsync(context.TenantId, moveResult.NewAdapterRtEntityId,
                    new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Pipeline {PipelineRtId} moved to {Adapter} but redeploy failed (CK method)",
                    pipelineRtId, moveResult.NewAdapterRtEntityId);
                warning = $"Move OK; redeploy on new adapter failed: {e.Message}";
            }
        }

        await StoreInformationEventQuietlyAsync(eventService, logger, context.TenantId,
            PipelineController.BuildMoveAuditMessage(pipelineRtId, moveResult, MethodSource)).ConfigureAwait(false);

        return new PipelineMoveToAdapterResult
        {
            Value = new RtPipelineMoveResultRecord
            {
                OldAdapterRtId = moveResult.OldAdapterRtEntityId.RtId.ToString(),
                NewAdapterRtId = moveResult.NewAdapterRtEntityId.RtId.ToString(),
                Warning = warning
            }
        };
    }
}

/// <summary>
///     Handler of <c>System.Communication/DataFlow.Deploy-1</c> (AB#8596). REST parity: <c>POST DataFlow/deploy</c>
///     (<c>DataFlowController.DeployDataFlow</c>): <see cref="IAdapterService.DeployDataFlowAsync" />.
/// </summary>
internal sealed class DataFlowDeployHandler(
    IAdapterService adapterService,
    ILogger<DataFlowDeployHandler> logger) : ICkMethodHandler<DataFlowDeployParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DataFlowDeployParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var dataFlowRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        try
        {
            await adapterService.DeployDataFlowAsync(context.TenantId, dataFlowRtId).ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to deploy data flow '{DataFlowRtId}' (CK method, invocation {InvocationId})",
                dataFlowRtId, context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.DataFlowDeployFailed,
                (CommunicationMethodErrors.MessageParameter, e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/DataFlow.Undeploy-1</c> (AB#8596). REST parity: <c>POST DataFlow/undeploy</c>
///     (<c>DataFlowController.UndeployDataFlow</c>): <see cref="IAdapterService.UndeployDataFlowAsync" />.
/// </summary>
internal sealed class DataFlowUndeployHandler(
    IAdapterService adapterService,
    ILogger<DataFlowUndeployHandler> logger) : ICkMethodHandler<DataFlowUndeployParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DataFlowUndeployParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var dataFlowRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        try
        {
            await adapterService.UndeployDataFlowAsync(context.TenantId, dataFlowRtId).ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to undeploy data flow '{DataFlowRtId}' (CK method, invocation {InvocationId})",
                dataFlowRtId, context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.DataFlowUndeployFailed,
                (CommunicationMethodErrors.MessageParameter, e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/DeployableWorkload.SetChartVersion-1</c> (AB#8596). REST parity:
///     <c>PATCH Workload/{workloadRtId}/chart-version</c> (<c>WorkloadController.UpdateChartVersion</c>): the same SemVer check
///     (<see cref="WorkloadController.IsValidSemVer" />, empty = latest),
///     <see cref="ICommunicationRepository.UpdateWorkloadChartVersionAsync" /> and the same information event. Does not deploy.
/// </summary>
/// <remarks>
///     An invalid version is <c>METHOD_VALIDATION_FAILED</c> (field <c>chartVersion</c>, REST 400); a workload that does not
///     exist is <c>METHOD_TARGET_NOT_FOUND</c> (REST 404).
/// </remarks>
internal sealed class DeployableWorkloadSetChartVersionHandler(
    ICommunicationRepository repository,
    ICommunicationEventService eventService,
    ILogger<DeployableWorkloadSetChartVersionHandler> logger)
    : ICkMethodHandler<DeployableWorkloadSetChartVersionParameters>
{
    /// <summary>The source tag of the audit event (REST writes <c>source: CI/CD</c>).</summary>
    internal const string ChartVersionSource = "source: CI/CD via CK method";

    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DeployableWorkloadSetChartVersionParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var workloadRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        var chartVersion = context.Parameters.ChartVersion?.Trim() ?? string.Empty;
        if (chartVersion.Length > 0 && !WorkloadController.IsValidSemVer(chartVersion))
        {
            return CkMethodOutcome.ValidationFailed("chartVersion", "invalid-semver");
        }

        string? previousVersion;
        try
        {
            previousVersion = await repository.UpdateWorkloadChartVersionAsync(context.TenantId, workloadRtId,
                chartVersion).ConfigureAwait(false);
        }
        catch (CommunicationRepositoryException e)
        {
            logger.LogWarning(e,
                "Chart version of workload '{WorkloadRtId}' not updated (CK method, invocation {InvocationId})",
                workloadRtId, context.InvocationId);
            return CkMethodOutcome.TargetNotFound();
        }

        await StoreInformationEventQuietlyAsync(eventService, logger, context.TenantId,
            WorkloadController.BuildChartVersionAuditMessage(workloadRtId, previousVersion, chartVersion,
                ChartVersionSource)).ConfigureAwait(false);
        return CkMethodOutcome.Ok();
    }
}
