using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
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
    private const string ImpersonationCheck = "impersonation";

    /// <summary>
    /// AB#5114: what the aggregate knows about the account's impersonation actor — the adapter's
    /// own client (its default pipeline service account, the credentials AB#5072 projects into the
    /// pod). <paramref name="HasAdapterContext" /> is false only for the configuration-scoped
    /// variant of a standalone account that no pipeline links yet; <paramref name="ActorClientId" />
    /// is non-null only when an actor with a usable secret exists that is not the account itself.
    /// </summary>
    private sealed record ImpersonationContext(bool HasAdapterContext, string? ActorClientId, string? NoActorReason)
    {
        public static readonly ImpersonationContext NoAdapter = new(false, null, null);
    }

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
                IssuerUriCheck, ImpersonationCheck));
            return Build(null, checks);
        }

        checks.Add(Healthy(ConfigurationCheck));
        // The adapter variant always evaluates the adapter's DEFAULT account — which is the
        // adapter's own client itself (AB#5072), so there is never a distinct impersonation actor
        // here: the account either authenticates with its own secret or the adapter has no
        // credentials at all.
        await EvaluateConfigurationAsync(tenantId, configuration, checks,
            new ImpersonationContext(HasAdapterContext: true, ActorClientId: null,
                NoActorReason:
                "this account IS the adapter's own client (AB#5072) — it cannot impersonate itself, so a " +
                "usable secret is its only credential"));
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
        await EvaluateConfigurationAsync(tenantId, configuration, checks,
            await ResolveImpersonationContextAsync(tenantId, configuration));
        return Build(configuration, checks);
    }

    /// <summary>
    /// AB#5114: derives the impersonation actor for the configuration-scoped variant. An
    /// adapter-owned account is the adapter's own client itself (no distinct actor, like the
    /// adapter variant); a standalone account's actors are the own clients of the adapters whose
    /// pipelines link it via <c>Uses</c> — the first one with a usable secret is reported (the
    /// aggregate names one concrete actor, the reconcile materialises the edges for all of them).
    /// Best-effort: any lookup failure degrades to "no adapter context" instead of failing the
    /// aggregate — this is a read-only diagnosis endpoint.
    /// </summary>
    private async Task<ImpersonationContext> ResolveImpersonationContextAsync(string tenantId,
        RtServiceAccountConfiguration configuration)
    {
        var targetClientId = ReadAttribute(configuration, nameof(RtServiceAccountConfiguration.ClientId));
        try
        {
            var owningAdapter = await communicationRepository.GetAdapterForServiceAccountAsync(tenantId,
                configuration.RtId);
            if (owningAdapter != null)
            {
                return new ImpersonationContext(HasAdapterContext: true, ActorClientId: null,
                    NoActorReason:
                    "this account IS the adapter's own client (AB#5072) — it cannot impersonate itself, so a " +
                    "usable secret is its only credential");
            }

            var pipelines = await communicationRepository.GetPipelinesUsingServiceAccountAsync(tenantId,
                configuration.RtId);
            var hasAdapterContext = false;
            var seenAdapterRtIds = new HashSet<OctoObjectId>();
            foreach (var pipeline in pipelines ?? [])
            {
                RtAdapter? adapter;
                try
                {
                    adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
                        pipeline.ToRtEntityId());
                }
                catch (CommunicationRepositoryException)
                {
                    continue; // pipeline without an Executes edge — owned by the deploy paths
                }

                if (adapter == null || !seenAdapterRtIds.Add(adapter.RtId))
                {
                    continue;
                }

                hasAdapterContext = true;
                var adapterDefault = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);
                var actorClientId = adapterDefault == null
                    ? null
                    : ReadAttribute(adapterDefault, nameof(RtServiceAccountConfiguration.ClientId));
                var actorSecret = adapterDefault == null
                    ? null
                    : ReadAttribute(adapterDefault, nameof(RtServiceAccountConfiguration.ClientSecret));
                if (!string.IsNullOrWhiteSpace(actorClientId) && actorClientId != targetClientId &&
                    PipelineServiceAccountProvisioningService.IsSecretUsable(actorSecret))
                {
                    return new ImpersonationContext(HasAdapterContext: true, actorClientId, null);
                }
            }

            return hasAdapterContext
                ? new ImpersonationContext(HasAdapterContext: true, ActorClientId: null,
                    NoActorReason:
                    "none of the adapters using this account has an own client with a usable secret " +
                    "(reconcile the adapters to provision their pipeline service accounts, AB#5072)")
                : ImpersonationContext.NoAdapter;
        }
        catch (Exception)
        {
            return ImpersonationContext.NoAdapter;
        }
    }

    /// <summary>
    /// The shared core: everything both variants check about the configuration entity and its
    /// identity client. Reads the four original attributes defensively
    /// (<c>GetAttributeValueOrDefault</c>), exactly like the reconcile — the half-written entity is
    /// one of the states this aggregate exists to diagnose, and a generated mandatory-attribute
    /// getter throwing would turn the diagnosis into the disease.
    /// </summary>
    private async Task EvaluateConfigurationAsync(string tenantId, RtServiceAccountConfiguration configuration,
        List<ServiceAccountHealthCheckDto> checks, ImpersonationContext impersonation)
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

        // ---- local checks: secret/impersonation, tenant, issuer (AB#5115/AB#5114 semantics) ----
        var secretUsable = PipelineServiceAccountProvisioningService.IsSecretUsable(secret);

        // Credential-aware secret check: an empty (or placeholder) secret is a violation ONLY when
        // impersonation cannot stand in for it — the adapter-side dual path (AB#5114) makes a
        // secretless account with a capable actor a fully legitimate configuration.
        if (secretUsable)
        {
            checks.Add(Healthy(SecretCheck));
        }
        else if (impersonation.ActorClientId != null)
        {
            checks.Add(Healthy(SecretCheck,
                "The configuration holds no usable client secret; the account is used via impersonation — " +
                $"adapter client '{impersonation.ActorClientId}' presents its own credentials and requests this " +
                "account's identity (AB#5114). See the impersonation check."));
        }
        else
        {
            checks.Add(Violation(SecretCheck, "secret-missing",
                "The configuration holds no usable client secret and no adapter client is available to impersonate " +
                "the account (AB#5114)" +
                (impersonation.NoActorReason == null ? "" : $" — {impersonation.NoActorReason}") +
                ". The reconcile issues a secret (an existing secret is never rotated); a compromised secret is " +
                "replaced via the rotate endpoint."));
        }

        // AB#5114: the impersonation view. Only meaningful for a secretless account; the MayActAs
        // edge itself is NOT verifiable from here — the identity REST surface the controller reads
        // through (GET {tenantId}/v1/Clients/…, IIdentityClientReader) exposes clients and role
        // edges but no client-to-client associations — so a capable actor is reported as Unknown
        // rather than pretending certainty either way.
        if (secretUsable)
        {
            checks.Add(NotApplicable(ImpersonationCheck,
                "The configuration authenticates with its own client secret; impersonation is not used."));
        }
        else if (!impersonation.HasAdapterContext)
        {
            checks.Add(NotApplicable(ImpersonationCheck,
                "No adapter context: nothing links this configuration to an adapter (no owning adapter, no " +
                "pipeline Uses edge), so the impersonating actor cannot be determined here. The adapter-scoped " +
                "health endpoint or the reconcile after linking a pipeline gives the definitive picture."));
        }
        else if (impersonation.ActorClientId != null)
        {
            checks.Add(Unknown(ImpersonationCheck,
                $"Adapter client '{impersonation.ActorClientId}' can impersonate this account only while identity " +
                $"holds a MayActAs edge onto '{clientId}'. The controller cannot verify that edge (the identity " +
                "REST surface exposes no client-association read), so it is reported as unknown; the reconcile " +
                "materialises it (AB#5114) and the identity token endpoint refuses impersonation without it."));
        }
        else
        {
            checks.Add(NotApplicable(ImpersonationCheck,
                "Impersonation is not possible: " +
                (impersonation.NoActorReason ?? "the adapter has no own client") +
                ". The secret check carries the resulting violation."));
        }

        // AB#5115: empty issuer/tenant is the canonical installation default; the historic
        // installation spellings stay healthy (the next reconcile converges them to empty); any
        // other concrete value is a deliberate foreign target, not drift.
        var issuerIsInstallation = string.IsNullOrWhiteSpace(issuerUri) ||
                                   PipelineServiceAccountProvisioningService.IsInstallationIssuer(issuerUri,
                                       options.Value.AuthorityUrl);

        if (string.IsNullOrWhiteSpace(configuredTenantId))
        {
            checks.Add(Healthy(TenantCheck,
                "installation default — an empty TenantId means the tenant the adapter runs for (AB#5115)."));
        }
        else if (configuredTenantId == tenantId)
        {
            checks.Add(Healthy(TenantCheck,
                "The configuration names the current tenant explicitly — the pre-AB#5115 spelling of the " +
                "installation default; the next reconcile converges it to empty."));
        }
        else if (!issuerIsInstallation)
        {
            checks.Add(Healthy(TenantCheck,
                $"The configuration points at tenant '{configuredTenantId}' of the foreign identity target " +
                $"'{issuerUri}' — a deliberate foreign pairing the reconcile leaves alone (AB#5115)."));
        }
        else
        {
            checks.Add(Violation(TenantCheck, "tenant-mismatch",
                $"The configuration points at tenant '{configuredTenantId}' instead of '{tenantId}' while its " +
                "IssuerUri resolves to this installation — typically an entity imported from another tenant. " +
                "Clear the TenantId (empty = the adapter's tenant, AB#5115) or pair it with the foreign IssuerUri " +
                "it belongs to."));
        }

        if (string.IsNullOrWhiteSpace(issuerUri))
        {
            checks.Add(Healthy(IssuerUriCheck,
                "installation default — an empty IssuerUri means the adapter's own installation (AB#5115)."));
        }
        else if (issuerIsInstallation)
        {
            checks.Add(Healthy(IssuerUriCheck,
                "The configuration spells this installation explicitly (the AB#5111 " +
                $"'{PipelineServiceAccountProvisioningService.IssuerUriToken}' token or this installation's " +
                "authority URL) — still healthy; the next reconcile converges it to empty (AB#5115)."));
        }
        else
        {
            checks.Add(Healthy(IssuerUriCheck,
                $"IssuerUri '{issuerUri}' is a deliberate foreign identity target; the reconcile leaves it " +
                "alone (AB#5115)."));
        }
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

    /// <summary>
    /// Healthy with an explanation — for the AB#5115/AB#5114 findings whose green verdict rests on
    /// a semantic ("installation default", "used via impersonation") the operator should see.
    /// </summary>
    private static ServiceAccountHealthCheckDto Healthy(string check, string message)
    {
        return new ServiceAccountHealthCheckDto(check, ServiceAccountHealthStatus.Healthy, null, message);
    }

    private static ServiceAccountHealthCheckDto NotApplicable(string check, string message)
    {
        return new ServiceAccountHealthCheckDto(check, ServiceAccountHealthStatus.NotApplicable, null, message);
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
