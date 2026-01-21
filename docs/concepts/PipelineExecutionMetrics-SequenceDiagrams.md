# Pipeline Execution Metrics - Sequenzdiagramme

Diese Datei enthält Mermaid-Sequenzdiagramme für die verschiedenen Abläufe des Pipeline Execution Tracking Features.

## 1. Normaler Ablauf (Online)

```mermaid
sequenceDiagram
    participant A as Edge Adapter
    participant H as AdapterHub
    participant S as PipelineExecutionService
    participant DB as MongoDB

    Note over A,DB: Pipeline-Ausführung startet

    A->>H: ReportExecutionStartAsync(executionStart)
    H->>S: StartExecutionAsync(tenantId, executionStart)
    S->>DB: CreatePipelineExecution(status=Running)
    S->>DB: SetPipelineCurrentExecution(executionId)
    S-->>H: executionId
    H-->>A: OK

    Note over A: Pipeline wird ausgeführt...

    alt Erfolgreiche Ausführung
        A->>H: ReportExecutionEndAsync(isSuccess=true)
        H->>S: CompleteExecutionAsync(tenantId, executionEnd)
        S->>DB: UpdatePipelineExecution(status=Completed)
        S->>DB: SetPipelineCurrentExecution(null)
        S->>S: UpdateStatisticsAsync (fire-and-forget)
        H-->>A: OK
    else Fehler bei Ausführung
        A->>H: ReportExecutionEndAsync(isSuccess=false, errorMessage)
        H->>S: CompleteExecutionAsync(tenantId, executionEnd)
        S->>DB: UpdatePipelineExecution(status=Failed)
        S->>DB: SetPipelineCurrentExecution(null)
        S->>S: UpdateStatisticsAsync (fire-and-forget)
        H-->>A: OK
    end
```

---

## 2. Disconnect während Execution

```mermaid
sequenceDiagram
    participant A as Edge Adapter
    participant H as AdapterHub
    participant S as PipelineExecutionService
    participant DB as MongoDB

    Note over A,DB: Pipeline läuft, dann Verbindungsverlust

    A->>H: ReportExecutionStartAsync(executionStart)
    H->>S: StartExecutionAsync(tenantId, executionStart)
    S->>DB: CreatePipelineExecution(status=Running)
    H-->>A: OK

    Note over A: Pipeline wird ausgeführt...

    A--xH: Verbindung verloren (Netzwerk, Crash, etc.)

    Note over H: SignalR erkennt Disconnect automatisch

    H->>S: MarkExecutionsAsInterruptedAsync(adapterId)
    S->>DB: GetRunningExecutionsForAdapter(adapterId)
    DB-->>S: [execution1, execution2, ...]
    loop Für jede laufende Execution
        S->>DB: UpdatePipelineExecution(status=Interrupted)
        S->>DB: SetPipelineCurrentExecution(null)
    end

    Note over A,DB: Zeit vergeht... Adapter reconnected

    A->>H: SignalR Connect
    H->>S: GetInterruptedExecutionIdsAsync(adapterId)
    S->>DB: Query Interrupted Executions
    DB-->>S: [executionId1, executionId2]
    S-->>H: [executionId1, executionId2]
    H-->>A: Interrupted ExecutionIds

    Note over A: Adapter prüft lokalen Status der Executions

    alt Execution war erfolgreich
        A->>H: ReportInterruptedExecutionResultAsync(isSuccess=true)
        H->>S: ReportInterruptedExecutionResultAsync(...)
        S->>DB: UpdatePipelineExecution(status=Completed)
        S->>S: UpdateStatisticsAsync
    else Execution war fehlerhaft
        A->>H: ReportInterruptedExecutionResultAsync(isSuccess=false)
        H->>S: ReportInterruptedExecutionResultAsync(...)
        S->>DB: UpdatePipelineExecution(status=Failed)
        S->>S: UpdateStatisticsAsync
    end
```

---

## 3. Offline-Betrieb mit Buffering

```mermaid
sequenceDiagram
    participant A as Edge Adapter
    participant B as Local Buffer (SQLite)
    participant H as AdapterHub
    participant S as PipelineExecutionService
    participant DB as MongoDB

    Note over A,DB: Adapter ist offline

    rect rgb(255, 240, 240)
        Note over A,B: Offline-Phase
        A->>B: RecordExecution(exec1, seq=43)
        A->>B: RecordExecution(exec2, seq=44)
        A->>B: RecordExecution(exec3, seq=45)
        Note over B: Executions lokal gepuffert
    end

    Note over A,DB: Adapter kommt online

    A->>H: SignalR Connect
    A->>H: GetLastSyncedSequenceNumberAsync(adapterId)
    H->>S: GetLastSyncedSequenceNumber(...)
    S->>DB: Query LastSyncedSequenceNumber
    DB-->>S: 42
    S-->>H: 42
    H-->>A: lastSyncedSeq = 42

    A->>B: GetUnsynedExecutions(seq > 42)
    B-->>A: [exec1, exec2, exec3]

    A->>H: SyncBufferedExecutionsAsync(executions, lastSeq=42)
    H->>S: ProcessBufferedExecutionsAsync(...)

    S->>DB: GetExistingExecutionIds([...])
    DB-->>S: [] (keine Duplikate)

    loop Für jede Execution
        S->>S: ValidateBufferedExecution
        S->>DB: BulkInsertPipelineExecutions
    end

    S->>DB: UpdateAdapterSyncSequenceNumber(57)

    loop Für jede betroffene Pipeline
        S->>S: UpdateStatisticsAsync
    end

    S-->>H: SyncResponse(synced=[...], newSeq=57)
    H-->>A: SyncResponse

    A->>B: DeleteSyncedExecutions([exec1, exec2, exec3])
    A->>B: SetLastSyncedSequenceNumber(57)

    Note over A,DB: Normaler Online-Betrieb fortgesetzt
    A->>H: ReportExecutionStartAsync(...)
```

