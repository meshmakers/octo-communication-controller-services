# Data Mapping Overview - UI Concept

## Problem

After running Auto-Map (DataFlow 4) or manually creating DataPointMappings, users need to:

1. **See all mappings at a glance** — not one entity at a time
2. **Validate correctness** — did the AI match the right rooms? Are the attributes correct?
3. **Find gaps** — which controls are unmapped? Which spaces receive no data?
4. **Fix issues** — quickly change target entity or attribute without navigating away
5. **Activate** — enable/disable mappings before running Configure Polling

The existing `DataMappingListComponent` (entity detail tab) works for single-entity editing but doesn't provide the overview needed for validation after bulk operations.

## Proposed Solution: Mapping Overview Page

A dedicated page in Refinery Studio under **Communication → Data Mappings** that shows all DataPointMappings in the tenant with grouping, filtering, and validation indicators.

### Layout

```
┌─────────────────────────────────────────────────────────────────────────┐
│  DATA POINT MAPPINGS                                         [Refresh] │
│                                                                         │
│  ┌─ Filters ──────────────────────────────────────────────────────────┐ │
│  │ Source Type: [All Types     ▼]  Target Type: [All Types     ▼]     │ │
│  │ Status:     [All           ▼]  Search:      [________________]     │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                         │
│  ┌─ Summary Bar ──────────────────────────────────────────────────────┐ │
│  │  ● 42 Total   ● 38 Valid   ● 2 Warnings   ● 2 Errors   ○ 5 Unmapped │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                         │
│  ┌─ Mapping Grid ────────────────────────────────────────────────────┐ │
│  │                                                                    │ │
│  │  ◉ │ Source                │ →  │ Target              │ Attribute  │ │
│  │ ───┼───────────────────────┼────┼─────────────────────┼──────────  │ │
│  │  ✓ │ Temperatur Wohnber.   │ →  │ Space "Wohnbereich" │ Temperature│ │
│  │    │ InfoOnlyAnalog        │    │ EnergyIQ/Space      │            │ │
│  │    │ Room: Wohnbereich     │    │                     │            │ │
│  │ ───┼───────────────────────┼────┼─────────────────────┼──────────  │ │
│  │  ✓ │ Feuchte Wohnbereich   │ →  │ Space "Wohnbereich" │ Humidity   │ │
│  │    │ InfoOnlyAnalog        │    │ EnergyIQ/Space      │            │ │
│  │    │ Room: Wohnbereich     │    │                     │            │ │
│  │ ───┼───────────────────────┼────┼─────────────────────┼──────────  │ │
│  │  ⚠ │ Dimmer Deckenlampe    │ →  │ Space "Wohnbereich" │ LightingLvl│
│  │    │ Dimmer                │    │ EnergyIQ/Space      │ value / 100│ │
│  │    │ Room: Wohnbereich     │    │                     │            │ │
│  │ ───┼───────────────────────┼────┼─────────────────────┼──────────  │ │
│  │  ✗ │ Temp Aussen           │ →  │ (no target)         │ —          │ │
│  │    │ InfoOnlyAnalog        │    │                     │            │ │
│  │    │ Room: —               │    │                     │            │ │
│  │                                                                    │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                         │
│  ┌─ Detail Panel (appears on row selection) ─────────────────────────┐ │
│  │                                                                    │ │
│  │  MAPPING: Temperatur Wohnbereich → Space.Temperature               │ │
│  │                                                                    │ │
│  │  Source          Loxone/Control "Temperatur Wohnbereich"    [🔗]    │ │
│  │  Source Attr.    CurrentValue                                       │ │
│  │  Expression      (none)                                             │ │
│  │  Target          EnergyIQ/Space "Wohnbereich"               [🔗]   │ │
│  │  Target Attr.    Temperature                                        │ │
│  │  Enabled         ✓                                                  │ │
│  │                                                                    │ │
│  │  Validation                                                         │ │
│  │  ✓ Source entity exists                                             │ │
│  │  ✓ Target entity exists                                             │ │
│  │  ✓ Target attribute "Temperature" exists on EnergyIQ/Space          │ │
│  │  ✓ Value type compatible (String → Double)                          │ │
│  │  ✓ No duplicate mapping (same source+state → same target+attr)      │ │
│  │                                                                    │ │
│  │  [Edit Mapping]  [Disable]  [Delete]                                │ │
│  │                                                                    │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

### Grid Columns

| Column | Source | Description |
|--------|--------|-------------|
| Status icon | Validation result | ✓ valid, ⚠ warning, ✗ error |
| Source | MapsFrom → Entity Name | Control name with ControlType and parent Room below |
| Arrow | — | Visual separator |
| Target | MapsTo → Entity Name | Target entity name with CK type below |
| Target Attribute | TargetAttributePath | Attribute being updated |
| Expression | MappingExpression | Shows expression if set, otherwise empty |
| Enabled | Enabled attribute | Toggle switch |

### Grouping Options

The grid supports grouping by:

- **By Room** (default): Groups mappings by the Loxone Room the source Control belongs to. Shows room name as group header with count of mappings per room.
- **By Target Entity**: Groups by the MapsTo target entity. Useful to see "what feeds into this Space?"
- **By Status**: Groups by validation status (Valid / Warning / Error / Unmapped)

### Summary Bar

KPI-style counters at the top showing:

| Counter | Color | Description |
|---------|-------|-------------|
| Total | neutral | Total number of DataPointMapping entities |
| Valid | green | Mappings passing all validation checks |
| Warnings | yellow | Mappings with non-critical issues |
| Errors | red | Mappings with critical issues |
| Unmapped | gray | Controls that have no DataPointMapping at all |

## Validation Rules

### Critical (Error ✗)

| Rule | Check | Description |
|------|-------|-------------|
| Source exists | MapsFrom target entity exists | Source Control was deleted after mapping |
| Target exists | MapsTo target entity exists | Target Space was deleted after mapping |
| Target attribute exists | TargetAttributePath on target CK type | Attribute name typo or CK model changed |
| Expression valid | MappingExpression parses as mXparser | Syntax error in expression |

### Warning (⚠)

| Rule | Check | Description |
|------|-------|-------------|
| Duplicate mapping | Same source + same state → same target + same attribute | Two mappings writing to the same attribute |
| Type mismatch | Source value type vs target attribute type | e.g., String mapped to Boolean without expression |
| Expression unnecessary | Expression is identity (`value`) | Can be removed |
| Room name mismatch | Source Room.Name != Target Space.Name | AI matched different names — may be intentional |

### Information

| Rule | Check | Description |
|------|-------|-------------|
| Unmapped controls | Controls without any DataPointMapping | Shows which controls are not yet mapped |
| Unmapped spaces | Spaces not receiving any data | Shows which spaces have no incoming data |

## Data Sources

### Primary: DataPointMapping Entities

```graphql
query getDataPointMappings($first: Int, $after: String, $filter: FieldFilterInput) {
  runtime {
    runtimeEntities(ckId: "System.Communication/DataPointMapping", first: $first, after: $after, fieldFilter: $filter) {
      totalCount
      items {
        rtId
        ckTypeId
        attributes {
          items {
            attributePath
            value
          }
        }
        associations {
          definitions(direction: OUTBOUND) {
            items {
              roleId
              targetRtId
              targetCkTypeId
              targetEntity {
                attributes {
                  items { attributePath value }
                }
              }
            }
          }
        }
      }
    }
  }
}
```

### Secondary: Unmapped Controls

Query all Loxone/Control entities and subtract those that appear as MapsFrom targets in any DataPointMapping.

```graphql
query getUnmappedControls {
  runtime {
    runtimeEntities(ckId: "Loxone/Control") {
      items {
        rtId
        attributes { items { attributePath value } }
        associations {
          definitions(direction: INBOUND, roleId: "System.Communication/MapsFrom") {
            totalCount   # 0 = unmapped
          }
        }
      }
    }
  }
}
```

### Tertiary: Room Hierarchy

To display the Room name for each Control, traverse: Control → (ParentChild) → Category → (ParentChild) → Room.

This can be done client-side by preloading all Rooms and Categories, or via a nested association query.

## Component Architecture

### New Components

```
octo-ui/src/lib/
└── data-mapping-overview/
    ├── data-mapping-overview.component.ts      # Main page component
    ├── data-mapping-overview.component.scss
    ├── data-mapping-summary-bar.component.ts    # KPI summary counters
    ├── data-mapping-detail-panel.component.ts   # Selected mapping details + validation
    ├── data-mapping-validation.service.ts       # Validation logic
    └── data-mapping-overview-data-source.ts     # GraphQL data source for grid
