using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Read-only aggregate over everything that can be wrong with a pipeline service account
/// (AB#5112, Epic AB#4979) — the diagnosis counterpart of the AB#5111 reconcile: every violation
/// this reports is healed by exactly that reconcile (or, for the secret, by the AB#5032 rotation),
/// and the endpoints' answers say so.
///
/// <para>
/// Checks: the adapter association (adapter-scoped variant only), configuration existence, the
/// identity client's existence, secret presence (never the value), role drift against the
/// declaration (only for declarative accounts — a legacy account without
/// <c>AssignedRoleNames</c> reports NotApplicable), the on-behalf-of grant against
/// <c>AllowDelegation</c>, the <c>TenantId</c> and <c>IssuerUri</c> (AB#5115: empty is the
/// installation default and healthy; the <c>{{service.authority}}</c> token and this
/// installation's authority stay healthy as legacy spellings — the same rule the convergence
/// sweep applies, shared via
/// <see cref="PipelineServiceAccountProvisioningService.IsInstallationIssuer" /> — and any other
/// concrete value is a deliberate foreign target), and the AB#5114 <c>impersonation</c> view of a
/// secretless account.
/// </para>
///
/// <para>
/// 🔴 Degrades instead of failing: when the identity service cannot be asked (down, or the call
/// has no bearer token to forward — see <see cref="IIdentityClientReader" />), the identity-backed
/// checks report <c>Unknown</c> and the aggregate still answers. A health endpoint that dies with
/// its patient is useless.
/// </para>
/// </summary>
public interface IServiceAccountHealthService
{
    /// <summary>
    /// Health of the adapter's pipeline service account — the adapter-scoped variant, which
    /// additionally checks that the <c>PipelineServiceAccount</c> association is present. Like the
    /// reconcile, an existing-but-unlinked configuration (found by its deterministic well-known
    /// name) is still evaluated, so the report shows "association missing" next to the otherwise
    /// intact account instead of an empty page.
    /// </summary>
    Task<ServiceAccountHealthDto> GetAdapterHealthAsync(string tenantId, RtAdapter adapter);

    /// <summary>
    /// Health of one <c>ServiceAccountConfiguration</c>, addressed directly — for callers that
    /// hold the configuration (Studio's configuration view, a per-pipeline override).
    /// </summary>
    Task<ServiceAccountHealthDto> GetConfigurationHealthAsync(string tenantId,
        RtServiceAccountConfiguration configuration);
}
