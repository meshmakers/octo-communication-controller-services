using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IAdapterNodeCapabilityService" />
internal sealed class AdapterNodeCapabilityService(
    IAdapterCache adapterCache,
    IAdapterPoolConnectionManager poolConnectionManager,
    ICommunicationRepository communicationRepository)
    : IAdapterNodeCapabilityService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly AdapterNodeCapabilities None =
        new(null, null, "no connected adapter");

    public async Task<AdapterNodeCapabilities> ResolveAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtAdapter? adapter)
    {
        if (adapter is { LifecycleMode: RtLifecycleModeEnum.Leased })
        {
            return await ResolveFromLendingPoolAsync(tenantId, adapter);
        }

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant) &&
            adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var connectedAdapter))
        {
            return new AdapterNodeCapabilities(connectedAdapter.NodeDescriptors,
                connectedAdapter.PipelineSchemaJson, $"adapter '{adapterRtEntityId}' of tenant '{tenantId}'");
        }

        return None;
    }

    private async Task<AdapterNodeCapabilities> ResolveFromLendingPoolAsync(string tenantId, RtAdapter adapter)
    {
        // AB#5271: the lender is named by the adapter's LentFrom edge to a borrower-local mirror,
        // not by two attributes on the adapter itself. A repository read, deliberately not cached —
        // see LentFromReference.
        LentFromReference? lentFrom;
        try
        {
            lentFrom = LentFromReference.FromMirror(
                await communicationRepository.GetLentAdapterPoolForAdapterAsync(tenantId, adapter.RtId));
        }
        catch (Exception e)
        {
            // Same stance as an unset edge: this method answers "whose descriptors decide", and
            // "nothing known" degrades every caller to its name-based fallback. Failing the deploy
            // on an unreadable mirror would be a harder failure than the question warrants.
            Logger.Warn(e, "[{TenantId}] Could not resolve the lending pool of leased adapter '{AdapterName}'",
                tenantId, adapter.Name);
            lentFrom = null;
        }

        // A half-configured borrower is refused at workload deploy (DeploymentSiteService), but DeployPipeline
        // can reach one that was never deployed — answer "nothing known" rather than guessing.
        if (lentFrom is null)
        {
            return new AdapterNodeCapabilities(null, null,
                "no lending pool (the adapter has no LentFrom mirror, or the mirror names no lender)");
        }

        var lenderTenantId = lentFrom.LenderTenantId;
        var poolRtId = lentFrom.AdapterPoolRtId;

        var capabilities = poolConnectionManager.TryGetAdapterPoolCapabilities(lenderTenantId, poolRtId);
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
