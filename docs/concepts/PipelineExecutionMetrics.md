# Konzept: Pipeline Execution Metrics

## Zusammenfassung der Anforderungen

| Anforderung       | Entscheidung                                                     |
|-------------------|------------------------------------------------------------------|
| **Storage**       | MongoDB (via Octo Runtime Engine)                                |
| **Reporting**     | Echtzeit via SignalR + periodische Aggregate                     |
| **Granularität**  | Pro Execution, Pipeline und Adapter                              |
| **Live Status**   | Ja, mit Live-Updates                                             |
| **Retention**     | 30 Tage für Detaildaten                                          |
| **Erfolg/Fehler** | Exception = Fehler                                               |
| **Abruf**         | Construction Kit Integration → GraphQL (Asset Repo) + WebSockets |
| **Performance**   | Ja, Dauer tracken                                                |

---

## 1. Übersicht

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                              Adapter (Edge/Mesh)                             │
│                                                                              │
│   ┌────────────────┐    Execution Start     ┌────────────────────────────┐   │
│   │    Pipeline    │ ──────────────────────▶│  ExecutionReporter         │   │
│   │   Executor     │    Execution End       │  (im Adapter)              │   │
│   └────────────────┘ ◀──────────────────────└────────────────────────────┘   │
│                                                        │                     │
└────────────────────────────────────────────────────────┼─────────────────────┘
                                                         │ SignalR
                                                         ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                     Communication Controller Service                         │
│                                                                              │
│   ┌─────────────────────┐      ┌──────────────────────────────────────────┐  │
│   │     AdapterHub      │─────▶│      PipelineExecutionService            │  │
│   │                     │      │  - Receive execution events              │  │
│   │  + StartExecution   │      │  - Store in MongoDB                      │  │
│   │  + EndExecution     │      │  - Update aggregates                     │  │
│   │  + Heartbeat        │      │  - Detect timeouts                       │  │
│   └─────────────────────┘      └──────────────────────────────────────────┘  │
│                                              │                               │
│                                              ▼                               │
│   ┌───────────────────────────────────────────────────────────────────────┐  │
│   │                    CommunicationRepository                            │  │
│   │  - Store PipelineExecution entities                                   │  │
│   │  - Store/Update PipelineStatistics                                    │  │
│   └───────────────────────────────────────────────────────────────────────┘  │
│                                              │                               │
└──────────────────────────────────────────────┼───────────────────────────────┘
                                               │
                                               ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                              MongoDB (Octo Runtime)                         │
│                                                                             │
│   ┌───────────────────┐   ┌───────────────────┐   ┌───────────────────────┐ │
│   │ PipelineExecution │   │ PipelineStatistics│   │ Pipeline (erweitert)  │ │
│   │ (einzelne Runs)   │   │ (Aggregate)       │   │ + currentExecution    │ │
│   └───────────────────┘   └───────────────────┘   └───────────────────────┘ │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
                                               │
                                               ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Asset Repository (GraphQL)                          │
│                                                                             │
│   Query: pipelineExecutions, pipelineStatistics                             │
│   Subscription: onPipelineExecutionChanged                                  │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Construction Kit Model Erweiterungen

### 2.1 Neue Entity: `PipelineExecution`

Speichert jede einzelne Pipeline-Ausführung.

```yaml
# types/pipelineExecution.yaml
id: PipelineExecution
ckTypeId: System.Communication/PipelineExecution
description: Represents a single pipeline execution instance
attributes:
  - name: executionId
    type: Guid
    description: Unique identifier for this execution
    required: true

  - name: status
    type: PipelineExecutionStatusEnum
    description: Current execution status
    required: true

  - name: startedAt
    type: DateTime
    description: Timestamp when execution started
    required: true

  - name: completedAt
    type: DateTime
    description: Timestamp when execution completed (null if running)
    required: false

  - name: durationMs
    type: Int64
    description: Execution duration in milliseconds (null if running)
    required: false

  - name: errorMessage
    type: String
    description: Error message if execution failed
    required: false

  - name: triggerType
    type: PipelineTriggerTypeEnum
    description: How the execution was triggered
    required: true

  - name: inputData
    type: String
    description: Optional input data (JSON) for debugging
    required: false

associations:
  - name: ExecutedPipeline
    targetType: Pipeline
    cardinality: ManyToOne
    description: The pipeline that was executed

  - name: ExecutingAdapter
    targetType: Adapter
    cardinality: ManyToOne
    description: The adapter that executed the pipeline
```

### 2.2 Neue Entity: `PipelineStatistics`

Aggregierte Statistiken pro Pipeline und Adapter.

```yaml
# types/pipelineStatistics.yaml
id: PipelineStatistics
ckTypeId: System.Communication/PipelineStatistics
description: Aggregated execution statistics for a pipeline
attributes:
  # Letzte Stunde
  - name: lastHourSuccessCount
    type: Int32
    description: Successful executions in the last hour

  - name: lastHourFailureCount
    type: Int32
    description: Failed executions in the last hour

  - name: lastHourAvgDurationMs
    type: Int64
    description: Average execution duration in the last hour

  # Letzte 12 Stunden
  - name: last12HoursSuccessCount
    type: Int32

  - name: last12HoursFailureCount
    type: Int32

  - name: last12HoursAvgDurationMs
    type: Int64

  # Letzte 24 Stunden
  - name: last24HoursSuccessCount
    type: Int32

  - name: last24HoursFailureCount
    type: Int32

  - name: last24HoursAvgDurationMs
    type: Int64

  # Letzte 30 Tage
  - name: last30DaysSuccessCount
    type: Int32

  - name: last30DaysFailureCount
    type: Int32

  - name: last30DaysAvgDurationMs
    type: Int64

  # Meta
  - name: lastUpdatedAt
    type: DateTime
    description: When statistics were last calculated

  - name: lastExecutionAt
    type: DateTime
    description: Timestamp of the most recent execution

associations:
  - name: StatisticsForPipeline
    targetType: Pipeline
    cardinality: OneToOne
    description: The pipeline these statistics belong to

  - name: StatisticsForAdapter
    targetType: Adapter
    cardinality: ManyToOne
    description: The adapter (optional, for adapter-specific stats)
```

