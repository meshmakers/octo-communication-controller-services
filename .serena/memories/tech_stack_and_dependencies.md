# Tech Stack and Dependencies

## Core Framework

- **Target Framework**: .NET 9.0
- **Project Type**: ASP.NET Core Web API
- **Language**: C#

## Key Technologies

### Real-Time Communication
- **SignalR**: Real-time bidirectional communication between service and clients
  - Configured with 100MB max message size

### Persistence
- **MongoDB**: Primary data store via Octo Runtime Engine MongoDb

### Messaging
- **RabbitMQ**: Message bus for event-driven architecture via Octo Infrastructure DistributionEventHub

### Logging
- **NLog**: Structured logging framework
  - Configuration in `src/CommunicationControllerServices/nlog.config`

### Observability
- Integrated via Meshmakers.Octo.Observability packages

## External Dependencies

### Octo Platform Packages
All `Meshmakers.Octo.*` packages provide core Octo platform functionality:
- **Meshmakers.Octo.Runtime**: Core runtime services
- **Meshmakers.Octo.Runtime.MongoDb**: MongoDB data access
- **Meshmakers.Octo.Infrastructure**: Infrastructure services
- **Meshmakers.Octo.Infrastructure.DistributionEventHub**: RabbitMQ messaging
- **Meshmakers.Octo.Observability**: Telemetry and logging

Version controlled via `$(OctoVersion)` variable in `Directory.Build.props`.

### Testing Framework
- **TUnit**: Modern testing framework (NOT xUnit or NUnit)
  - Uses `[Test]` attribute for test methods
- **NSubstitute**: Mocking framework
  - Fluent API for mock setup
- **FluentAssertions**: Assertion library

## Configuration Management

### Configuration Classes
- **OctoSystemConfiguration**: Bound from `System` section
- **CommunicationControllerOptions**: Bound from `CommunicationController` section

### Configuration Sources (Priority Order)
1. Environment variables with `OCTO_` prefix
2. `appsettings.Development.json` (when running locally)
3. `appsettings.json` (base configuration)

## Docker Support

Dockerfile located at `src/CommunicationControllerServices/Dockerfile` for containerized deployment.

## Build System

- **MSBuild** via .NET SDK
- Shared build properties in `Directory.Build.props`
- Solution file: `Octo.CommunicationController.sln`

## Development Tools

- **JetBrains Rider** settings included (`.sln.DotSettings`)
- **HTTP Client** test files (`comtest.http`, `http-client.env.json`)
- **Git** for version control

## Construction Kit Model

The `SystemCommunicationCkModel` project uses YAML-based model definitions that generate C# types at build time. Model definitions are in `src/SystemCommunicationCkModel/ConstructionKit/`:
- `ckModel.yaml` - main model definition
- `types/` - entity type definitions
- `associations/` - relationship definitions
- `enums/` - enumeration definitions
- `attributes/` - attribute definitions

Generated C# types are output to `src/SystemCommunicationCkModel/Generated/`.
