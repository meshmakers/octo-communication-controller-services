using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// AB#8982: finds the <b>standalone</b> <c>ServiceAccountConfiguration</c>s a tenant's pipelines link
/// through the generic <c>Uses</c> role (per-pipeline overrides such as <c>accounting-ai-prompt</c>),
/// i.e. the accounts no adapter owns through its <c>PipelineServiceAccount</c> edge. The automatic
/// reconcile paths (blueprint applied, tenant load, workload deploy) and the adapter-scoped health
/// aggregate use it so that these accounts are converged and diagnosed like the adapters' own ones.
/// </summary>
/// <remarks>
/// Walks the configurations and asks for the pipelines that use each one
/// (<see cref="ICommunicationRepository.GetPipelinesUsingServiceAccountAsync" />) instead of walking
/// the pipelines' <c>Uses</c> targets: those are materialised as the base <c>RtConfiguration</c>, so a
/// type test would never match a loaded service account (see
/// <c>AdapterService.CreatePipelineConfigurationAsync</c>). A configuration that no pipeline uses is
/// not returned — nothing runs under it, and the manual reconcile endpoint stays available for it.
/// The result is ordered by rtId, so every pass reconciles in the same order.
/// </remarks>
internal static class UsesLinkedServiceAccountLookup
{
    /// <summary>
    /// Standalone configurations linked via <c>Uses</c> by at least one pipeline of the tenant, or —
    /// when <paramref name="adapterRtId" /> is set — by at least one pipeline that runs on that adapter.
    /// Throws when the tenant cannot be read (e.g. its CK cache is being unloaded); the callers isolate it.
    /// </summary>
    public static async Task<IReadOnlyList<RtServiceAccountConfiguration>> FindAsync(
        ICommunicationRepository communicationRepository, string tenantId, OctoObjectId? adapterRtId = null)
    {
        var configurations = await communicationRepository.GetServiceAccountsAsync(tenantId) ?? [];
        var result = new List<RtServiceAccountConfiguration>();
        foreach (var configuration in configurations.OrderBy(c => c.RtId.ToString(), StringComparer.Ordinal))
        {
            // Adapter-owned accounts are reconciled and diagnosed through their adapter.
            if (await communicationRepository.GetAdapterForServiceAccountAsync(tenantId, configuration.RtId) != null)
            {
                continue;
            }

            var pipelines =
                await communicationRepository.GetPipelinesUsingServiceAccountAsync(tenantId, configuration.RtId) ?? [];
            if (pipelines.Count == 0)
            {
                continue;
            }

            if (adapterRtId == null ||
                await AnyRunsOnAdapterAsync(communicationRepository, tenantId, pipelines, adapterRtId.Value))
            {
                result.Add(configuration);
            }
        }

        return result;
    }

    private static async Task<bool> AnyRunsOnAdapterAsync(ICommunicationRepository communicationRepository,
        string tenantId, IEnumerable<RtPipeline> pipelines, OctoObjectId adapterRtId)
    {
        foreach (var pipeline in pipelines)
        {
            RtAdapter? adapter;
            try
            {
                adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId, pipeline.ToRtEntityId());
            }
            catch (CommunicationRepositoryException)
            {
                // Not assigned to any adapter: nothing executes it.
                continue;
            }

            if (adapter?.RtId == adapterRtId)
            {
                return true;
            }
        }

        return false;
    }
}
