# Testing Strategy - Communication Controller Services

This document describes the testing strategy for the Communication Controller Services, including unit tests, integration tests with MongoDB Testcontainers, and HTTP API tests.

---

## 1. Overview

The testing strategy follows a layered approach:

```
┌─────────────────────────────────────────────────────────────┐
│                    HTTP API Tests                           │
│           (WebApplicationFactory, TestServer)               │
├─────────────────────────────────────────────────────────────┤
│                 Integration Tests                           │
│        (MongoDB Testcontainers, Real Services)              │
├─────────────────────────────────────────────────────────────┤
│                    Unit Tests                               │
│              (Mocking, Isolated Logic)                      │
└─────────────────────────────────────────────────────────────┘
```

### Test Types

| Test Type | Purpose | Database | Speed |
|-----------|---------|----------|-------|
| **Unit Tests** | Test isolated business logic | Mocked | Fast |
| **Integration Tests** | Test service + repository layer | MongoDB (Testcontainers) | Medium |
| **HTTP API Tests** | Test full request/response cycle | MongoDB (Testcontainers) | Slow |

---

## 2. Project Structure

```
tests/
├── CommunicationControllerService.Tests/           # Existing unit tests
│   ├── Services/
│   │   └── AdapterServiceTests/
│   │       ├── AdapterServiceTestsBase.cs          # Base class with mocks
│   │       ├── RegisterAdapterTests.cs
│   │       └── ...
│   └── Helper/
│       └── RtEntityCreator.cs
│
├── CommunicationControllerServices.IntegrationTests/   # New integration tests
│   ├── Configuration/
│   │   ├── IntegrationTestConfiguration.cs
│   │   └── IntegrationTestOptions.cs
│   ├── Fixtures/
│   │   ├── ServiceCollectionFixture.cs             # Base: ServiceCollection + DI
│   │   ├── ConfigurationFixture.cs                 # Adds configuration loading
│   │   ├── DatabaseFixture.cs                      # Adds MongoDB Testcontainer
│   │   └── CommunicationControllerFixture.cs       # Full service initialization
│   ├── Infrastructure/
│   │   ├── CustomWebApplicationFactory.cs          # HTTP API testing
│   │   ├── IntegrationTestBase.cs                  # Base for HTTP tests
│   │   └── TestAuthHandler.cs                      # Test authentication
│   ├── Services/
│   │   ├── AdapterServiceIntegrationTests.cs
│   │   └── PoolServiceIntegrationTests.cs
│   ├── Repository/
│   │   └── CommunicationRepositoryTests.cs
│   ├── Api/
│   │   ├── HealthCheckTests.cs
│   │   └── AdapterApiTests.cs
│   ├── appsettings.test.json
│   └── CommunicationControllerServices.IntegrationTests.csproj
```

---

## 3. Unit Tests (Existing Pattern)

The existing unit tests use **TUnit** framework with **NSubstitute** for mocking.

### 3.1 Test Framework

```xml
<!-- CommunicationControllerService.Tests.csproj -->
<ItemGroup>
    <PackageReference Include="NSubstitute" Version="5.3.0" />
    <PackageReference Include="NSubstitute.Analyzers.CSharp" Version="1.0.17" />
    <PackageReference Include="TUnit" Version="1.3.15" />
</ItemGroup>
```

### 3.2 Base Test Class Pattern

```csharp
internal abstract class AdapterServiceTestsBase
{
    protected readonly string TenantId = "tenantId";
    protected readonly string ConnectionId = "connectionId";
    protected readonly AdapterService AdapterService;
    protected readonly IAdapterHubCallbacks AdapterHubCallbacks;
    protected readonly IAdapterCache AdapterCache;
    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly IAdapterCachePublish AdapterCachePublish;
    protected readonly ICommunicationEventService CommunicationEventService;
    protected readonly AdapterTenant AdapterTenant;

    protected AdapterServiceTestsBase()
    {
        // Create mocks
        AdapterHubCallbacks = Substitute.For<IAdapterHubCallbacks>();
        AdapterCache = Substitute.For<IAdapterCache>();
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        AdapterCachePublish = Substitute.For<IAdapterCachePublish>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();

        // Create real service with mocked dependencies
        AdapterService = new AdapterService(
            CommunicationRepository,
            AdapterCache,
            AdapterHubCallbacks,
            CommunicationEventService);

        // Create real tenant cache for state management
        AdapterTenant = new AdapterTenant(AdapterCachePublish, TenantId);

        InitAdapterCache();
    }

    private void InitAdapterCache()
    {
        AdapterCache.TryGetTenant(TenantId, out Arg.Any<AdapterTenant?>())
            .Returns(x =>
            {
                x[1] = AdapterTenant;
                return true;
            });
    }
}
```

