# Suggested Commands

This file contains essential commands for developing in the Octo Communication Controller Services project.

## Package Management

### Restore Dependencies
```bash
dotnet restore Octo.CommunicationController.sln
```
Restores all NuGet packages for the solution.

## Build Commands

### Build Solution
```bash
dotnet build Octo.CommunicationController.sln --configuration Release
```
Builds the entire solution in Release mode.

### Build Debug
```bash
dotnet build Octo.CommunicationController.sln --configuration Debug
```
Builds the entire solution in Debug mode (default).

### Build Construction Kit Model
```bash
dotnet build src/SystemCommunicationCkModel/SystemCommunicationCkModel.csproj
```
Builds the CK model, generates C# types from YAML definitions, and publishes NuGet package.
Generated types are in `src/SystemCommunicationCkModel/Generated/`.

## Testing Commands

### Run All Tests
```bash
dotnet test --configuration Release
```
Runs all unit tests in the solution. Uses TUnit framework.

### Run Specific Test Class
```bash
dotnet test --filter "FullyQualifiedName~AdapterServiceTests"
```
Runs all tests in the `AdapterServiceTests` class.

### Run Specific Test Method
```bash
dotnet test --filter "FullyQualifiedName~RegisterAdapterTests.ShouldRegisterNewAdapter"
```
Runs a single test method.

### Run Tests with Verbose Output
```bash
dotnet test --verbosity normal
```
Shows detailed test execution information.

## Run the Service

### Run Locally (Development)
```bash
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj
```
Runs the service using `appsettings.Development.json` configuration.

### Run with Specific Configuration
```bash
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj --configuration Debug
```
Runs with Debug configuration.

### Run with Watch (Auto-Reload)
```bash
dotnet watch --project src/CommunicationControllerServices/CommunicationControllerServices.csproj
```
Runs the service and automatically reloads on file changes.

## Docker Commands

### Build Docker Image
```bash
docker build -f src/CommunicationControllerServices/Dockerfile -t octo-communication-controller .
```
Builds a Docker image for the service. Must be run from the repository root.

### Run Docker Container
```bash
docker run -p 8080:8080 octo-communication-controller
```
Runs the containerized service (adjust port mapping as needed).

## Git Commands

### Check Status
```bash
git status
```

### Create Feature Branch
```bash
git checkout -b feature/my-feature-name
```

### Stage and Commit Changes
```bash
git add .
git commit -m "AB#<work-item-id>: <description>"
```
Commit messages should reference Azure Board work items.

### Push Changes
```bash
git push origin <branch-name>
```

## File Operations (macOS/Darwin)

Since this project is developed on **Darwin (macOS)**, use these commands:

### List Files
```bash
ls -la
```

### Find Files
```bash
find . -name "*.cs" -type f
```

### Search in Files
```bash
grep -r "pattern" src/
```

### View File Contents
```bash
cat file.cs
```

### Navigate Directories
```bash
cd src/CommunicationControllerServices
pwd  # Print working directory
```

## Clean Build Artifacts

### Clean Solution
```bash
dotnet clean Octo.CommunicationController.sln
```

### Remove bin and obj Directories
```bash
find . -name "bin" -o -name "obj" | xargs rm -rf
```

## NuGet Commands

### List Package Sources
```bash
dotnet nuget list source
```

### Clear NuGet Cache
```bash
dotnet nuget locals all --clear
```

## Useful Development Commands

### Check .NET Version
```bash
dotnet --version
```

### List Installed SDKs
```bash
dotnet --list-sdks
```

### Format Code (if formatter is configured)
```bash
dotnet format Octo.CommunicationController.sln
```

## Configuration Notes

- **Configuration files**: `appsettings.json`, `appsettings.Development.json`
- **Environment variables**: Prefix with `OCTO_` to override appsettings
- **NLog configuration**: `src/CommunicationControllerServices/nlog.config`
- **Output directory**: `bin/$(Configuration)/`
