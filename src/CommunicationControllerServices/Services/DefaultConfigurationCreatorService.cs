using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class DefaultConfigurationCreatorService(
    ILogger<DefaultConfigurationCreatorService> logger,
    ITriggerManagementService triggerManagementService,
    ISystemContext systemContext,
    IPoolService poolService,
    IAdapterCachePublish adapterCachePublish,
    IAdapterService adapterService)
    : DefaultConfigurationCreatorServiceBase(logger), IConfigurationService
{
    protected override async Task SetupTenantAsync(string tenantId)
    {
        // Do nothing if the system tenant is not existing.
        // Identity Service is creating the system tenant currently.
        // We wait for a PosTenantCreated event to create the default configuration.
        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            return;
        }

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        using var session = await tenantContext.GetAdminSessionAsync();
        session.StartTransaction();

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
}