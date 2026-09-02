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
    IPipelineServiceAccountProvisioningService serviceAccountProvisioningService,
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
    // Explicit field instead of capturing the primary-constructor parameter in method bodies:
    // the parameter is also passed to the base constructor, and capturing it as well would
    // store it twice (CS9107).
    private readonly ISystemContext _systemContext = systemContext;

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

        // AB#5027 phase 2 — the backfill that keeps the phase 1 deploy guard from bricking tenants.
        // Runs AFTER the service-managed blueprints so the default Adapter they seed is already there
        // on a fresh tenant, which makes this the creation path and the backfill path at once.
        //
        // This hook is the per-tenant setup path on purpose: it is driven by service start (every
        // tenant), by Enable, and by PosUpdateTenant — i.e. the documented `clearCache` recovery
        // lever — so an operator has a way to force convergence without a pod restart, and a tenant
        // that failed once is retried by the existing FailedTenantRegistry machinery anyway. The
        // controller has no adapter-CREATE code path of its own (adapters are RtEntities written
        // through the asset repository), so an adapter added by hand between two tenant loads is
        // picked up by its workload deploy (PoolService.DeployWorkloadAsync) or by the next load.
        await EnsurePipelineServiceAccountsAsync(tenantId);

        // try to load the configuration from the cache
        await adapterCachePublish.LoadConfigurationAsync(tenantId);

        await adapterService.PosUpdateTenantAsync(tenantId);
        await poolService.PosUpdateTenantAsync(tenantId);

        await triggerManagementService.UpdateScheduleAsync(tenantId);
    }

    /// <summary>
    /// AB#5027: makes sure every adapter of the tenant has a pipeline service account, and reports
    /// loudly and persistently when it could not.
    ///
    /// <para>
    /// Fault tolerance is the whole point. The provisioning service already isolates each adapter and
    /// never throws; this wrapper adds the belt-and-braces catch so that even an unexpected failure
    /// cannot fail <c>StartTenantAsync</c> — a tenant that cannot reach the identity service must
    /// still load, keep serving its already-deployed pipelines, and get its adapters, pools and
    /// trigger schedules. What it must NOT do is fail silently: every failure lands in the tenant's
    /// event log (written per adapter by the provisioning service) so the refusal an operator later
    /// sees on a pipeline deploy has a visible cause.
    /// </para>
    /// </summary>
    internal async Task EnsurePipelineServiceAccountsAsync(string tenantId)
    {
        try
        {
            var report = await serviceAccountProvisioningService.EnsureTenantProvisionedAsync(tenantId);

            if (report.HasChanges)
            {
                logger.LogInformation(
                    "Pipeline service accounts for tenant '{TenantId}': {Provisioned} provisioned, {Repaired} repaired, {Unchanged} already in place",
                    tenantId, report.Provisioned, report.Repaired, report.AlreadyProvisioned);
                await communicationEventService.StoreInformationEventAsync(tenantId,
                    $"Pipeline service accounts provisioned (AB#5027): {report.Provisioned} created, {report.Repaired} repaired, {report.AlreadyProvisioned} unchanged.");
            }

            if (report.HasFailures)
            {
                logger.LogError(
                    "Pipeline service account provisioning incomplete for tenant '{TenantId}': {Failures}",
                    tenantId, string.Join(" ", report.Failures));
            }
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Pipeline service account provisioning failed for tenant '{TenantId}'. Tenant startup continues; " +
                "deploying pipelines of the affected adapters will be refused until it succeeds",
                tenantId);
        }
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
    /// AB#4884 adds the AI Services flag: EnableAi refuses while Communication is disabled, so the
    /// reverse holds too — disabling Communication under a still-enabled AI Services would leave the
    /// tenant in a state EnableAi could never have produced.
    /// </summary>
    protected override async Task<string?> GetDisableBlockerAsync(string tenantId)
    {
        var activeDeployments = await poolService.GetActiveDeploymentsAsync(tenantId);
        var aiServicesEnabled = await IsAiServicesEnabledAsync(tenantId);

        var blockers = new List<string>();
        if (activeDeployments.Count > 0)
        {
            blockers.Add(BuildDisableBlockedMessage(tenantId, activeDeployments));
        }

        if (aiServicesEnabled)
        {
            blockers.Add(BuildAiDisableBlockedMessage(tenantId));
        }

        return blockers.Count == 0 ? null : string.Join(" ", blockers);
    }

    /// <summary>
    /// Reads the AI Services enabled flag exactly as the tenant delete/detach guard does: from the
    /// tenant's own configuration store, missing key or false = disabled. A read failure propagates —
    /// an unreadable state must never look torn down.
    /// </summary>
    private async Task<bool> IsAiServicesEnabledAsync(string tenantId)
    {
        var tenantContext = await _systemContext.FindTenantContextAsync(tenantId);
        using var session = await tenantContext.GetAdminSessionAsync();
        var aiFlag = await tenantContext.GetConfigurationAsync(
            session,
            TenantCapabilityConfigurationKeys.AiServices,
            new DefaultConfigurationEnabled { IsEnabled = false });
        return aiFlag is { IsEnabled: true };
    }

    /// <summary>
    /// The operator-facing refusal for the AI Services dependency (AB#4884). Surfaced verbatim by
    /// CLI, MCP and Studio, so it names the disable verb and the Studio path.
    /// </summary>
    internal static string BuildAiDisableBlockedMessage(string tenantId) =>
        $"Communication cannot be disabled for tenant '{tenantId}' while AI Services is still enabled - " +
        "the AI service depends on Communication. Disable AI Services first (DisableAi, octo-cli in a " +
        $"context of tenant '{tenantId}', or Refinery Studio > General > Settings > Tenant Features) - " +
        "then retry DisableCommunication.";

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