---

## 4. Statistik-Aggregation (Background Service)

```mermaid
sequenceDiagram
    participant BG as BackgroundService
    participant S as PipelineExecutionService
    participant DB as MongoDB

    loop Alle 5 Minuten
        BG->>S: UpdateAllStatisticsAsync(tenantId)

        S->>DB: GetPipelinesWithRecentExecutions()
        DB-->>S: [pipeline1, pipeline2, ...]

        loop Für jede Pipeline
            S->>DB: GetExecutionAggregate(last 1h)
            DB-->>S: {success: 10, failed: 2, avgDuration: 500ms}

            S->>DB: GetExecutionAggregate(last 12h)
            DB-->>S: {success: 100, failed: 15, avgDuration: 480ms}

            S->>DB: GetExecutionAggregate(last 24h)
            DB-->>S: {success: 200, failed: 25, avgDuration: 490ms}

            S->>DB: GetExecutionAggregate(last 30d)
            DB-->>S: {success: 5000, failed: 300, avgDuration: 510ms}

            S->>DB: UpdatePipelineStatistics(aggregates)
        end
    end
```

---

## 5. Cleanup (Background Service)

```mermaid
sequenceDiagram
    participant BG as BackgroundService
    participant S as PipelineExecutionService
    participant DB as MongoDB

    loop Einmal täglich
        BG->>S: CleanupOldExecutionsAsync(tenantId, retentionDays=30)

        S->>DB: DeleteOldExecutions(olderThan=30 days)
        DB-->>S: Deleted count: 1500

        S->>DB: MarkExpiredInterruptedAsFailed(olderThan=7 days)
        DB-->>S: Updated count: 3

        Note over S: Log cleanup results
    end
```

---

## 6. Zustandsdiagramm: Execution Status

```mermaid
stateDiagram-v2
    [*] --> Running: ReportExecutionStartAsync

    Running --> Completed: ReportExecutionEndAsync(success=true)
    Running --> Failed: ReportExecutionEndAsync(success=false)
    Running --> Interrupted: Adapter Disconnect

    Interrupted --> Completed: ReportInterruptedExecutionResultAsync(success=true)
    Interrupted --> Failed: ReportInterruptedExecutionResultAsync(success=false)
    Interrupted --> Failed: Expiry (7 Tage ohne Reconnect)

    Running --> Cancelled: CancelExecutionAsync

    Completed --> [*]
    Failed --> [*]
    Cancelled --> [*]
```

---

## 7. Komponenten-Interaktion

```mermaid
flowchart TB
    subgraph Adapter["Edge/Mesh Adapter"]
        PE[Pipeline Executor]
        ER[Execution Reporter]
        LB[Local Buffer]
        SM[Sync Manager]
    end

    subgraph Controller["Communication Controller Service"]
        AH[AdapterHub]
        PES[PipelineExecutionService]
        CR[CommunicationRepository]
        BS[Background Services]
    end

    subgraph Storage["Persistence"]
        MDB[(MongoDB)]
    end

    subgraph External["External Systems"]
        AR[Asset Repository]
        GQL[GraphQL API]
    end

    PE -->|execution events| ER
    ER -->|online| AH
    ER -->|offline| LB
    LB -->|on reconnect| SM
    SM -->|sync| AH

    AH -->|start/end| PES
    PES -->|store| CR
    CR -->|persist| MDB

    BS -->|aggregate stats| PES
    BS -->|cleanup old| CR

    MDB -->|replicate| AR
    AR -->|query| GQL
```

---

## Hinweise zur Verwendung

Diese Diagramme können in folgenden Tools gerendert werden:
- **GitHub/GitLab**: Unterstützt Mermaid nativ in Markdown
- **VS Code**: Mit der "Markdown Preview Mermaid Support" Extension
- **Mermaid Live Editor**: https://mermaid.live/
- **Confluence**: Mit dem Mermaid Plugin

### Beispiel: GitHub Rendering

GitHub rendert Mermaid-Diagramme automatisch, wenn sie in einem `mermaid` Code-Block stehen:

````markdown
```mermaid
sequenceDiagram
    A->>B: Message
```
````
