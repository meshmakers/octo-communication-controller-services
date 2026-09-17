using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IAdapterNodeCapabilityService" />
internal sealed class AdapterNodeCapabilityService(
    IAdapterCache adapterCache,
    IAdapterPoolConnectionManager poolConnectionManager)
    : IAdapterNodeCapabilityService
{
    private static readonly AdapterNodeCapabilities None =
        new(null, null, "no connected adapter");

    public AdapterNodeCapabilities Resolve(string tenantId, RtEntityId adapterRtEntityId, RtAdapter? adapter)
    {
        if (adapter is { LifecycleMode: RtLifecycleModeEnum.Leased })
        {
            return ResolveFromLendingPool(adapter);
        }

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant) &&
            adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var connectedAdapter))
        {
            return new AdapterNodeCapabilities(connectedAdapter.NodeDescriptors,
                connectedAdapter.PipelineSchemaJson, $"adapter '{adapterRtEntityId}' of tenant '{tenantId}'");
        }

        return None;
    }

    private AdapterNodeCapabilities ResolveFromLendingPool(RtAdapter adapter)
    {
        var lenderTenantId = adapter.LentFromTenantId;
        var poolRtId = adapter.LentFromAdapterPoolRtId;

        // A half-configured borrower is refused at workload deploy (DeploymentSiteService), but DeployPipeline
        // can reach one that was never deployed — answer "nothing known" rather than guessing.
        if (string.IsNullOrWhiteSpace(lenderTenantId) || string.IsNullOrWhiteSpace(poolRtId))
        {
            return new AdapterNodeCapabilities(null, null,
                "no lending pool (LentFromTenantId / LentFromAdapterPoolRtId are not both set)");
        }

        var capabilities = poolConnectionManager.TryGetPoolCapabilities(lenderTenantId, poolRtId);
        if (capabilities != null)
        {
            return new AdapterNodeCapabilities(capabilities.NodeDescriptors, capabilities.PipelineSchemaJson,
                $"pool {poolRtId} of tenant '{lenderTenantId}' (member '{capabilities.MemberId}')");
        }

        // 🔴 Deliberately NOT falling through to the borrower's own adapter cache. A tenant that
        // switched an adapter from AlwaysOn to Leased can still have a stale AdapterById entry from
        // the process it used to run; those descriptors describe a process that no longer exists and
        // no longer executes anything. Reporting "nothing known" degrades to the name-based
        // fallback, which is wrong-but-conservative; reporting a dead process's descriptors would be
        // wrong-and-confident.
        return new AdapterNodeCapabilities(null, null,
            $"pool {poolRtId} of tenant '{lenderTenantId}' (no member registered on this controller instance)");
    }
}
