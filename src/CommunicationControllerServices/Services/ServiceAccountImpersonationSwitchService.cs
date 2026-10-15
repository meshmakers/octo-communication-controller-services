using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc />
internal class ServiceAccountImpersonationSwitchService(
    ICommunicationRepository communicationRepository,
    IPipelineServiceAccountProvisioningService provisioningService,
    IAdapterService adapterService,
    IOptions<ServiceAccountImpersonationOptions> options)
    : IServiceAccountImpersonationSwitchService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly Comparer<OctoObjectId> OrdinalRtIdComparer =
        Comparer<OctoObjectId>.Create((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));

    /// <inheritdoc />
    public async Task<ServiceAccountImpersonationSwitchReport> SwitchTenantAsync(string tenantId, bool apply,
        bool redeployDataFlows)
    {
        if (apply && !options.Value.AllowSecretClearing)
        {
            throw new ServiceAccountImpersonationSwitchDisabledException();
        }

        // Steps (a) edges and (b) clear, per account. Never throws per account (the provisioning
        // service reports Failed instead).
        var accounts = (await communicationRepository.GetServiceAccountsAsync(tenantId))
            .OrderBy(a => a.RtId.ToString(), StringComparer.Ordinal)
            .ToList();
        var results = new List<ServiceAccountImpersonationSwitchResult>();
        foreach (var account in accounts)
        {
            results.Add(await provisioningService.SwitchToImpersonationAsync(tenantId, account, apply));
        }

        var redeployed = new List<OctoObjectId>();
        var failedRedeploys = new Dictionary<OctoObjectId, string>();
        if (apply && redeployDataFlows)
        {
            // (c) Redeploy the data flows of every account whose invalidation this run may
            // complete: the ones cleared just now and the secret-less ones a previous run left
            // pending (indistinguishable from long-switched ones — invalidating those again is a
            // harmless throwaway-for-throwaway replacement).
            var candidates = results
                .Select((result, index) => (result, index))
                .Where(c => c.result.CanCompleteInvalidation)
                .ToList();
            var dataFlowsByAccount = new Dictionary<int, IReadOnlyCollection<OctoObjectId>?>();
            var allDataFlows = new SortedSet<OctoObjectId>(OrdinalRtIdComparer);
            foreach (var (result, index) in candidates)
            {
                var dataFlows = await ResolveDataFlowsAsync(tenantId, result);
                dataFlowsByAccount[index] = dataFlows;
                if (dataFlows != null)
                {
                    allDataFlows.UnionWith(dataFlows);
                }
            }

            foreach (var dataFlowRtId in allDataFlows)
            {
                try
                {
                    await adapterService.DeployDataFlowAsync(tenantId, dataFlowRtId);
                    redeployed.Add(dataFlowRtId);
                }
                catch (Exception e)
                {
                    // One unreachable adapter must not stop the others; the report names it.
                    Logger.Warn(e, "[{TenantId}] Redeploying data flow '{DataFlowRtId}' after the impersonation switch failed",
                        tenantId, dataFlowRtId);
                    failedRedeploys[dataFlowRtId] = $"{e.GetType().Name}: {e.Message}";
                }
            }

            // (d) Invalidate the old secret only where EVERY data flow of the account redeployed:
            // an adapter still holding the old secret in its cached configuration would otherwise
            // lose authentication.
            foreach (var (result, index) in candidates)
            {
                results[index] = await CompleteInvalidationAsync(tenantId, accounts[index], result,
                    dataFlowsByAccount[index], failedRedeploys);
            }
        }
        else if (apply)
        {
            for (var i = 0; i < results.Count; i++)
            {
                if (results[i].Outcome == ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation)
                {
                    results[i] = results[i] with
                    {
                        Message = results[i].Message +
                                  " Data flows were not redeployed (redeployDataFlows=false): redeploy them, then re-run " +
                                  "the switch with redeployDataFlows=true to invalidate the old secret."
                    };
                }
            }
        }

        Logger.Info(
            "[{TenantId}] Impersonation switch ({Mode}): {AccountCount} account(s), {SwitchedCount} switched, {PendingCount} pending invalidation, {RedeployedCount} data flow(s) redeployed, {FailedRedeployCount} redeploy failure(s)",
            tenantId, apply ? "apply" : "dry run", results.Count,
            results.Count(r => r.Outcome == ServiceAccountImpersonationSwitchOutcome.Switched),
            results.Count(r => r.Outcome == ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation),
            redeployed.Count, failedRedeploys.Count);

        return new ServiceAccountImpersonationSwitchReport(tenantId, apply, results, redeployed, failedRedeploys);
    }

    /// <summary>
    /// The data flows running the account's pipelines, or <c>null</c> when one could not be
    /// resolved (then the account's redeploy cannot be confirmed and nothing is invalidated). A
    /// pipeline without a data flow is never deployed to an adapter and contributes nothing.
    /// </summary>
    private async Task<IReadOnlyCollection<OctoObjectId>?> ResolveDataFlowsAsync(string tenantId,
        ServiceAccountImpersonationSwitchResult result)
    {
        var dataFlowRtIds = new SortedSet<OctoObjectId>(OrdinalRtIdComparer);
        foreach (var pipelineRtId in result.PipelineRtIds)
        {
            try
            {
                var dataFlow = await communicationRepository.GetDataFlowByPipelineAsync(tenantId, pipelineRtId);
                if (dataFlow != null)
                {
                    dataFlowRtIds.Add(dataFlow.RtId);
                }
            }
            catch (Exception e)
            {
                Logger.Warn(e, "[{TenantId}] Could not resolve the data flow of pipeline '{PipelineRtId}'",
                    tenantId, pipelineRtId);
                return null;
            }
        }

        return dataFlowRtIds;
    }

    private async Task<ServiceAccountImpersonationSwitchResult> CompleteInvalidationAsync(string tenantId,
        RtServiceAccountConfiguration account,
        ServiceAccountImpersonationSwitchResult result, IReadOnlyCollection<OctoObjectId>? dataFlows,
        IReadOnlyDictionary<OctoObjectId, string> failedRedeploys)
    {
        var clearedNow = result.Outcome == ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation;

        string? blocker = null;
        if (dataFlows == null)
        {
            blocker = "the data flows of its pipelines could not be resolved";
        }
        else
        {
            var failed = dataFlows.Where(failedRedeploys.ContainsKey).ToList();
            if (failed.Count > 0)
            {
                blocker = $"the redeploy of data flow(s) {string.Join(", ", failed)} failed";
            }
        }

        if (blocker != null)
        {
            return clearedNow
                ? result with
                {
                    Message = result.Message +
                              $" Not invalidated: {blocker}. Re-run the switch with redeployDataFlows=true to complete it."
                }
                : result;
        }

        try
        {
            await provisioningService.InvalidateSwitchedSecretAsync(tenantId, account, result.ActorClientIds!);
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{TenantId}] The old secret of pipeline service account '{WellKnownName}' could not be invalidated at the identity service",
                tenantId, result.WellKnownName);
            return clearedNow
                ? result with
                {
                    Message = result.Message +
                              $" Not invalidated ({e.GetType().Name}: {e.Message}). Re-run the switch to complete it."
                }
                : result;
        }

        return clearedNow
            ? result with
            {
                Outcome = ServiceAccountImpersonationSwitchOutcome.Switched,
                Message = $"Switched: impersonated by {string.Join(", ", result.ActorClientIds!)}; " +
                          $"{dataFlows!.Count} data flow(s) redeployed; the old secret is invalidated."
            }
            : result with
            {
                Message = result.Message +
                          " Its data flows were redeployed and any secret still valid at the identity service was invalidated."
            };
    }
}