### 2.3 Neue Enums

```yaml
# enums/pipelineExecutionStatus.yaml
id: PipelineExecutionStatusEnum
values:
  - name: Running
    value: 0
    description: Execution is currently in progress

  - name: Completed
    value: 1
    description: Execution completed successfully

  - name: Failed
    value: 2
    description: Execution failed with an error

  - name: Interrupted
    value: 3
    description: Adapter disconnected during execution (awaiting final status on reconnect)

  - name: Cancelled
    value: 4
    description: Execution was cancelled
```

```yaml
# enums/pipelineTriggerType.yaml
id: PipelineTriggerTypeEnum
values:
  - name: Manual
    value: 0
    description: Manually triggered via API

  - name: Scheduled
    value: 1
    description: Triggered by a scheduled trigger

  - name: Event
    value: 2
    description: Triggered by an external event

  - name: Startup
    value: 3
    description: Triggered on adapter startup
```

### 2.4 Erweiterung bestehender Entities

**Pipeline** - Erweiterung um aktuelle Execution:

```yaml
# Ergänzung zu types/pipeline.yaml
attributes:
  - name: currentExecutionId
    type: Guid
    description: ID of currently running execution (null if not running)
    required: false

  - name: isExecuting
    type: Boolean
    description: Whether the pipeline is currently executing
    required: false
    default: false
```

---

## 3. SignalR Hub Erweiterungen

### 3.1 Neue AdapterHub Methoden

#### Client → Server

```csharp
/// <summary>
/// Called by adapter when a pipeline execution starts
/// </summary>
Task ReportExecutionStartAsync(PipelineExecutionStartDto executionStart);

/// <summary>
/// Called by adapter when a pipeline execution completes
/// </summary>
Task ReportExecutionEndAsync(PipelineExecutionEndDto executionEnd);

/// <summary>
/// Called by adapter on reconnect to report final status of interrupted executions
/// </summary>
Task ReportInterruptedExecutionResultAsync(PipelineExecutionEndDto executionEnd);
```

> **Hinweis:** Ein separater Heartbeat ist nicht notwendig, da SignalR bereits
> Connection-Monitoring (Ping/Pong) bereitstellt. Bei Disconnect werden laufende
> Executions auf `Interrupted` gesetzt.

#### DTOs

```csharp
public record PipelineExecutionStartDto(
    Guid ExecutionId,
    RtEntityId PipelineRtEntityId,
    RtEntityId AdapterRtEntityId,
    PipelineTriggerTypeEnum TriggerType,
    DateTime StartedAt,
    string? InputData
);

public record PipelineExecutionEndDto(
    Guid ExecutionId,
    RtEntityId PipelineRtEntityId,
    RtEntityId AdapterRtEntityId,
    DateTime CompletedAt,
    bool IsSuccess,
    string? ErrorMessage,
    long DurationMs
);
```

---

## 4. Service Layer

### 4.1 Neuer Service: `IPipelineExecutionService`

```csharp
public interface IPipelineExecutionService
{
    // Execution Tracking
    Task<Guid> StartExecutionAsync(string tenantId, PipelineExecutionStartDto start);
    Task CompleteExecutionAsync(string tenantId, PipelineExecutionEndDto end);

    // Disconnect Handling (called from AdapterHub.OnDisconnectedAsync)
    Task MarkExecutionsAsInterruptedAsync(string tenantId, RtEntityId adapterRtEntityId);

    // Reconnect Handling (adapter reports final status of interrupted executions)
    Task ReportInterruptedExecutionResultAsync(string tenantId, PipelineExecutionEndDto end);

    // Statistics
    Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);
    Task<PipelineStatisticsDto> GetStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);

    // Queries
    Task<IReadOnlyList<PipelineExecutionDto>> GetExecutionsAsync(
        string tenantId,
        RtEntityId pipelineRtEntityId,
        DateTime? from,
        DateTime? to,
        int? limit);

    Task<PipelineExecutionDto?> GetCurrentExecutionAsync(
        string tenantId,
        RtEntityId pipelineRtEntityId);

    // Get interrupted executions for an adapter (called on reconnect)
    Task<IReadOnlyList<Guid>> GetInterruptedExecutionIdsAsync(
        string tenantId,
        RtEntityId adapterRtEntityId);

    // Cleanup (Background Service)
    Task CleanupOldExecutionsAsync(string tenantId, int retentionDays = 30);
}
```

### 4.2 Implementierung: `PipelineExecutionService`

