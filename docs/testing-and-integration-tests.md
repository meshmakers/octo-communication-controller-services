---
description: "Testing framework and integration tests: TUnit and xUnit setup, fixtures, tenant initialization, service registration"
applies_to: tests/CommunicationControllerServices.IntegrationTests/Fixtures/**, azure-pipelines.yml
---

## Testing Framework

Tests use **TUnit** (not xUnit or NUnit) with **NSubstitute** for mocking:
- Base test classes define common setup and mocked dependencies (e.g., `AdapterServiceTestsBase`)
- Test methods use `[Test]` attribute
- Assertions use TUnit's `Assert.That()` fluent API
- Mock setup uses NSubstitute's fluent API

Example test structure:
```csharp
public class MyTests : MyTestsBase
{
    [Test]
    public async Task ShouldDoSomething()
    {
        // Arrange - mocks configured in base class
        _mockRepository.GetAsync(Arg.Any<string>()).Returns(expectedResult);

        // Act
        var result = await _service.DoSomethingAsync();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Value).IsEqualTo(expected);
    }
}
```

**Mocking System Events:**
When testing services that use `ICommunicationEventService`, create a mock in the test base class:
```csharp
protected readonly ICommunicationEventService CommunicationEventService = Substitute.For<ICommunicationEventService>();
```

## Integration Testing

Integration tests are located in `tests/CommunicationControllerServices.IntegrationTests/` and use **xUnit** with **FluentAssertions** (unlike unit tests which use TUnit).

### Fixture Architecture

Integration tests use a hierarchical fixture structure:

1. **`ServiceCollectionFixture`** - Base fixture providing DI container setup with all required services
2. **`DatabaseFixture`** - Extends ServiceCollectionFixture, adds MongoDB connection via Testcontainers
3. **`CommunicationControllerFixture`** - Main fixture that initializes system tenant, test tenant, and CK cache

### CK Model Dependencies

The System.Communication CK model depends on:
- `System` (base model)
- `System.Bot` (required dependency)

All three models must be imported in order before tests can work with typed entities like `RtPool`, `RtAdapter`, etc.

### Tenant Initialization for Tests

**CRITICAL:** To properly initialize a tenant for integration testing, you must:
1. Import CK models in dependency order (System → System.Bot → System.Communication) within a transaction
2. **Commit the transaction BEFORE loading the CK cache** (LoadCacheForTenantAsync creates a separate MongoDB session that cannot see uncommitted data)
3. **Unload any previously loaded tenant cache** before reloading (the cache loader skips tenants already in the cache)
4. Load the CK cache using `ITenantRepository.LoadCacheForTenantAsync(cacheService)` with the shared `ICkCacheService` from DI

The extension method `InitializeTenantForTestingAsync` in `src/CommunicationControllerServices/Extensions/TenantInitializationExtensions.cs` handles this:

```csharp
// In fixture initialization:
var ckCacheService = GetService<ICkCacheService>();
await systemContext.InitializeTenantForTestingAsync(TestTenantId, ckCacheService);
```

**Implementation details of `InitializeTenantForTestingAsync`:**
```csharp
// 1. Import CK models in a transaction (System → System.Bot → System.Communication)
using (var importSession = await systemContext.GetAdminSessionAsync())
{
    // ... import models in dependency order ...
    await importSession.CommitTransactionAsync();
}

// 2. Unload stale cache (if tenant was loaded during imports)
if (ckCacheService.IsTenantLoaded(tenantId))
{
    ckCacheService.Unload(tenantId);
}

// 3. Load the CK cache AFTER the import transaction is committed
var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
await tenantRepository.LoadCacheForTenantAsync(ckCacheService);
```

**Why this matters:** `LoadCacheForTenantAsync` internally creates a new MongoDB session (via `TenantRepository.RefreshCkCacheServiceAsync`) that cannot see uncommitted data from other transactions. If the cache is loaded before the import transaction commits, newly imported models (like System.Communication) will be silently missing from the cache. Additionally, `ModelLoaderService.LoadAsync` has an `IsTenantLoaded` guard that skips loading if the tenant is already cached, so any stale cache must be unloaded first.

**Embedded version or newer:** each model goes through
`TenantInitializationExtensions.EnsureCkModelAsync(tenantContext, embeddedId)`, which imports only when
`ITenantContext.IsCkModelSatisfiedAsync(embeddedId)` is false — the tenant has neither the embedded version nor a
newer one. Never check `IsCkModelExistingAsync` (exact) for the service's own embedded models: since the engine's
embedded-import downgrade guard a tenant may legitimately have a newer System / System.Bot / System.Communication than
this service embeds (the guard keeps it, the import would only be skipped with a warning). A warning-only
`OperationResult` (guard skip) is not an error; errors throw `InvalidOperationException`. Pinned by
`tests/CommunicationControllerService.Tests/Extensions/TenantInitializationExtensionsTests.cs`. The runtime paths
need no change: the `System.Communication` `IServiceManagedCkModelDescriptor` and the blueprint floors
(`System.Communication-[3.0,4.0)`) are guarded / range-based in the engine.

### Running Integration Tests

```bash
# Run all integration tests (requires Docker for MongoDB Testcontainers)
dotnet test --project tests/CommunicationControllerServices.IntegrationTests/CommunicationControllerServices.IntegrationTests.csproj

# Run a specific integration test
dotnet test --project tests/CommunicationControllerServices.IntegrationTests/CommunicationControllerServices.IntegrationTests.csproj --filter "FullyQualifiedName~CommunicationRepositoryTests"
```

### Service Registration in Tests

**CRITICAL - Registration Order:** CK models MUST be registered BEFORE `AddMongoDbRuntimeRepository()`. This ensures BSON class maps are available when MongoDB is initialized for typed entity deserialization.

```csharp
// CORRECT order in ServiceCollectionFixture:

// 1. Add CK models FIRST (order matters: base models first, then dependent models)
// IMPORTANT: CK models must be registered before AddMongoDbRuntimeRepository()
// to ensure BSON class maps are available for typed entity deserialization
Services.AddCkModelSystemV2();
Services.AddCkModelSystemBotV2();
Services.AddCkModelSystemCommunicationV2();
Services.AddCkModelSystemNotificationV2();

// 2. Add runtime engine with MongoDB AFTER CK models
Services.AddRuntimeEngine()
    .AddMongoDbRuntimeRepository();
```

**Common Error:** If you see `InvalidCastException: Unable to cast object of type 'RtEntity' to type 'RtPool'` (or similar), it typically means:
1. CK models were registered after MongoDB repository initialization, OR
2. The CK cache was not properly loaded for the tenant
