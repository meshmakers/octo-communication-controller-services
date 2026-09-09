# Octo Communication Controller Services

ASP.NET Core web service for managing communication adapters and pools in the Octo Mesh ecosystem.

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Quick Start](#quick-start)
- [Project Structure](#project-structure)
- [Core Components](#core-components)
- [API Reference](#api-reference)
- [Configuration](#configuration)
- [Development](#development)
- [Testing](#testing)
- [Deployment](#deployment)
- [System Events](#system-events)

## Overview

The Communication Controller Service is the central hub for coordinating communication between external adapters (data pipeline executors) and pools (device groups) in a multi-tenant environment.

### Key Features

- **Adapter Management**: Registration, configuration, and monitoring of communication adapters
- **Pool Management**: Organization and management of device groups
- **Pipeline Orchestration**: Deployment and control of data pipelines
- **Real-time Communication**: Bidirectional SignalR connections for live updates
- **Multi-Tenancy**: Complete tenant isolation at all levels

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        External Clients                             │
│    ┌─────────────────┐              ┌─────────────────┐             │
│    │  Edge Adapter   │              │  Pool Operator  │             │
│    └────────┬────────┘              └────────┬────────┘             │
└─────────────┼────────────────────────────────┼──────────────────────┘
              │ SignalR                        │ SignalR
              ▼                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│                  Communication Controller Service                   │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │                       SignalR Hubs                          │    │
│  │   ┌──────────────┐              ┌──────────────┐            │    │
│  │   │  AdapterHub  │              │   PoolHub    │            │    │
│  │   │ /adapterHub  │              │  /poolHub    │            │    │
│  │   └──────┬───────┘              └──────┬───────┘            │    │
│  └──────────┼─────────────────────────────┼────────────────────┘    │
│             ▼                             ▼                         │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                      Service Layer                           │   │
│  │  ┌───────────────┐ ┌───────────────┐ ┌────────────────────┐  │   │
│  │  │AdapterService │ │  PoolService  │ │PipelineDebugService│  │   │
│  │  └───────┬───────┘ └───────┬───────┘ └─────────┬──────────┘  │   │
│  └──────────┼─────────────────┼───────────────────┼─────────────┘   │
│             │                 │                   │                 │
│  ┌──────────┴─────────────────┴───────────────────┴─────────────┐   │
│  │                       Cache Layer                            │   │
│  │  ┌───────────────┐              ┌───────────────┐            │   │
│  │  │ AdapterCache  │              │   PoolCache   │            │   │
│  │  └───────────────┘              └───────────────┘            │   │
│  └──────────────────────────────────────────────────────────────┘   │
│             │                                                       │
│  ┌──────────┴───────────────────────────────────────────────────┐   │
│  │                    Repository Layer                          │   │
│  │              ┌─────────────────────────┐                     │   │
│  │              │ CommunicationRepository │                     │   │
│  │              └───────────┬─────────────┘                     │   │
│  └──────────────────────────┼───────────────────────────────────┘   │
└─────────────────────────────┼───────────────────────────────────────┘
                              │
                              ▼
                    ┌─────────────────┐
                    │    MongoDB      │
                    │ (via Octo RE)   │
                    └─────────────────┘
```

### Layered Architecture

| Layer | Responsibility |
|-------|----------------|
| **Hubs** | SignalR endpoints for real-time communication |
| **Service** | Business logic for adapters, pools, and pipelines |
| **Cache** | In-memory state management, synchronized across nodes |
| **Repository** | Data access via MongoDB Runtime Engine |

## Quick Start

### Prerequisites

- .NET 10.0 SDK
- MongoDB (via Octo Runtime Engine)
- RabbitMQ (for message bus)
- Valid OAuth2/JWT token for authentication

### Build

```bash
# Restore and build solution
dotnet restore Octo.CommunicationController.sln
dotnet build Octo.CommunicationController.sln --configuration Release
```

### Run

```bash
# Development mode
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj

# With specific configuration
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj --configuration Debug
```

### Run Tests

```bash
# All tests (Microsoft.Testing.Platform runner, selected via global.json)
dotnet test --configuration Release

# Specific unit test class (TUnit understands the MTP tree-node filter only)
dotnet test --project tests/CommunicationControllerService.Tests/CommunicationControllerService.Tests.csproj --treenode-filter "/*/*/ReportAdapterMetricsAsyncTests/*"

# Specific unit test method
dotnet test --project tests/CommunicationControllerService.Tests/CommunicationControllerService.Tests.csproj --treenode-filter "/*/*/ReportAdapterMetricsAsyncTests/ReportAdapterMetricsAsync_ServiceThrows_Swallowed"
```

## Project Structure

```
octo-communication-controller-services/
├── src/
│   ├── CommunicationControllerServices/      # Main service
│   │   ├── Hubs/                             # SignalR Hubs
│   │   ├── Services/                         # Business Logic
│   │   ├── Caches/                           # In-Memory Caches
│   │   ├── Repository/                       # Data Access
│   │   ├── Consumers/                        # Message Bus Consumers
│   │   ├── SystemApi/v1/Controllers/         # System-Level API
│   │   ├── TenantApi/v1/Controllers/         # Tenant-Level API
│   │   └── Options/                          # Configuration Classes
│   │
│   ├── SystemCommunicationCkModel/           # Construction Kit Model
│   │   ├── ConstructionKit/
│   │   │   ├── ckModel.yaml                  # Main model definition
│   │   │   ├── types/                        # Entity types
│   │   │   ├── enums/                        # Enumerations
│   │   │   ├── attributes/                   # Attribute definitions
│   │   │   └── associations/                 # Relationships
│   │   └── Generated/                        # Generated C# code
│   │
│   └── CommunicationControllerServices.Resources/  # Localization
│
├── tests/
│   └── CommunicationControllerService.Tests/ # Unit Tests (TUnit)
│
├── devops-build/                             # CI/CD Pipelines
├── Directory.Build.props                      # Build Configuration
├── CLAUDE.md                                  # AI Assistant Guidelines
└── Octo.CommunicationController.sln          # Solution File
```

## Core Components

### SignalR Hubs

#### AdapterHub (`/{tenantId}/adapterHub`)

Manages adapter lifecycle and pipeline debugging.

| Method | Description |
|--------|-------------|
| `RegisterAdapterAsync` | Registers new adapter with pipeline configuration |
| `UnregisterAsync` | Unregisters adapter, sets pipelines to Pending |
| `GetAdapterConfigurationAsync` | Retrieves pipeline configuration |
| `DeployPipelineAsync` | Deploys pipeline to adapter |
| `UndeployPipelineAsync` | Removes pipeline deployment |
| `UpdateConfigurationStateAsync` | Updates validation status |

#### PoolHub (`/{tenantId}/poolHub`)

Manages pool operator connections. Workloads (Adapters + Applications) are not pushed back through this hub — the Communication Operator subscribes to `WorkloadDeployedAsync` / `WorkloadUndeployedAsync` on `/operatorHub` for the Helm-based deploy flow.

| Method | Description |
|--------|-------------|
| `RegisterPoolOperatorAsync` | Registers pool operator (returns `Task`) |
| `UnregisterPoolOperatorAsync` | Unregisters pool operator |
| `UpdateAdapterDeploymentStateAsync` | Reports adapter deployment state back to the controller |

### Services

#### AdapterService

Core business logic for adapter operations:

```csharp
public interface IAdapterService
{
    Task RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId);
    Task UnregisterAsync(string tenantId, RtEntityId adapterRtEntityId);
    Task<AdapterConfiguration> GetAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId);
    Task DeployPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId);
    Task UndeployPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId);
    Task SetAdapterCommunicationStateOnlineAsync(string tenantId, RtEntityId adapterRtEntityId);
    Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId);
}
```

#### PoolService

Business logic for pool management. Workloads managed by the pool (Adapters + Applications) are fanned out as Helm deploys by `DeployPoolAsync` via the `/operatorHub` SignalR channel — no adapter list is returned to the operator on pool registration.

```csharp
public interface IPoolService
{
    Task<OctoObjectId> RegisterPoolOperatorAsync(string tenantId, string poolName, string connectionId);
    Task UnregisterPoolOperatorAsync(string tenantId, string poolName);
    Task DeployPoolAsync(string tenantId, OctoObjectId poolRtId);
    Task UndeployPoolAsync(string tenantId, OctoObjectId poolRtId);
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId);
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId);
}
```

#### PipelineDebugService

Debug information for pipeline executions:

```csharp
public interface IPipelineDebugService
{
    Task StartDebugPointAsync(string executionId, string nodeId, object data);
    Task<IReadOnlyCollection<DebugPoint>> GetDebugPointsAsync(string executionId);
    Task<IReadOnlyCollection<DebugHistory>> GetDebugHistoryAsync(string tenantId);
}
```

#### CommunicationEventService

System event logging for auditing and monitoring:

```csharp
public interface ICommunicationEventService
{
    Task StoreEventAsync(string tenantId, RtEventLevelsEnum level, string message);
    Task StoreInformationEventAsync(string tenantId, string message);
    Task StoreWarningEventAsync(string tenantId, string message);
    Task StoreErrorEventAsync(string tenantId, string message);
    void StoreSystemInformationEvent(string message);
    void StoreSystemWarningEvent(string message);
    void StoreSystemErrorEvent(string message);
}
```

This service wraps `IEventRepository` from `Meshmakers.Octo.Services.Notifications` to handle scoped lifetime requirements for singleton services.

### Caches

The cache system uses a dual-interface pattern for separation of concerns:

```csharp
// Read interface
public interface IAdapterCache
{
    bool TryGetTenant(string tenantId, out AdapterTenant? adapterTenant);
    AdapterTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
}

// Publish interface
public interface IAdapterCachePublish
{
    Task LoadConfigurationAsync(string tenantId);
    Task PublishConfigurationAsync(string tenantId);
    Task ReloadConfigurationAsync(ComControllerAdapterUpdate configuration);
}
```

Registration as singleton with multiple interfaces:

```csharp
services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();
```

### Repository

Abstraction of MongoDB persistence via Octo Runtime Engine:

```csharp
public interface ICommunicationRepository
{
    // Adapter operations
    Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId);
    Task<RtAdapter> GetAdapterAsync(string tenantId, RtEntityId adapterRtEntityId);

    // Pool operations
    Task<IReadOnlyCollection<RtPool>> GetPoolsAsync(string tenantId);
    Task CreatePoolAsync(string tenantId, string poolName);
    Task SetPoolDeploymentStateAsync(string tenantId, OctoObjectId poolRtId, RtDeploymentStateEnum state);

    // Pipeline operations
    Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, RtEntityId adapterRtEntityId);
    Task SetPipelineDeploymentStateAsync(string tenantId, RtEntityId pipelineRtId, RtDeploymentStateEnum state, object? error);
}
```

## API Reference

### System API

Base route: `system/v{version}/`

| Endpoint | Method | Description | Policy |
|----------|--------|-------------|--------|
| `/communication/ping` | GET | Health check | Anonymous |
| `/communication/enable` | POST | Enable service for tenant | SystemCommunicationApiPolicy |
| `/communication/disable` | POST | Disable service for tenant | SystemCommunicationApiPolicy |
| `/communication/status` | POST | Query status | SystemCommunicationApiPolicy |
| `/diagnostics/*` | GET | Diagnostics endpoints | SystemCommunicationApiPolicy |

### Tenant API

Base route: `{tenantId}/v{version}/`

#### Adapter Endpoints

| Endpoint | Method | Description | Policy |
|----------|--------|-------------|--------|
| `/adapter` | GET | List all adapters | ReadOnly |
| `/adapter/{adapterId}` | GET | Get specific adapter | ReadOnly |
| `/adapter/{adapterId}/state` | PUT | Update adapter state | ReadWrite |

#### Pool Endpoints

| Endpoint | Method | Description | Policy |
|----------|--------|-------------|--------|
| `/pool` | GET | List all pools | ReadOnly |
| `/pool/{poolId}` | GET | Get specific pool | ReadOnly |
| `/pool` | POST | Create pool | ReadWrite |

#### Pipeline Endpoints

| Endpoint | Method | Description | Policy |
|----------|--------|-------------|--------|
| `/pipeline` | GET | List pipelines for adapter | ReadOnly |
| `/pipeline/{pipelineId}/deploy` | POST | Deploy pipeline | ReadWrite |
| `/pipeline/{pipelineId}/undeploy` | POST | Undeploy pipeline | ReadWrite |
| `/pipeline/{pipelineId}/state` | GET | Query deployment state | ReadOnly |

#### Pipeline Debug Endpoints

| Endpoint | Method | Description | Policy |
|----------|--------|-------------|--------|
| `/pipelinedebug/history` | GET | Get execution history | ReadOnly |
| `/pipelinedebug/{executionId}` | GET | Get debug points for execution | ReadOnly |
| `/pipelinedebug/{executionId}/point/{nodeId}` | GET | Get specific debug point | ReadOnly |

## Configuration

### Environment Variables

All settings can be overridden via environment variables with prefix `OCTO_`.

### appsettings.json

```json
{
  "CommunicationController": {
    "PublicUrl": "https://localhost:5015",
    "AuthorityUrl": "https://localhost:5003",
    "BrokerHost": "localhost",
    "BrokerPort": 5672,
    "BrokerVirtualHost": "/",
    "BrokerUser": "guest",
    "BrokerPassword": "guest",
    "MinLogLevel": "Information",
    "InstancePrefix": ""
  },
  "System": {
    // Octo System Configuration
  }
}
```

### CommunicationControllerOptions

```csharp
public class CommunicationControllerOptions
{
    public string PublicUrl { get; set; }           // Service public URL
    public string AuthorityUrl { get; set; }        // OAuth authority
    public string BrokerHost { get; set; }          // RabbitMQ host
    public int BrokerPort { get; set; }             // RabbitMQ port
    public string BrokerVirtualHost { get; set; }   // RabbitMQ vhost
    public string BrokerUser { get; set; }          // RabbitMQ user
    public string BrokerPassword { get; set; }      // RabbitMQ password
    public string MinLogLevel { get; set; }         // Log level
    public string InstancePrefix { get; set; }      // Multi-instance prefix
}
```

### Authentication & Authorization

JWT Bearer authentication with scope-based authorization:

| Policy | Scopes | Description |
|--------|--------|-------------|
| `SystemCommunicationApiPolicy` | `communication:system` | System-level access |
| `TenantCommunicationApiReadWritePolicy` | `communication:tenant` | Tenant full access |
| `TenantCommunicationApiReadOnlyPolicy` | `communication:tenant:read` | Tenant read-only access |

## Development

### Construction Kit Model

The CK model defines domain objects in YAML:

```yaml
# types/adapter.yaml
id: Adapter
ckTypeId: system-communication
inherits: DeployableEntity
abstract: true
attributes:
  - $ref: "CommunicationState"
  - $ref: "ConfigurationState"
associations:
  - $ref: "Manages"
```

Generate model:

```bash
dotnet build src/SystemCommunicationCkModel/SystemCommunicationCkModel.csproj
```

Generated C# types are located in `src/SystemCommunicationCkModel/Generated/`.

### Exception Handling Pattern

Services use factory methods for exceptions:

```csharp
// Correct - use factory methods
throw AdapterServiceException.TenantNotEnabled(tenantId);
throw AdapterServiceException.PipelineNotFound(tenantId, pipelineId);

// Wrong - do not use generic exceptions
throw new AdapterServiceException("Tenant not enabled");  // Don't use!
```

### State Management

#### Deployment States

```csharp
enum RtDeploymentStateEnum
{
    Undeployed = 0,  // Not yet deployed
    Pending = 1,     // Deployment requested
    Deployed = 2,    // Actively deployed
    Error = 3        // Deployment error
}
```

#### Communication States

```csharp
enum RtCommunicationStateEnum
{
    Unregistered = 0,  // Not registered
    Online = 1,        // Connected
    Offline = 2        // Disconnected
}
```

### Service Registration

Multi-interface registration for services:

```csharp
// Extension methods
services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();
services.AddScopedMultipleInterfaces<DefaultConfigurationCreatorService,
    IDefaultConfigurationCreatorService, IConfigurationService>();
```

## Testing

### Framework

- **TUnit** (not xUnit or NUnit)
- **NSubstitute** for mocking

### Test Structure

```csharp
public class MyServiceTests : MyServiceTestsBase
{
    [Test]
    public async Task ShouldDoSomething_WhenCondition()
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

### Test Base Classes

`AdapterServiceTestsBase`:
- Pre-configured mock dependencies
- Test data setup
- Helper methods for entity creation

### Running Tests

```bash
# All tests with verbose output (Microsoft.Testing.Platform switch; VSTest --logger is not available)
dotnet test --configuration Release --output Detailed

# Unit tests with code coverage (TUnit ships the Microsoft.Testing.Extensions.CodeCoverage extension;
# the .coverage file lands in TestResults/)
dotnet test --configuration Release --project tests/CommunicationControllerService.Tests/CommunicationControllerService.Tests.csproj --coverage
```

## Deployment

### Docker Build

```bash
# Build image
docker build \
  --build-arg OCTO_VERSION=1.0.0 \
  --build-arg OCTO_PRIVATE_NUGET_SERVICE=https://nuget.example.com \
  -f src/CommunicationControllerServices/Dockerfile \
  -t octo-communication-controller:1.0.0 .
```

### Dockerfile Multi-Stage Build

1. **base**: ASP.NET 10.0 Runtime
2. **build**: .NET 10.0 SDK + Private NuGet
3. **publish**: Release Build
4. **final**: Production Runtime

### Kubernetes Deployment

The service supports horizontal scaling with:
- Distributed caches via message bus
- SignalR with Redis backplane (optional)
- Stateless design for load balancing

### Health Checks

- `/system/v1/communication/ping` - Basic health check
- Custom `AdapterHealthChecks` for detailed status monitoring

## External Dependencies

| Package | Description |
|---------|-------------|
| `Meshmakers.Octo.Runtime.Engine.MongoDb` | MongoDB abstraction |
| `Meshmakers.Octo.Services.Infrastructure` | Shared infrastructure |
| `Meshmakers.Octo.Services.Swagger` | OpenAPI/Swagger support |
| `Meshmakers.Octo.Services.Notifications` | System event logging |
| `Meshmakers.Octo.Infrastructure.DistributionEventHub` | RabbitMQ messaging |

Octo package versions are centrally managed via `$(OctoVersion)` in `Directory.Build.props`.

## System Events

The service logs important business events for auditing and monitoring using the Octo Notification system.

### Event Architecture

Events are stored per-tenant in MongoDB via `IEventRepository`. The service uses `ICommunicationEventService` as a wrapper to handle scoped lifetime requirements for singleton services.

```csharp
// Registration in Program.cs
builder.Services.AddOctoNotification();
builder.Services.AddSingleton<ICommunicationEventService, CommunicationEventService>();

// Service start event
app.Lifetime.ApplicationStarted.Register(() =>
{
    var eventRepository = app.Services.GetRequiredService<IEventRepository>();
    eventRepository.StoreSystemInformationEvent(RtEventSourcesEnum.CommunicationService,
        "Communication Controller Services started.");
});
```

### Logged Events

| Component | Event | Level | Trigger |
|-----------|-------|-------|---------|
| **Service Startup** | Service started | Information | Application start |
| **AdapterService** | Adapter registered | Information | Adapter connects and registers |
| | Adapter unregistered | Information | Adapter disconnects |
| | Adapter online | Information | SignalR connection established |
| | Adapter offline | Information | SignalR connection lost |
| | Configuration deployed | Information | Pipeline config sent to adapter |
| | Configuration failed | Error | Adapter reports deployment error |
| | Data pipeline deployed | Information | Pipeline deployed to adapters |
| | Data pipeline undeployed | Information | Pipeline removed from adapters |
| | Tenant pre-update | Information | Before tenant config reload |
| | Tenant post-update | Information | After tenant config reload |
| **PoolService** | Pool operator registered | Information | Pool operator connects |
| | Pool operator unregistered | Information | Pool operator disconnects |
| | Adapter deployed to pool | Information | Adapter assigned to pool |
| | Adapter undeployed from pool | Information | Adapter removed from pool |
| | Tenant pre-update | Information | Before tenant config reload |
| | Tenant post-update | Information | After tenant config reload |
| **TriggerManagementService** | Pipeline execution started | Information | Manual pipeline trigger |
| | Pipeline execution failed | Error | Pipeline execution error |
| | Trigger schedule updated | Information | Scheduled triggers updated |
| | Trigger schedule update failed | Error | Schedule update error |
| **TenantManagementConsumer** | Pre-update failed | Error | Error during pre-update |
| | Post-update failed | Error | Error during post-update |
| | Pre-delete failed | Error | Error during pre-delete |
| **Hubs** | Registration failed | Error | Hub operation error |
| | Debug data cache failed | Error | Error caching debug data |
| | Deployment update failed | Error | Error updating deployment result |

### Usage Example

```csharp
// In a singleton service
internal class MyService(ICommunicationEventService eventService)
{
    public async Task DoOperationAsync(string tenantId)
    {
        try
        {
            // ... perform operation ...

            await eventService.StoreInformationEventAsync(tenantId,
                "Operation completed successfully.");
        }
        catch (Exception e)
        {
            await eventService.StoreErrorEventAsync(tenantId,
                $"Operation failed: {e.Message}");
            throw;
        }
    }
}
```

### Event Levels

| Level | Usage |
|-------|-------|
| `Information` | Normal operations, state changes, successful completions |
| `Warning` | Recoverable issues, deprecated operations |
| `Error` | Failed operations, exceptions |
| `Critical` | System-level failures (not currently used) |

## License

Proprietary - Meshmakers GmbH

## Support

For questions or issues, please contact the development team.
