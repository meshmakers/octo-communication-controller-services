using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// AB#5537 / AB#5114 (concept decision 7, §5.2 phase 4): tenant-wide maintenance operation that
/// moves standalone pipeline service accounts from a stored client secret to impersonation and
/// redeploys the affected data flows, so no secret of those accounts remains stored.
/// </summary>
/// <remarks>
/// Order: (a) MayActAs edges with the current secret and (b) clearing the stored secret, per account
/// (<see cref="IPipelineServiceAccountProvisioningService.SwitchToImpersonationAsync" />); (c) the
/// redeploy of the affected data flows — the adapter caches a pipeline's configuration list at
/// registration, so it keeps presenting the old secret until then; (d) only for accounts whose data
/// flows ALL redeployed, the old secret is invalidated identity-side
/// (<see cref="IPipelineServiceAccountProvisioningService.InvalidateSwitchedSecretAsync" />). Without
/// redeploy, or after a failed one, the account is reported as
/// <see cref="ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation" />; a re-run with
/// <c>redeployDataFlows=true</c> completes it idempotently (secret-less accounts are redeployed and
/// invalidated again). The explicit guard (<c>ServiceAccountImpersonation:AllowSecretClearing</c>)
/// gates every write.
/// </remarks>
public interface IServiceAccountImpersonationSwitchService
{
    /// <summary>
    /// Classifies (dry run) or switches every <c>ServiceAccountConfiguration</c> of the tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="apply"><c>false</c> = dry run, nothing is written.</param>
    /// <param name="redeployDataFlows">
    /// Redeploy the data flows of the affected pipelines and, on success, invalidate the old secrets.
    /// <c>false</c> leaves switched accounts at <see cref="ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation" />.
    /// </param>
    /// <exception cref="ServiceAccountImpersonationSwitchDisabledException">
    /// <paramref name="apply" /> was requested while the guard is off.
    /// </exception>
    Task<ServiceAccountImpersonationSwitchReport> SwitchTenantAsync(string tenantId, bool apply,
        bool redeployDataFlows);
}

/// <summary>
/// AB#5537: the tenant-wide impersonation switch report.
/// </summary>
/// <param name="TenantId">Tenant.</param>
/// <param name="Applied">Whether secrets were cleared (<c>false</c> = dry run).</param>
/// <param name="Accounts">One entry per account.</param>
/// <param name="RedeployedDataFlowRtIds">Data flows redeployed after the switch.</param>
/// <param name="FailedDataFlowRedeploys">Data flows whose redeploy failed, with the reason (redeploy them by hand).</param>
public record ServiceAccountImpersonationSwitchReport(
    string TenantId,
    bool Applied,
    IReadOnlyList<ServiceAccountImpersonationSwitchResult> Accounts,
    IReadOnlyList<OctoObjectId> RedeployedDataFlowRtIds,
    IReadOnlyDictionary<OctoObjectId, string> FailedDataFlowRedeploys);

/// <summary>
/// AB#5537: the impersonation switch was asked to write while
/// <c>ServiceAccountImpersonation:AllowSecretClearing</c> is off.
/// </summary>
public class ServiceAccountImpersonationSwitchDisabledException()
    : InvalidOperationException(
        "Clearing pipeline service account secrets is disabled. Review the dry-run report, then arm it with " +
        "OCTO_SERVICEACCOUNTIMPERSONATION__ALLOWSECRETCLEARING=true for this environment.");