### 3.3 Test Example

```csharp
internal class RegisterAdapterTests : AdapterServiceTestsBase
{
    [Test]
    public async Task RegisterAdapterToUnknownTenant()
    {
        await Assert.That(async () =>
                await AdapterService.RegisterAdapterAsync("unknown", new RtEntityId(""), ""))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task RegisterAdapter_Empty_Cache()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        InitAdapterConfiguration(rtAdapter, rtDataPipeline, [rtPipeline]);

        // Act
        var configuration =
            await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);
    }
}
```

---

## 4. Integration Tests with MongoDB Testcontainers

Integration tests use real MongoDB instances via Testcontainers for accurate database behavior testing.

### 4.1 Required NuGet Packages

```xml
<!-- CommunicationControllerServices.IntegrationTests.csproj -->
<ItemGroup>
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageReference Include="FluentAssertions" Version="8.8.0" />
    <PackageReference Include="Testcontainers.MongoDb" Version="4.10.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.2" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.2" />
    <PackageReference Include="MartinCostello.Logging.XUnit.v3" Version="0.7.0" />
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
</ItemGroup>

<ItemGroup>
    <ProjectReference Include="..\..\src\CommunicationControllerServices\CommunicationControllerServices.csproj" />
</ItemGroup>

<ItemGroup>
    <None Update="appsettings.test.json">
        <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
</ItemGroup>
```

### 4.2 Configuration

#### appsettings.test.json

```json
{
  "integrationTest": {
    "tenantId": "test-tenant",
    "mongoDbImage": "mongo:8.0.15",
    "adminUser": "octo-system-admin",
    "adminUserPassword": "OctoAdmin1",
    "databaseUserPassword": "OctoUser1",
    "useDirectConnection": true
  }
}
```

#### IntegrationTestOptions.cs

```csharp
namespace CommunicationControllerServices.IntegrationTests.Configuration;

public class IntegrationTestOptions
{
    public string TenantId { get; set; } = null!;
    public string MongoDbImage { get; set; } = "mongo:8.0.15";
    public string AdminUser { get; set; } = "octo-system-admin";
    public string AdminUserPassword { get; set; } = null!;
    public string DatabaseUserPassword { get; set; } = null!;
    public bool UseDirectConnection { get; set; }
}
```

#### IntegrationTestConfiguration.cs

```csharp
using Microsoft.Extensions.Configuration;

namespace CommunicationControllerServices.IntegrationTests.Configuration;

public class IntegrationTestConfiguration : ConfigurationBuilder
{
    public IntegrationTestConfiguration()
    {
        SetBasePath(Directory.GetCurrentDirectory());
        AddJsonFile("appsettings.test.json", optional: false);
        AddEnvironmentVariables();
    }
}
```

### 4.3 Fixture Hierarchy

```
ServiceCollectionFixture      (Base: DI container setup)
         │
         ▼
ConfigurationFixture          (Adds configuration loading)
         │
         ▼
DatabaseFixture               (Adds MongoDB Testcontainer)
         │
         ▼
CommunicationControllerFixture (Full service + tenant setup)
```

#### ServiceCollectionFixture.cs

