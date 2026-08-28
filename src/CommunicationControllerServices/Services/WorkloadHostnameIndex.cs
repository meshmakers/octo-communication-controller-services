using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
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
    private volatile FrozenDictionary<string, ActivatorTarget> _byHost =
        FrozenDictionary<string, ActivatorTarget>.Empty;

    public bool TryResolve(string? host, [NotNullWhen(true)] out ActivatorTarget? target)
    {
        target = null;
        return !string.IsNullOrWhiteSpace(host) && _byHost.TryGetValue(host, out target);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, ActivatorTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (var tenantId in adapterCache.GetEnabledTenantIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var workload in await communicationRepository.GetWorkloadsAsync(tenantId))
                {
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

                    if (!map.TryAdd(hostname!, target))
                    {
                        // Two workloads claiming one hostname is a misconfiguration the ingress
                        // cannot resolve either — first one wins here, but say so.
                        logger.LogWarning(
                            "[{TenantId}] Hostname '{Hostname}' is claimed by more than one workload; " +
                            "the activator will use '{WorkloadName}'",
                            tenantId, hostname, map[hostname!].WorkloadName);
                    }
                }
            }
            catch (Exception e)
            {
                // Keep going: one unreadable tenant must not empty the index for the others, which
                // would turn every activator request into a 404.
                logger.LogWarning(e, "[{TenantId}] Could not index workload hostnames for the activator", tenantId);
            }
        }

        _byHost = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        logger.LogDebug("Activator hostname index rebuilt with {Count} entries", _byHost.Count);
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
