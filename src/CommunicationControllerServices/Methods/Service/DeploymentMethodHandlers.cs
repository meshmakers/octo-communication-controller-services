using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationMethodSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>
///     The Communication-enabled gate of the resource-creating deployment operations (AB#4255), shared by the REST endpoints of
///     <c>DeploymentSiteController</c> and the methods of AB#9050: deploying creates operator-managed cluster resources, which
///     must not happen on a tenant whose Communication is disabled (the tenant delete guard only sees the flag, and a site or
///     workload deployed afterwards would be orphaned). Undeploy stays open so remediation always works.
/// </summary>
internal static class CommunicationEnabledGate
{
    /// <summary>The text of the refusal (REST 409 <c>OperationFailedErrorDto</c>, method <c>COMMUNICATION_DISABLED</c>).</summary>
    public static string DisabledMessage(string tenantId) =>
        $"Communication is disabled for tenant '{tenantId}'. Enable it first (EnableCommunication) before deploying deploymentSites or workloads.";

    /// <summary>
    ///     <c>null</c> when Communication is enabled for the tenant; otherwise the refusal text, after one WARN line naming the
    ///     operation and the tenant (as REST logged it).
    /// </summary>
    public static async Task<string?> RefusalAsync(IConfigurationService configurationService, ILogger logger,
        string tenantId, string operation)
    {
        if (await configurationService.IsEnabledAsync(tenantId).ConfigureAwait(false))
        {
            return null;
        }

        logger.LogWarning("Rejected {Operation} for tenant '{TenantId}': Communication is disabled", operation, tenantId);
        return DisabledMessage(tenantId);
    }
}