```csharp
using MartinCostello.Logging.XUnit;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Base fixture that provides a service collection and service provider.
/// </summary>
public abstract class ServiceCollectionFixture : ITestOutputHelperAccessor, IAsyncLifetime
{
    private bool _isInitialized;

    protected ServiceCollectionFixture()
    {
        Services = new ServiceCollection();

        // Add runtime engine and communication controller services
        Services.AddRuntimeEngine()
            .AddOctoCommunicationControllerServices(
                _ => new OctoSystemConfiguration(),
                configureDistributionEventHub: null);

        // Reset tenant notification to default (no RabbitMQ)
        Services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();

        // Add logging with xUnit output
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Trace);
            loggingBuilder.AddXUnit(this);
        });
    }

    public ServiceCollection Services { get; }
    public ServiceProvider? Provider { get; private set; }
    public ITestOutputHelper? OutputHelper { get; set; }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync first.");
    }

    public async ValueTask InitializeAsync()
    {
        if (_isInitialized) return;
        await InitializeServicesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeServicesAsync();
        if (Provider is not null)
            await Provider.DisposeAsync();
    }

    protected virtual Task InitializeServicesAsync()
    {
        Provider = Services.BuildServiceProvider();
        _isInitialized = true;
        return Task.CompletedTask;
    }

    protected abstract Task DisposeServicesAsync();

    public T GetService<T>() where T : notnull
    {
        EnsureInitialized();
        return Provider!.GetRequiredService<T>();
    }

    public ISystemContext GetSystemContext()
    {
        EnsureInitialized();
        return Provider!.GetRequiredService<ISystemContext>();
    }
}
```

#### ConfigurationFixture.cs

```csharp
using CommunicationControllerServices.IntegrationTests.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that loads configuration from appsettings.test.json.
/// </summary>
public abstract class ConfigurationFixture : ServiceCollectionFixture
{
    private readonly IntegrationTestConfiguration _configuration;
    public string SystemDatabaseName => "CommunicationControllerIntegrationTests".ToLower();

    protected ConfigurationFixture()
    {
        _configuration = new IntegrationTestConfiguration();
        Services.Configure<IntegrationTestOptions>(options =>
            _configuration.GetSection("integrationTest").Bind(options));
    }

    protected T GetOptions<T>(string sectionName)
    {
        var option = Activator.CreateInstance<T>();
        _configuration.GetSection(sectionName).Bind(option);
        return option!;
    }
}
```

#### DatabaseFixture.cs

```csharp
using CommunicationControllerServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Fixture that starts MongoDB Testcontainer.
/// </summary>
public class DatabaseFixture : ConfigurationFixture
{
    protected readonly IntegrationTestOptions _options;
    private MongoDbContainer? _mongoDbContainer;

    public DatabaseFixture()
    {
        _options = GetOptions<IntegrationTestOptions>("integrationTest");
    }

    protected override async Task InitializeServicesAsync()
    {
        // Start MongoDB Testcontainer with replica set (required for transactions)
        _mongoDbContainer = new MongoDbBuilder(_options.MongoDbImage)
            .WithReplicaSet()
            .WithName($"mongodb-commcontroller-test-{Guid.NewGuid():N}")
            .WithUsername(_options.AdminUser)
            .WithPassword(_options.AdminUserPassword)
            .WithCleanUp(true)
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _mongoDbContainer.StartAsync(cts.Token);

        var mappedPort = _mongoDbContainer.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";

        // Configure MongoDB connection
        Services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = SystemDatabaseName;
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
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
```

#### CommunicationControllerFixture.cs

```csharp
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Main fixture for Communication Controller integration tests.
/// Initializes MongoDB, system tenant, and test tenant.
/// </summary>
public class CommunicationControllerFixture : DatabaseFixture
{
    public string TestTenantId => _options.TenantId;

    protected override async Task InitializeServicesAsync()
    {
        await base.InitializeServicesAsync();

        var systemContext = GetSystemContext();

        // Ensure clean state - delete if exists
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (i == 0 && await systemContext.IsSystemTenantExistingAsync())
                {
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (await systemContext.IsSystemTenantExistingAsync())
                {
                    await Task.Delay(1000);
                    continue;
                }
                break;
            }
            catch (TenantException)
            {
                // Ignore tenant exceptions during cleanup
            }
        }

        // Create system tenant
        await systemContext.CreateSystemTenantAsync();

        // Create test tenant
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        try
        {
            await systemContext.CreateChildTenantAsync(session, TestTenantId, TestTenantId);
            await session.CommitTransactionAsync();
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
```

