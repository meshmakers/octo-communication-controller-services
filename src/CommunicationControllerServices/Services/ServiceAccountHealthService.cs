using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// The status vocabulary of the AB#5112 health aggregate — string-typed in the DTO so the
/// endpoint's JSON stays self-describing (and adding a status is not a wire-breaking enum change).
/// </summary>
internal static class ServiceAccountHealthStatus
{
    public const string Healthy = "Healthy";
    public const string Violation = "Violation";
    public const string Unknown = "Unknown";
    public const string NotApplicable = "NotApplicable";

    public const string OverallHealthy = "Healthy";
    public const string OverallUnhealthy = "Unhealthy";
    public const string OverallUnknown = "Unknown";
}

/// <inheritdoc cref="IServiceAccountHealthService" />
internal class ServiceAccountHealthService(
    ICommunicationRepository communicationRepository,
    IPipelineServiceAccountResolver serviceAccountResolver,
    IIdentityClientReader identityClientReader,
    IOptions<CommunicationControllerOptions> options)
    : IServiceAccountHealthService
{
    // Check names — the machine-readable half of every finding.
    private const string AssociationCheck = "association";
    private const string ConfigurationCheck = "configuration";
    private const string ClientCheck = "client";
    private const string SecretCheck = "secret";
    private const string RolesCheck = "roles";
    private const string DelegationCheck = "delegation";
    private const string TenantCheck = "tenant";
    private const string IssuerUriCheck = "issuerUri";

    /// <inheritdoc />
    public async Task<ServiceAccountHealthDto> GetAdapterHealthAsync(string tenantId, RtAdapter adapter)
    {
        var checks = new List<ServiceAccountHealthCheckDto>();

        // Same two-step lookup as the reconcile: the linked default first, then the deterministic
        // well-known name — an existing-but-unlinked entity must show up as "association missing",
        // not as a completely absent account.
        var linked = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);
        var configuration = linked ?? await communicationRepository.GetServiceAccountByWellKnownNameAsync(tenantId,
            PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId));

        var adapterName = adapter.Name ?? adapter.RtId.ToString();
        checks.Add(linked != null
            ? Healthy(AssociationCheck)
            : Violation(AssociationCheck, "association-missing",
                $"Adapter '{adapterName}' ({adapter.RtId}) has no ServiceAccountConfiguration linked through its " +
                "'PipelineServiceAccount' association. Run the service-account reconcile for this adapter " +
                "(POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile), or open the adapter in Studio."));

        if (configuration == null)
        {
            checks.Add(Violation(ConfigurationCheck, "configuration-missing",
                $"No ServiceAccountConfiguration exists for adapter '{adapterName}' ({adapter.RtId}). " +
                "The reconcile creates it with the declaration defaults."));
            checks.AddRange(NotEvaluated(ClientCheck, RolesCheck, DelegationCheck, SecretCheck, TenantCheck,
                IssuerUriCheck));
            return Build(null, checks);
        }

        checks.Add(Healthy(ConfigurationCheck));
        await EvaluateConfigurationAsync(tenantId, configuration, checks);
        return Build(configuration, checks);
    }

    /// <inheritdoc />
    public async Task<ServiceAccountHealthDto> GetConfigurationHealthAsync(string tenantId,
        RtServiceAccountConfiguration configuration)
    {
        // The caller already holds the entity, so existence is a fact — reported anyway to keep the
        // two variants' check lists congruent (minus the association, which has no meaning for a
        // standalone / per-pipeline configuration).
        var checks = new List<ServiceAccountHealthCheckDto> { Healthy(ConfigurationCheck) };
        await EvaluateConfigurationAsync(tenantId, configuration, checks);
        return Build(configuration, checks);
    }

    /// <summary>
    /// The shared core: everything both variants check about the configuration entity and its
    /// identity client. Reads the four original attributes defensively
    /// (<c>GetAttributeValueOrDefault</c>), exactly like the reconcile — the half-written entity is
    /// one of the states this aggregate exists to diagnose, and a generated mandatory-attribute
    /// getter throwing would turn the diagnosis into the disease.
    /// </summary>
    private async Task EvaluateConfigurationAsync(string tenantId, RtServiceAccountConfiguration configuration,
        List<ServiceAccountHealthCheckDto> checks)
    {
        var clientId = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.ClientId));
        var secret = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.ClientSecret));
        var issuerUri = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.IssuerUri));
        var configuredTenantId = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.TenantId));

        // AB#5111 declaration semantics: null AssignedRoleNames = legacy account, roles unmanaged.
        var declaredRoleNames = configuration.AssignedRoleNames?.ToList();
        var allowDelegation = configuration.AllowDelegation ?? true;

        // ---- identity-backed checks: client existence, role drift, delegation grant ----
        if (string.IsNullOrWhiteSpace(clientId))
        {
            checks.Add(Violation(ClientCheck, "client-missing",
                "The configuration declares no ClientId, so no identity client can back it. " +
                "The reconcile derives and materialises one."));
            checks.AddRange(NotEvaluated(RolesCheck, DelegationCheck));
        }
        else
        {
            var lookup = await identityClientReader.GetClientAsync(tenantId, clientId!,
                includeRoles: declaredRoleNames != null);
            switch (lookup.Status)
            {
                case IdentityClientLookupStatus.NotFound:
                    checks.Add(Violation(ClientCheck, "client-missing",
                        $"Client '{clientId}' does not exist in tenant '{tenantId}'. " +
                        "Run the service-account reconcile to materialise it."));
                    // Without a client there are no role edges and no grants to compare against.
                    checks.AddRange(NotEvaluated(RolesCheck, DelegationCheck));
                    break;

                case IdentityClientLookupStatus.Unavailable:
                    var reason = lookup.UnavailableReason ?? "the identity service could not be queried";
                    checks.Add(Unknown(ClientCheck, $"Client existence could not be verified: {reason}."));
                    checks.Add(declaredRoleNames == null
                        ? RolesNotDeclared()
                        : Unknown(RolesCheck, $"Role assignment could not be verified: {reason}."));
                    checks.Add(Unknown(DelegationCheck, $"The delegation grant could not be verified: {reason}."));
                    break;

                default:
                    checks.Add(Healthy(ClientCheck));
                    checks.Add(EvaluateRoles(declaredRoleNames, lookup.AssignedRoleNames));
                    checks.Add(EvaluateDelegation(allowDelegation, lookup.Client!.AllowedGrantTypes));
                    break;
            }
        }

        // ---- local checks: secret presence, tenant, issuer ----
        checks.Add(!string.IsNullOrWhiteSpace(secret)
            ? Healthy(SecretCheck)
            : Violation(SecretCheck, "secret-missing",
                "The configuration holds no client secret, so no pipeline can authenticate with this account. " +
                "The reconcile issues one (an existing secret is never rotated); a compromised secret is replaced " +
                "via the rotate endpoint."));

        checks.Add(configuredTenantId == tenantId
            ? Healthy(TenantCheck)
            : Violation(TenantCheck, "tenant-mismatch",
                string.IsNullOrWhiteSpace(configuredTenantId)
                    ? "The configuration carries no TenantId; the adapter cannot address the delegation grant without it."
                    : $"The configuration points at tenant '{configuredTenantId}' instead of '{tenantId}' — " +
                      "typically an entity imported from another tenant. The reconcile re-derives it."));

        checks.Add(PipelineServiceAccountProvisioningService.IsIssuerUriHealthy(issuerUri,
            options.Value.AuthorityUrl)
            ? Healthy(IssuerUriCheck)
            : Violation(IssuerUriCheck, "issuer-uri-drift",
                string.IsNullOrWhiteSpace(issuerUri)
                    ? "The configuration carries no IssuerUri."
                    : $"IssuerUri '{issuerUri}' is neither the portable " +
                      $"'{PipelineServiceAccountProvisioningService.IssuerUriToken}' token nor this installation's " +
                      "authority. The reconcile converges it to the token."));
    }

    /// <summary>
    /// Role drift against the declaration. Only called with a Found client; a <c>null</c> actual
    /// list then means the role reads themselves failed (partial identity degradation) → Unknown.
    /// </summary>
    private static ServiceAccountHealthCheckDto EvaluateRoles(List<string>? declaredRoleNames,
        IReadOnlyList<string>? actualRoleNames)
    {
        if (declaredRoleNames == null)
        {
            return RolesNotDeclared();
        }

        if (actualRoleNames == null)
        {
            return Unknown(RolesCheck,
                "The client's role assignment could not be read from the identity service.");
        }

        // Ordinal on purpose: role names are exact identifiers tenant-side (the reconcile
        // materialises them verbatim), so a case-folded "match" would hide a real mismatch.
        var missing = declaredRoleNames.Except(actualRoleNames, StringComparer.Ordinal).ToList();
        var superfluous = actualRoleNames.Except(declaredRoleNames, StringComparer.Ordinal).ToList();

        if (missing.Count == 0 && superfluous.Count == 0)
        {
            return Healthy(RolesCheck);
        }

        return new ServiceAccountHealthCheckDto(RolesCheck, ServiceAccountHealthStatus.Violation, "roles-drift",
            "The client's roles drift from the declaration " +
            $"(missing: {FormatRoles(missing)}; superfluous: {FormatRoles(superfluous)}). " +
            "A reconcile by a caller holding UserManagement (or the next system-initiated one) syncs them.",
            missing, superfluous);
    }

    /// <summary>
    /// The on-behalf-of grant against <c>AllowDelegation</c>. Both directions are drift: a missing
    /// grant breaks delegation (AB#5031) silently, a superfluous one keeps a capability alive the
    /// declaration revoked.
    /// </summary>
    private static ServiceAccountHealthCheckDto EvaluateDelegation(bool allowDelegation,
        IEnumerable<string>? allowedGrantTypes)
    {
        var hasGrant = allowedGrantTypes?.Contains(Constants.OnBehalfOfGrantType) ?? false;
        if (hasGrant == allowDelegation)
        {
            return Healthy(DelegationCheck);
        }

        return Violation(DelegationCheck, "delegation-drift",
            allowDelegation
                ? "The declaration allows delegation, but the identity client lacks the on-behalf-of grant — " +
                  "delegated (on-behalf-of) token requests are refused. The reconcile adds the grant."
                : "The declaration forbids delegation, but the identity client still carries the on-behalf-of " +
                  "grant. The reconcile removes it.");
    }

    private static ServiceAccountHealthCheckDto RolesNotDeclared()
    {
        return new ServiceAccountHealthCheckDto(RolesCheck, ServiceAccountHealthStatus.NotApplicable, null,
            "The configuration declares no AssignedRoleNames (pre-3.32.0 legacy account) — its role edges are " +
            "deliberately unmanaged. Set the attribute to opt into declarative role management.");
    }

    private static ServiceAccountHealthDto Build(RtServiceAccountConfiguration? configuration,
        IReadOnlyList<ServiceAccountHealthCheckDto> checks)
    {
        var overall = checks.Any(c => c.Status == ServiceAccountHealthStatus.Violation)
            ? ServiceAccountHealthStatus.OverallUnhealthy
            : checks.Any(c => c.Status == ServiceAccountHealthStatus.Unknown)
                ? ServiceAccountHealthStatus.OverallUnknown
                : ServiceAccountHealthStatus.OverallHealthy;

        return new ServiceAccountHealthDto(overall,
            configuration?.RtId.ToString(),
            configuration?.RtWellKnownName,
            configuration == null
                ? null
                : ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.ClientId)),
            checks);
    }

    private static ServiceAccountHealthCheckDto Healthy(string check)
    {
        return new ServiceAccountHealthCheckDto(check, ServiceAccountHealthStatus.Healthy, null, null);
    }

    private static ServiceAccountHealthCheckDto Violation(string check, string code, string message)
    {
        return new ServiceAccountHealthCheckDto(check, ServiceAccountHealthStatus.Violation, code, message);
    }

    private static ServiceAccountHealthCheckDto Unknown(string check, string message)
    {
        return new ServiceAccountHealthCheckDto(check, ServiceAccountHealthStatus.Unknown, null, message);
    }

    private static IEnumerable<ServiceAccountHealthCheckDto> NotEvaluated(params string[] checkNames)
    {
        return checkNames.Select(name => new ServiceAccountHealthCheckDto(name,
            ServiceAccountHealthStatus.NotApplicable, null,
            "Not evaluated — a prerequisite check failed."));
    }

    private static string FormatRoles(IReadOnlyList<string> roles)
    {
        return roles.Count == 0 ? "none" : string.Join(", ", roles.Select(r => $"'{r}'"));
    }

    /// <summary>Same defensive read as everywhere on the service-account path.</summary>
    private static string? ReadAttribute(RtServiceAccountConfiguration configuration, string attributeName)
    {
        return configuration.GetAttributeValueOrDefault(attributeName) as string;
    }
}
