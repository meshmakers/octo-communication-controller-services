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
    ///     Rebuilds the index across every enabled tenant. Driven by
    ///     <c>WorkloadHostnameIndexBackgroundService</c> on a timer. Never throws for a single
    ///     tenant: one unreadable tenant must not empty the index for the others, because an empty
    ///     index silently turns every activator request into a 404.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
