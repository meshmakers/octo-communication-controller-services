# Expression Validation Concept

> **Status: implemented (AB#4189).** The backend authoritative validation and the *shared
> formula engine* this document originally proposed have shipped. The historical proposal is
> kept below for context; **the authoritative current state is the section directly under this
> banner.** User-facing formula-language reference lives in the public docs:
> *Tech Guide → Communication → Formula Expressions*.

## Current Implementation (AB#4189)

The mXparser glue is now a single shared component, not duplicated per consumer:

| Concern | Location |
|---------|----------|
| Abstraction | `IFormulaEngine` + `FormulaResultType` + `FormulaSyntaxResult` in `Runtime.Contracts/Formulas` (no mXparser dependency) |
| Implementation | `FormulaEngine` + `OctoExpression` + `now` / `startOfDay` extensions in the **`Meshmakers.Octo.Runtime.Engine.Formulas`** package (net10.0; a dedicated package because mXparser ships `netstandard2.1` but not `netstandard2.0`) |
| DI | `AddFormulaEngine()` — invoked from `AddMongoDbRuntimeRepository()`, so every runtime-engine host gets `IFormulaEngine` |

`IFormulaEngine` exposes `NormalizeTernary`, `Validate(expression, arguments)`,
`EvaluateRaw(expression, arguments)` and `Evaluate(expression, arguments, resultType)` (the
latter casts the `double` result back to `Boolean` / `Int` / `Int64` / `Double` / `DateTime`).

Consumers are now thin callers:

- **`ExpressionValidationService`** (this service) delegates `Validate` to `IFormulaEngine` and
  maps `FormulaSyntaxResult` → `ExpressionValidationResult`. It no longer carries its own
  `ConvertTernaryToIf`.
- **`ApplyDataPointMappingsNode`** (mesh-adapter) evaluates via `IFormulaEngine.EvaluateRaw`; its
  private `ConvertTernaryToIf` was removed.
- **`FieldFilterResolver` / `RtFieldFilterResolver`** (runtime query) use `OctoExpression` from the
  new package.

The supported formula syntax (operators, `if` / ternary, built-in functions, the `now` /
`startOfDay` / `null` extensions, null/NaN handling) is documented once in the public
**Formula Expressions** page and reused by every feature, including the forthcoming archive
*computed columns* (AB#4189 epic).

---

## Problem

Users enter mXparser expressions in the `MappingExpression` field of DataPointMappings (e.g., `value / 100`, `value > 0 ? value : 0`). Currently there is no validation — errors only surface at runtime when `ApplyDataPointMappingsNode` evaluates the expression and logs a warning. Users have no way to test or verify their expressions before activating polling.

## Current State

| Layer | Engine | Validation | When |
|-------|--------|-----------|------|
| Backend (Runtime) | mXparser via `OctoExpression` | None — errors logged as warnings, fallback to raw value | Pipeline execution |
| Frontend (Process Designer) | expr-eval via `ExpressionEvaluatorService` | `validate()` method, live preview | Symbol binding editor |
| Frontend (Data Mapping) | — | **None** | — |

### Backend Expression Handling

`ApplyDataPointMappingsNode.EvaluateExpression()` (in `octo-mesh-adapter`):
1. Extracts numeric value from input (handles strings with units like "27.0 %")
2. Converts ternary `cond ? a : b` → mXparser `if(cond, a, b)`
3. Creates `OctoExpression` with `value` argument
4. On NaN or exception: falls back to numeric value, logs warning

### Frontend Expression Evaluation

`ExpressionEvaluatorService` (in `octo-process-diagrams`):
- Uses `expr-eval` library (JavaScript)
- Has `validate(expr)` → `{ valid: boolean, error?: string }`
- Has `evaluate(expr, context)` → `{ success: boolean, value?, error? }`
- Supports: arithmetic, comparisons, ternary, `abs()`, `min()`, `max()`, `round()`, `floor()`, `ceil()`

### Syntax Compatibility

For DataPointMapping expressions, the common subset works in both engines:

| Expression | expr-eval (frontend) | mXparser (backend) |
|------------|---------------------|---------------------|
| `value / 100` | ✓ | ✓ |
| `value * 100` | ✓ | ✓ |
| `abs(value)` | ✓ | ✓ |
| `min(max(value, 0), 100)` | ✓ | ✓ |
| `value > 0 ? value : 0` | ✓ (native) | ✓ (converted to `if()`) |
| `round(value, 2)` | ✓ | ✓ |
| `sqrt(value)` | ✓ | ✓ |
| `value ^ 2` | ✓ (power) | ✓ (power) |

Edge cases where they differ:

| Expression | expr-eval | mXparser |
|------------|----------|----------|
| `if(cond, a, b)` | ✗ (not a function) | ✓ |
| `now()` | ✗ | ✓ (OctoExpression custom) |
| `startOfDay()` | ✗ | ✓ (OctoExpression custom) |
| `lerp(v, 0, 100, 0, 1)` | ✓ (custom) | ✗ |
| `clamp(v, 0, 100)` | ✓ (custom) | ✗ |

For DataPointMapping use cases, `now()` and `startOfDay()` are not relevant. The `if()` function syntax is backend-only but users write ternary instead (which both support). **The common subset covers all practical mapping expressions.**

## Proposed Solution

### Layer 1: Frontend Instant Validation (expr-eval)

Reuse the existing `ExpressionEvaluatorService` from `octo-process-diagrams` in the `DataMappingListComponent`. Provides instant syntax feedback as the user types.

#### Changes to DataMappingListComponent

```typescript
// New imports
import { ExpressionEvaluatorService } from '@meshmakers/octo-process-diagrams';

// Add to DataPointMappingItem interface
export interface DataPointMappingItem {
  // ... existing fields ...
  
  /** Client-side validation result (set by component, not persisted) */
  _expressionValid?: boolean;
  _expressionError?: string;
  _expressionPreview?: string;
}
```

**Expression field with validation feedback:**

```html
<div class="mapping-row">
  <label>Expression</label>
  <kendo-textbox [(value)]="mapping.mappingExpression"
    placeholder="e.g. value > 0 ? value : 0"
    (valueChange)="onExpressionChange(mapping, $event)">
  </kendo-textbox>
  
  <!-- Validation result -->
  @if (mapping.mappingExpression) {
    @if (mapping._expressionValid === false) {
      <div class="expression-error">
        ✗ {{ mapping._expressionError }}
      </div>
    } @else if (mapping._expressionValid === true) {
      <div class="expression-preview">
        ✓ value=42 → {{ mapping._expressionPreview }}
      </div>
    }
  }
</div>
```

**Validation logic (debounced):**

```typescript
private readonly expressionService = inject(ExpressionEvaluatorService);

onExpressionChange(mapping: DataPointMappingItem, expression: string): void {
  this.mappingChanged.emit(mapping);
  
  if (!expression || expression.trim() === '') {
    mapping._expressionValid = undefined;
    mapping._expressionError = undefined;
    mapping._expressionPreview = undefined;
    return;
  }
  
  // Step 1: Syntax validation
  const validation = this.expressionService.validate(expression);
  if (!validation.valid) {
    mapping._expressionValid = false;
    mapping._expressionError = validation.error ?? 'Invalid expression syntax';
    mapping._expressionPreview = undefined;
    return;
  }
  
  // Step 2: Test evaluation with sample value
  const testResult = this.expressionService.evaluate(expression, { value: 42 });
  if (!testResult.success) {
    mapping._expressionValid = false;
    mapping._expressionError = testResult.error ?? 'Expression evaluation failed';
    mapping._expressionPreview = undefined;
    return;
  }
  
  mapping._expressionValid = true;
  mapping._expressionError = undefined;
  mapping._expressionPreview = String(testResult.value);
}
```

**Styling:**

```scss
.expression-error {
  font-size: 0.75rem;
  color: var(--kendo-color-error, #dc3545);
  padding: 2px 0;
}

.expression-preview {
  font-size: 0.75rem;
  color: var(--kendo-color-success, #28a745);
  padding: 2px 0;
  font-family: monospace;
}
```

#### Advantages

- Instant feedback (no API call)
- Already tested (99 test cases)
- Works offline
- No backend changes needed

#### Limitations

- Cannot validate mXparser-specific functions (`now()`, `startOfDay()`)
- Edge case: expression passes expr-eval but fails mXparser at runtime (very rare for mapping use cases)

### Layer 2: Backend Authoritative Validation (mXparser)

A new GraphQL mutation on the Communication Controller that validates an expression using the real `OctoExpression` engine, the same code path as `ApplyDataPointMappingsNode`.

#### GraphQL Schema

```graphql
type Mutation {
  communication {
    validateMappingExpression(input: ValidateExpressionInput!): ValidateExpressionResult!
  }
}

input ValidateExpressionInput {
  """The mXparser expression to validate (e.g., 'value > 0 ? value : 0')"""
  expression: String!
  """Optional test value for evaluation (default: 42.0)"""
  testValue: Float
}

type ValidateExpressionResult {
  """Whether the expression is syntactically valid and evaluates successfully"""
  valid: Boolean!
  """Error message if invalid"""
  error: String
  """Evaluated result for the test value"""
  result: Float
  """The normalized expression after ternary conversion (for debugging)"""
  normalizedExpression: String
}
```

#### Backend Implementation

New method in `CommunicationRepository` or a dedicated service:

```csharp
// In CommunicationControllerServices
public class ExpressionValidationService
{
    public ValidateExpressionResult Validate(string expression, double testValue = 42.0)
    {
        try
        {
            // Step 1: Convert ternary syntax (same as ApplyDataPointMappingsNode)
            var normalized = ConvertTernaryToIf(expression);
            
            // Step 2: Parse and validate
            var expr = new OctoExpression(normalized);
            expr.addArguments(new Argument("value", testValue));
            
            // Step 3: Check syntax
            if (!expr.checkSyntax())
            {
                return new ValidateExpressionResult
                {
                    Valid = false,
                    Error = expr.getErrorMessage(),
                    NormalizedExpression = normalized
                };
            }
            
            // Step 4: Evaluate
            var result = expr.calculate();
            if (double.IsNaN(result))
            {
                return new ValidateExpressionResult
                {
                    Valid = false,
                    Error = "Expression evaluates to NaN",
                    NormalizedExpression = normalized
                };
            }
            
            return new ValidateExpressionResult
            {
                Valid = true,
                Result = result,
                NormalizedExpression = normalized
            };
        }
        catch (Exception ex)
        {
            return new ValidateExpressionResult
            {
                Valid = false,
                Error = ex.Message
            };
        }
    }
}
```

#### Controller Endpoint

```csharp
// In TenantApi CommunicationController
[HttpPost("validate-expression")]
[Authorize(Policy = TenantCommunicationApiReadWritePolicy)]
public ActionResult<ValidateExpressionResult> ValidateExpression(
    [FromBody] ValidateExpressionInput input)
{
    var result = _expressionValidationService.Validate(
        input.Expression, input.TestValue ?? 42.0);
    return Ok(result);
}
```

#### Frontend Integration

Call the backend validation on save (or on blur with debounce):

```typescript
// In the host app's mapping service
async validateExpressionBackend(expression: string, testValue: number = 42): Promise<ValidateExpressionResult> {
  return this.graphql.mutate({
    mutation: VALIDATE_EXPRESSION,
    variables: { input: { expression, testValue } }
  });
}
```

### Combined Flow

```
User types expression
       │
       ▼
  ┌─────────────┐
  │ expr-eval   │ ← Instant (< 1ms)
  │ validate()  │
  └──────┬──────┘
         │
    Valid?├── No → Show error immediately
         │
         │ Yes
         ▼
  ┌─────────────┐
  │ expr-eval   │ ← Instant
  │ evaluate()  │
  │ value=42    │
  └──────┬──────┘
         │
         ▼
  Show preview: "✓ value=42 → 0.42"
         │
    On Save
         │
         ▼
  ┌──────────────────────┐
  │ Backend API           │ ← ~50ms
  │ validateExpression()  │
  │ OctoExpression        │
  └──────────┬────────────┘
             │
        Valid?├── No → Show server error, block save
             │
             │ Yes
             ▼
        Save DataPointMapping
```

## Implementation Plan

### Phase 1: Frontend Validation (expr-eval)

**Scope:** Add instant expression validation to `DataMappingListComponent`

**Changes:**
1. `DataMappingListComponent` — add expression validation UI and logic
2. `DataPointMappingItem` interface — add `_expressionValid`, `_expressionError`, `_expressionPreview` fields
3. Add `ExpressionEvaluatorService` dependency from `octo-process-diagrams`

**Effort:** ~1 day

**Dependencies:** None (ExpressionEvaluatorService already exported from octo-process-diagrams)

### Phase 2: Backend Validation API

**Scope:** New endpoint in Communication Controller for authoritative mXparser validation

**Changes:**
1. `ExpressionValidationService` — new service in `CommunicationControllerServices`
2. `CommunicationController` (Tenant API) — new `validate-expression` endpoint
3. Unit tests for the service
4. Frontend GraphQL query/mutation for calling the endpoint
5. Integration into save flow

**Effort:** ~2 days

**Dependencies:** *(resolved in AB#4189 — see "Current Implementation" above)*
- ~~`OctoExpression` class accessible from Communication Controller (via `Runtime.Engine.MongoDb` package)~~ → now `IFormulaEngine` from the `Runtime.Engine.Formulas` package
- ~~`ConvertTernaryToIf` logic needs to be extracted to a shared location or duplicated~~ → extracted into the shared `FormulaEngine`

### Phase 3: Expression Help / Autocomplete (optional)

**Scope:** Expression input field with autocomplete for functions and inline help

**Features:**
- Dropdown with available functions: `abs()`, `min()`, `max()`, `round()`, `floor()`, `ceil()`, `sqrt()`
- Variable hint: "Use `value` for the source value"
- Quick-insert buttons for common patterns:
  - `value / 100` (percentage to ratio)
  - `value * 100` (ratio to percentage)
  - `value > 0 ? value : 0` (positive only)
  - `abs(value)` (absolute value)
  - `min(max(value, 0), 100)` (clamp 0-100)

**Effort:** ~1 day
