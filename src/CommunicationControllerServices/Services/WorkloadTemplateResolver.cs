using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc />
public sealed class WorkloadTemplateResolver : IWorkloadTemplateResolver
{
    // Matches one of three families in a single pass:
    //   {{domain.NAME}}, {{service.NAME}}, {{context.tenantId}}
    // NAME accepts ASCII letters, digits, dot, dash and underscore — broad
    // enough for typical lookup keys (default, internal, customer-acme,
    // env_a) without permitting whitespace or braces inside the identifier,
    // which would mask malformed templates.
    //
    // Whitelisting the three families on purpose: an open-ended
    // {{anything}} pattern would clash with literal {{ }} fragments in
    // ValuesYaml (charts sometimes ship example values that look like Go
    // templates). The fast-path substring guard below also relies on the
    // family prefixes.
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*(?:(?<ns>domain|service)\.(?<name>[\w][\w.\-]*)|(?<ctx>context\.tenantId))\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IOptionsMonitor<CommunicationControllerOptions> _options;

    /// <summary>
    /// Constructor — captures the options monitor so each call sees the current
    /// snapshot of <c>Domains</c> / <c>ServiceUrls</c> without a pod restart.
    /// </summary>
    public WorkloadTemplateResolver(IOptionsMonitor<CommunicationControllerOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> AvailableDomains => SnapshotCaseInsensitive(_options.CurrentValue.Domains);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> AvailableServiceUrls => SnapshotCaseInsensitive(_options.CurrentValue.ServiceUrls);

    /// <inheritdoc />
    public bool TryResolve(string? template, WorkloadTemplateContext context,
        out string? resolved, out string? unknownPlaceholder)
    {
        if (string.IsNullOrEmpty(template))
        {
            resolved = template;
            unknownPlaceholder = null;
            return true;
        }

        // Fast path: no placeholder syntax at all → return the literal, save
        // the regex walk. The substring check on '{{' covers all three
        // families since every recognised placeholder starts with it.
        if (template.IndexOf("{{", StringComparison.Ordinal) < 0)
        {
            resolved = template;
            unknownPlaceholder = null;
            return true;
        }

        var domains = SnapshotCaseInsensitive(_options.CurrentValue.Domains);
        var serviceUrls = SnapshotCaseInsensitive(_options.CurrentValue.ServiceUrls);
        string? firstUnknown = null;

        var output = PlaceholderPattern.Replace(template, match =>
        {
            if (firstUnknown != null)
            {
                return match.Value;
            }

            if (match.Groups["ctx"].Success)
            {
                if (string.IsNullOrEmpty(context.TenantId))
                {
                    firstUnknown = "context.tenantId";
                    return match.Value;
                }
                return context.TenantId;
            }

            var ns = match.Groups["ns"].Value;
            var name = match.Groups["name"].Value;
            var lookup = ns.Equals("domain", StringComparison.OrdinalIgnoreCase) ? domains : serviceUrls;
            if (!lookup.TryGetValue(name, out var value))
            {
                firstUnknown = $"{ns.ToLowerInvariant()}.{name}";
                return match.Value;
            }
            return value;
        });

        if (firstUnknown != null)
        {
            resolved = null;
            unknownPlaceholder = firstUnknown;
            return false;
        }

        resolved = output;
        unknownPlaceholder = null;
        return true;
    }

    private static IReadOnlyDictionary<string, string> SnapshotCaseInsensitive(Dictionary<string, string> configured)
    {
        // Re-snapshot every call so config reloads (e.g. IConfigurationRoot
        // refresh in test harnesses) are picked up without a pod restart.
        // The dictionary copies the configured comparer if present, otherwise
        // we force case-insensitive lookup so OCTO_…__{DOMAINS|SERVICEURLS}__DEFAULT
        // and ...__default both work.
        if (configured.Comparer == StringComparer.OrdinalIgnoreCase)
        {
            return configured;
        }
        return new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
    }
}
