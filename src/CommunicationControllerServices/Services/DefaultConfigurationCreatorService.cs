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
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands.Payloads;
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
    ICommandClient<CreateIdentityDataCommandRequest> commandClient,
    ISystemContext systemContext,
    IPoolService poolService,
    IAdapterCachePublish adapterCachePublish,
    IAdapterService adapterService)
    : DefaultConfigurationCreatorServiceBase(logger), IConfigurationService
{
    public override async Task InitializeAsync()
    {
        // Reconfigure the log level based on the configuration
        await diagnosticsService.ReconfigureLogLevelAsync(options.Value.MinLogLevel);

        await base.InitializeAsync();
    }

    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }

        logger.LogInformation("Setting up default configuration for tenant '{TenantId}'", tenantId);

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Identity configuration is next
        await SetupIdentityDataAsync(tenantId);

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync<DefaultConfigurationVersion>(session,
                Constants.CommunicationControllerServiceSchemaVersionKey, null);
        if (configurationVersion == null)
        {
            await session.CommitTransactionAsync();
            return;
        }

        if (configurationVersion.Version < Constants.CommunicationControllerServiceSchemaVersionValue)
        {
            await ImportCkModelAsync(tenantId);

            await tenantContext.SetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion
                    { Version = Constants.CommunicationControllerServiceSchemaVersionValue });
        }

        await session.CommitTransactionAsync();

        // try to load the configuration from the cache
        await adapterCachePublish.LoadConfigurationAsync(tenantId);

        await StartTenantAsync(tenantId);

        // TODO: Implement security configuration
    }

    public async Task EnableAsync(string tenantId)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (configurationVersion?.Version == Constants.CommunicationControllerServiceSchemaVersionValue)
        {
            throw ConfigurationException.TenantAlreadyEnabled(tenantId);
        }

        await ImportCkModelAsync(tenantId);

        await tenantContext.SetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
            new DefaultConfigurationVersion { Version = Constants.CommunicationControllerServiceSchemaVersionValue });

        await session.CommitTransactionAsync();

        await StartTenantAsync(tenantId);
    }

    public async Task DisableAsync(string tenantId)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (configurationVersion == null || configurationVersion.Version == -1)
        {
            throw ConfigurationException.TenantAlreadyDisabled(tenantId);
        }

        await tenantContext.DeleteConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey);

        await session.CommitTransactionAsync();

        await StopTenantAsync(tenantId);
    }

    public async Task<bool> IsEnabledAsync(string tenantId)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

        // If there is a configuration version, check if we need to update the configuration
        var configurationVersion =
            await tenantContext.GetConfigurationAsync(session, Constants.CommunicationControllerServiceSchemaVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (configurationVersion == null)
        {
            return false;
        }

        return configurationVersion.Version == Constants.CommunicationControllerServiceSchemaVersionValue;
    }

    private async Task ImportCkModelAsync(string tenantId)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        if (!await tenantContext.IsCkModelExistingAsync(SystemCommunicationCkIds.ModelId))
        {
            OperationResult operationResult = new();
            await tenantContext.ImportCkModelAsync(SystemCommunicationCkIds.ModelId, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw InitializationException.ImportCkModelFailed(tenantContext.TenantId,
                    operationResult.GetMessages());
            }
        }
    }

    private async Task StartTenantAsync(string tenantId)
    {
        logger.LogInformation("Loading tenant '{TenantId}'", tenantId);

        await adapterService.PosUpdateTenantAsync(tenantId);
        await poolService.PosUpdateTenantAsync(tenantId);

        await triggerManagementService.UpdateScheduleAsync(tenantId);
    }

    private async Task StopTenantAsync(string tenantId)
    {
        logger.LogInformation("Unloading tenant '{TenantId}'", tenantId);

        await triggerManagementService.RemoveScheduleAsync(tenantId);

        await adapterService.PreUpdateTenantAsync(tenantId);
        await poolService.PreUpdateTenantAsync(tenantId);
    }

    private async Task SetupIdentityDataAsync(string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        // Identity configuration is next
        if (tenantId != systemContext.TenantId)
        {
            // Currently we only support the system tenant.
            return;
        }

        logger.LogInformation("Setting up default identity data for tenant '{TenantId}'", tenantId);

        var serviceConfiguration =
            await systemContext.GetConfigurationAsync(session, Constants.CommunicationControllerServiceIdentityDataVersionKey,
                new DefaultConfigurationVersion { Version = -1 });
        if (serviceConfiguration == null ||
            serviceConfiguration.Version < Constants.CommunicationControllerServiceIdentityDataVersionValue)
        {
            logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantId);


            CreateIdentityDataCommandRequest createIdentityDataCommandRequest = new(systemContext.TenantId);
            CreateApiScopes(createIdentityDataCommandRequest);
            CreateApiResources(createIdentityDataCommandRequest);
            CreateClients(createIdentityDataCommandRequest);

            logger.LogInformation("Creating identity data for tenant '{TenantId}'", tenantId);
            var r = await commandClient.GetResponseWithRetry<EnumCommandResponse<CreateIdentityDataResult>>(
                createIdentityDataCommandRequest);
            logger.LogInformation("Create identity data response: {Response}", r.Response);
            if (r.Response == CreateIdentityDataResult.Success)
            {
                await systemContext.SetConfigurationAsync(session,
                    Constants.CommunicationControllerServiceIdentityDataVersionKey,
                    new DefaultConfigurationVersion
                        { Version = Constants.CommunicationControllerServiceIdentityDataVersionValue });
            }
            else if (r.Response != CreateIdentityDataResult.FailedTenantHasNoIdentityCk)
            {
                logger.LogInformation("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantId);
            }
            else
            {
                logger.LogError("The tenant '{TenantId}' has no identity CK, skipped to create identity data",
                    tenantId);
            }
        }

        await session.CommitTransactionAsync();
    }

    private void CreateApiScopes(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
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

    private void CreateApiResources(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
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

    private void CreateClients(CreateIdentityDataCommandRequest createIdentityDataCommandRequest)
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