```csharp
internal class PipelineExecutionService : IPipelineExecutionService
{
    private readonly ICommunicationRepository _repository;
    private readonly ICommunicationEventService _eventService;
    private readonly ILogger<PipelineExecutionService> _logger;

    public async Task<Guid> StartExecutionAsync(string tenantId, PipelineExecutionStartDto start)
    {
        _logger.LogInformation(
            "[{TenantId}] Pipeline execution started: {ExecutionId} for {PipelineRtEntityId}",
            tenantId, start.ExecutionId, start.PipelineRtEntityId);

        // 1. Create execution record in MongoDB
        var execution = new RtPipelineExecution
        {
            ExecutionId = start.ExecutionId,
            Status = RtPipelineExecutionStatusEnum.Running,
            StartedAt = start.StartedAt,
            TriggerType = start.TriggerType,
            InputData = start.InputData
        };

        await _repository.CreatePipelineExecutionAsync(tenantId, execution,
            start.PipelineRtEntityId, start.AdapterRtEntityId);

        // 2. Update pipeline's current execution
        await _repository.SetPipelineCurrentExecutionAsync(tenantId,
            start.PipelineRtEntityId, start.ExecutionId);

        // 3. Log event
        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pipeline execution started: {start.ExecutionId}",
            start.PipelineRtEntityId);

        return start.ExecutionId;
    }

    public async Task CompleteExecutionAsync(string tenantId, PipelineExecutionEndDto end)
    {
        _logger.LogInformation(
            "[{TenantId}] Pipeline execution completed: {ExecutionId}, Success: {IsSuccess}",
            tenantId, end.ExecutionId, end.IsSuccess);

        // 1. Update execution record
        var status = end.IsSuccess
            ? RtPipelineExecutionStatusEnum.Completed
            : RtPipelineExecutionStatusEnum.Failed;

        await _repository.UpdatePipelineExecutionAsync(tenantId, end.ExecutionId,
            status, end.CompletedAt, end.DurationMs, end.ErrorMessage);

        // 2. Clear pipeline's current execution
        await _repository.SetPipelineCurrentExecutionAsync(tenantId,
            end.PipelineRtEntityId, null);

        // 3. Update statistics (async, fire-and-forget)
        _ = UpdateStatisticsAsync(tenantId, end.PipelineRtEntityId);

        // 4. Log event
        var level = end.IsSuccess
            ? RtEventLevelsEnum.Information
            : RtEventLevelsEnum.Error;

        var message = end.IsSuccess
            ? $"Pipeline execution completed: {end.ExecutionId}, Duration: {end.DurationMs}ms"
            : $"Pipeline execution failed: {end.ExecutionId}, Error: {end.ErrorMessage}";

        await _eventService.StoreEventAsync(tenantId, level, message, end.PipelineRtEntityId);
    }

    /// <summary>
    /// Called from AdapterHub.OnDisconnectedAsync to mark all running executions
    /// for this adapter as Interrupted
    /// </summary>
    public async Task MarkExecutionsAsInterruptedAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        _logger.LogInformation(
            "[{TenantId}] Marking running executions as interrupted for adapter {AdapterRtEntityId}",
            tenantId, adapterRtEntityId);

        // 1. Find all running executions for this adapter
        var runningExecutions = await _repository.GetRunningExecutionsForAdapterAsync(
            tenantId, adapterRtEntityId);

        if (!runningExecutions.Any())
        {
            return;
        }

        // 2. Mark each as Interrupted
        foreach (var execution in runningExecutions)
        {
            await _repository.UpdatePipelineExecutionAsync(tenantId, execution.ExecutionId,
                RtPipelineExecutionStatusEnum.Interrupted, DateTime.UtcNow, null,
                "Adapter disconnected during execution");

            // Clear pipeline's current execution
            await _repository.SetPipelineCurrentExecutionAsync(tenantId,
                execution.PipelineRtEntityId, null);

            _logger.LogWarning(
                "[{TenantId}] Execution {ExecutionId} marked as interrupted",
                tenantId, execution.ExecutionId);
        }

        await _eventService.StoreWarningEventAsync(tenantId,
            $"Adapter {adapterRtEntityId} disconnected. {runningExecutions.Count} execution(s) marked as interrupted.",
            adapterRtEntityId);
    }

    /// <summary>
    /// Called when adapter reconnects and reports final status of interrupted executions
    /// </summary>
    public async Task ReportInterruptedExecutionResultAsync(string tenantId, PipelineExecutionEndDto end)
    {
        _logger.LogInformation(
            "[{TenantId}] Reporting interrupted execution result: {ExecutionId}, Success: {IsSuccess}",
            tenantId, end.ExecutionId, end.IsSuccess);

        // Get current execution state
        var execution = await _repository.GetPipelineExecutionAsync(tenantId, end.ExecutionId);
        if (execution == null)
        {
            _logger.LogWarning(
                "[{TenantId}] Execution {ExecutionId} not found for interrupted result report",
                tenantId, end.ExecutionId);
            return;
        }

        // Only update if still in Interrupted state
        if (execution.Status != RtPipelineExecutionStatusEnum.Interrupted)
        {
            _logger.LogDebug(
                "[{TenantId}] Execution {ExecutionId} is not in Interrupted state, skipping",
                tenantId, end.ExecutionId);
            return;
        }

        // Update with final status
        var status = end.IsSuccess
            ? RtPipelineExecutionStatusEnum.Completed
            : RtPipelineExecutionStatusEnum.Failed;

        await _repository.UpdatePipelineExecutionAsync(tenantId, end.ExecutionId,
            status, end.CompletedAt, end.DurationMs, end.ErrorMessage);

        // Update statistics
        _ = UpdateStatisticsAsync(tenantId, end.PipelineRtEntityId);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Interrupted execution {end.ExecutionId} final status reported: {(end.IsSuccess ? "Completed" : "Failed")}",
            end.PipelineRtEntityId);
    }

    /// <summary>
    /// Returns execution IDs that are in Interrupted state for this adapter
    /// Called by adapter on reconnect to know which executions need final status
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetInterruptedExecutionIdsAsync(
        string tenantId, RtEntityId adapterRtEntityId)
    {
        return await _repository.GetInterruptedExecutionIdsAsync(tenantId, adapterRtEntityId);
    }
}
```

