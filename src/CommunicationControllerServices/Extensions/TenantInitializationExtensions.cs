using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using SystemBotCkModel.Generated.System.Bot.v2;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Extensions;

/// <summary>
/// Extension methods for tenant initialization in integration tests.
/// </summary>
public static class TenantInitializationExtensions
{
    /// <summary>
    /// Imports the System.Communication CK model to a tenant and loads the CK cache.
    /// This is used by integration tests to initialize a tenant for testing.
    /// </summary>
    /// <param name="systemContext">The system context.</param>
    /// <param name="tenantId">The tenant ID to initialize.</param>
    /// <param name="ckCacheService">The CK cache service to use for loading the cache.</param>
    public static async Task InitializeTenantForTestingAsync(
        this ISystemContext systemContext,
        string tenantId,
        ICkCacheService ckCacheService)
    {
        // Import the CK models to the tenant's database
        // System.Communication depends on System and System.Bot, so we import in order
        using (var importSession = await systemContext.GetAdminSessionAsync())
        {
            importSession.StartTransaction();
            try
            {
                var tenantContext = await systemContext.GetChildTenantContextAsync(importSession, tenantId);

                // Import the base System CK model first (required dependency)
                if (!await tenantContext.IsCkModelExistingAsync(SystemCkIds.CkModelId))
                {
                    var systemOperationResult = new OperationResult();
                    await tenantContext.ImportCkModelAsync(SystemCkIds.CkModelId, systemOperationResult);
                    if (systemOperationResult.HasErrors || systemOperationResult.HasFatalErrors)
                    {
                        throw new InvalidOperationException(
                            $"Failed to import System CK model: {systemOperationResult.GetMessages()}");
                    }
                }

                // Import the System.Bot CK model (required dependency for System.Communication)
                if (!await tenantContext.IsCkModelExistingAsync(SystemBotCkIds.CkModelId))
                {
                    var botOperationResult = new OperationResult();
                    await tenantContext.ImportCkModelAsync(SystemBotCkIds.CkModelId, botOperationResult);
                    if (botOperationResult.HasErrors || botOperationResult.HasFatalErrors)
                    {
                        throw new InvalidOperationException(
                            $"Failed to import System.Bot CK model: {botOperationResult.GetMessages()}");
                    }
                }

                // Import the System.Communication CK model
                if (!await tenantContext.IsCkModelExistingAsync(SystemCommunicationCkIds.CkModelId))
                {
                    var operationResult = new OperationResult();
                    await tenantContext.ImportCkModelAsync(SystemCommunicationCkIds.CkModelId, operationResult);
                    if (operationResult.HasErrors || operationResult.HasFatalErrors)
                    {
                        throw new InvalidOperationException(
                            $"Failed to import System.Communication CK model: {operationResult.GetMessages()}");
                    }
                }

                await importSession.CommitTransactionAsync();
            }
            catch
            {
                await importSession.AbortTransactionAsync();
                throw;
            }
        }

        // Load the CK cache for the tenant AFTER the import transaction is committed
        // This ensures the cache loader can see the imported models in MongoDB
        // Use FindTenantRepositoryAsync to get the same repository instance that CommunicationRepository uses
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);
    }
}
