using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc />
public sealed class HostnameTemplateResolver : IHostnameTemplateResolver
{
    // Matches {{domain.NAME}}. NAME accepts ASCII letters, digits, dot, dash
    // and underscore — broad enough for typical lookup keys (default, internal,
    // customer-acme, env_a) without permitting whitespace or braces inside the
    // identifier, which would mask malformed templates.
    private static readonly Regex PlaceholderPattern = new(
        @"\{\{\s*domain\.([\w][\w.\-]*)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IOptionsMonitor<CommunicationControllerOptions> _options;

    /// <summary>
    /// Constructor — captures the options monitor so each call sees the current
    /// snapshot of <c>Domains</c> without a pod restart.
    /// </summary>
    public HostnameTemplateResolver(IOptionsMonitor<CommunicationControllerOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> AvailableDomains => SnapshotDomains();

    /// <inheritdoc />
    public bool TryResolve(string? template, out string? resolved, out string? unknownDomainName)
    {
        if (string.IsNullOrEmpty(template))
        {
            resolved = template;
            unknownDomainName = null;
            return true;
        }

        // Fast path: no placeholder syntax at all → return the literal, save
        // the regex walk.
        if (template.IndexOf("{{", StringComparison.Ordinal) < 0)
        {
            resolved = template;
            unknownDomainName = null;
            return true;
        }

        var domains = SnapshotDomains();
        string? firstUnknown = null;

        var output = PlaceholderPattern.Replace(template, match =>
        {
            if (firstUnknown != null)
            {
                return match.Value;
            }

            var name = match.Groups[1].Value;
            if (!domains.TryGetValue(name, out var value))
            {
                firstUnknown = name;
                return match.Value;
            }
            return value;
        });

        if (firstUnknown != null)
        {
            resolved = null;
            unknownDomainName = firstUnknown;
            return false;
        }

        resolved = output;
        unknownDomainName = null;
        return true;
    }

    private IReadOnlyDictionary<string, string> SnapshotDomains()
    {
        // Re-snapshot every call so config reloads (e.g. IConfigurationRoot
        // refresh in test harnesses) are picked up without a pod restart.
        // The dictionary copies the configured comparer if present, otherwise
        // we force case-insensitive lookup so OCTO_…__DOMAINS__DEFAULT and
        // ...__default both work.
        var configured = _options.CurrentValue.Domains;
        if (configured.Comparer == StringComparer.OrdinalIgnoreCase)
        {
            return configured;
        }
        return new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
    }
}