---

## 5. Background Services

### 5.1 Statistik-Aggregation

```csharp
public class PipelineStatisticsBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IPipelineExecutionService>();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();

            foreach (var tenantId in await tenantService.GetEnabledTenantsAsync())
            {
                try
                {
                    await service.UpdateAllStatisticsAsync(tenantId);
                }
                catch (Exception ex)
                {
                    // Log and continue
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Alle 5 Minuten
        }
    }
}
```

> **Hinweis:** Ein Timeout-Detection Background Service ist nicht notwendig.
> SignalR erkennt Disconnects automatisch, woraufhin `OnDisconnectedAsync`
> die laufenden Executions auf `Interrupted` setzt.

### 5.2 Cleanup (Retention)

```csharp
public class ExecutionCleanupBackgroundService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Run once per day
            foreach (var tenantId in await GetEnabledTenantsAsync())
            {
                await _executionService.CleanupOldExecutionsAsync(tenantId, retentionDays: 30);
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
```

---

## 6. Repository Erweiterungen

```csharp
public interface ICommunicationRepository
{
    // Existing methods...

    // Pipeline Execution
    Task CreatePipelineExecutionAsync(string tenantId, RtPipelineExecution execution,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId);

    Task UpdatePipelineExecutionAsync(string tenantId, Guid executionId,
        RtPipelineExecutionStatusEnum status, DateTime? completedAt,
        long? durationMs, string? errorMessage);

    Task<RtPipelineExecution?> GetPipelineExecutionAsync(string tenantId, Guid executionId);

    Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(
        string tenantId, RtEntityId pipelineRtEntityId,
        DateTime? from, DateTime? to, int? limit);

    Task<RtPipelineExecution?> GetCurrentExecutionAsync(
        string tenantId, RtEntityId pipelineRtEntityId);

    Task SetPipelineCurrentExecutionAsync(string tenantId,
        RtEntityId pipelineRtEntityId, Guid? executionId);

    Task DeleteOldExecutionsAsync(string tenantId, DateTime olderThan);

    // Pipeline Statistics
    Task<RtPipelineStatistics?> GetPipelineStatisticsAsync(
        string tenantId, RtEntityId pipelineRtEntityId);

    Task UpdatePipelineStatisticsAsync(string tenantId, RtPipelineStatistics statistics);

    // Aggregation Queries
    Task<ExecutionAggregateResult> GetExecutionAggregateAsync(
        string tenantId, RtEntityId pipelineRtEntityId,
        DateTime from, DateTime to);
}

public record ExecutionAggregateResult(
    int SuccessCount,
    int FailureCount,
    long TotalDurationMs,
    int ExecutionCount
)
{
    public long AvgDurationMs => ExecutionCount > 0 ? TotalDurationMs / ExecutionCount : 0;
}
```

---

## 7. Datenfluss

### 7.1 Execution Start

```
1. Adapter startet Pipeline
   │
   ▼
2. Adapter ruft ReportExecutionStartAsync via SignalR
   │
   ▼
3. AdapterHub empfängt und ruft PipelineExecutionService.StartExecutionAsync
   │
   ├──▶ 4a. Erstellt PipelineExecution in MongoDB
   │
   ├──▶ 4b. Setzt Pipeline.currentExecutionId
   │
   ├──▶ 4c. Speichert in In-Memory Cache (für Timeout-Detection)
   │
   └──▶ 4d. Loggt System Event
   │
   ▼
5. GraphQL Subscription benachrichtigt verbundene Clients (Asset Repo)
```

### 7.2 Execution End

```
1. Adapter beendet Pipeline (Erfolg oder Fehler)
   │
   ▼
2. Adapter ruft ReportExecutionEndAsync via SignalR
   │
   ▼
3. AdapterHub empfängt und ruft PipelineExecutionService.CompleteExecutionAsync
   │
   ├──▶ 4a. Aktualisiert PipelineExecution (status, completedAt, duration)
   │
   ├──▶ 4b. Löscht Pipeline.currentExecutionId
   │
   ├──▶ 4c. Entfernt aus In-Memory Cache
   │
   ├──▶ 4d. Triggert Statistik-Update (async)
   │
   └──▶ 4e. Loggt System Event
   │
   ▼
5. GraphQL Subscription benachrichtigt verbundene Clients
```

### 7.3 Statistik-Berechnung

```
1. Background Service triggert alle 5 Minuten
   │
   ▼
2. Für jede Pipeline mit Änderungen:
   │
   ├──▶ 3a. Aggregate für letzte Stunde berechnen
   │         SELECT COUNT(*) WHERE status=Completed AND completedAt > NOW-1h
   │
   ├──▶ 3b. Aggregate für letzte 12h berechnen
   │
   ├──▶ 3c. Aggregate für letzte 24h berechnen
   │
   └──▶ 3d. Aggregate für letzte 30 Tage berechnen
   │
   ▼
4. PipelineStatistics Entity aktualisieren
   │
   ▼
5. GraphQL Subscription für Statistik-Updates
```

