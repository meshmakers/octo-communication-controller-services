namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Resolves <c>{{domain.NAME}}</c> placeholders inside a workload's
/// <c>Hostname</c> attribute against the named base domains configured on
/// <see cref="Options.CommunicationControllerOptions.Domains"/>.
///
/// Late-binding: placeholders are stored verbatim on the runtime entity
/// (blueprint authors and Studio users see and edit the template), and the
/// concrete hostname is built at deploy time. Workloads therefore stay
/// portable across clusters — moving a tenant from staging to prod picks up
/// the prod cluster's domain config without re-seeding the entities.
///
/// Lookup is case-insensitive on the domain name. The base domain value is
/// substituted verbatim; the resolver does NOT prepend a scheme or strip
/// trailing dots. Strings without any placeholder are returned unchanged.
/// </summary>
public interface IHostnameTemplateResolver
{
    /// <summary>
    /// Returns the configured named domains (e.g. <c>{"default" =&gt; "staging.octo-mesh.com"}</c>).
    /// Exposed for the read-only API endpoint that surfaces choices in the UI.
    /// </summary>
    IReadOnlyDictionary<string, string> AvailableDomains { get; }

    /// <summary>
    /// Substitutes every <c>{{domain.NAME}}</c> placeholder in
    /// <paramref name="template"/> with the matching base domain. Returns
    /// <c>null</c> / empty unchanged. When a referenced NAME is not in
    /// <see cref="AvailableDomains"/>, sets <paramref name="unknownDomainName"/>
    /// to the first offender and returns <c>false</c>; <paramref name="resolved"/>
    /// is then <c>null</c>.
    /// </summary>
    bool TryResolve(string? template, out string? resolved, out string? unknownDomainName);
}