### 4.4 Integration Test Example

```csharp
using CommunicationControllerServices.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CommunicationControllerServices.IntegrationTests.Repository;

[Collection("Sequential")]
public class CommunicationRepositoryTests(CommunicationControllerFixture fixture)
    : IClassFixture<CommunicationControllerFixture>
{
    [Fact]
    public async Task SystemTenant_ShouldExist()
    {
        var systemContext = fixture.GetSystemContext();
        var result = await systemContext.IsSystemTenantExistingAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task TestTenant_ShouldBeAccessible()
    {
        var systemContext = fixture.GetSystemContext();
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var tenant = await systemContext.GetChildTenantAsync(session, fixture.TestTenantId);

        await session.CommitTransactionAsync();

        tenant.TenantId.Should().Be(fixture.TestTenantId.ToLower());
    }

    [Fact]
    public async Task CreateAndDeleteAdapter_ShouldSucceed()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var tenantContext = await fixture.GetTestTenantContextAsync();

        // Create adapter
        var adapterId = new RtEntityId(EdgeAdapterOids.CkTypeId, ObjectId.GenerateNewId().ToString());

        await repository.CreateAdapterAsync(fixture.TestTenantId, new RtAdapter
        {
            RtId = adapterId.RtId,
            Name = "Test Adapter"
        });

        // Verify adapter exists
        var adapter = await repository.GetAdapterAsync(fixture.TestTenantId, adapterId);
        adapter.Should().NotBeNull();
        adapter.Name.Should().Be("Test Adapter");

        // Delete adapter
        await repository.DeleteAdapterAsync(fixture.TestTenantId, adapterId);

        // Verify adapter is deleted
        adapter = await repository.GetAdapterAsync(fixture.TestTenantId, adapterId);
        adapter.Should().BeNull();
    }
}
```

---

## 5. HTTP API Tests with WebApplicationFactory

HTTP API tests verify the full request/response cycle through the ASP.NET Core pipeline.

### 5.1 CustomWebApplicationFactory

```csharp
using CommunicationControllerServices.IntegrationTests.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.MongoDb;
using Xunit;

namespace CommunicationControllerServices.IntegrationTests.Infrastructure;

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

    public async ValueTask InitializeAsync()
    {
        _mongoContainer = new MongoDbBuilder(_options.MongoDbImage)
            .WithReplicaSet()
            .WithName($"mongodb-commcontroller-webtest-{Guid.NewGuid():N}")
            .WithUsername(_options.AdminUser)
            .WithPassword(_options.AdminUserPassword)
            .WithCleanUp(true)
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _mongoContainer.StartAsync(cts.Token);

        // Initialize system tenant before web host starts
        await InitializeSystemTenantAsync();
    }

    private async Task InitializeSystemTenantAsync()
    {
        if (_mongoContainer == null)
            throw new InvalidOperationException("MongoDB container not initialized");

        var mappedPort = _mongoContainer.GetMappedPublicPort();
        var databaseHost = $"localhost:{mappedPort}";

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRuntimeEngine()
            .AddOctoCommunicationControllerServices(
                _ => new OctoSystemConfiguration(),
                configureDistributionEventHub: null);

        services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();

        services.Configure<OctoSystemConfiguration>(t =>
        {
            t.SystemDatabaseName = "communicationcontrollerintegrationtests";
            t.DatabaseHost = databaseHost;
            t.AdminUser = _options.AdminUser;
            t.AdminUserPassword = _options.AdminUserPassword;
            t.DatabaseUserPassword = _options.DatabaseUserPassword;
            t.UseDirectConnection = true;
        });

        await using var provider = services.BuildServiceProvider();
        var systemContext = provider.GetRequiredService<ISystemContext>();

        // Ensure clean state
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (i == 0 && await systemContext.IsSystemTenantExistingAsync())
                {
                    await systemContext.DeleteSystemTenantAsync();
                }

                if (!await systemContext.IsSystemTenantExistingAsync())
                    break;

                await Task.Delay(1000);
            }
            catch (TenantException)
            {
                // Ignore
            }
        }

        await systemContext.CreateSystemTenantAsync();
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
        // Disable RabbitMQ
        Environment.SetEnvironmentVariable("OCTO_System__DistributionEventHub__Enabled", "false");
        Environment.SetEnvironmentVariable("OCTO_System__DistributionEventHub__HostName", "");

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonFile("appsettings.test.json", optional: true);
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["System:DistributionEventHub:Enabled"] = "false",
                ["System:DistributionEventHub:HostName"] = "",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Add test authentication handler
            services.AddAuthentication()
                .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });

            // Configure MongoDB connection
            if (_mongoContainer != null)
            {
                var mappedPort = _mongoContainer.GetMappedPublicPort();
                var databaseHost = $"localhost:{mappedPort}";

                services.Configure<OctoSystemConfiguration>(t =>
                {
                    t.SystemDatabaseName = "communicationcontrollerintegrationtests";
                    t.DatabaseHost = databaseHost;
                    t.AdminUser = _options.AdminUser;
                    t.AdminUserPassword = _options.AdminUserPassword;
                    t.DatabaseUserPassword = _options.DatabaseUserPassword;
                    t.UseDirectConnection = true;
                });
            }

            // Remove MassTransit hosted services
            var massTransitHostedServices = services
                .Where(s => s.ServiceType == typeof(IHostedService) &&
                            s.ImplementationType?.FullName?.Contains("MassTransit") == true)
                .ToList();
            foreach (var service in massTransitHostedServices)
            {
                services.Remove(service);
            }

            // Replace tenant notifications
            services.RemoveAll<ITenantNotifications>();
            services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();
        });
    }
}
```