---

## 8. GraphQL Queries (Asset Repository)

Die folgenden Queries werden durch die CK-Model-Integration automatisch verfügbar:

```graphql
# Alle Executions einer Pipeline
query GetPipelineExecutions($pipelineId: ID!, $from: DateTime, $to: DateTime) {
  pipelineExecutions(
    filter: { executedPipeline: { rtId: $pipelineId } }
    dateFilter: { startedAt: { gte: $from, lte: $to } }
    orderBy: { startedAt: DESC }
    first: 100
  ) {
    edges {
      node {
        executionId
        status
        startedAt
        completedAt
        durationMs
        errorMessage
        triggerType
        executingAdapter {
          rtId
          ckTypeId
        }
      }
    }
  }
}

# Aktuelle laufende Execution
query GetCurrentExecution($pipelineId: ID!) {
  pipelines(filter: { rtId: $pipelineId }) {
    edges {
      node {
        currentExecutionId
        isExecuting
      }
    }
  }
}

# Statistiken einer Pipeline
query GetPipelineStatistics($pipelineId: ID!) {
  pipelineStatistics(filter: { statisticsForPipeline: { rtId: $pipelineId } }) {
    edges {
      node {
        lastHourSuccessCount
        lastHourFailureCount
        lastHourAvgDurationMs
        last12HoursSuccessCount
        last12HoursFailureCount
        last24HoursSuccessCount
        last24HoursFailureCount
        last30DaysSuccessCount
        last30DaysFailureCount
        last30DaysAvgDurationMs
        lastExecutionAt
        lastUpdatedAt
      }
    }
  }
}

# Subscription für Live-Updates
subscription OnPipelineExecutionChanged($pipelineId: ID!) {
  pipelineExecutionChanged(pipelineId: $pipelineId) {
    executionId
    status
    startedAt
    completedAt
    durationMs
    errorMessage
  }
}
```

---

## 9. Implementierungs-Roadmap

### Phase 1: CK Model & Datenstrukturen (2-3 Tage)

- [ ] Neue Enums definieren (`PipelineExecutionStatusEnum`, `PipelineTriggerTypeEnum`)
- [ ] `PipelineExecution` Entity definieren
- [ ] `PipelineStatistics` Entity definieren
- [ ] `Pipeline` Entity erweitern (currentExecutionId, isExecuting)
- [ ] CK Model bauen und generieren

### Phase 2: Repository Layer (2 Tage)

- [ ] `ICommunicationRepository` erweitern
- [ ] MongoDB Queries für Executions implementieren
- [ ] Aggregation Queries implementieren
- [ ] Unit Tests

### Phase 3: Service Layer (3 Tage)

- [ ] `IPipelineExecutionService` Interface definieren
- [ ] `PipelineExecutionService` implementieren
- [ ] In-Memory Cache für laufende Executions
- [ ] Statistik-Berechnung implementieren
- [ ] Unit Tests

### Phase 4: SignalR Integration (2 Tage)

- [ ] DTOs für Execution Start/End definieren
- [ ] AdapterHub Methoden hinzufügen
- [ ] Error Handling und Logging

### Phase 5: Background Services (1-2 Tage)

- [ ] Statistik-Aggregation Background Service
- [ ] Timeout-Detection Background Service
- [ ] Cleanup (Retention) Background Service

### Phase 6: Testing & Integration (2-3 Tage)

- [ ] Integration Tests
- [ ] Performance Tests (viele Executions)
- [ ] End-to-End Test mit Adapter

### Gesamt: ~12-15 Arbeitstage

---

## 10. Offene Fragen / Entscheidungen

1. **Statistik-Update-Intervall**: Wie oft sollen die Aggregate neu berechnet werden? (Vorschlag: alle 5 Minuten)

2. **InputData speichern**: Soll das Input-JSON jeder Execution gespeichert werden? (Könnte viel Speicher benötigen)

3. **Adapter-Änderungen**: Welche Änderungen sind am Adapter-Projekt nötig? (Muss separat geplant werden)

4. **Interrupted Executions**: Wie lange sollen Executions im "Interrupted" Status bleiben, bevor sie als "Failed" markiert werden? (Vorschlag: Nach 7 Tagen ohne Adapter-Reconnect auf Failed setzen)

---

## 11. Risiken & Mitigation

| Risiko                           | Wahrscheinlichkeit | Auswirkung | Mitigation                                  |
|----------------------------------|--------------------|------------|---------------------------------------------|
| Hohe Last durch viele Executions | Mittel             | Hoch       | Batching, Aggregation, Index-Optimierung    |
| MongoDB Performance              | Mittel             | Hoch       | TTL Index, Sharding, Archivierung           |
| Adapter meldet End nicht (Crash) | Mittel             | Niedrig    | SignalR Disconnect → Interrupted Status     |
| Viele Interrupted Executions     | Niedrig            | Niedrig    | Adapter meldet bei Reconnect finalen Status |
| Offline-Sync bei großen Buffern  | Mittel             | Mittel     | Batch-Sync in Chunks (max 1000 pro Request) |

---

## 12. Offline-Szenario: Edge Adapter mit Buffering

### 12.1 Problem

