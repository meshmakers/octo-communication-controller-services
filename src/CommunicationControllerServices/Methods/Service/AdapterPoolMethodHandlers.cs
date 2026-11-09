using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationMethodSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>Maps the mirror reconcile result to the method result record (same members as the REST JSON).</summary>
internal static class AdapterPoolMirrorSyncResultMapping
{
    public static RtAdapterPoolMirrorSyncResultRecord ToRecord(AdapterPoolMirrorSyncResult result) => new()
    {
        TenantsReconciled = result.TenantsReconciled,
        MirrorsCreatedOrUpdated = result.MirrorsCreatedOrUpdated,
        MirrorsRemoved = result.MirrorsRemoved,
        LendersUnreadable = result.LendersUnreadable,
        AdaptersRelinked = result.AdaptersRelinked,
        IsNoOp = result.IsNoOp
    };
}

/// <summary>
///     Handler of the static <c>System.Communication/AdapterPool.RefreshMirrors-1</c> (AB#8595). REST parity:
///     <c>POST AdapterPool/mirrors/refresh</c>: <see cref="IAdapterPoolMirrorProvisioningService.ProvisionForBorrowerAsync" />
///     for the call's tenant (the borrower).
/// </summary>
internal sealed class AdapterPoolRefreshMirrorsHandler(
    IAdapterPoolMirrorProvisioningService mirrorProvisioningService,
    ILogger<AdapterPoolRefreshMirrorsHandler> logger)
    : ICkMethodHandler<AdapterPoolRefreshMirrorsParameters, AdapterPoolRefreshMirrorsResult>
{
    public async Task<CkMethodOutcome<AdapterPoolRefreshMirrorsResult>> HandleAsync(
        CkMethodContext<AdapterPoolRefreshMirrorsParameters> context, CancellationToken cancellationToken)
    {
        var result = await mirrorProvisioningService.ProvisionForBorrowerAsync(context.TenantId, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation(
            "[{TenantId}] Lent adapter pool mirrors refreshed by CK method: {Created} present, {Removed} removed, " +
            "{Relinked} leased adapter(s) re-linked",
            context.TenantId, result.MirrorsCreatedOrUpdated, result.MirrorsRemoved, result.AdaptersRelinked);
        return new AdapterPoolRefreshMirrorsResult { Value = AdapterPoolMirrorSyncResultMapping.ToRecord(result) };
    }
}

/// <summary>
///     Handler of the static <c>System.Communication/AdapterPool.PublishMirrors-1</c> (AB#8595). REST parity:
///     <c>POST AdapterPool/mirrors/publish</c>: <see cref="IAdapterPoolMirrorProvisioningService.ProvisionForLenderAsync" />
///     for the call's tenant (the lender).
/// </summary>
internal sealed class AdapterPoolPublishMirrorsHandler(
    IAdapterPoolMirrorProvisioningService mirrorProvisioningService,
    ILogger<AdapterPoolPublishMirrorsHandler> logger)
    : ICkMethodHandler<AdapterPoolPublishMirrorsParameters, AdapterPoolPublishMirrorsResult>
{
    public async Task<CkMethodOutcome<AdapterPoolPublishMirrorsResult>> HandleAsync(
        CkMethodContext<AdapterPoolPublishMirrorsParameters> context, CancellationToken cancellationToken)
    {
        var result = await mirrorProvisioningService.ProvisionForLenderAsync(context.TenantId, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation(
            "[{TenantId}] Adapter pool mirrors published by CK method: {Tenants} borrower(s), {Created} present, " +
            "{Removed} removed, {Relinked} leased adapter(s) re-linked",
            context.TenantId, result.TenantsReconciled, result.MirrorsCreatedOrUpdated, result.MirrorsRemoved,
            result.AdaptersRelinked);
        return new AdapterPoolPublishMirrorsResult { Value = AdapterPoolMirrorSyncResultMapping.ToRecord(result) };
    }
}

/// <summary>
///     Handler of <c>System.Communication/AdapterPool.GrantLease-1</c> (AB#8595). REST parity:
///     <c>POST AdapterPool/{adapterPoolRtId}/lease</c> (<c>AdapterPoolController.GrantLeaseAsync</c>): the call's tenant is the
///     lender, the borrower is a parameter and is authorized by the lending scope of the pool, never by the caller's token.
///     A refused lease is a success with <c>granted = false</c> and the reason, as in REST.
/// </summary>
internal sealed class AdapterPoolGrantLeaseHandler(
    ILeaseService leaseService,
    ILogger<AdapterPoolGrantLeaseHandler> logger)
    : ICkMethodHandler<AdapterPoolGrantLeaseParameters, AdapterPoolGrantLeaseResult>
{
    /// <summary>The range of the REST request body (<c>GrantAdapterPoolLeaseRequestDto.TtlSeconds</c>).</summary>
    internal const int MinTtlSeconds = 1;

    /// <summary>The range of the REST request body (<c>GrantAdapterPoolLeaseRequestDto.TtlSeconds</c>).</summary>
    internal const int MaxTtlSeconds = 3600;

    public async Task<CkMethodOutcome<AdapterPoolGrantLeaseResult>> HandleAsync(
        CkMethodContext<AdapterPoolGrantLeaseParameters> context, CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var poolRtId))
        {
            return CkMethodOutcome<AdapterPoolGrantLeaseResult>.TargetNotFound();
        }

        var parameters = context.Parameters;
        if (string.IsNullOrWhiteSpace(parameters.BorrowerTenantId))
        {
            return CkMethodOutcome<AdapterPoolGrantLeaseResult>.ValidationFailed("borrowerTenantId", "required");
        }

        if (!OctoObjectId.TryParse(parameters.AdapterRtId, out var adapterRtId))
        {
            return CkMethodOutcome<AdapterPoolGrantLeaseResult>.ValidationFailed("adapterRtId", "invalid-object-id");
        }

        if (parameters.TtlSeconds is { } ttl && (ttl < MinTtlSeconds || ttl > MaxTtlSeconds))
        {
            return CkMethodOutcome<AdapterPoolGrantLeaseResult>.ValidationFailed("ttlSeconds", "out-of-range");
        }

        var result = await leaseService.GrantLeaseAsync(context.TenantId, poolRtId,
            new LeaseRequest(parameters.BorrowerTenantId, adapterRtId, parameters.ExecutionId,
                parameters.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null),
            cancellationToken).ConfigureAwait(false);

        if (!result.Granted)
        {
            logger.LogInformation(
                "Lease of adapter pool {AdapterPoolRtId} in tenant '{TenantId}' to '{BorrowerTenantId}' was not " +
                "granted (CK method): {Reason}",
                poolRtId, context.TenantId, parameters.BorrowerTenantId, result.StatusMessage);
        }

        return new AdapterPoolGrantLeaseResult
        {
            Value = new RtAdapterPoolLeaseResultRecord
            {
                Granted = result.Granted,
                LeaseId = result.LeaseId,
                MemberId = result.MemberId,
                StatusMessage = result.StatusMessage
            }
        };
    }
}

