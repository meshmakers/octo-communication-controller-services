# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the **Octo Communication Controller Services** - an ASP.NET Core web service that manages communication adapters and pools for data ingress and egress in an Octo Mesh instance. The service acts as a central hub for coordinating communication between external adapters (data pipeline executors) and pools (device groups) in a multi-tenant environment.

## Key Architecture Concepts

### Core Components

1. **Adapters**: External clients that execute data pipelines. They connect via SignalR and receive pipeline configurations from the service.
2. **Pools**: Groups of managed devices/entities. Pool operators register via SignalR to handle device communication.
3. **SignalR Hubs**: Real-time bidirectional communication channels:
   - `AdapterHub` at `/{tenantId}/adapterHub` - manages adapter lifecycle and pipeline debugging
   - `PoolHub` at `/{tenantId}/poolHub` - manages pool operator connections
4. **Caches**: In-memory synchronized state for Adapters and Pools across service nodes
5. **Repository Layer**: `CommunicationRepository` - abstracts MongoDB persistence via Octo Runtime Engine
6. **Construction Kit Model**: YAML-based model definitions in `SystemCommunicationCkModel` that generate C# types

### Service Architecture

The service follows a layered architecture:
- **Hubs Layer** (`src/CommunicationControllerServices/Hubs/`) - SignalR hubs for real-time communication
- **Service Layer** (`src/CommunicationControllerServices/Services/`) - Core business logic (`AdapterService`, `PoolService`, `PipelineDebugService`, `TriggerManagementService`, `CommunicationEventService`)
- **Repository Layer** (`src/CommunicationControllerServices/Repository/`) - Data access via MongoDB Runtime Engine
- **Cache Layer** (`src/CommunicationControllerServices/Caches/`) - In-memory state synchronized across nodes via hub callbacks
- **Consumers** (`src/CommunicationControllerServices/Consumers/`) - Message bus event consumers for tenant lifecycle management

### Multi-Tenancy

All operations are tenant-scoped. Routes use a custom `tenantId` constraint. The MongoDB-backed repository supports per-tenant data isolation via `ISystemContext.FindTenantRepositoryAsync(tenantId)`.

### Authentication & Authorization

- JWT Bearer authentication with scope-based authorization
- Three policies:
  - `SystemCommunicationApiPolicy` - full system access
  - `TenantCommunicationApiReadWritePolicy` - tenant full access
  - `TenantCommunicationApiReadOnlyPolicy` - tenant read-only
- Scopes defined in `CommonConstants` from the Contracts library

### External Dependencies

- **Meshmakers.Octo.*** packages - Octo platform libraries for runtime, infrastructure, observability
- Version controlled via `$(OctoVersion)` in `Directory.Build.props`
- MongoDB for persistence (via Octo Runtime Engine MongoDb)
- RabbitMQ for messaging (via Octo Infrastructure DistributionEventHub)

### System Events

The service uses the Octo Notification system to log important business events for auditing and monitoring. Events are stored per-tenant in MongoDB.

**Event Service Architecture:**
- `ICommunicationEventService` / `CommunicationEventService` - Helper service that handles scoped `IEventRepository` access for singleton services
- Events use `RtEventSourcesEnum.CommunicationService` as the source identifier

**Logged Events:**

| Component | Event | Level | Description |
|-----------|-------|-------|-------------|
| Service Startup | Service started | Information | Logged on application start |
| AdapterService | Adapter registered | Information | Adapter connected and registered |
| AdapterService | Adapter unregistered | Information | Adapter disconnected |
| AdapterService | Adapter online/offline | Information | Adapter connection state changed |
| AdapterService | Configuration deployed | Information | Pipeline configuration sent to adapter |
| AdapterService | Configuration failed | Error | Adapter deployment failed with errors |
| AdapterService | Data pipeline deployed/undeployed | Information | Pipeline deployment to adapters |
| AdapterService | Tenant pre/post-update | Information | Tenant lifecycle events |
| PoolService | Pool operator registered/unregistered | Information | Pool operator connection state |
| PoolService | Adapter deployed/undeployed to pool | Information | Adapter assignment to pools |
| PoolService | Tenant pre/post-update | Information | Tenant lifecycle events |
| TriggerManagementService | Pipeline execution started | Information | Manual pipeline trigger |
| TriggerManagementService | Pipeline execution failed | Error | Pipeline execution errors |
| TriggerManagementService | Trigger schedule updated | Information | Scheduled triggers updated |
| TenantManagementConsumer | Tenant update failed | Error | Errors during tenant lifecycle |
| Hubs | Operation failed | Error | Hub operation errors |

**Usage in Services:**
```csharp
// Inject ICommunicationEventService in singleton services
public class MyService(ICommunicationEventService eventService)
{
    public async Task DoSomethingAsync(string tenantId)
    {
        // Store information event
        await eventService.StoreInformationEventAsync(tenantId, "Operation completed.");

        // Store error event
        await eventService.StoreErrorEventAsync(tenantId, $"Operation failed: {error}");
    }
}
```

