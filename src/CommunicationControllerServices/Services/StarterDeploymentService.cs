using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     AB#8452 — what <see cref="IStarterDeploymentService.DeployDefaultsAsync" /> did.
/// </summary>
internal enum StarterDeploymentOutcome
{
    /// <summary>The default site and its adapter were deployed (or the deploy of the adapter was queued).</summary>
    Deployed,

    /// <summary>Neither the site nor the adapter is Undeployed (deployed, pending, disabled or failed); nothing was changed.</summary>
    NothingToDeploy,

    /// <summary>
    ///     The tenant does not have exactly one Cloud deployment site with exactly one adapter (not the shape of a fresh
    ///     tenant); nothing was changed.
    /// </summary>
    NotFreshTenantShape,

    /// <summary>A deploy call failed; the cause is logged, the blueprint install is not affected.</summary>
    Failed
}

/// <summary>
///     AB#8452 — deploys the default Cloud deployment site of a fresh tenant and then its default Mesh Adapter, so a
///     tenant created with a starter blueprint can validate and run pipelines without a manual deploy.
/// </summary>
/// <remarks>
///     <para>
///         The caller decides WHEN (only right after the platform itself enabled Communication for a blueprint that
///         needs System.Ai, see <c>BlueprintAppliedConsumer</c>); this service decides WHAT and never undeploys
///         anything. It acts only on the shape a fresh tenant has after <c>EnableCommunication</c>: exactly one Cloud
///         deployment site with exactly one adapter. A site or adapter that is deployed (or pending) already is left
///         alone, so a repeated call is idempotent. Every deploy it starts is written to the tenant's event log and the
///         service log. Cost: one adapter pod per tenant on the central cluster.
///     </para>
/// </remarks>
internal interface IStarterDeploymentService
{
    /// <summary>
    ///     Deploys the default site and adapter of <paramref name="tenantId" /> when the tenant has the fresh shape.
    ///     Never throws (except for cancellation of the host); failures are logged and reported as
    ///     <see cref="StarterDeploymentOutcome.Failed" />.
    /// </summary>
    Task<StarterDeploymentOutcome> DeployDefaultsAsync(string tenantId, string? blueprintId, Guid correlationId);
}

/// <inheritdoc />
internal sealed class StarterDeploymentService(
    ICommunicationRepository communicationRepository,
    IDeploymentSiteService deploymentSiteService,
    ICommunicationEventService eventService,
    ILogger<StarterDeploymentService> logger) : IStarterDeploymentService
{
    /// <inheritdoc />
    public async Task<StarterDeploymentOutcome> DeployDefaultsAsync(string tenantId, string? blueprintId,
        Guid correlationId)
    {
        try
        {
            var sites = await communicationRepository.GetDeploymentSitesAsync(tenantId);
            if (sites.Count != 1 || sites.First().Environment != RtEnvironmentEnum.Cloud)
            {
                logger.LogWarning(
                    "Default site and adapter of tenant '{TenantId}' are not deployed after blueprint '{BlueprintId}' (correlation {CorrelationId}): the tenant has {SiteCount} deployment site(s), expected exactly one Cloud site; deploy them by hand",
                    tenantId, blueprintId, correlationId, sites.Count);
                return StarterDeploymentOutcome.NotFreshTenantShape;
            }

            var site = sites.First();
            var workloads = await communicationRepository.GetWorkloadsForDeploymentSiteAsync(tenantId, site.RtId);
            var adapters = workloads.OfType<RtAdapter>().ToList();
            if (adapters.Count != 1 || workloads.Count != 1)
            {
                logger.LogWarning(
                    "Default site and adapter of tenant '{TenantId}' are not deployed after blueprint '{BlueprintId}' (correlation {CorrelationId}): site '{SiteName}' hosts {WorkloadCount} workload(s), expected exactly one adapter; deploy them by hand",
                    tenantId, blueprintId, correlationId, site.Name, workloads.Count);
                return StarterDeploymentOutcome.NotFreshTenantShape;
            }

            var adapter = adapters[0];
            var deployedSomething = false;

            if (site.DeploymentState == RtDeploymentStateEnum.Undeployed)
            {
                await deploymentSiteService.DeployDeploymentSiteAsync(tenantId, site.RtId);
                deployedSomething = true;
                logger.LogInformation(
                    "Deployment site '{SiteName}' ({SiteRtId}) of tenant '{TenantId}' deployed by the platform: blueprint '{BlueprintId}' needs System.Ai and Communication was just enabled for it (correlation {CorrelationId})",
                    site.Name, site.RtId, tenantId, blueprintId, correlationId);
            }

            if (adapter.DeploymentState == RtDeploymentStateEnum.Undeployed)
            {
                // The operator may not have registered the site yet; the controller queues the deploy notification
                // until it does (OperatorConnectionManager), so no wait is needed here.
                await deploymentSiteService.DeployWorkloadAsync(tenantId, adapter.RtId);
                deployedSomething = true;
                logger.LogInformation(
                    "Adapter '{AdapterName}' ({AdapterRtId}) of tenant '{TenantId}' deployed by the platform: blueprint '{BlueprintId}' needs System.Ai and Communication was just enabled for it (correlation {CorrelationId})",
                    adapter.Name, adapter.RtId, tenantId, blueprintId, correlationId);
            }

            if (!deployedSomething)
            {
                return StarterDeploymentOutcome.NothingToDeploy;
            }

            await eventService.StoreInformationEventAsync(tenantId,
                $"Deployment site '{site.Name}' and adapter '{adapter.Name}' deployed by the platform because blueprint '{blueprintId}' needs AI (one adapter pod for this tenant).");
            return StarterDeploymentOutcome.Deployed;
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Default site and adapter of tenant '{TenantId}' could not be deployed after blueprint '{BlueprintId}' (correlation {CorrelationId}): {Cause}. The blueprint stays installed; deploy them by hand (DeployDeploymentSite, then DeployWorkload)",
                tenantId, blueprintId, correlationId, e.Message);
            return StarterDeploymentOutcome.Failed;
        }
    }
}
