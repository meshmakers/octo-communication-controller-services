using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
/// AB#5111 — "reconcile automatically after import/creation". The controller has no generic
/// runtime-entity change stream to subscribe to (the asset repository publishes no per-entity
/// insert/update events on the distribution event hub; the only entity-shaped broadcast,
/// <c>ComControllerAdapterUpdate</c>, is a controller-to-controller cache sync whose registration
/// is disabled in Program.cs). What DOES exist is the blueprint lifecycle broadcast: the asset
/// repository publishes <see cref="BlueprintApplied"/> after every successful blueprint apply — and
/// a blueprint apply is exactly the bulk path that creates <c>Adapter</c> /
/// <c>ServiceAccountConfiguration</c> entities behind the controller's back, between two tenant
/// loads. Subscribing here closes that window with an existing mechanism instead of inventing an
/// event bus.
///
/// <para>
/// The remaining gap, documented rather than plugged: an Adapter or ServiceAccountConfiguration
/// created <b>directly</b> through the asset repository's GraphQL/REST API (outside a blueprint) is
/// still only picked up by its workload deploy (<c>PoolService.DeployWorkloadAsync</c>), by the
/// next tenant load (<c>DefaultConfigurationCreatorService.StartTenantAsync</c>), or by the manual
/// reconcile endpoints (AB#5111). Closing it for real needs entity-change events from the asset
/// repository — a platform feature, not something to counterfeit here with polling.
/// </para>
///
/// <para>
/// The sweep is the idempotent tenant-wide reconcile, so over-triggering is cheap (steady state is
/// a handful of reads plus one identity command per adapter) and self-applied blueprints (the
/// controller's own service-managed ones run through a different runner and publish nothing) cannot
/// loop: the reconcile writes entities through the repository, never through a blueprint.
/// </para>
/// </summary>
internal class BlueprintAppliedConsumer(
    ILogger<BlueprintAppliedConsumer> logger,
    IPipelineServiceAccountProvisioningService serviceAccountProvisioningService)
    : IDistributedConsumer<BlueprintApplied>
{
    public async Task ConsumeAsync(IDistributedContext<BlueprintApplied> context)
    {
        var message = context.Message;

        // A blueprint that touched nothing cannot have created an adapter or a service account.
        if (message.EntitiesAdded == 0 && message.EntitiesUpdated == 0)
        {
            return;
        }

        logger.LogDebug(
            "Blueprint '{BlueprintId}' applied to tenant '{TenantId}' ({EntitiesAdded} added, {EntitiesUpdated} updated) — reconciling pipeline service accounts",
            message.BlueprintId, message.TenantId, message.EntitiesAdded, message.EntitiesUpdated);

        try
        {
            // EnsureTenantProvisionedAsync is contractually non-throwing and per-adapter isolated;
            // it also tolerates tenants where Communication is not enabled (the adapter lookup
            // fails and is reported, not thrown). The belt-and-braces catch mirrors
            // DefaultConfigurationCreatorService.EnsurePipelineServiceAccountsAsync — a broadcast
            // consumer must never fault the bus over one tenant.
            var report = await serviceAccountProvisioningService.EnsureTenantProvisionedAsync(message.TenantId);

            if (report.HasChanges)
            {
                logger.LogInformation(
                    "Pipeline service accounts reconciled after blueprint '{BlueprintId}' on tenant '{TenantId}': {Provisioned} provisioned, {Repaired} repaired",
                    message.BlueprintId, message.TenantId, report.Provisioned, report.Repaired);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Pipeline service account reconcile after blueprint '{BlueprintId}' on tenant '{TenantId}' failed; the next tenant load retries",
                message.BlueprintId, message.TenantId);
        }
    }
}
