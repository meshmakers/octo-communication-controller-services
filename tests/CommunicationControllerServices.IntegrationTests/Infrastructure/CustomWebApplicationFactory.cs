using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.MongoDb;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;

/// <summary>
/// Custom WebApplicationFactory for HTTP integration tests.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly IntegrationTestConfiguration _configuration = new();
    private readonly IntegrationTestOptions _options;
    private MongoDbContainer? _mongoContainer;

    public CustomWebApplicationFactory()
    {
        _options = new IntegrationTestOptions();
        _configuration.GetSection("integrationTest").Bind(_options);
    }

    public string MongoConnectionString => _mongoContainer?.GetConnectionString()
        ?? throw new InvalidOperationException("MongoDB container not initialized");

    public string TestTenantId => _options.TenantId;

    public async ValueTask InitializeAsync()
    {
        Console.Error.WriteLine($"[WebFactory] Starting MongoDB container with image: {_options.MongoDbImage}");
        Console.Error.WriteLine($"[WebFactory] DOCKER_HOST: {Environment.GetEnvironmentVariable("DOCKER_HOST") ?? "(not set)"}");
        Console.Error.WriteLine($"[WebFactory] TESTCONTAINERS_HOST_OVERRIDE: {Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE") ?? "(not set)"}");
        Console.Error.Flush();

        _mongoContainer = new MongoDbBuilder(_options.MongoDbImage)
            .WithReplicaSet()
            .WithName($"mongodb-commcontroller-webtest-{Guid.NewGuid():N}")
            .WithUsername(_options.AdminUser)
            .WithPassword(_options.AdminUserPassword)
            .WithCleanUp(true)
            .Build();

        Console.Error.WriteLine("[WebFactory] Container built, starting...");
        Console.Error.Flush();
        var startTime = DateTime.UtcNow;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await _mongoContainer.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[WebFactory] ERROR: Container startup timed out after 2 minutes!");
            Console.Error.Flush();
            throw new TimeoutException("MongoDB container startup timed out after 2 minutes");
        }

        var elapsed = DateTime.UtcNow - startTime;
        Console.Error.WriteLine($"[WebFactory] Container started in {elapsed.TotalSeconds:F1}s");
        Console.Error.Flush();

        // Initialize system tenant before web host starts
        Console.Error.WriteLine("[WebFactory] Initializing system tenant...");
        Console.Error.Flush();
        await InitializeSystemTenantAsync();
        Console.Error.WriteLine("[WebFactory] System tenant initialized");
        Console.Error.Flush();
    }

    private async Task InitializeSystemTenantAsync()
    {
        if (_mongoContainer == null)
        {
            throw new InvalidOperationException("MongoDB container not initialized");
        }

        var mappedPort = _mongoContainer.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";
        Console.Error.WriteLine($"[WebFactory] MongoDB connection: {databaseHost}");
        Console.Error.Flush();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddConsole());

        // Add infrastructure with short service name (MongoDB app name limit is 128 bytes)
        services.AddOctoServiceInfrastructure("CommCtrlTests", _ => { });

        services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();

        services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();

        services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = "commctrl-web-tests";
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = true;
        });

        await using var provider = services.BuildServiceProvider();
        var systemContext = provider.GetRequiredService<ISystemContext>();

        Console.Error.WriteLine("[WebFactory] Ensuring system CK model...");
        Console.Error.Flush();
        await systemContext.EnsureSystemCkModelAsync();

        // Ensure clean state - delete if exists
        Console.Error.WriteLine("[WebFactory] Checking for existing system tenant...");
        Console.Error.Flush();
        for (var i = 0; i < 10; i++)
        {
            try
            {
                var exists = await systemContext.IsSystemTenantExistingAsync();
                Console.Error.WriteLine($"[WebFactory] Iteration {i}: System tenant exists = {exists}");
                Console.Error.Flush();

                if (i == 0 && exists)
                {
                    Console.Error.WriteLine("[WebFactory] Deleting existing system tenant...");
                    Console.Error.Flush();
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (await systemContext.IsSystemTenantExistingAsync())
                {
                    Console.Error.WriteLine($"[WebFactory] Tenant still exists, waiting 1s (iteration {i})...");
                    Console.Error.Flush();
                    await Task.Delay(1000);
                    continue;
                }

                Console.Error.WriteLine("[WebFactory] Tenant cleanup complete");
                Console.Error.Flush();
                break;
            }
            catch (TenantException ex)
            {
                Console.Error.WriteLine($"[WebFactory] TenantException during cleanup: {ex.Message}");
                Console.Error.Flush();
            }
        }

        Console.Error.WriteLine("[WebFactory] Creating system tenant...");
        Console.Error.Flush();
        await systemContext.CreateSystemTenantAsync();
        Console.Error.WriteLine("[WebFactory] System tenant created");
        Console.Error.Flush();

        // Create test tenant
        Console.Error.WriteLine($"[WebFactory] Creating test tenant: {_options.TenantId}...");
        Console.Error.Flush();

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        try
        {
            await systemContext.CreateChildTenantAsync(session, _options.TenantId, _options.TenantId);
            await session.CommitTransactionAsync();
            Console.Error.WriteLine("[WebFactory] Test tenant created");
            Console.Error.Flush();
        }
        catch
        {
            await session.AbortTransactionAsync();
            throw;
        }
    }

    public new async ValueTask DisposeAsync()
    {
        if (_mongoContainer != null)
        {
            await _mongoContainer.StopAsync();
            await _mongoContainer.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Console.Error.WriteLine("[WebFactory] ConfigureWebHost called");
        Console.Error.Flush();

        // Disable RabbitMQ/Distribution Event Hub for tests
        Environment.SetEnvironmentVariable("OCTO_System__DistributionEventHub__Enabled", "false");
        Environment.SetEnvironmentVariable("OCTO_System__DistributionEventHub__HostName", "");

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            Console.Error.WriteLine("[WebFactory] ConfigureAppConfiguration called");
            Console.Error.Flush();
            config.AddJsonFile("appsettings.test.json", optional: true);

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["System:DistributionEventHub:Enabled"] = "false",
                ["System:DistributionEventHub:HostName"] = "",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            Console.Error.WriteLine("[WebFactory] ConfigureTestServices called");
            Console.Error.Flush();

            // Add test authentication handler
            services.AddAuthentication()
                .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            // Configure MongoDB connection using the test container
            if (_mongoContainer != null)
            {
                var mappedPort = _mongoContainer.GetMappedPublicPort();
                var databaseHost = $"localhost:{mappedPort}";

                services.Configure<OctoSystemConfiguration>(t =>
                {
                    t.SystemDatabaseName = "commctrl-web-tests";
                    t.DatabaseHost = databaseHost;
                    t.AdminUser = _options.AdminUser;
                    t.AdminUserPassword = _options.AdminUserPassword;
                    t.DatabaseUserPassword = _options.DatabaseUserPassword;
                    t.UseDirectConnection = true;
                });
            }

            // Remove hosted services that try to connect to RabbitMQ or initialize configuration
            Console.Error.WriteLine("[WebFactory] Removing hosted services that require external connections...");
            Console.Error.Flush();
            var hostedServicesToRemove = services
                .Where(s => s.ServiceType == typeof(IHostedService) &&
                            (s.ImplementationType?.FullName?.Contains("MassTransit") == true ||
                             s.ImplementationType?.FullName?.Contains("ConfigurationInitialization") == true ||
                             s.ImplementationType?.FullName?.Contains("HostedInitializer") == true))
                .ToList();
            foreach (var service in hostedServicesToRemove)
            {
                Console.Error.WriteLine($"[WebFactory] Removing: {service.ImplementationType?.FullName}");
                services.Remove(service);
            }

            // Replace ITenantNotifications with default (non-RabbitMQ) implementation
            services.RemoveAll<ITenantNotifications>();
            services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();

            Console.Error.WriteLine("[WebFactory] ConfigureTestServices completed");
            Console.Error.Flush();
        });

        Console.Error.WriteLine("[WebFactory] ConfigureWebHost completed");
        Console.Error.Flush();
    }
}