Edge Adapter können zeitweise offline arbeiten und Daten puffern:
- Netzwerkunterbrechungen
- Geplante Offline-Phasen
- Mobile/Remote Deployments

Während dieser Zeit werden Pipelines weiterhin ausgeführt, aber der Communication Controller weiß nichts davon.

### 12.2 Lösungsansatz: Execution Buffer & Sync

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        Edge Adapter (Offline-fähig)                         │
│                                                                             │
│   ┌────────────────┐    ┌─────────────────────┐    ┌────────────────────┐   │
│   │    Pipeline    │───▶│ ExecutionRecorder   │───▶│  Local Buffer      │   │
│   │    Executor    │    │ (lokal speichern)   │    │  (SQLite/File)     │   │
│   └────────────────┘    └─────────────────────┘    └─────────┬──────────┘   │
│                                                               │             │
│                                                               │ Online?     │
│                                                               ▼             │
│   ┌──────────────────────────────────────────────────────────────────────┐  │
│   │                      SyncManager                                     │  │
│   │  - Erkennt Online-Status                                             │  │
│   │  - Sendet gepufferte Executions                                      │  │
│   │  - Bestätigt erfolgreiche Sync                                       │  │
│   │  - Löscht synchronisierte Daten                                      │  │
│   └──────────────────────────────────────────────────────────────────────┘  │
│                                              │                              │
└──────────────────────────────────────────────┼──────────────────────────────┘
                                               │ SignalR (wenn online)
                                               ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                     Communication Controller Service                        │
│                                                                             │
│   ┌─────────────────────────────────────────────────────────────────────┐   │
│   │                     AdapterHub (erweitert)                          │   │
│   │                                                                     │   │
│   │   + SyncBufferedExecutionsAsync(List<BufferedExecution>)            │   │
│   │   + AcknowledgeBufferedExecutionsAsync(List<Guid> executionIds)     │   │
│   │                                                                     │   │
│   └─────────────────────────────────────────────────────────────────────┘   │
│                                    │                                        │
│                                    ▼                                        │
│   ┌─────────────────────────────────────────────────────────────────────┐   │
│   │                  PipelineExecutionService (erweitert)               │   │
│   │                                                                     │   │
│   │   + ProcessBufferedExecutionsAsync(...)                             │   │
│   │     - Validierung (Duplikate, Reihenfolge)                          │   │
│   │     - Batch-Insert in MongoDB                                       │   │
│   │     - Statistik-Neuberechnung                                       │   │
│   │                                                                     │   │
│   └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 12.3 Datenstrukturen für Buffering

#### Gepufferte Execution (Adapter-seitig)

```csharp
public record BufferedPipelineExecution
{
    /// <summary>
    /// Unique execution ID (generated on adapter)
    /// </summary>
    public Guid ExecutionId { get; init; }

    /// <summary>
    /// Pipeline that was executed
    /// </summary>
    public RtEntityId PipelineRtEntityId { get; init; }

    /// <summary>
    /// Adapter that executed the pipeline
    /// </summary>
    public RtEntityId AdapterRtEntityId { get; init; }

    /// <summary>
    /// Execution status
    /// </summary>
    public PipelineExecutionStatusEnum Status { get; init; }

    /// <summary>
    /// When execution started (UTC)
    /// </summary>
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// When execution completed (UTC)
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Execution duration in milliseconds
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// How the execution was triggered
    /// </summary>
    public PipelineTriggerTypeEnum TriggerType { get; init; }

    /// <summary>
    /// Sequence number for ordering (adapter-local)
    /// </summary>
    public long SequenceNumber { get; init; }

    /// <summary>
    /// Hash of the execution data for integrity check
    /// </summary>
    public string? Checksum { get; init; }
}
```

#### Sync Request/Response

```csharp
public record BufferedExecutionsSyncRequest
{
    /// <summary>
    /// Adapter sending the buffered data
    /// </summary>
    public RtEntityId AdapterRtEntityId { get; init; }

    /// <summary>
    /// Buffered executions to sync
    /// </summary>
    public List<BufferedPipelineExecution> Executions { get; init; }

    /// <summary>
    /// Last known sequence number that was synced
    /// </summary>
    public long LastSyncedSequenceNumber { get; init; }

    /// <summary>
    /// Timestamp when buffer started (for gap detection)
    /// </summary>
    public DateTime BufferStartedAt { get; init; }
}

public record BufferedExecutionsSyncResponse
{
    /// <summary>
    /// Successfully synced execution IDs
    /// </summary>
    public List<Guid> SyncedExecutionIds { get; init; }

    /// <summary>
    /// IDs that were rejected (duplicates, invalid)
    /// </summary>
    public List<Guid> RejectedExecutionIds { get; init; }

    /// <summary>
    /// New sync sequence number
    /// </summary>
    public long NewSequenceNumber { get; init; }

    /// <summary>
    /// Any errors during sync
    /// </summary>
    public string? ErrorMessage { get; init; }
}
```

### 12.4 SignalR Hub Erweiterungen für Offline-Sync

```csharp
public interface IAdapterHub
{
    // Existing methods...

    /// <summary>
    /// Syncs buffered executions from an adapter that was offline
    /// </summary>
    /// <param name="request">Buffered executions and metadata</param>
    /// <returns>Sync result with ack/nack for each execution</returns>
    Task<BufferedExecutionsSyncResponse> SyncBufferedExecutionsAsync(
        BufferedExecutionsSyncRequest request);

    /// <summary>
    /// Gets the last synced sequence number for resume after disconnect
    /// </summary>
    Task<long> GetLastSyncedSequenceNumberAsync(RtEntityId adapterRtEntityId);
}
```