/// <summary>
///     Handler of <c>System.Communication/AdapterPool.CancelQueuedExecution-1</c> (AB#8595). REST parity:
///     <c>DELETE AdapterPool/{adapterPoolRtId}/queue/{executionId}</c>: <see cref="ILeaseSchedulerService.CancelQueuedExecutionAsync" />;
///     REST 409 is <c>EXECUTION_ALREADY_LEASED</c>, REST 404 is <c>QUEUED_EXECUTION_NOT_FOUND</c>. It cancels a queue
///     entry, never a running pipeline.
/// </summary>
internal sealed class AdapterPoolCancelQueuedExecutionHandler(
    ILeaseSchedulerService leaseScheduler,
    ILogger<AdapterPoolCancelQueuedExecutionHandler> logger)
    : ICkMethodHandler<AdapterPoolCancelQueuedExecutionParameters>
{
    public async Task<CkMethodOutcome> HandleAsync(CkMethodContext<AdapterPoolCancelQueuedExecutionParameters> context,
        CancellationToken cancellationToken)
    {
        if (!TryGetTarget(context, out var poolRtId))
        {
            return CkMethodOutcome.TargetNotFound();
        }

        if (string.IsNullOrWhiteSpace(context.Parameters.ExecutionId))
        {
            return CkMethodOutcome.ValidationFailed("executionId", "required");
        }

        var result = await leaseScheduler.CancelQueuedExecutionAsync(context.TenantId, poolRtId,
            context.Parameters.ExecutionId, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case QueueCancellationResult.Cancelled:
                logger.LogInformation(
                    "Cancelled queued execution '{ExecutionId}' of adapter pool {AdapterPoolRtId} in tenant " +
                    "'{TenantId}' (CK method)",
                    context.Parameters.ExecutionId, poolRtId, context.TenantId);
                return CkMethodOutcome.Ok();
            case QueueCancellationResult.AlreadyLeased:
                return CkMethodOutcome.Fail(CommunicationMethodErrors.ExecutionAlreadyLeased);
            default:
                return CkMethodOutcome.Fail(CommunicationMethodErrors.QueuedExecutionNotFound);
        }
    }
}