## Development Commands

### Build & Test

```bash
# Restore packages
dotnet restore Octo.CommunicationController.sln

# Build solution
dotnet build Octo.CommunicationController.sln --configuration Release

# Run all unit tests
dotnet test --configuration Release

# Run a specific test class
dotnet test --filter "FullyQualifiedName~AdapterServiceTests"

# Run a specific test method
dotnet test --filter "FullyQualifiedName~RegisterAdapterTests.ShouldRegisterNewAdapter"
```

### Run the Service

```bash
# Run locally (uses appsettings.Development.json)
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj

# Run with specific configuration
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj --configuration Debug
```

### Construction Kit Model

The `SystemCommunicationCkModel` project contains YAML model definitions that generate C# types:

```bash
# Build the CK model (generates code and publishes NuGet package)
dotnet build src/SystemCommunicationCkModel/SystemCommunicationCkModel.csproj

# The generated types are in src/SystemCommunicationCkModel/Generated/
```

Model definitions are in `src/SystemCommunicationCkModel/ConstructionKit/`:
- `ckModel.yaml` - main model definition
- `types/` - entity type definitions
- `associations/` - relationship definitions
- `enums/` - enumeration definitions
- `attributes/` - attribute definitions

### Docker

```bash
# Build Docker image
docker build -f src/CommunicationControllerServices/Dockerfile -t octo-communication-controller .
```

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

## Important Patterns

### Service Registration

Services expose multiple interfaces (e.g., cache as `IAdapterCache` and `IAdapterCachePublish`). Use extension methods:
- `AddSingletonMultipleInterfaces<TImpl, TInterface1, TInterface2>()`
- `AddScopedMultipleInterfaces<TImpl, TInterface1, TInterface2>()`

### Error Handling

Services throw custom exceptions (e.g., `AdapterServiceException`, `PoolServiceException`) with static factory methods for specific error scenarios. Always log errors with NLog before throwing.

### Configuration

Configuration is bound to strongly-typed options classes:
- `OctoSystemConfiguration` from `System` section
- `CommunicationControllerOptions` from `CommunicationController` section
- Environment variables prefixed with `OCTO_` override appsettings

### Deployment State Management

Adapters and Pools track deployment state:
- `RtDeploymentStateEnum.Pending` - not deployed
- `RtDeploymentStateEnum.Deployed` - active
- Communication state tracked separately via `RtCommunicationStateEnum`

## Project Structure Notes

- Main service: `src/CommunicationControllerServices/`
- Construction Kit model: `src/SystemCommunicationCkModel/`
- Resource strings: `src/CommunicationControllerServices.Resources/`
- Tests: `tests/CommunicationControllerService.Tests/`
- CI/CD: `devops-build/azure-pipelines.yml`
- Output directory: `bin/$(Configuration)/`

## Configuration Notes

- `InternalsVisibleTo` configured for tests and DynamicProxy (for NSubstitute proxy generation)
- Target framework: .NET 10.0
- NLog configuration in `src/CommunicationControllerServices/nlog.config`
- SignalR configured with 100MB max message size
- Three build configurations: `Debug`, `Release`, `DebugL` (local development with version 999.0.0)

## Exception Handling Pattern

Services use a factory method pattern for exceptions with private constructors and static factory methods:

```csharp
// Wrong - don't throw generic exceptions
throw new AdapterServiceException("Tenant not enabled");

// Correct - use factory methods
throw AdapterServiceException.TenantNotEnabled(tenantId);
```

Each service has a dedicated exception class: `AdapterServiceException`, `PoolServiceException`, `PipelineDebugServiceException`, `TriggerManagementServiceException`, `CommunicationRepositoryException`.

## Cache Architecture

Caches use a dual-interface pattern for separation of concerns:
- **Read interface** (e.g., `IAdapterCache`): Query cached state
- **Publish interface** (e.g., `IAdapterCachePublish`): Load and publish configuration changes

Both interfaces are implemented by the same singleton, registered via:
```csharp
services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();
```

## HTTP Context Extensions

Access tenant and adapter info from request context via extension methods in `Constants.cs`:
- `HttpContext.GetTenantId()` - from route value
- `HttpContext.GetPoolName()` - from "pool-name" header
- `HttpContext.GetAdapterRtEntityId()` - constructs `RtEntityId` from "adapter-rtId" and "adapter-ckTypeId" headers

## API Structure

Controllers are split by scope:
- **System API** (`SystemApi/v1/Controllers/`): System-level operations (enable/disable tenants)
- **Tenant API** (`TenantApi/v1/Controllers/`): Tenant-scoped operations for adapters, pools, pipelines

Routes follow pattern: `{tenantId:tenantId}/v{version:apiVersion}/[controller]`
