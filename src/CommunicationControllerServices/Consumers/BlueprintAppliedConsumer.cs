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
/// still only picked up by its workload deploy (<c>DeploymentSiteService.DeployWorkloadAsync</c>), by the
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
///
/// <para>
/// AB#6631 — the same broadcasts also carry the second thing a blueprint does behind the controller's
/// back: it rewrites the <c>Pipeline</c> entities in place. A <c>BlueprintUpdated</c> (an update) or a
/// force re-apply replaces the definition of a pipeline that an adapter is already running, but the
/// adapter's registered triggers are only rebuilt when the controller pushes a changed configuration.
/// Nothing did, so the live <c>FromHttpRequest</c> route kept the authorization of the OLD definition
/// (<c>requiredRoles</c>, <c>allowAnonymous</c>) until someone undeployed and deployed the data flow by
/// hand. After the service accounts are in place, the consumer therefore pushes the refreshed definitions
/// of the pipelines each connected adapter already runs (<see cref="IAdapterService.RefreshDeployedPipelinesAsync"/>).
/// Both messages are handled here — <c>BlueprintApplied</c> for an install or force re-apply,
/// <c>BlueprintUpdated</c> for <c>UpdateBlueprint</c>.
/// </para>
/// </summary>
internal class BlueprintAppliedConsumer(
    ILogger<BlueprintAppliedConsumer> logger,
    IPipelineServiceAccountProvisioningService serviceAccountProvisioningService,
    IAdapterService adapterService)
    : IDistributedConsumer<BlueprintApplied>, IDistributedConsumer<BlueprintUpdated>
{
    public Task ConsumeAsync(IDistributedContext<BlueprintApplied> context)
    {
        var message = context.Message;
        return ReconcileAsync(message.TenantId, message.BlueprintId, message.EntitiesAdded,
            message.EntitiesUpdated);
    }

    public Task ConsumeAsync(IDistributedContext<BlueprintUpdated> context)
    {
        var message = context.Message;
        return ReconcileAsync(message.TenantId, message.BlueprintId, message.EntitiesAdded,
            message.EntitiesUpdated);
    }

    private async Task ReconcileAsync(string tenantId, string blueprintId, int entitiesAdded, int entitiesUpdated)
    {
        // A blueprint that touched nothing cannot have created an adapter or a service account, nor
        // changed a pipeline definition.
        if (entitiesAdded == 0 && entitiesUpdated == 0)
        {
            return;
        }

        logger.LogDebug(
            "Blueprint '{BlueprintId}' applied to tenant '{TenantId}' ({EntitiesAdded} added, {EntitiesUpdated} updated) — reconciling pipeline service accounts and deployed pipelines",
            blueprintId, tenantId, entitiesAdded, entitiesUpdated);

        try
        {
            // EnsureTenantProvisionedAsync is contractually non-throwing and per-adapter isolated;
            // it also tolerates tenants where Communication is not enabled (the adapter lookup
            // fails and is reported, not thrown). The belt-and-braces catch mirrors
            // DefaultConfigurationCreatorService.EnsurePipelineServiceAccountsAsync — a broadcast
            // consumer must never fault the bus over one tenant.
            var report = await serviceAccountProvisioningService.EnsureTenantProvisionedAsync(tenantId);

            if (report.HasChanges)
            {
                logger.LogInformation(
                    "Pipeline service accounts reconciled after blueprint '{BlueprintId}' on tenant '{TenantId}': {Provisioned} provisioned, {Repaired} repaired",
                    blueprintId, tenantId, report.Provisioned, report.Repaired);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Pipeline service account reconcile after blueprint '{BlueprintId}' on tenant '{TenantId}' failed; the next tenant load retries",
                blueprintId, tenantId);
        }

        // Separate try: a failed service-account sweep must not keep a changed route authorization from
        // reaching the adapter (AB#6631).
        try
        {
            var adapters = await adapterService.RefreshDeployedPipelinesAsync(tenantId);
            if (adapters > 0)
            {
                logger.LogInformation(
                    "Blueprint '{BlueprintId}' changed pipeline definitions on tenant '{TenantId}'; refreshed configuration pushed to {AdapterCount} adapter(s)",
                    blueprintId, tenantId, adapters);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Refreshing the deployed pipelines after blueprint '{BlueprintId}' on tenant '{TenantId}' failed; a redeploy of the data flow applies the new definitions",
                blueprintId, tenantId);
        }
    }
}
