using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
///     Fixture that starts a MongoDB Testcontainer with a replica set (required for transactions).
///
///     Container-bringup pattern matches octo-construction-kit-engine-mongodb /
///     octo-ai-services — Testcontainers' rs.initiate() handshake and mongo's keyfile-init
///     entrypoint race with port binding on CI agents under load (build 34386 hung 40+ min
///     in a sibling service due to exit-48 on 27017 inside the entrypoint restart). The
///     retry loop with a *fresh* container per attempt is the proven fix.
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
        await Console.Error.WriteLineAsync($"[DatabaseFixture] Starting MongoDB container with image: {Options.MongoDbImage}");
        await Console.Error.FlushAsync();

        const int maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromMinutes(2);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await Console.Error.WriteLineAsync($"[DatabaseFixture] StartAsync attempt {attempt}/{maxAttempts}");
            await Console.Error.FlushAsync();

            // No WithCleanUp(true) — Ryuk's TCP handshake blocks silently on the self-hosted
            // DinD agent; DisposeServicesAsync handles cleanup explicitly.
            _mongoDbContainer = new MongoDbBuilder(Options.MongoDbImage)
                .WithReplicaSet()
                .WithName($"mongodb-commcontroller-test-{Guid.NewGuid():N}")
                .WithUsername(Options.AdminUser)
                .WithPassword(Options.AdminUserPassword)
                .Build();

            using var startCts = new CancellationTokenSource(perAttemptTimeout);
            var startTime = DateTime.UtcNow;

            try
            {
                await _mongoDbContainer.StartAsync(startCts.Token);
                var elapsed = DateTime.UtcNow - startTime;
                await Console.Error.WriteLineAsync($"[DatabaseFixture] Container started in {elapsed.TotalSeconds:F1}s");
                await Console.Error.FlushAsync();
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[DatabaseFixture] StartAsync attempt {attempt}/{maxAttempts} failed: {ex.GetType().Name}: {ex.Message}");
                await Console.Error.FlushAsync();

                try
                {
                    await _mongoDbContainer.DisposeAsync();
                }
                catch (Exception disposeEx)
                {
                    await Console.Error.WriteLineAsync($"[DatabaseFixture]   Disposal of failed container also threw: {disposeEx.Message}");
                    await Console.Error.FlushAsync();
                }

                _mongoDbContainer = null;

                if (attempt == maxAttempts)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }

        var mappedPort = _mongoDbContainer!.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";

        await Console.Error.WriteLineAsync($"[DatabaseFixture] MongoDB connection: {databaseHost}");
        await Console.Error.FlushAsync();

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
