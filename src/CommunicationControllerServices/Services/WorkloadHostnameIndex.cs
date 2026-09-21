using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Default <see cref="IWorkloadHostnameIndex"/>. Singleton; the map is swapped wholesale on
///     every refresh so readers never see a half-built index and need no lock.
/// </summary>
internal sealed class WorkloadHostnameIndex(
    ILogger<WorkloadHostnameIndex> logger,
    IAdapterCache adapterCache,
    ICommunicationRepository communicationRepository,
    IWorkloadTemplateResolver templateResolver,
    IOptions<CommunicationControllerOptions> options) : IWorkloadHostnameIndex
{
    /// <summary>
    ///     Every workload that claims a host, in enumeration order (AB#5300). One entry is the
    ///     dedicated-host case; several are the shared-host layout every cluster ships by default.
    /// </summary>
    private volatile FrozenDictionary<string, ActivatorTarget[]> _byHost =
        FrozenDictionary<string, ActivatorTarget[]>.Empty;

    public bool TryResolve(string? host, [NotNullWhen(true)] out ActivatorTarget? target)
        => TryResolve(host, null, out target);

    public bool TryResolve(string? host, string? path, [NotNullWhen(true)] out ActivatorTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(host) || !_byHost.TryGetValue(host, out var claimants))
        {
            return false;
        }

        if (claimants.Length == 1)
        {
            // A dedicated host: the ingress rule's path is the tenant's too, but nginx has already
            // matched it, and a request that reaches the activator under this host can only be
            // meant for this workload.
            target = claimants[0];
            return true;
        }

        // A shared host: the ingress path is "/<tenantId>" per adapter (adapter chart, lowercased),
        // so the first path segment names the tenant - and therefore the claimant.
        var tenantSegment = FirstPathSegment(path);
        if (tenantSegment is null)
        {
            return false;
        }

        target = claimants.FirstOrDefault(c =>
            string.Equals(c.TenantId, tenantSegment, StringComparison.OrdinalIgnoreCase));
        return target is not null;
    }

    internal static string? FirstPathSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var trimmed = path.AsSpan().TrimStart('/');
        var end = trimmed.IndexOf('/');
        var segment = end < 0 ? trimmed : trimmed[..end];
        return segment.IsEmpty ? null : segment.ToString();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, List<ActivatorTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach (var tenantId in adapterCache.GetEnabledTenantIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var workload in await communicationRepository.GetWorkloadsAsync(tenantId))
                {
                    // AB#4924: adapter pools are not reachable through the activator, and this is
                    // the one place where "one namespace for everything" is still assumed. The
                    // address this index publishes is built from the release name alone
                    // (ActivatorWorkloadAddressTemplate defaults to "http://{release}"), which
                    // resolves in the CONTROLLER's namespace — a pool runs in the platform
                    // namespace, so an entry for one would point the activator at a Service that
                    // is not there, or worse at a same-named one that is. A pool is reached by
                    // being leased, never by an inbound request, so it has no business in a
                    // hostname map; skipping it is the scoping this increment needs and the
                    // narrowest change that achieves it.
                    if (workload is RtAdapterPool)
                    {
                        continue;
                    }

                    if (!workload.IngressEnabled || string.IsNullOrWhiteSpace(workload.Hostname))
                    {
                        continue;
                    }

                    // The entity may carry a {{domain.NAME}} template; the deployed Ingress — and
                    // therefore the Host header we have to match — carries the resolved value.
                    if (!templateResolver.TryResolve(workload.Hostname, new WorkloadTemplateContext(tenantId),
                            out var hostname, out var unknownPlaceholder))
                    {
                        logger.LogWarning(
                            "[{TenantId}] Workload '{WorkloadName}' has an unresolvable hostname placeholder " +
                            "'{Placeholder}'; it will not be reachable through the activator",
                            tenantId, workload.Name, unknownPlaceholder);
                        continue;
                    }

                    var target = new ActivatorTarget(tenantId, workload.RtId, workload.Name ?? string.Empty,
                        BuildAddress(tenantId, workload.RtId.ToString()));

                    if (!map.TryGetValue(hostname!, out var claimants))
                    {
                        claimants = [];
                        map[hostname!] = claimants;
                    }

                    if (claimants.Any(c => string.Equals(c.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Two workloads of the SAME tenant on one host is the ambiguity the path
                        // cannot resolve either (both render "/<tenantId>"); first one wins, say so.
                        // Different tenants on one host are the platform's default layout and are
                        // told apart by the path (AB#5300) - no warning for those.
                        logger.LogWarning(
                            "[{TenantId}] Hostname '{Hostname}' is claimed by more than one workload of this tenant; " +
                            "the activator will use '{WorkloadName}'",
                            tenantId, hostname, claimants.First(c => c.TenantId == tenantId).WorkloadName);
                        continue;
                    }

                    claimants.Add(target);
                }
            }
            catch (Exception e)
            {
                // Keep going: one unreadable tenant must not empty the index for the others, which
                // would turn every activator request into a 404.
                logger.LogWarning(e, "[{TenantId}] Could not index workload hostnames for the activator", tenantId);
            }
        }

        _byHost = map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        // Info, not Debug: this is once per refresh interval, and it is the only signal that the
        // activator can attribute anything at all. A silent index is indistinguishable from a
        // feature that is switched off.
        logger.LogInformation("Activator hostname index rebuilt with {Count} entries on {HostCount} host(s)",
            map.Values.Sum(v => v.Count), _byHost.Count);
    }

    private Uri BuildAddress(string tenantId, string workloadRtId)
    {
        var template = options.Value.ActivatorWorkloadAddressTemplate;
        return new Uri(template.Replace("{release}", ReleaseName(tenantId, workloadRtId), StringComparison.Ordinal));
    }

    /// <summary>
    ///     Mirrors the operator's <c>WorkloadReconciler.ReleaseName</c> / <c>K8sNaming.DnsName</c>:
    ///     lowercase, every non-alphanumeric character becomes a dash, runs of dashes collapse,
    ///     leading and trailing dashes are trimmed, capped at Helm's 53-character release-name
    ///     limit. The operator names the workload's Deployment, Service and Ingress after the
    ///     release, so this is also its Service name.
    ///
    ///     Duplicated rather than shared because the two services have no common library, and
    ///     coupling the controller to the operator assembly for one pure string function would be
    ///     the worse trade. <c>ReleaseNameTests</c> pins the cases that matter; the pair ships as
    ///     one release train.
    /// </summary>
    internal static string ReleaseName(string tenantId, string workloadRtId)
    {
        const int maxLength = 53;
        var joined = $"{tenantId}-{workloadRtId}";

        var sb = new StringBuilder(joined.Length);
        foreach (var c in joined.ToLowerInvariant())
        {
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }

        var name = sb.ToString();
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        name = name.Trim('-');
        return name.Length > maxLength ? name[..maxLength].TrimEnd('-') : name;
    }
}
