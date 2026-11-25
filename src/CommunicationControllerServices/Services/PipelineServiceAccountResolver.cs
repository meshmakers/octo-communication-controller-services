using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IPipelineServiceAccountResolver" />
internal class PipelineServiceAccountResolver(ICommunicationRepository communicationRepository)
    : IPipelineServiceAccountResolver
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task<PipelineServiceAccountResolution> ResolveAsync(string tenantId, OctoObjectId pipelineRtId,
        OctoObjectId adapterRtId)
    {
        var ownOverride = await GetPipelineOverrideAsync(tenantId, pipelineRtId);
        if (ownOverride != null)
        {
            return new PipelineServiceAccountResolution(ownOverride, PipelineServiceAccountSource.PipelineOverride);
        }

        var adapterDefault = await GetAdapterDefaultAsync(tenantId, adapterRtId);
        return adapterDefault != null
            ? new PipelineServiceAccountResolution(adapterDefault, PipelineServiceAccountSource.AdapterDefault)
            : PipelineServiceAccountResolution.Unresolved;
    }

    /// <inheritdoc />
    public async Task<PipelineServiceAccountResolution> ResolveForPipelineAsync(string tenantId,
        RtEntityId pipelineRtEntityId)
    {
        var ownOverride = await GetPipelineOverrideAsync(tenantId, pipelineRtEntityId.RtId);
        if (ownOverride != null)
        {
            return new PipelineServiceAccountResolution(ownOverride, PipelineServiceAccountSource.PipelineOverride);
        }

        RtAdapter? adapter;
        try
        {
            adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId, pipelineRtEntityId);
        }
        catch (CommunicationRepositoryException e)
        {
            // A pipeline with no Executes edge cannot inherit an adapter default. That is a
            // separate error condition owned by the deploy paths (PipelineAdapterNotAssigned);
            // for the identity question it simply means "unresolved".
            Logger.Debug(e,
                "[{TenantId}] Pipeline '{PipelineRtEntityId}' has no resolvable adapter for service-account inheritance",
                tenantId, pipelineRtEntityId);
            return PipelineServiceAccountResolution.Unresolved;
        }

        if (adapter == null)
        {
            return PipelineServiceAccountResolution.Unresolved;
        }

        var adapterDefault = await GetAdapterDefaultAsync(tenantId, adapter.RtId);
        return adapterDefault != null
            ? new PipelineServiceAccountResolution(adapterDefault, PipelineServiceAccountSource.AdapterDefault)
            : PipelineServiceAccountResolution.Unresolved;
    }

    /// <inheritdoc />
    public Task<RtServiceAccountConfiguration?> GetAdapterDefaultAsync(string tenantId, OctoObjectId adapterRtId)
    {
        return communicationRepository.GetServiceAccountForAdapterAsync(tenantId, adapterRtId);
    }

    /// <summary>
    ///     A service-account configuration regardless of how it was materialised: the typed instance the
    ///     resolver/provisioning builds, or the base-typed <see cref="RtConfiguration" /> the generic <c>Uses</c>
    ///     load returns (the Mongo discriminator is the generic one, so a type test never matches a loaded
    ///     override — found live on AB#5111, missed by the resolver until AB#9776). Detect by CkTypeId.
    /// </summary>
    internal static bool IsServiceAccountConfiguration(RtConfiguration configuration)
    {
        return configuration is RtServiceAccountConfiguration ||
               SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId.Equals(configuration.CkTypeId);
    }

    /// <summary>
    /// The per-pipeline override: a service-account configuration (<see cref="IsServiceAccountConfiguration" />)
    /// among the configurations the pipeline links through the generic <c>Uses</c> role. No model change
    /// was needed for this — Pipeline→Configuration already exists, and the configuration list
    /// is exactly what the adapter receives.
    /// </summary>
    private async Task<RtConfiguration?> GetPipelineOverrideAsync(string tenantId,
        OctoObjectId pipelineRtId)
    {
        var configurations = await communicationRepository.GetConfigurationsByPipelineAsync(tenantId, pipelineRtId);

        var serviceAccounts = configurations
            .Where(IsServiceAccountConfiguration)
            // Deterministic pick. The model does not (and cannot) cap the generic Uses role at
            // one service account, so a hand-linked pipeline may carry several; picking by rtId
            // keeps every controller pod and every redeploy on the same answer.
            .OrderBy(c => c.RtId.ToString(), StringComparer.Ordinal)
            .ToList();

        if (serviceAccounts.Count > 1)
        {
            Logger.Warn(
                "[{TenantId}] Pipeline '{PipelineRtId}' links {Count} service accounts via 'Uses'; using '{Chosen}'. Link exactly one to make the identity unambiguous (AB#5027).",
                tenantId, pipelineRtId, serviceAccounts.Count, serviceAccounts[0].RtId);
        }

        return serviceAccounts.FirstOrDefault();
    }
}