```

### DataMappingOverviewComponent

Main container using the existing list-detail pattern:

```typescript
@Component({
  selector: 'mm-data-mapping-overview',
  standalone: true,
  imports: [
    ListViewComponent,
    DataMappingSummaryBarComponent,
    DataMappingDetailPanelComponent,
    // Kendo modules
  ],
})
export class DataMappingOverviewComponent {
  // Inputs from host app
  @Input() messages: Partial<DataMappingOverviewMessages> = {};

  // State
  selectedMapping = signal<DataPointMappingOverviewItem | null>(null);
  summaryData = signal<MappingSummary>({ total: 0, valid: 0, warnings: 0, errors: 0, unmapped: 0 });
  groupBy = signal<'room' | 'target' | 'status'>('room');

  // Outputs
  @Output() navigateToEntity = new EventEmitter<{ rtId: string; ckTypeId: string }>();
  @Output() mappingChanged = new EventEmitter<DataPointMappingOverviewItem>();
  @Output() mappingDeleted = new EventEmitter<DataPointMappingOverviewItem>();
}
```

### DataMappingOverviewItem (ViewModel)

```typescript
interface DataPointMappingOverviewItem {
  // Mapping entity
  rtId: string;
  name: string;
  enabled: boolean;
  sourceAttributePath: string;
  mappingExpression: string;
  targetAttributePath: string;

  // Resolved source (MapsFrom)
  sourceRtId: string;
  sourceCkTypeId: string;
  sourceName: string;
  sourceControlType: string;        // e.g., "InfoOnlyAnalog"
  sourceRoomName: string;           // Resolved from hierarchy

