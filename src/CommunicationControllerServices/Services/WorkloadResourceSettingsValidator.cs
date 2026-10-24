using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     AB#7427 (Feature AB#7417): validates the typed sizing attributes <c>CpuRequest</c>, <c>CpuLimit</c>,
///     <c>MemoryRequest</c> and <c>MemoryLimit</c> of a deployable workload. Nothing hooks the write of a
///     workload entity (entities are written generically through the asset repository), so the deploy is
///     the enforcement net: an invalid value is rejected before anything is sent to the operator.
/// </summary>
internal static class WorkloadResourceSettingsValidator
{
    /// <summary>
    ///     Throws a <see cref="DeploymentSiteServiceException" /> naming the attribute when the typed settings
    ///     cannot work. Empty attributes are fine: they mean "the chart default".
    /// </summary>
    public static void Validate(string tenantId, RtDeployableWorkload workload)
    {
        // One sizing path per pool: a pool's members are sized by PoolMember*, which stays the pool
        // author's declaration. Accepting the inherited attributes on a pool as well would silently give
        // it two sizings (AB#7427, merging them into one set belongs to CK v2, AB#5584).
        if (workload is RtAdapterPool)
        {
            var set = TypedSettings(workload).Where(s => !string.IsNullOrWhiteSpace(s.Value))
                .Select(s => s.Name).ToList();
            if (set.Count > 0)
            {
                throw DeploymentSiteServiceException.AdapterPoolInheritedSizingSet(tenantId, workload.RtId,
                    workload.Name, set);
            }

            return;
        }

        var parsed = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (name, value) in TypedSettings(workload))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!KubernetesQuantity.TryParse(value, out var quantity))
            {
                throw DeploymentSiteServiceException.WorkloadResourceQuantityInvalid(tenantId, workload.RtId,
                    workload.Name, name, value);
            }

            parsed[name] = quantity;
        }

        EnsureRequestWithinLimit(tenantId, workload, parsed, nameof(RtDeployableWorkload.CpuRequest),
            workload.CpuRequest, nameof(RtDeployableWorkload.CpuLimit), workload.CpuLimit);
        EnsureRequestWithinLimit(tenantId, workload, parsed, nameof(RtDeployableWorkload.MemoryRequest),
            workload.MemoryRequest, nameof(RtDeployableWorkload.MemoryLimit), workload.MemoryLimit);
    }

    private static void EnsureRequestWithinLimit(string tenantId, RtDeployableWorkload workload,
        Dictionary<string, decimal> parsed, string requestName, string? requestText, string limitName,
        string? limitText)
    {
        if (parsed.TryGetValue(requestName, out var request) && parsed.TryGetValue(limitName, out var limit) &&
            request > limit)
        {
            throw DeploymentSiteServiceException.WorkloadResourceRequestExceedsLimit(tenantId, workload.RtId,
                workload.Name, requestName, requestText!.Trim(), limitName, limitText!.Trim());
        }
    }

    private static IEnumerable<(string Name, string? Value)> TypedSettings(RtDeployableWorkload workload)
    {
        yield return (nameof(RtDeployableWorkload.CpuRequest), workload.CpuRequest);
        yield return (nameof(RtDeployableWorkload.CpuLimit), workload.CpuLimit);
        yield return (nameof(RtDeployableWorkload.MemoryRequest), workload.MemoryRequest);
        yield return (nameof(RtDeployableWorkload.MemoryLimit), workload.MemoryLimit);
    }
}
