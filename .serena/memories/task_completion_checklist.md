# Task Completion Checklist

This file defines what should be done when completing a development task in the Octo Communication Controller Services project.

## Before Committing Code

### 1. Code Quality Checks

#### Build Verification
```bash
dotnet build Octo.CommunicationController.sln --configuration Release
```
- ✅ Build must succeed without errors
- ✅ No compiler warnings should be introduced (check build output)

#### Run Tests
```bash
dotnet test --configuration Release
```
- ✅ All existing tests must pass
- ✅ New functionality should have corresponding unit tests
- ✅ Tests should follow TUnit conventions (not xUnit/NUnit)
- ✅ Use NSubstitute for mocking
- ✅ Use FluentAssertions for assertions

### 2. Code Review Checklist

#### Error Handling
- ✅ Errors are logged with NLog before throwing
- ✅ Custom exceptions use static factory methods
- ✅ Appropriate exception types are thrown

#### Security
- ✅ No security vulnerabilities introduced (SQL injection, XSS, etc.)
- ✅ Sensitive data is not logged or exposed
- ✅ Authorization policies are correctly applied

#### Async/Await
- ✅ Async methods end with `Async` suffix
- ✅ No blocking calls (`.Result`, `.Wait()`)
- ✅ `ConfigureAwait(false)` used where appropriate

#### Dependency Injection
- ✅ Constructor injection is used
- ✅ Services are registered in `Program.cs` or extension methods
- ✅ Appropriate service lifetime (Singleton, Scoped, Transient)

#### API Design (if applicable)
- ✅ Routes follow conventions (`/system/api/...` or `/{tenantId}/api/...`)
- ✅ Authorization attributes applied correctly
- ✅ Tenant isolation maintained

### 3. Documentation

#### Code Comments
- ✅ Complex logic is commented
- ✅ Public APIs have XML documentation comments
- ✅ Resource strings used for localizable messages

#### README/CLAUDE.md Updates
- ✅ Update CLAUDE.md if new patterns or conventions introduced
- ✅ Update README.md if user-facing changes

### 4. Construction Kit Model (if modified)

If YAML model definitions were changed:
```bash
dotnet build src/SystemCommunicationCkModel/SystemCommunicationCkModel.csproj
```
- ✅ Model builds successfully
- ✅ Generated C# types are committed
- ✅ No breaking changes to existing model consumers (or migration plan exists)

## After Task Completion

### 1. Git Workflow

#### Verify Changes
```bash
git status
git diff
```
- ✅ Only intended files are modified
- ✅ No debug code or temporary files included

#### Commit Changes
```bash
git add .
git commit -m "AB#<work-item-id>: <Type>: <Description>"
```
Commit message format:
- **AB#xxxx**: Azure Board work item reference
- **Type**: `New`, `Update`, `Fix`, `Refactor`, etc.
- **Description**: Clear, concise summary of changes

Examples:
- `AB#2811: New: Introducing DeleteOptions and reworking DataQueryOperation`
- `AB#2758: Fix: Updated unit tests for new API`

#### Push Changes
```bash
git push origin <branch-name>
```

### 2. Testing in Context

#### Local Testing
- ✅ Service runs successfully locally
- ✅ SignalR hubs are accessible
- ✅ API endpoints respond correctly
- ✅ Configuration loads properly

#### Integration Testing (if applicable)
- ✅ Service integrates with MongoDB
- ✅ Service integrates with RabbitMQ
- ✅ Multi-tenant isolation verified

## Task-Specific Checklists

### When Adding New Features

- ✅ Feature is documented in CLAUDE.md if it's a significant pattern
- ✅ Unit tests cover happy path and error cases
- ✅ Service interfaces are properly registered
- ✅ Authorization is applied correctly
- ✅ Logging is comprehensive

### When Fixing Bugs

- ✅ Root cause is identified and documented
- ✅ Test added to prevent regression
- ✅ Related code reviewed for similar issues
- ✅ Impact analysis performed (what else might be affected?)

### When Refactoring

- ✅ No functional changes (or clearly separated)
- ✅ All tests still pass
- ✅ No performance degradation
- ✅ Code is more maintainable after refactoring

### When Updating Dependencies

- ✅ `Directory.Build.props` updated if Octo packages changed
- ✅ Breaking changes handled
- ✅ All tests pass with new versions
- ✅ Security vulnerabilities addressed

## Pre-PR Checklist

Before creating a pull request:

- ✅ All commits follow naming conventions
- ✅ Branch is up to date with main
- ✅ All tests pass
- ✅ Build succeeds in Release mode
- ✅ Code review feedback addressed (if applicable)
- ✅ CHANGELOG or release notes updated (if required)

## Quick Verification Script

Run these commands in sequence to verify task completion:

```bash
# 1. Clean build
dotnet clean Octo.CommunicationController.sln

# 2. Restore packages
dotnet restore Octo.CommunicationController.sln

# 3. Build in Release mode
dotnet build Octo.CommunicationController.sln --configuration Release

# 4. Run all tests
dotnet test --configuration Release

# 5. Check git status
git status
```

If all commands succeed, the task is ready for commit/PR.