### 5.2 Test Authentication Handler

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CommunicationControllerServices.IntegrationTests.Infrastructure;

public class TestAuthHandlerOptions : AuthenticationSchemeOptions { }

public class TestAuthHandler : AuthenticationHandler<TestAuthHandlerOptions>
{
    public const string SchemeName = "TestScheme";

    public TestAuthHandler(
        IOptionsMonitor<TestAuthHandlerOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "TestUser"),
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
            new Claim("scope", "system.communication"),
            new Claim("scope", "tenant.communication.readwrite"),
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

### 5.3 IntegrationTestBase

```csharp
using Xunit;

namespace CommunicationControllerServices.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for HTTP-based integration tests.
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<CustomWebApplicationFactory>
{
    protected readonly HttpClient Client;
    protected readonly CustomWebApplicationFactory Factory;

    protected IntegrationTestBase(CustomWebApplicationFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }
}
```

### 5.4 HTTP API Test Example

```csharp
using System.Net;
using CommunicationControllerServices.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace CommunicationControllerServices.IntegrationTests.Api;

public class HealthCheckTests : IntegrationTestBase
{
    public HealthCheckTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsExpectedStatusCode()
    {
        var response = await Client.GetAsync("/health", TestContext.Current.CancellationToken);

        // Health check may return OK or ServiceUnavailable depending on component states
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task HomeEndpoint_ReturnsSuccessStatusCode()
    {
        var response = await Client.GetAsync("/", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
```

---

## 6. Test Organization

### 6.1 Test Categories

Use xUnit collections to control test execution order:

```csharp
// Tests that modify shared state run sequentially
[Collection("Sequential")]
public class TenantModificationTests { }

// Tests that only read can run in parallel (default behavior)
public class ReadOnlyTests { }
```

### 6.2 Naming Conventions

| Type | Pattern | Example |
|------|---------|---------|
| Unit Test Class | `{ServiceName}Tests` | `AdapterServiceTests` |
| Integration Test Class | `{ServiceName}IntegrationTests` | `AdapterServiceIntegrationTests` |
| API Test Class | `{Controller}ApiTests` | `AdapterApiTests` |
| Test Method | `{Method}_{Scenario}_{ExpectedResult}` | `RegisterAdapter_ValidInput_ReturnsConfiguration` |

### 6.3 Arrange-Act-Assert Pattern

```csharp
[Fact]
public async Task CreateAdapter_ValidInput_ReturnsCreatedAdapter()
{
    // Arrange
    var repository = fixture.GetService<ICommunicationRepository>();
    var adapterId = new RtEntityId(EdgeAdapterOids.CkTypeId, ObjectId.GenerateNewId().ToString());
    var adapter = new RtAdapter { RtId = adapterId.RtId, Name = "Test" };

    // Act
    await repository.CreateAdapterAsync(fixture.TestTenantId, adapter);
    var result = await repository.GetAdapterAsync(fixture.TestTenantId, adapterId);

    // Assert
    result.Should().NotBeNull();
    result.Name.Should().Be("Test");
}
```

---

## 7. CI/CD Integration

### 7.1 Azure Pipelines Configuration

```yaml
# azure-pipelines.yml
stages:
  - stage: Test
    jobs:
      - job: UnitTests
        pool:
          vmImage: 'ubuntu-latest'
        steps:
          - task: UseDotNet@2
            inputs:
              version: '10.x'
          - script: dotnet test tests/CommunicationControllerService.Tests --configuration Release
            displayName: 'Run Unit Tests'

      - job: IntegrationTests
        pool:
          vmImage: 'ubuntu-latest'
        services:
          docker: true
        steps:
          - task: UseDotNet@2
            inputs:
              version: '10.x'
          - script: dotnet test tests/CommunicationControllerServices.IntegrationTests --configuration Release
            displayName: 'Run Integration Tests'
            env:
              DOCKER_HOST: unix:///var/run/docker.sock
```

### 7.2 Docker-in-Docker Configuration

For CI environments with Docker-in-Docker:

```csharp
// In fixture initialization
var builder = new MongoDbBuilder(_options.MongoDbImage)
    .WithReplicaSet()
    .WithCleanUp(true);  // Ensure cleanup even if Ryuk is disabled

// Set TESTCONTAINERS_HOST_OVERRIDE if needed
var hostOverride = Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE");
if (!string.IsNullOrEmpty(hostOverride))
{
    // Use the override host for connection
}
```

---

## 8. Best Practices

### 8.1 Test Isolation

- Each test should be independent
- Use unique IDs for test data (e.g., `Guid.NewGuid()`)
- Clean up created resources in teardown or use unique database names

### 8.2 Performance

- Share fixtures across tests when possible (`IClassFixture<T>`)
- Use `[Collection("Sequential")]` only when necessary
- Consider parallel test execution for read-only tests

### 8.3 Reliability

- Use timeouts for container operations
- Implement retry logic for flaky operations
- Log diagnostic information for debugging

### 8.4 Maintainability

- Use helper methods for common setup
- Create entity creators/builders for test data
- Keep assertions focused and readable

---

## 9. Running Tests

```bash
# Run all unit tests
dotnet test tests/CommunicationControllerService.Tests --configuration Release

# Run all integration tests
dotnet test tests/CommunicationControllerServices.IntegrationTests --configuration Release

# Run specific test class
dotnet test --filter "FullyQualifiedName~CommunicationRepositoryTests"

# Run with verbose logging
dotnet test --logger "console;verbosity=detailed"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

---

## 10. Known Issues

### MongoDB Application Name Length Limit

The MongoDB driver limits application names to 128 bytes. When using fixture-based tests (ServiceCollectionFixture, DatabaseFixture), the Octo Runtime Engine automatically uses the entry assembly name for the MongoDB connection. If this name is too long, it will cause:

```
System.ArgumentException: Application name exceeds 128 bytes after encoding to UTF8.
```

**Workaround**: Use HTTP tests via `CustomWebApplicationFactory` which properly initialize the web host with a shorter application name.

**Permanent Solution**: A `ServiceName` property should be added to `OctoSystemConfiguration` to override the default application name.

---

## 11. Migration Path

To implement this testing strategy:

1. **Phase 1**: Keep existing unit tests as-is (TUnit + NSubstitute)
2. **Phase 2**: Create integration test project with fixture hierarchy
3. **Phase 3**: Add MongoDB Testcontainer integration
4. **Phase 4**: Add HTTP API tests with WebApplicationFactory
5. **Phase 5**: Integrate into CI/CD pipeline

The existing unit tests will continue to work while integration tests are added incrementally.
