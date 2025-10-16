using IdentityModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
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
    IAdapterService adapterService)
    : DefaultConfigurationCreatorServiceStandardized(logger, systemContext, createIdentityDataCommandClient,
        Constants.CommunicationControllerServiceIdentityDataVersionKey,
        Constants.CommunicationControllerServiceIdentityDataVersionValue,
        null, // we don't need migrations in this service
        Constants.CommunicationControllerServiceEnabledKey // the service can be enabled/disabled
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
        if (!await tenantContext.IsCkModelExistingAsync(SystemCommunicationCkIds.CkModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemCommunicationCkIds.CkModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                    operationResult.GetMessages());
            }
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

    protected override void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiScopes = new List<DistApiScopeDto>
        {
            new(CommonConstants.CommunicationSystemApiFullAccess,
                CommonConstants.CommunicationSystemApiFullAccessDisplayName),
            new(CommonConstants.CommunicationTenantApiFullAccess,
                CommonConstants.CommunicationTenantApiFullAccessDisplayName),
            new(CommonConstants.CommunicationTenantApiReadOnly,
                CommonConstants.CommunicationTenantApiReadOnlyDisplayName),
        };
    }

    protected override  void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
    {
        createIdentityDataCommandRequest.ApiResources = new List<DistApiResourcesDto>
        {
            new(CommonConstants.CommunicationSystemApi, CommonConstants.CommunicationSystemApiDisplayName)
            {
                Description = CommonConstants.CommunicationSystemApiDescription,
                IsEnabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.CommunicationSystemApiFullAccess,
                }
            },
            new(CommonConstants.CommunicationTenantApi, CommonConstants.CommunicationTenantApiDisplayName)
            {
                Description = CommonConstants.CommunicationTenantApiDescription,
                IsEnabled = true,
                Scopes = new List<string>
                {
                    CommonConstants.CommunicationTenantApiReadOnly,
                    CommonConstants.CommunicationTenantApiFullAccess
                }
            }
        };
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
                    CommonConstants.CommunicationSystemApiFullAccess,
                    CommonConstants.CommunicationTenantApiReadOnly,
                    CommonConstants.CommunicationTenantApiFullAccess
                ]
            }
        };
    }
}