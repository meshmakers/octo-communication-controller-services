using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     The workload an inbound request belongs to, plus the address the activator forwards to
///     once it is awake.
/// </summary>
/// <param name="TenantId">Tenant owning the workload.</param>
/// <param name="WorkloadRtId">Runtime id — the wake gate's key.</param>
/// <param name="WorkloadName">Human-readable name, used in log and error messages only.</param>
/// <param name="Address">In-cluster base address, built from the configured template.</param>
public sealed record ActivatorTarget(string TenantId, OctoObjectId WorkloadRtId, string WorkloadName, Uri Address);

/// <summary>
///     Maps the public hostname of every ingress-enabled workload to that workload (AB#4923).
///
///     The activator has to attribute an inbound request that carries nothing but the original
///     Host header — nginx forwards it to the default backend unchanged, with no hint about which
///     workload it was meant for. Resolving that per request against the repository would put a
///     tenant-scoped database round-trip in front of a path that also serves the controller's own
///     API, so the mapping is held in memory and rebuilt in the background instead.
///
///     Hostnames are compared case-insensitively (DNS is), and stored resolved: the entity may
///     carry a <c>{{domain.NAME}}</c> template, while the deployed Ingress and therefore the
///     inbound Host header carry the resolved value.
/// </summary>
public interface IWorkloadHostnameIndex
{
    /// <summary>
    ///     Resolves an inbound Host header. False for every host that is not an ingress-enabled
    ///     workload — including the controller's own API hostname, which is the common case and
    ///     must stay a plain dictionary miss.
    /// </summary>
    bool TryResolve(string? host, [NotNullWhen(true)] out ActivatorTarget? target);

    /// <summary>
    ///     Resolves an inbound Host header plus request path (AB#5300). A host claimed by exactly one
    ///     workload resolves regardless of the path, as before. A host claimed by several — the
    ///     platform's default layout, <c>adapter.{{domain.default}}</c> with one ingress rule
    ///     <c>/&lt;tenantId&gt;</c> per adapter — is disambiguated by the first path segment, which
    ///     the adapter chart renders as the lowercased tenant id. A shared host whose first segment
    ///     names no claimant is a miss, never a guess: waking the wrong tenant's adapter and
    ///     forwarding it somebody else's request is the one outcome this index must not produce.
    /// </summary>
    bool TryResolve(string? host, string? path, [NotNullWhen(true)] out ActivatorTarget? target);

    /// <summary>
    ///     Rebuilds the index across every enabled tenant. Driven by
    ///     <c>WorkloadHostnameIndexBackgroundService</c> on a timer. Never throws for a single
    ///     tenant: one unreadable tenant must not empty the index for the others, because an empty
    ///     index silently turns every activator request into a 404.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
