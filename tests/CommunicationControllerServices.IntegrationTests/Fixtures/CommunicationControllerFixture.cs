using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Main fixture for Communication Controller integration tests.
/// Initializes MongoDB, system tenant, and test tenant.
/// </summary>
public class CommunicationControllerFixture : DatabaseFixture
{
    public string TestTenantId => Options.TenantId;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        Console.Error.WriteLine("[CommunicationControllerFixture] Initializing system tenant...");
        Console.Error.Flush();

        var systemContext = GetSystemContext();

        // Ensure the system CK model is available
        Console.Error.WriteLine("[CommunicationControllerFixture] Ensuring system CK model...");
        Console.Error.Flush();
        await systemContext.EnsureSystemCkModelAsync();

        // Ensure clean state - delete if exists
        Console.Error.WriteLine("[CommunicationControllerFixture] Checking for existing system tenant...");
        Console.Error.Flush();

        for (var i = 0; i < 10; i++)
        {
            try
            {
                var exists = await systemContext.IsSystemTenantExistingAsync();
                Console.Error.WriteLine($"[CommunicationControllerFixture] Iteration {i}: System tenant exists = {exists}");
                Console.Error.Flush();

                if (i == 0 && exists)
                {
                    Console.Error.WriteLine("[CommunicationControllerFixture] Deleting existing system tenant...");
                    Console.Error.Flush();
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (await systemContext.IsSystemTenantExistingAsync())
                {
                    Console.Error.WriteLine($"[CommunicationControllerFixture] Tenant still exists, waiting 1s (iteration {i})...");
                    Console.Error.Flush();
                    await Task.Delay(1000);
                    continue;
                }

                Console.Error.WriteLine("[CommunicationControllerFixture] Tenant cleanup complete");
                Console.Error.Flush();
                break;
            }
            catch (TenantException ex)
            {
                Console.Error.WriteLine($"[CommunicationControllerFixture] TenantException during cleanup: {ex.Message}");
                Console.Error.Flush();
                // Ignore tenant exceptions during cleanup
            }
        }

        // Create system tenant
        Console.Error.WriteLine("[CommunicationControllerFixture] Creating system tenant...");
        Console.Error.Flush();
        await systemContext.CreateSystemTenantAsync();
        Console.Error.WriteLine("[CommunicationControllerFixture] System tenant created");
        Console.Error.Flush();

        // Create test tenant
        Console.Error.WriteLine($"[CommunicationControllerFixture] Creating test tenant: {TestTenantId}...");
        Console.Error.Flush();

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        try
        {
            await systemContext.CreateChildTenantAsync(session, TestTenantId, TestTenantId);
            await session.CommitTransactionAsync();
            Console.Error.WriteLine("[CommunicationControllerFixture] Test tenant created");
            Console.Error.Flush();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    /// <summary>
    /// Gets a tenant context for the test tenant.
    /// </summary>
    public async Task<ITenantContext> GetTestTenantContextAsync()
    {
        EnsureInitialized();

        var systemContext = GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        try
        {
            var tenantContext = await systemContext.GetChildTenantContextAsync(session, TestTenantId);
            await session.CommitTransactionAsync();
            return tenantContext;
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }
}
