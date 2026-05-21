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
    IBlueprintService blueprintService)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        Constants.CommunicationControllerServiceIdentityDataVersionKey,
        Constants.CommunicationControllerServiceIdentityDataVersionValue,
        null, // migrationService - we don't need migrations in this service
        null, // ckModelUpgradeService - we don't need CK model migrations in this service
        null, // runtimeRepositoryProvider - not needed without CK model migrations
        Constants.CommunicationControllerServiceEnabledKey, // the service can be enabled/disabled
        failedTenantRegistry: failedTenantRegistry
        )
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override async Task ImportCkModelAsync(IOctoAdminSession session, ITenantContext tenantContext)
    {
        // The Communication CK model + initial Pool/Adapter seed entities are now packaged together
        // in the Communication-1.0.0 blueprint. Applying the blueprint resolves the
        // `ckModelDependencies` (System.Communication-[3.0,4.0)) and imports the CK model first,
        // then upserts the seed entities — so the explicit ImportCkModelAsync that used to live
        // here is no longer needed. The blueprint runner is idempotent: re-apply on a tenant that
        // already has the same version is a no-op (unless `force` is set).
        var blueprintId = new BlueprintId("Communication-1.0.0");

        var result = await blueprintService.ApplyBlueprintAsync(tenantContext.TenantId, blueprintId);
        if (!result.IsSuccess)
        {
            // Surface every error as a single InitializationException; the tenant-lifecycle layer
            // will route this through OnTenantStartFailedAsync and into the audit log.
            throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                result.OperationResult.GetMessages());
        }
    }


    protected override async Task StartTenantAsync(string tenantId)
    {
        logger.LogInformation("Loading tenant '{TenantId}'", tenantId);
        if (!await IsEnabledAsync(tenantId))
        {
            logger.LogInformation("Schema not available for tenant '{TenantId}'", tenantId);
            return;
        }

        // try to load the configuration from the cache
        await adapterCachePublish.LoadConfigurationAsync(tenantId);

        await adapterService.PosUpdateTenantAsync(tenantId);
        await poolService.PosUpdateTenantAsync(tenantId);

        await triggerManagementService.UpdateScheduleAsync(tenantId);
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