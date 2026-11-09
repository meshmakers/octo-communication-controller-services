using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationMethodSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>
///     Handler of <c>System.Communication/Adapter.DeployUpdate-1</c> (AB#8595). REST parity:
///     <c>POST Adapter/deployUpdate</c> (<c>AdapterController.DeployUpdate</c>): <see cref="IAdapterService.DeployAdapterConfigurationAsync" />
///     (wake first, then push the configuration over the adapter hub).
/// </summary>
/// <remarks>
///     The adapter is resolved through the tenant's adapter list first, so an id that names no adapter is
///     <c>METHOD_TARGET_NOT_FOUND</c> (REST answers 400 "not connected" for it). A failure of the push is
///     <c>ADAPTER_CONFIGURATION_DEPLOY_FAILED</c> with the service's message, as the REST error body carries it.
/// </remarks>
internal sealed class AdapterDeployUpdateHandler(
    ICommunicationRepository repository,
    IAdapterService adapterService,
    ILogger<AdapterDeployUpdateHandler> logger) : ICkMethodHandler<AdapterDeployUpdateParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<AdapterDeployUpdateParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var adapterRtId) ||
            await FindAdapterAsync(repository, context.TenantId, adapterRtId).ConfigureAwait(false) is not { } adapter)
        {
            return CkMethodOutcome.TargetNotFound();
        }

        try
        {
            await adapterService.DeployAdapterConfigurationAsync(context.TenantId, adapter.ToRtEntityId())
                .ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (Exception e) when (e is AdapterServiceException or AdapterHubCallbackException)
        {
            logger.LogError(e, "Error deploying adapter configuration (CK method, invocation {InvocationId})",
                context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.AdapterConfigurationDeployFailed,
                (CommunicationMethodErrors.MessageParameter, e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/DeployableWorkload.Wake-1</c> (AB#8595). REST parity:
///     <c>POST Adapter/{workloadRtId}/wake</c> (<c>AdapterController.Wake</c>):
///     <see cref="IWorkloadLifecycleService.EnsureWorkloadRunningAsync(string, Meshmakers.Octo.ConstructionKit.Contracts.OctoObjectId)" />.
///     No-op where REST is a no-op (AlwaysOn, no scale-to-zero, running).
/// </summary>
internal sealed class DeployableWorkloadWakeHandler(
    IWorkloadLifecycleService workloadLifecycleService,
    ILogger<DeployableWorkloadWakeHandler> logger) : ICkMethodHandler<DeployableWorkloadWakeParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<DeployableWorkloadWakeParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var workloadRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        try
        {
            await workloadLifecycleService.EnsureWorkloadRunningAsync(context.TenantId, workloadRtId)
                .ConfigureAwait(false);
            return CkMethodOutcome.Ok();
        }
        catch (WorkloadLifecycleServiceException e)
        {
            logger.LogWarning(e, "Wake of workload '{WorkloadRtId}' failed (CK method, invocation {InvocationId})",
                workloadRtId, context.InvocationId);
            return CkMethodOutcome.Fail(CommunicationMethodErrors.WorkloadWakeFailed,
                (CommunicationMethodErrors.MessageParameter, e.Message));
        }
    }
}

/// <summary>
///     Handler of <c>System.Communication/Adapter.ReconcileServiceAccount-1</c> (AB#8595). REST parity:
///     <c>POST Adapter/{adapterRtId}/serviceAccount/reconcile</c> (<c>AdapterController.ReconcileServiceAccount</c>): the same
///     provisioning call with the user context (declared roles only with <c>UserManagement</c>, checked on the validated
///     caller), the same result (<see cref="ServiceAccountController.BuildReconcileDto" />) and the same information event.
/// </summary>
/// <remarks>
///     A failure is <c>SERVICE_ACCOUNT_RECONCILE_FAILED</c> without the exception text (REST echoes it): the reconcile spans
///     the identity bus and the repository, and a method error is not the place to carry free text of unknown origin. The
///     exception is logged with the invocation id.
/// </remarks>
internal sealed class AdapterReconcileServiceAccountHandler(
    ICommunicationRepository repository,
    IPipelineServiceAccountProvisioningService provisioningService,
    ICommunicationEventService eventService,
    ILogger<AdapterReconcileServiceAccountHandler> logger)
    : ICkMethodHandler<AdapterReconcileServiceAccountParameters, AdapterReconcileServiceAccountResult>
{
    public async Task<CkMethodOutcome<AdapterReconcileServiceAccountResult>> HandleAsync(
        CkMethodContext<AdapterReconcileServiceAccountParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var adapterRtId) ||
            await FindAdapterAsync(repository, context.TenantId, adapterRtId).ConfigureAwait(false) is not { } adapter)
        {
            return CkMethodOutcome<AdapterReconcileServiceAccountResult>.TargetNotFound();
        }

        ServiceAccountReconcileResult result;
        try
        {
            result = await provisioningService.ReconcileAdapterAsync(context.TenantId, adapter,
                ServiceAccountReconcileContext.User(HasRole(context.CallerPrincipal,
                    CommonConstants.UserManagementRole))).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e,
                "Reconciling the pipeline service account of adapter '{AdapterRtId}' failed (CK method, invocation {InvocationId})",
                context.TargetRtId, context.InvocationId);
            return CkMethodOutcome<AdapterReconcileServiceAccountResult>.Fail(
                CommunicationMethodErrors.ServiceAccountReconcileFailed);
        }

        // The reconcile has happened: a failing event store must not turn it into a reported failure.
        await StoreInformationEventQuietlyAsync(eventService, logger, context.TenantId,
            $"Pipeline service account '{result.WellKnownName}' (client '{result.ClientId}') of adapter " +
            $"'{adapter.Name ?? context.TargetRtId}' ({context.TargetRtId}) reconciled ({MethodSource}): " +
            $"{result.Outcome}.{(result.RoleChangesSkipped ? " Declared roles were skipped." : string.Empty)}")
            .ConfigureAwait(false);

        var dto = ServiceAccountController.BuildReconcileDto(result);
        return new AdapterReconcileServiceAccountResult
        {
            Value = new RtServiceAccountReconcileResultRecord
            {
                Outcome = dto.Outcome,
                ClientId = dto.ClientId,
                ConfigurationWellKnownName = dto.ConfigurationWellKnownName,
                RoleChangesSkipped = dto.RoleChangesSkipped,
                Message = dto.Message
            }
        };
    }
}

