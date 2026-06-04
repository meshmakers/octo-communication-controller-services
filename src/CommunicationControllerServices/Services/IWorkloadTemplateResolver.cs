namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Context the resolver consumes for per-deploy placeholders that aren't
/// cluster-config-driven (e.g. <c>{{context.tenantId}}</c>).
/// </summary>
/// <param name="TenantId">The tenant the workload is deployed for.</param>
public sealed record WorkloadTemplateContext(string TenantId);

/// <summary>
/// Resolves deploy-time template placeholders inside a workload's
/// <c>Hostname</c>, non-secret <c>ValueOverride.Value</c> entries and
/// <c>ValuesYaml</c> against the controller's configuration plus the
/// per-deploy <see cref="WorkloadTemplateContext"/>.
///
/// Three placeholder families are supported:
/// <list type="bullet">
///   <item><description><c>{{domain.NAME}}</c> — cluster-config-driven, from
///     <see cref="Options.CommunicationControllerOptions.Domains"/>.</description></item>
///   <item><description><c>{{service.NAME}}</c> — cluster-config-driven, from
///     <see cref="Options.CommunicationControllerOptions.ServiceUrls"/>.</description></item>
///   <item><description><c>{{context.tenantId}}</c> — workload-identity-driven,
///     from <see cref="WorkloadTemplateContext"/>.</description></item>
/// </list>
///
/// Late-binding: placeholders are stored verbatim on the runtime entity
/// (blueprint authors and Studio users see and edit the template), and the
/// concrete value is built at deploy time. Workloads therefore stay portable
/// across clusters — moving a tenant from staging to prod picks up the prod
/// cluster's domain / service config without re-seeding the entities.
///
/// Lookup is case-insensitive on the NAME segment. Configured values are
/// substituted verbatim — the resolver does NOT prepend a scheme or strip
/// trailing dots. Strings without any recognised placeholder are returned
/// unchanged.
///
/// Secret-flagged <c>ValueOverride</c> entries are intentionally not part of
/// the input surface: the encryption/decryption sentinel layer owns those
/// values, and a template pass over decrypted secret material would mix the
/// two contracts.
/// </summary>
public interface IWorkloadTemplateResolver
{
    /// <summary>
    /// Returns the configured named domains (e.g. <c>{"default" =&gt; "staging.octo-mesh.com"}</c>).
    /// Exposed for the read-only API endpoint that surfaces choices in the UI.
    /// </summary>
    IReadOnlyDictionary<string, string> AvailableDomains { get; }

    /// <summary>
    /// Returns the configured named service URLs (e.g. <c>{"authority" =&gt; "https://identity.staging.octo-mesh.com"}</c>).
    /// Exposed for the read-only API endpoint that surfaces choices in the UI.
    /// </summary>
    IReadOnlyDictionary<string, string> AvailableServiceUrls { get; }

    /// <summary>
    /// Substitutes every recognised placeholder in <paramref name="template"/>
    /// with the matching value. Returns <c>null</c> / empty unchanged. When a
    /// referenced placeholder cannot be resolved (unknown <c>domain.NAME</c> /
    /// <c>service.NAME</c>, or missing <c>context.tenantId</c> on the
    /// <paramref name="context"/>), sets <paramref name="unknownPlaceholder"/>
    /// to the first offender's full placeholder identifier (e.g.
    /// <c>"domain.does-not-exist"</c> or <c>"context.tenantId"</c>) and returns
    /// <c>false</c>; <paramref name="resolved"/> is then <c>null</c>.
    /// </summary>
    bool TryResolve(string? template, WorkloadTemplateContext context,
        out string? resolved, out string? unknownPlaceholder);
}
