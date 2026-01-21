using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that starts MongoDB Testcontainer.
/// </summary>
public class DatabaseFixture : ConfigurationFixture
{
    protected readonly IntegrationTestOptions Options;
    private MongoDbContainer? _mongoDbContainer;

    public DatabaseFixture()
    {
        Options = GetOptions<IntegrationTestOptions>("integrationTest");
    }

    protected override async Task InitializeServicesAsync()
    {
        // Write to stderr for immediate output (stdout is buffered)
        Console.Error.WriteLine($"[DatabaseFixture] Starting MongoDB container with image: {Options.MongoDbImage}");
        Console.Error.Flush();

        // Start MongoDB Testcontainer with replica set (required for transactions)
        _mongoDbContainer = new MongoDbBuilder(Options.MongoDbImage)
            .WithReplicaSet()
            .WithName($"mongodb-commcontroller-test-{Guid.NewGuid():N}")
            .WithUsername(Options.AdminUser)
            .WithPassword(Options.AdminUserPassword)
            .WithCleanUp(true)
            .Build();

        // Use explicit timeout for container startup (2 minutes)
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var startTime = DateTime.UtcNow;

        try
        {
            await _mongoDbContainer.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[DatabaseFixture] ERROR: Container startup timed out after 2 minutes!");
            Console.Error.Flush();
            throw new TimeoutException("MongoDB container startup timed out after 2 minutes");
        }

        var elapsed = DateTime.UtcNow - startTime;
        Console.Error.WriteLine($"[DatabaseFixture] Container started in {elapsed.TotalSeconds:F1}s");
        Console.Error.Flush();

        var mappedPort = _mongoDbContainer.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";

        Console.Error.WriteLine($"[DatabaseFixture] MongoDB connection: {databaseHost}");
        Console.Error.Flush();

        // Configure MongoDB connection
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = SystemDatabaseName;
            t.DatabaseHost = databaseHost;
            t.AdminUser = Options.AdminUser;
            t.AdminUserPassword = Options.AdminUserPassword;
            t.DatabaseUserPassword = Options.DatabaseUserPassword;
            t.UseDirectConnection = true;
        });

        await base.InitializeServicesAsync();
    }

    protected override async Task DisposeServicesAsync()
    {
        if (_mongoDbContainer != null)
        {
            await _mongoDbContainer.StopAsync();
            await _mongoDbContainer.DisposeAsync();
        }
    }

    public string GetConnectionString()
    {
        EnsureInitialized();
        return _mongoDbContainer?.GetConnectionString()
               ?? throw new InvalidOperationException("MongoDB container not initialized");
    }
}
