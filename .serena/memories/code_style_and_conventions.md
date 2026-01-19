# Code Style and Conventions

## Testing Conventions

### Testing Framework: TUnit
- **NOT xUnit or NUnit** - this project uses TUnit
- Test methods use `[Test]` attribute
- Test classes can inherit from base test classes for common setup
- Base test classes define shared fixtures and mocks (e.g., `AdapterServiceTestsBase`)

### Test Structure
```csharp
public class MyTests : MyTestsBase
{
    [Test]
    public async Task ShouldDoSomething()
    {
        // Arrange - setup in base class or local
        // Act
        var result = await _service.DoSomethingAsync();
        // Assert
        result.Should()... // FluentAssertions
    }
}
```

### Mocking
- **NSubstitute** is the mocking framework
- Use fluent API for mock setup:
  ```csharp
  _mockRepository.GetByIdAsync(Arg.Any<string>()).Returns(entity);
  ```

### Assertions
- **FluentAssertions** for expressive assertions:
  ```csharp
  result.Should().NotBeNull();
  result.Should().BeOfType<MyType>();
  collection.Should().HaveCount(3);
  ```

## Service Design Patterns

### Multiple Interface Registration
Services often expose multiple interfaces (e.g., a cache as `IAdapterCache` and `IAdapterCachePublish`).

Use extension methods for registration:
```csharp
services.AddSingletonMultipleInterfaces<TImpl, TInterface1, TInterface2>();
services.AddScopedMultipleInterfaces<TImpl, TInterface1, TInterface2>();
```

### Error Handling
- Services throw **custom exceptions** (e.g., `AdapterServiceException`, `PoolServiceException`)
- Use **static factory methods** for specific error scenarios
- **Always log errors** with NLog before throwing:
  ```csharp
  _logger.Error(ex, "Error message");
  throw AdapterServiceException.CreateNotFound(adapterId);
  ```

### Configuration Pattern
- Use **strongly-typed options classes** bound from configuration
- Options classes are injected via `IOptions<T>`
- Example: `OctoSystemConfiguration`, `CommunicationControllerOptions`

## Code Organization

### Naming Conventions
- **Async methods** end with `Async` suffix
- **Private fields** use underscore prefix: `_fieldName`
- **Interfaces** start with `I`: `IAdapterService`
- **Constants** use UPPER_SNAKE_CASE or PascalCase based on context

### File Organization
- One class per file (generally)
- File name matches class name
- Group related functionality in folders (e.g., Services, Hubs, Caches)

### Dependency Injection
- Constructor injection is the standard
- Register services in `Program.cs` or extension methods
- Scoped services for per-request state
- Singleton services for shared state (caches)

## Async/Await Patterns
- Use `async`/`await` throughout
- Avoid blocking calls (`.Result`, `.Wait()`)
- Use `ConfigureAwait(false)` when appropriate
- Return `Task` or `Task<T>` from async methods

## Resource Management
- Resource strings in `CommunicationControllerServices.Resources` project
- Use resource files for localizable strings
- Constants defined in `Constants.cs`

## API Design

### Route Templates
- System API: `/system/api/...`
- Tenant API: `/{tenantId}/api/...`
- SignalR Hubs: `/{tenantId}/adapterHub`, `/{tenantId}/poolHub`

### Authorization
- Apply authorization attributes at controller or action level
- Use policy-based authorization:
  - `[Authorize(Policy = "SystemCommunicationApiPolicy")]`
  - `[Authorize(Policy = "TenantCommunicationApiReadWritePolicy")]`
  - `[Authorize(Policy = "TenantCommunicationApiReadOnlyPolicy")]`

## Important Project Settings

### InternalsVisibleTo
Configured for:
- Test assembly: `CommunicationControllerService.Tests`
- DynamicProxy for mocking: `DynamicProxyGenAssembly2`

Located in `.csproj` file around line 52-53.

## Construction Kit Model

### YAML Model Definitions
- Located in `src/SystemCommunicationCkModel/ConstructionKit/`
- Organized into subdirectories: `types/`, `associations/`, `enums/`, `attributes/`
- Main entry point: `ckModel.yaml`
- Building the CK model project generates C# types and publishes NuGet package
- Generated types are in `src/SystemCommunicationCkModel/Generated/`

## Git Conventions
- Current branch: `main`
- Feature branches should be created for new work
- Commit messages should reference Azure Board work items (e.g., `AB#2811: New: ...`)
