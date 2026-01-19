# Project Purpose and Architecture

## Project Purpose

**Octo Communication Controller Services** is an ASP.NET Core web service that manages communication adapters and pools for data ingress and egress in an Octo Mesh instance. The service acts as a central hub for coordinating communication between external adapters (data pipeline executors) and pools (device groups) in a multi-tenant environment.

## Core Components

### 1. Adapters
External clients that execute data pipelines. They connect via SignalR and receive pipeline configurations from the service.

### 2. Pools
Groups of managed devices/entities. Pool operators register via SignalR to handle device communication.

### 3. SignalR Hubs
Real-time bidirectional communication channels:
- **AdapterHub** at `/{tenantId}/adapterHub` - manages adapter lifecycle and pipeline debugging
- **PoolHub** at `/{tenantId}/poolHub` - manages pool operator connections

### 4. Caches
In-memory synchronized state for Adapters and Pools across service nodes.

### 5. Repository Layer
`CommunicationRepository` - abstracts MongoDB persistence via Octo Runtime Engine.

### 6. Construction Kit Model
YAML-based model definitions in `SystemCommunicationCkModel` that generate C# types.

## Service Architecture (Layered)

The service follows a layered architecture with clear separation of concerns:

- **Hubs Layer** (`src/CommunicationControllerServices/Hubs/`) - SignalR hubs for real-time communication
- **Service Layer** (`src/CommunicationControllerServices/Services/`) - Core business logic:
  - `AdapterService` - manages adapter lifecycle
  - `PoolService` - manages pool operations
  - `PipelineDebugService` - handles pipeline debugging
  - `TriggerManagementService` - manages triggers
- **Repository Layer** (`src/CommunicationControllerServices/Repository/`) - Data access via MongoDB Runtime Engine
- **Cache Layer** (`src/CommunicationControllerServices/Caches/`) - In-memory state synchronized across nodes via hub callbacks
- **Consumers** (`src/CommunicationControllerServices/Consumers/`) - Message bus event consumers for tenant lifecycle management
- **API Controllers**:
  - `SystemApi/` - System-level endpoints
  - `TenantApi/` - Tenant-scoped endpoints

## Multi-Tenancy

All operations are tenant-scoped. Routes use a custom `tenantId` constraint. The MongoDB-backed repository supports per-tenant data isolation via `ISystemContext.FindTenantRepositoryAsync(tenantId)`.

## Authentication & Authorization

- **JWT Bearer authentication** with scope-based authorization
- **Three policies**:
  - `SystemCommunicationApiPolicy` - full system access
  - `TenantCommunicationApiReadWritePolicy` - tenant full access
  - `TenantCommunicationApiReadOnlyPolicy` - tenant read-only
- Scopes defined in `CommonConstants` from the Contracts library

## Deployment State Management

Adapters and Pools track deployment state:
- `RtDeploymentStateEnum.Pending` - not deployed
- `RtDeploymentStateEnum.Deployed` - active
- Communication state tracked separately via `RtCommunicationStateEnum`

## Project Structure

```
octo-communication-controller-services/
├── src/
│   ├── CommunicationControllerServices/        # Main service
│   │   ├── Hubs/                               # SignalR hubs
│   │   ├── Services/                           # Business logic
│   │   ├── Repository/                         # Data access
│   │   ├── Caches/                             # In-memory caches
│   │   ├── Consumers/                          # Message bus consumers
│   │   ├── SystemApi/                          # System controllers
│   │   ├── TenantApi/                          # Tenant controllers
│   │   ├── Options/                            # Configuration classes
│   │   ├── Extensions/                         # Extension methods
│   │   ├── Routing/                            # Custom routing
│   │   └── Models/                             # DTOs and models
│   ├── SystemCommunicationCkModel/             # Construction Kit model
│   │   └── ConstructionKit/                    # YAML model definitions
│   └── CommunicationControllerServices.Resources/ # Resource strings
├── tests/
│   └── CommunicationControllerService.Tests/   # Unit tests
├── devops-build/                               # CI/CD pipelines
├── Octo.CommunicationController.sln            # Solution file
└── CLAUDE.md                                   # Development guide
```
