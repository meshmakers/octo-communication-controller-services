using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

// ReSharper disable once ClassNeverInstantiated.Global
internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    IDiagnosticsService diagnosticsService,
    IOptions<CommunicationControllerOptions> options,
    ITriggerManagementService triggerManagementService,
    ICommandClient<CreateIdentityDataCommandRequest> createIdentityDataCommandClient,
    ISystemContext systemContext,
    IPoolService poolService,
    IAdapterCachePublish adapterCachePublish,
    IAdapterService adapterService,
    FailedTenantRegistry failedTenantRegistry,
    ICommunicationEventService communicationEventService,
    IBlueprintService blueprintService,
    IEnumerable<IBlueprintEmbeddedSource> embeddedBlueprintSources)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        Constants.CommunicationControllerServiceIdentityDataVersionKey,
        Constants.CommunicationControllerServiceIdentityDataVersionValue,
        null, // migrationService - we don't need migrations in this service
        null, // ckModelUpgradeService - we don't need CK model migrations in this service
        null, // runtimeRepositoryProvider - not needed without CK model migrations
        Constants.CommunicationControllerServiceEnabledKey, // the service can be enabled/disabled
        failedTenantRegistry: failedTenantRegistry,
        blueprintService: blueprintService,
        embeddedBlueprintSources: embeddedBlueprintSources
        )
{
    /// <summary>
    /// Prefix used to recognise blueprints this service owns. Every embedded blueprint whose
    /// name starts with <c>System.Communication.</c> is auto-applied on tenant Enable /
    /// startup by the base class's <see cref="DefaultConfigurationCreatorServiceBase.ApplyServiceManagedBlueprintsAsync"/>;
    /// each blueprint's <c>requires:</c> block decides whether it actually runs on the given
    /// tenant. By OctoMesh convention these are <c>System.*</c> blueprints — service-managed,
    /// Studio hides install / uninstall actions for them, and the runtime trusts that the
    /// owning service keeps them in sync per tenant.
    /// </summary>
    /// <remarks>
    /// Note the trailing dot: only <c>System.Communication.&lt;Variant&gt;-x.y.z</c> matches,
    /// not the legacy unnamed <c>System.Communication-x.y.z</c> blueprint (whose folder was
    /// retired in favour of the Release / MainLatest variants). The trailing dot keeps the
    /// match anchored so a future foreign embedded source named e.g.
    /// <c>System.CommunicationOps-1.0.0</c> wouldn't accidentally get applied here.
    /// </remarks>
    protected override string ServiceManagedBlueprintPrefix => "System.Communication.";

    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override async Task ImportCkModelAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // The Communication CK model + initial Pool/Adapter seed entities are now packaged
        // together in the Communication-<x.y.z> blueprint. Applying the blueprint resolves the
        // ckModelDependencies (System.Communication-[3.0,4.0)) and upserts the seed entities, so
        // the explicit ImportCkModelAsync that used to live here is no longer needed. The runner
        // is idempotent: re-applying the same version is a no-op (unless `force` is set), so the
        // same call is safe to make from Enable and from per-tenant startup.
        await ApplyServiceManagedBlueprintsAsync(tenantContext.TenantId, throwOnFailure: true);
    }


    protected override async Task StartTenantAsync(string tenantId)
    {
        logger.LogInformation("Loading tenant '{TenantId}'", tenantId);
        if (!await IsEnabledAsync(tenantId))
        {
            logger.LogInformation("Schema not available for tenant '{TenantId}'", tenantId);
            return;
        }

        // Auto-roll forward any service-managed blueprint whose embedded version is newer than
        // the one currently installed for this tenant. Mirrors how ICkModelUpgradeService is
        // designed to run on tenant start for CK models — the runner short-circuits on
        // already-current versions, so the cost when nothing changed is minimal.
        await ApplyServiceManagedBlueprintsAsync(tenantId, throwOnFailure: false);

        // try to load the configuration from the cache
        await adapterCachePublish.LoadConfigurationAsync(tenantId);

        await adapterService.PosUpdateTenantAsync(tenantId);
        await poolService.PosUpdateTenantAsync(tenantId);

        await triggerManagementService.UpdateScheduleAsync(tenantId);
    }

    /// <summary>
    /// Surfaces a blueprint auto-apply failure on the startup path (<c>throwOnFailure: false</c>)
    /// as an Error event in the tenant's event log so operators see the regression alongside
    /// other tenant lifecycle errors. The base class has already logged the failure;
    /// this override adds the audit-event side effect.
    /// </summary>
    protected override async Task OnServiceManagedBlueprintApplyFailedAsync(
        string tenantId,
        BlueprintId blueprintId,
        OperationResult operationResult,
        CancellationToken cancellationToken)
    {
        await communicationEventService.StoreErrorEventAsync(tenantId,
            $"Auto-update of blueprint {blueprintId.FullName} failed: {string.Join("; ", operationResult.GetMessages())}");
    }

    /// <summary>
    /// AB#4255: Communication may only be disabled once nothing it manages is deployed any more. The
    /// answer is a verified precondition on the persisted deployment state (mirrored back by the
    /// operator), not a teardown: the operator undeploys through the existing paths and retries.
    /// Pipelines and triggers are deliberately not part of it — pipelines are no cluster resource and
    /// become undeployable once their adapter is gone, and Disable removes trigger schedules itself.
    /// </summary>
    protected override async Task<string?> GetDisableBlockerAsync(string tenantId)
    {
        var activeDeployments = await poolService.GetActiveDeploymentsAsync(tenantId);
        return activeDeployments.Count == 0 ? null : BuildDisableBlockedMessage(tenantId, activeDeployments);
    }

    /// <summary>
    /// The operator-facing refusal. Surfaced verbatim by CLI, MCP and Studio, so it names every
    /// resource with its kind and state and the commands that remove them.
    /// </summary>
    internal static string BuildDisableBlockedMessage(string tenantId, IReadOnlyList<ActiveDeployment> activeDeployments)
    {
        var resources = string.Join(", ", activeDeployments.Select(d => d.ToString()));
        return $"Communication cannot be disabled for tenant '{tenantId}' while the following resources are still deployed: " +
               $"{resources}. Undeploy them first - workloads with UndeployWorkload, pools with UndeployPool " +
               $"(octo-cli in a context of tenant '{tenantId}', or Refinery Studio > Communication > Adapters / Applications / Pools) - " +
               "then retry DisableCommunication.";
    }

    protected override async Task StopTenantAsync(string tenantId)
    {
        logger.LogInformation("Unloading tenant '{TenantId}'", tenantId);

        await triggerManagementService.RemoveScheduleAsync(tenantId);

        await adapterService.PreUpdateTenantAsync(tenantId);
        await poolService.PreUpdateTenantAsync(tenantId);
    }

    protected override async Task OnTenantStartFailedAsync(string tenantId, Exception exception)
    {
        await communicationEventService.StoreErrorEventAsync(tenantId,
            $"Tenant startup failed: {exception.Message}. Will retry in background.");
    }

    protected override async Task OnTenantRetrySucceededAsync(string tenantId)
    {
        await communicationEventService.StoreInformationEventAsync(tenantId,
            "Tenant startup retry succeeded.");
    }

    protected override async Task OnTenantRetriesExhaustedAsync(string tenantId, int retryCount)
    {
        await communicationEventService.StoreErrorEventAsync(tenantId,
            $"Tenant startup permanently failed after {retryCount} retries. Manual intervention required.");
    }

    protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // Scopes are now registered centrally by the identity service
    }

    protected override  void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        // API resources are now registered centrally by the identity service
    }

    protected override  void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.Clients = new List<DistClientDto>
        {
            new(CommonConstants.CommunicationControllerServicesSwaggerClientId,
                CommunicationControllerTexts.SwaggerClient_Description,
                options.Value.PublicUrl)
            {
                AllowedGrantTypes = [OidcConstants.GrantTypes.AuthorizationCode],

                RedirectUris =
                [
                    options.Value.PublicUrl.EnsureEndsWith("/swagger/oauth2-redirect.html")
                ],

                PostLogoutRedirectUris = [options.Value.PublicUrl.EnsureEndsWith("/")],
                AllowedCorsOrigins = [options.Value.PublicUrl.TrimEnd('/')],
                AllowedScopes =
                [
                    CommonConstants.Scopes.OpenId,
                    CommonConstants.Scopes.Profile,
                    CommonConstants.Scopes.Email,
                    JwtClaimTypes.Role,
                    CommonConstants.OctoApiFullAccess
                ]
            }
        };
    }
}