### 12.5 Service-Erweiterungen

```csharp
public interface IPipelineExecutionService
{
    // Existing methods...

    /// <summary>
    /// Processes a batch of buffered executions from an offline adapter
    /// </summary>
    Task<BufferedExecutionsSyncResponse> ProcessBufferedExecutionsAsync(
        string tenantId,
        BufferedExecutionsSyncRequest request);

    /// <summary>
    /// Gets the last synced sequence number for an adapter
    /// </summary>
    Task<long> GetLastSyncedSequenceNumberAsync(
        string tenantId,
        RtEntityId adapterRtEntityId);
}
```

### 12.6 Implementierung: Buffered Execution Processing

```csharp
public async Task<BufferedExecutionsSyncResponse> ProcessBufferedExecutionsAsync(
    string tenantId,
    BufferedExecutionsSyncRequest request)
{
    var syncedIds = new List<Guid>();
    var rejectedIds = new List<Guid>();

    _logger.LogInformation(
        "[{TenantId}] Processing {Count} buffered executions from adapter {AdapterId}",
        tenantId, request.Executions.Count, request.AdapterRtEntityId);

    // 1. Sortieren nach SequenceNumber
    var orderedExecutions = request.Executions
        .OrderBy(e => e.SequenceNumber)
        .ToList();

    // 2. Duplikate erkennen
    var existingIds = await _repository.GetExistingExecutionIdsAsync(
        tenantId,
        orderedExecutions.Select(e => e.ExecutionId).ToList());

    // 3. Batch verarbeiten
    var newExecutions = new List<RtPipelineExecution>();
    foreach (var buffered in orderedExecutions)
    {
        if (existingIds.Contains(buffered.ExecutionId))
        {
            _logger.LogDebug(
                "[{TenantId}] Skipping duplicate execution {ExecutionId}",
                tenantId, buffered.ExecutionId);
            rejectedIds.Add(buffered.ExecutionId);
            continue;
        }

        // Validierung
        if (!ValidateBufferedExecution(buffered, out var validationError))
        {
            _logger.LogWarning(
                "[{TenantId}] Invalid buffered execution {ExecutionId}: {Error}",
                tenantId, buffered.ExecutionId, validationError);
            rejectedIds.Add(buffered.ExecutionId);
            continue;
        }

        newExecutions.Add(MapToRtPipelineExecution(buffered));
        syncedIds.Add(buffered.ExecutionId);
    }

    // 4. Batch Insert
    if (newExecutions.Any())
    {
        await _repository.BulkInsertPipelineExecutionsAsync(tenantId, newExecutions);

        // 5. Statistiken für betroffene Pipelines aktualisieren
        var affectedPipelines = newExecutions
            .Select(e => e.PipelineRtEntityId)
            .Distinct();

        foreach (var pipelineId in affectedPipelines)
        {
            await UpdateStatisticsAsync(tenantId, pipelineId);
        }
    }

    // 6. Sequence Number aktualisieren
    var newSequenceNumber = orderedExecutions.Any()
        ? orderedExecutions.Max(e => e.SequenceNumber)
        : request.LastSyncedSequenceNumber;

    await _repository.UpdateAdapterSyncSequenceNumberAsync(
        tenantId, request.AdapterRtEntityId, newSequenceNumber);

    // 7. Event loggen
    await _eventService.StoreInformationEventAsync(tenantId,
        $"Synced {syncedIds.Count} buffered executions from adapter {request.AdapterRtEntityId}. " +
        $"Rejected: {rejectedIds.Count}",
        request.AdapterRtEntityId);

    return new BufferedExecutionsSyncResponse
    {
        SyncedExecutionIds = syncedIds,
        RejectedExecutionIds = rejectedIds,
        NewSequenceNumber = newSequenceNumber
    };
}
```

### 12.7 Adapter-seitige Implementierung (Konzept)

```csharp
public class ExecutionBufferManager
{
    private readonly ILocalStorage _storage; // SQLite oder File-basiert
    private readonly ISignalRConnection _connection;
    private long _sequenceNumber;

    /// <summary>
    /// Records an execution (online or offline)
    /// </summary>
    public async Task RecordExecutionAsync(PipelineExecutionRecord record)
    {
        record.SequenceNumber = Interlocked.Increment(ref _sequenceNumber);

        if (_connection.IsConnected)
        {
            // Online: Direkt senden
            try
            {
                await _connection.InvokeAsync("ReportExecutionStartAsync", record.ToStartDto());
                // ... später ReportExecutionEndAsync
            }
            catch
            {
                // Fallback: Puffern
                await BufferExecutionAsync(record);
            }
        }
        else
        {
            // Offline: Puffern
            await BufferExecutionAsync(record);
        }
    }

    /// <summary>
    /// Syncs all buffered executions when coming online
    /// </summary>
    public async Task SyncBufferedExecutionsAsync()
    {
        var buffered = await _storage.GetUnsynedExecutionsAsync();
        if (!buffered.Any()) return;

        var request = new BufferedExecutionsSyncRequest
        {
            AdapterRtEntityId = _adapterRtEntityId,
            Executions = buffered,
            LastSyncedSequenceNumber = await _storage.GetLastSyncedSequenceNumberAsync(),
            BufferStartedAt = buffered.Min(e => e.StartedAt)
        };

        var response = await _connection.InvokeAsync<BufferedExecutionsSyncResponse>(
            "SyncBufferedExecutionsAsync", request);

        // Erfolgreich synchronisierte löschen
        await _storage.DeleteExecutionsAsync(response.SyncedExecutionIds);

        // Sequence Number aktualisieren
        await _storage.SetLastSyncedSequenceNumberAsync(response.NewSequenceNumber);
    }

    private async Task BufferExecutionAsync(PipelineExecutionRecord record)
    {
        await _storage.InsertExecutionAsync(new BufferedPipelineExecution
        {
            ExecutionId = record.ExecutionId,
            PipelineRtEntityId = record.PipelineRtEntityId,
            AdapterRtEntityId = _adapterRtEntityId,
            Status = record.Status,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            DurationMs = record.DurationMs,
            ErrorMessage = record.ErrorMessage,
            TriggerType = record.TriggerType,
            SequenceNumber = record.SequenceNumber,
            Checksum = ComputeChecksum(record)
        });
    }
}
```