  // Resolved target (MapsTo)
  targetRtId: string;
  targetCkTypeId: string;
  targetName: string;

  // Validation
  validationStatus: 'valid' | 'warning' | 'error';
  validationMessages: ValidationMessage[];
}

interface ValidationMessage {
  level: 'error' | 'warning' | 'info';
  code: string;
  message: string;
}

interface MappingSummary {
  total: number;
  valid: number;
  warnings: number;
  errors: number;
  unmapped: number;
}
```

### DataMappingValidationService

```typescript
@Injectable()
export class DataMappingValidationService {

  validate(
    mapping: DataPointMappingOverviewItem,
    allMappings: DataPointMappingOverviewItem[],
    targetCkTypes: Map<string, CkTypeInfo>
  ): ValidationMessage[] {
    const messages: ValidationMessage[] = [];

    // Error: Source entity missing
    if (!mapping.sourceRtId) {
      messages.push({ level: 'error', code: 'SOURCE_MISSING', message: 'Source entity not found' });
    }

    // Error: Target entity missing
    if (!mapping.targetRtId) {
      messages.push({ level: 'error', code: 'TARGET_MISSING', message: 'Target entity not found' });
    }

    // Error: Target attribute not found on CK type
    if (mapping.targetCkTypeId && mapping.targetAttributePath) {
      const ckType = targetCkTypes.get(mapping.targetCkTypeId);
      if (ckType && !ckType.attributes.some(a => a.name === mapping.targetAttributePath)) {
        messages.push({
          level: 'error',
          code: 'TARGET_ATTR_MISSING',
          message: `Attribute "${mapping.targetAttributePath}" not found on ${mapping.targetCkTypeId}`
        });
      }
    }

    // Warning: Duplicate mapping
    const duplicates = allMappings.filter(m =>
      m.rtId !== mapping.rtId &&
      m.sourceRtId === mapping.sourceRtId &&
      m.sourceAttributePath === mapping.sourceAttributePath &&
      m.targetRtId === mapping.targetRtId &&
      m.targetAttributePath === mapping.targetAttributePath
    );
    if (duplicates.length > 0) {
      messages.push({
        level: 'warning',
        code: 'DUPLICATE',
        message: 'Duplicate mapping — same source and target attribute'
      });
    }

    // Warning: Room name mismatch
    if (mapping.sourceRoomName && mapping.targetName &&
        !fuzzyMatch(mapping.sourceRoomName, mapping.targetName)) {
      messages.push({
        level: 'warning',
        code: 'NAME_MISMATCH',
        message: `Room "${mapping.sourceRoomName}" mapped to "${mapping.targetName}" — verify this is intentional`
      });
    }

    return messages;
  }
}
```

## Integration in Refinery Studio

### Routing

```typescript
// In the host app routing module
{
  path: ':tenantId/communication/data-mappings',
  component: DataMappingOverviewComponent,
  data: {
    breadcrumb: [
      { label: 'Communication', url: 'communication' },
      { label: 'Data Mappings' }
    ]
  }
}
```

### Navigation

Add "Data Mappings" to the Communication section in the sidebar navigation.

### Connection to Entity Detail

The overview links to the entity detail view for deep editing:
- Click source entity → navigates to Runtime Browser with Control selected
- Click target entity → navigates to Runtime Browser with Space selected
- The existing `DataMappingListComponent` tab on the entity detail continues to work for single-entity mapping management

## Implementation Phases

### Phase 1: Read-Only Overview (MVP)

- Grid showing all DataPointMappings with resolved source/target names
- Summary bar with counters
- Basic validation (source/target exists)
- Filter by source type, target type, status
- Link to source/target entities in Runtime Browser

**Effort:** ~3-4 days
**Dependencies:** GraphQL queries for DataPointMapping with nested associations

### Phase 2: Inline Editing + Full Validation

- Toggle Enabled directly in grid
- Edit expression inline
- Change target entity/attribute via dialogs
- Full validation suite (type compatibility, duplicates, name mismatch)
- Unmapped controls list

**Effort:** ~3-4 days
**Dependencies:** Phase 1, GraphQL mutations for DataPointMapping update

### Phase 3: Bulk Operations + Auto-Map Integration

- Bulk enable/disable selected mappings
- Bulk delete
- "Run Auto-Map" button triggering DataFlow 4
- Progress indicator during auto-map execution
- Auto-refresh grid after auto-map completes

**Effort:** ~2-3 days
**Dependencies:** Phase 2, Pipeline execution API

## MeshBoard Widget Alternative

For users who prefer a dashboard view, the mapping overview can also be exposed as a **MeshBoard widget**:

### Mapping Status Widget (StatusIndicator type)

Shows mapping health as a traffic light:
- Green: all mappings valid
- Yellow: some warnings
- Red: errors detected

Data source: aggregation query counting DataPointMappings by validation status.

### Mapping Table Widget (Table type)

A table widget showing DataPointMappings with source → target columns. Uses `persistentQuery` data source pointing to a pre-configured query for DataPointMapping entities.

This allows placing mapping status on any existing dashboard alongside other building system KPIs.