/// <summary>
///     Handler of <c>System.Communication/Adapter.RotateServiceAccountSecret-1</c> (AB#8595). REST parity:
///     <c>POST Adapter/{adapterRtId}/serviceAccount/rotateSecret</c> (<c>AdapterController.RotateServiceAccountSecret</c>):
///     the same rotation, message (<see cref="AdapterController.BuildRotationMessage" />) and events.
/// </summary>
/// <remarks>
///     🔴 The new secret is never part of the answer, of a log line or of an event: the provisioning service writes it into
///     the <c>ServiceAccountConfiguration</c> and hashes it into the identity client, and returns only the client id (the
///     REST endpoint returns no secret either, AB#5032). The result record therefore is not <c>sensitive</c>; the redaction
///     test asserts that the secret the configuration holds afterwards appears nowhere. A failure is
///     <c>SERVICE_ACCOUNT_ROTATION_FAILED</c> without the exception text; the error event says the previous secret remains.
/// </remarks>
internal sealed class AdapterRotateServiceAccountSecretHandler(
    ICommunicationRepository repository,
    IPipelineServiceAccountProvisioningService provisioningService,
    ICommunicationEventService eventService,
    ILogger<AdapterRotateServiceAccountSecretHandler> logger)
    : ICkMethodHandler<AdapterRotateServiceAccountSecretParameters, AdapterRotateServiceAccountSecretResult>
{
    public async Task<CkMethodOutcome<AdapterRotateServiceAccountSecretResult>> HandleAsync(
        CkMethodContext<AdapterRotateServiceAccountSecretParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var adapterRtId) ||
            await FindAdapterAsync(repository, context.TenantId, adapterRtId).ConfigureAwait(false) is not { } adapter)
        {
            return CkMethodOutcome<AdapterRotateServiceAccountSecretResult>.TargetNotFound();
        }

        var adapterName = adapter.Name ?? context.TargetRtId!;
        PipelineServiceAccountRotationResult result;
        try
        {
            result = await provisioningService.RotateAdapterSecretAsync(context.TenantId, adapter).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e,
                "Rotating the pipeline service account secret of adapter '{AdapterRtId}' failed (CK method, invocation {InvocationId})",
                context.TargetRtId, context.InvocationId);
            try
            {
                await eventService.StoreErrorEventAsync(context.TenantId,
                    $"Rotating the pipeline service account secret of adapter '{adapterName}' ({context.TargetRtId}) " +
                    $"failed ({MethodSource}). The previous secret remains in effect.").ConfigureAwait(false);
            }
            catch (Exception eventException)
            {
                logger.LogWarning(eventException, "Could not store the rotation failure event");
            }

            return CkMethodOutcome<AdapterRotateServiceAccountSecretResult>.Fail(
                CommunicationMethodErrors.ServiceAccountRotationFailed);
        }

        // The secret is rotated on both sides now. A failing event store must not report the rotation as failed: the
        // caller would believe the previous secret is still valid and skip the pipeline redeploy.
        var message = AdapterController.BuildRotationMessage(result, adapterName);
        await StoreInformationEventQuietlyAsync(eventService, logger, context.TenantId,
            $"Pipeline service account secret rotated for adapter '{adapterName}' ({context.TargetRtId}), " +
            $"client '{result.ClientId}' ({MethodSource}). {message}").ConfigureAwait(false);

        return new AdapterRotateServiceAccountSecretResult
        {
            Value = new RtServiceAccountSecretRotationResultRecord
            {
                ClientId = result.ClientId,
                ConfigurationWellKnownName = result.WellKnownName,
                WasCreated = result.WasCreated,
                RequiresPipelineRedeploy = result.RequiresPipelineRedeploy,
                Message = message
            }
        };
    }
}