### 12.8 Sync-Protokoll

```
┌─────────────────┐                           ┌─────────────────────────────┐
│   Edge Adapter  │                           │  Communication Controller   │
└────────┬────────┘                           └──────────────┬──────────────┘
         │                                                   │
         │  ══════════ OFFLINE PHASE ══════════              │
         │                                                   │
         │  [Pipeline executes]                              │
         │  [Record to local buffer]                         │
         │  [Pipeline executes]                              │
         │  [Record to local buffer]                         │
         │  ...                                              │
         │                                                   │
         │  ══════════ ONLINE PHASE ══════════               │
         │                                                   │
         │──────── SignalR Connect ─────────────────────────▶│
         │                                                   │
         │──────── GetLastSyncedSequenceNumberAsync ────────▶│
         │◀─────── Returns: 42 ──────────────────────────────│
         │                                                   │
         │  [Query local buffer for seq > 42]                │
         │                                                   │
         │──────── SyncBufferedExecutionsAsync ─────────────▶│
         │         { executions: [...], lastSeq: 42 }        │
         │                                                   │
         │                                    [Validate]     │
         │                                    [Dedupe]       │
         │                                    [Batch Insert] │
         │                                    [Update Stats] │
         │                                                   │
         │◀─────── SyncResponse ─────────────────────────────│
         │         { synced: [...], rejected: [...],         │
         │           newSeq: 57 }                            │
         │                                                   │
         │  [Delete synced from local buffer]                │
         │  [Update local sequence number]                   │
         │                                                   │
         │  ══════════ NORMAL OPERATION ══════════           │
         │                                                   │
         │──────── ReportExecutionStartAsync ───────────────▶│
         │──────── ReportExecutionEndAsync ─────────────────▶│
         │                                                   │
```

### 12.9 Edge Cases & Fehlerbehandlung

| Szenario                         | Handling                                                              |
|----------------------------------|-----------------------------------------------------------------------|
| **Duplikate**                    | Server erkennt via ExecutionId, ignoriert Duplikate                   |
| **Lücken in Sequenz**            | Warnung loggen, aber akzeptieren (Daten könnten gelöscht worden sein) |
| **Sehr großer Buffer**           | Batch in Chunks senden (z.B. 1000 pro Request)                        |
| **Sync-Unterbrechung**           | Bei nächstem Connect ab letzter Sequenz fortsetzen                    |
| **Checksum-Fehler**              | Execution ablehnen, Adapter muss erneut senden                        |
| **Zeitstempel in Zukunft**       | Warnung, aber akzeptieren (Uhrzeit-Sync-Problem)                      |
| **Adapter meldet nach Löschung** | Adapter-ID nicht mehr bekannt → Fehler an Adapter                     |

### 12.10 Konfiguration für Offline-Modus

```json
{
  "CommunicationController": {
    "OfflineSync": {
      "MaxBatchSize": 1000,
      "SyncTimeoutSeconds": 120,
      "MaxBufferAge": "30.00:00:00",
      "EnableChecksumValidation": true
    }
  }
}
```

### 12.11 Erweiterung der Implementierungs-Roadmap

#### Phase 7: Offline-Sync Support (3-4 Tage)

- [ ] DTOs für Buffered Executions definieren
- [ ] SignalR Hub Methoden für Sync
- [ ] `ProcessBufferedExecutionsAsync` implementieren
- [ ] Bulk-Insert Repository Methoden
- [ ] Sequence Number Tracking
- [ ] Integration Tests für Offline-Szenarien

#### Phase 8: Adapter-seitige Dokumentation (1 Tag)

- [ ] Spezifikation für Adapter-Entwickler
- [ ] Beispiel-Implementierung `ExecutionBufferManager`
- [ ] Lokaler Storage Empfehlungen (SQLite)

---

## Anhang: Beispiel-Konfiguration

```json
{
  "CommunicationController": {
    "PipelineExecution": {
      "StatisticsUpdateIntervalMinutes": 5,
      "RetentionDays": 30,
      "StoreInputData": false,
      "MaxInputDataLength": 10000,
      "InterruptedExecutionExpiryDays": 7
    },
    "OfflineSync": {
      "MaxBatchSize": 1000,
      "SyncTimeoutSeconds": 120,
      "MaxBufferAge": "30.00:00:00",
      "EnableChecksumValidation": true
    }
  }
}
```