/// <summary>
///     Handler of <c>System.Communication/DeploymentSite.Deploy-1</c> (AB#9050). REST parity: <c>POST DeploymentSite/deploy</c>
///     (<c>DeploymentSiteController.DeployDeploymentSiteAsync</c>): the Communication-enabled gate, then
///     <see cref="IDeploymentSiteService.DeployDeploymentSiteAsync" />.
/// </summary>
/// <remarks>
///     A refusal of the service (<see cref="DeploymentSiteServiceException" />: the site does not exist, it is an Edge site) is
///     <c>DEPLOYMENT_SITE_DEPLOY_FAILED</c> with the text REST answered (400); any other exception surfaces as
///     <c>METHOD_HANDLER_FAILED</c> (REST: 500), its text stays in the log.
/// </remarks>
internal sealed class DeploymentSiteDeployHandler(
    IDeploymentSiteService deploymentSiteService,
    IConfigurationService configurationService,
    ILogger<DeploymentSiteDeployHandler> logger) : ICkMethodHandler<DeploymentSiteDeployParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DeploymentSiteDeployParameters> context,
        CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        if (!TryGetTarget(context, out var deploymentSiteRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        if (await CommunicationEnabledGate.RefusalAsync(configurationService, logger, context.TenantId,
                "deploymentSite deploy").ConfigureAwait(false) is { } refusal)
        {
            return CkMethodOutcome.Fail(CommunicationMethodErrors.CommunicationDisabled, MessageParameters(refusal));
        }

        try
        {
            await deploymentSiteService.DeployDeploymentSiteAsync(context.TenantId, deploymentSiteRtId)
                .ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (DeploymentSiteServiceException e)
        {
            logger.LogError(e, "Error deploying deploymentSite (CK method, invocation {InvocationId})",
                context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.DeploymentSiteDeployFailed,
                MessageParameters(e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/DeploymentSite.Undeploy-1</c> (AB#9050). REST parity:
///     <c>POST DeploymentSite/undeploy</c> (<c>DeploymentSiteController.UndeployDeploymentSiteAsync</c>):
///     <see cref="IDeploymentSiteService.UndeployDeploymentSiteAsync" />, also while Communication is disabled.
/// </summary>
internal sealed class DeploymentSiteUndeployHandler(
    IDeploymentSiteService deploymentSiteService,
    ILogger<DeploymentSiteUndeployHandler> logger) : ICkMethodHandler<DeploymentSiteUndeployParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DeploymentSiteUndeployParameters> context,
        CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        if (!TryGetTarget(context, out var deploymentSiteRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        try
        {
            await deploymentSiteService.UndeployDeploymentSiteAsync(context.TenantId, deploymentSiteRtId)
                .ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (DeploymentSiteServiceException e)
        {
            logger.LogError(e, "Error undeploying deploymentSite (CK method, invocation {InvocationId})",
                context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.DeploymentSiteUndeployFailed,
                MessageParameters(e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/DeployableWorkload.Deploy-1</c> (AB#9050). REST parity:
///     <c>POST DeploymentSite/workloads/deploy</c> (<c>DeploymentSiteController.DeployWorkloadAsync</c>): the
///     Communication-enabled gate, then <see cref="IDeploymentSiteService.DeployWorkloadAsync" />; the warning of AB#7428 is the
///     result (<c>warning</c>, absent when the deploy had none - REST answers 204 then, 200 with the warning otherwise).
/// </summary>
internal sealed class DeployableWorkloadDeployHandler(
    IDeploymentSiteService deploymentSiteService,
    IConfigurationService configurationService,
    ILogger<DeployableWorkloadDeployHandler> logger)
    : ICkMethodHandler<DeployableWorkloadDeployParameters, DeployableWorkloadDeployResult>
{
    public async Task<CkMethodOutcome<DeployableWorkloadDeployResult>> HandleAsync(
        CkMethodContext<DeployableWorkloadDeployParameters> context, CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        if (!TryGetTarget(context, out var workloadRtId))
        {
            return CkMethodOutcome<DeployableWorkloadDeployResult>.TargetNotFound();
        }

        if (await CommunicationEnabledGate.RefusalAsync(configurationService, logger, context.TenantId,
                "workload deploy").ConfigureAwait(false) is { } refusal)
        {
            return CkMethodOutcome<DeployableWorkloadDeployResult>.Fail(CommunicationMethodErrors.CommunicationDisabled,
                MessageParameters(refusal));
        }

        try
        {
            var warning = await deploymentSiteService.DeployWorkloadAsync(context.TenantId, workloadRtId)
                .ConfigureAwait(false);
            return new DeployableWorkloadDeployResult
            {
                Value = new RtWorkloadDeployResultRecord { Warning = string.IsNullOrEmpty(warning) ? null : warning }
            };
        }
        catch (DeploymentSiteServiceException e)
        {
            logger.LogError(e, "Error deploying workload (CK method, invocation {InvocationId})", context.InvocationId);
            return CkMethodOutcome<DeployableWorkloadDeployResult>.Fail(CommunicationMethodErrors.WorkloadDeployFailed,
                MessageParameters(e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/DeployableWorkload.Undeploy-1</c> (AB#9050). REST parity:
///     <c>POST DeploymentSite/workloads/undeploy</c> (<c>DeploymentSiteController.UndeployWorkloadAsync</c>):
///     <see cref="IDeploymentSiteService.UndeployWorkloadAsync" />, also while Communication is disabled.
/// </summary>
internal sealed class DeployableWorkloadUndeployHandler(
    IDeploymentSiteService deploymentSiteService,
    ILogger<DeployableWorkloadUndeployHandler> logger) : ICkMethodHandler<DeployableWorkloadUndeployParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DeployableWorkloadUndeployParameters> context,
        CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        if (!TryGetTarget(context, out var workloadRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        try
        {
            await deploymentSiteService.UndeployWorkloadAsync(context.TenantId, workloadRtId).ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (DeploymentSiteServiceException e)
        {
            logger.LogError(e, "Error undeploying workload (CK method, invocation {InvocationId})", context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.WorkloadUndeployFailed, MessageParameters(e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/AdapterPool.Scale-1</c> (AB#9050). REST parity:
///     <c>POST DeploymentSite/workloads/adapter-pool/scale</c> (<c>DeploymentSiteController.ScaleAdapterPoolAsync</c>): a
///     negative count is refused first (<c>METHOD_VALIDATION_FAILED</c>, field <c>replicas</c>; REST 400), then the
///     Communication-enabled gate, then <see cref="IDeploymentSiteService.ScaleAdapterPoolAsync" />; the result is the count the
///     operator was asked for.
/// </summary>
internal sealed class AdapterPoolScaleHandler(
    IDeploymentSiteService deploymentSiteService,
    IConfigurationService configurationService,
    ILogger<AdapterPoolScaleHandler> logger) : ICkMethodHandler<AdapterPoolScaleParameters, AdapterPoolScaleResult>
{
    /// <summary>The reason of the validation failure of a negative count.</summary>
    internal const string NegativeReplicas = "negative";

    public async Task<CkMethodOutcome<AdapterPoolScaleResult>> HandleAsync(
        CkMethodContext<AdapterPoolScaleParameters> context, CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        if (!TryGetTarget(context, out var poolWorkloadRtId))
        {
            return CkMethodOutcome<AdapterPoolScaleResult>.TargetNotFound();
        }

        var replicas = context.Parameters.Replicas;
        if (replicas < 0)
        {
            return CkMethodOutcome<AdapterPoolScaleResult>.ValidationFailed("replicas", NegativeReplicas);
        }

        if (await CommunicationEnabledGate.RefusalAsync(configurationService, logger, context.TenantId,
                "adapter deploymentSite scale").ConfigureAwait(false) is { } refusal)
        {
            return CkMethodOutcome<AdapterPoolScaleResult>.Fail(CommunicationMethodErrors.CommunicationDisabled,
                MessageParameters(refusal));
        }

        try
        {
            var effective = await deploymentSiteService.ScaleAdapterPoolAsync(context.TenantId, poolWorkloadRtId,
                replicas).ConfigureAwait(false);
            return new AdapterPoolScaleResult { Value = new RtAdapterPoolScaleResultRecord { Replicas = effective } };
        }
        catch (DeploymentSiteServiceException e)
        {
            logger.LogError(e, "Error scaling adapter deploymentSite (CK method, invocation {InvocationId})",
                context.InvocationId);
            return CkMethodOutcome<AdapterPoolScaleResult>.Fail(CommunicationMethodErrors.AdapterPoolScaleFailed,
                MessageParameters(e.Message));
        }
    }
}

/// <summary>
///     Handler of the static <c>System.Communication/PipelineTrigger.DeployAll-1</c> (AB#9050). REST parity:
///     <c>POST PipelineTrigger/deploy</c> (<c>PipelineTriggerController.DeployTrigger</c>):
///     <see cref="ITriggerManagementService.UpdateScheduleAsync" /> for the call's tenant.
/// </summary>
/// <remarks>
///     REST answers every failure 400 with the exception text; the method answers <c>PIPELINE_TRIGGERS_DEPLOY_FAILED</c> with
///     the caller-safe text (<see cref="CommunicationMethodSupport.FailureMessage" />: the text of the controller's own
///     exceptions, otherwise a fixed text with the invocation id), which the REST facade answers with 400.
/// </remarks>
internal sealed class PipelineTriggerDeployAllHandler(
    ITriggerManagementService triggerManagementService,
    ILogger<PipelineTriggerDeployAllHandler> logger) : ICkMethodHandler<PipelineTriggerDeployAllParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<PipelineTriggerDeployAllParameters> context,
        CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        try
        {
            await triggerManagementService.UpdateScheduleAsync(context.TenantId).ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to deploy triggers for tenant (CK method, invocation {InvocationId})",
                context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.PipelineTriggersDeployFailed,
                MessageParameters(FailureMessage(e, "The pipeline triggers could not be deployed", context,
                    context.CallerAccessToken)));
        }
    }
}

/// <summary>
///     Handler of the static <c>System.Communication/PipelineTrigger.UndeployAll-1</c> (AB#9050). REST parity:
///     <c>POST PipelineTrigger/undeploy</c> (<c>PipelineTriggerController.UndeployTrigger</c>):
///     <see cref="ITriggerManagementService.RemoveScheduleAsync" /> for the call's tenant. Failures as
///     <see cref="PipelineTriggerDeployAllHandler" />.
/// </summary>
internal sealed class PipelineTriggerUndeployAllHandler(
    ITriggerManagementService triggerManagementService,
    ILogger<PipelineTriggerUndeployAllHandler> logger) : ICkMethodHandler<PipelineTriggerUndeployAllParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<PipelineTriggerUndeployAllParameters> context,
        CancellationToken cancellationToken)
    {
        // AB#9048: the services below judge this caller, not "no caller" (CommunicationMethodCaller)
        using var methodCaller = CommunicationMethodCaller.Begin(context);
        try
        {
            await triggerManagementService.RemoveScheduleAsync(context.TenantId).ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to undeploy triggers for tenant (CK method, invocation {InvocationId})",
                context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.PipelineTriggersUndeployFailed,
                MessageParameters(FailureMessage(e, "The pipeline triggers could not be undeployed", context,
                    context.CallerAccessToken)));
        }
    }
}
