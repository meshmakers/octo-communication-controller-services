---
description: Workload template resolution, Helm repository resolution and the CI/CD workload rollout pointers
applies_to: **/Services/WorkloadTemplateResolver.cs, **/Services/WorkloadResource*.cs
---

### Workload Template Resolution

Workloads' `Hostname`, non-secret `ValueOverride.Value` and `ValuesYaml`
may carry template placeholders that are resolved at **deploy time**, not
at blueprint-apply time. Late binding keeps workload runtime entities
portable: moving a tenant between clusters picks up the destination
cluster's domain / service config without re-seeding entities.

Three placeholder families are supported:

| Placeholder | Source | Example |
|---|---|---|
| `{{domain.NAME}}` | `CommunicationControllerOptions.Domains` (cluster config) | `{{domain.default}}` → `staging.octo-mesh.com` |
| `{{service.NAME}}` | `CommunicationControllerOptions.ServiceUrls` (cluster config) | `{{service.authority}}` → `https://identity.staging.octo-mesh.com` |
| `{{context.tenantId}}` | `WorkloadTemplateContext` (per deploy) | `{{context.tenantId}}` → `acme` |

The semantic key `authority` maps to the Identity Service public URI; other
`service.*` keys follow the helm-section name (`assetRepository`, `bot`,
`communication`, `adminPanel`, `studio`). The `context.*` namespace is kept
so future per-deploy values (workload rtId, pool name) can land without
touching templates already in the field.

- **Options:**
  - `CommunicationControllerOptions.Domains` — bound from
    `OCTO_COMMUNICATIONCONTROLLER__DOMAINS__{NAME}={baseDomain}` env vars.
  - `CommunicationControllerOptions.ServiceUrls` — bound from
    `OCTO_COMMUNICATIONCONTROLLER__SERVICEURLS__{NAME}={url}` env vars.
  - Helm chart emits one env var per `services.communication.domains` map
    key, plus a fixed block of `SERVICEURLS__AUTHORITY`,
    `__ASSETREPOSITORY`, `__BOT`, `__COMMUNICATION`, `__ADMINPANEL` and
    (if-gated) `__STUDIO` from the corresponding `services.<name>.publicUri`
    helm values (`octo-helm-core/src/octo-mesh/templates/_env.tpl`,
    communication branch).
- **Resolver:** `IWorkloadTemplateResolver` / `WorkloadTemplateResolver`
  (`Services/WorkloadTemplateResolver.cs`). Singleton, regex-based, lookup
  is case-insensitive on NAME. `TryResolve(template, context, out resolved,
  out unknownPlaceholder)` returns `false` on the first unresolvable
  placeholder so callers get a stable, actionable error; the offender is
  reported in its full `family.NAME` form (e.g. `service.nope`,
  `context.tenantId`). Unknown namespaces (`{{foo.bar}}`) deliberately do
  NOT trigger an error — they pass through verbatim so a ValuesYaml block
  can carry literal Go-template-looking strings without the resolver
  tripping.
- **Hook points in `DeploymentSiteService`:**
  - `EnsureWorkloadIsHelmDeployableAsync` validates the template up-front
    on **all three input surfaces** (Hostname, every non-secret
    `ValueOverride.Value`, and the whole `ValuesYaml` string) and throws
    `DeploymentSiteServiceException.WorkloadTemplateUnknownPlaceholder` so
    misconfigured workloads fail at Deploy with an actionable message
    instead of producing an Ingress with a literal `{{...}}` host (which
    k8s admission would reject mid-rollout) or a helm values file with
    unresolved placeholders.
  - `BuildWorkloadDeployedDtoAsync` applies the resolver to the same three
    surfaces before assigning to `WorkloadDeployedDto`. By this point the
    template has already been validated, so the resolver call cannot fail
    in practice.
- **Secret-flagged `ValueOverride` entries are not part of the input
  surface.** The encryption / sentinel layer owns those values; running
  templating over decrypted secret material would mix two contracts and
  could leak placeholder text into the chart secret. Decrypt runs alone
  on secret entries.
- **API:**
  - `GET {tenantId}/v1/communication/domains` (legacy) returns just the
    configured `DomainConfigurationDto[]`.
  - `GET {tenantId}/v1/communication/workload-variables` returns every
    available placeholder as `WorkloadVariableDto[]` across all three
    families so the Studio's workload editor can offer a single suggestion
    list. `context.tenantId` is always present with `SampleValue=null`;
    domain / service entries carry the configured value as `SampleValue`.
    Both endpoints use `TenantCommunicationApiReadOnlyPolicy`; results are
    identical per tenant.
- **Per-cluster wiring** for domains lives in
  `octo-mesh-deployment/clusters/*/values-octo-mesh.yaml` under
  `services.communication.domains`. Service URLs are derived from the
  existing `services.identity.publicUri`, `services.assetRepository.publicUri`
  etc. helm values — those are already mandatory in production clusters,
  so no values-file change is needed to pick up the new `{{service.*}}`
  family.
- **Why `{{ }}` and not `${ }`:** Blueprint variables already use `${name}`
  (`BlueprintVariableInterpolator` in `octo-construction-kit-engine`) and
  are resolved at blueprint-apply time. Reusing that syntax for deploy-time
  substitution would either pollute the blueprint warning log (the engine's
  default provider warns on unknown vars) or require a cross-layer
  skip-prefix coupling. Double-brace `{{family.NAME}}` keeps the two
  resolution layers visually distinct in YAML and decoupled in code.

Tests:
- `Services/WorkloadTemplateResolverTests` — literal pass-through, single +
  mixed-family substitution (`domain.* + service.* + context.tenantId`),
  case-insensitive NAME match, unknown domain / service / empty-context
  paths each report the fully-qualified offender, first-offender wins,
  single-brace `${...}` is ignored, whitespace inside `{{ }}` tolerated,
  unknown namespace (`{{foo.bar}}`) stays literal.
- `Services/PoolServiceTests/DeployPoolAsyncTests` — Hostname,
  ValueOverride and ValuesYaml resolution + unknown-placeholder paths;
  `DeployWorkloadAsync_SecretValueOverride_NotSubstituted` pins the
  encryption/template boundary.
- `Controllers/CommunicationControllerWorkloadVariablesTests` — pins the
  ordering and shape of `GET /workload-variables` so the Studio's
  suggestion list stays stable.


## Helm repository resolution: explicit edge, else by purpose (AB#5295)

`DeploymentSiteService.ResolveHelmRepositoryAsync` is the one place a workload's chart repository
is decided; every deploy/DTO/deployability path goes through it. An explicit `HelmRepository`
association always wins. Without one, the tenant's `HelmRepositoryConfiguration`s are filtered by
`Purpose` against the workload type — `Adapter`/`AdapterPool` → `Adapters`, `Application` →
`Applications` — and the fallback applies only when that leaves **exactly one** candidate. Zero or
several resolve to null, which the callers already treat as "no repository linked" (the exception
text names both ways out).

🔴 **Why a modelled purpose and not the well-known name.** On the release channel adapter charts
and tenant-app charts live in two different indexes (`github.io/charts` = `meshmakers-public`,
`github.io/apps` = `meshmakers-apps`), so "the tenant's repository" is ambiguous there; on the dev
channel both names point at one index. The only thing that told them apart was the
`rtWellKnownName` convention. `System.Communication` 4.1.0 adds enum `HelmRepositoryPurpose`
(`Adapters`, `Applications`) and the optional attribute `HelmRepositoryConfiguration.Purpose`;
the channel blueprints (`System.Communication.Release-2.1.0`, `System.Communication.MainLatest-2.1.0`)
stamp it. A repository that declares no purpose is **never** chosen by the fallback, so tenants seeded
before 4.1.0 keep their exact previous behaviour until the blueprint bump reaches them.

**Why this exists.** An app blueprint used to have to pin a channel-specific repository rtId in its
seed. The accounting blueprint pinned `670…003` (`meshmakers-dev`, the DEV adapter index) on Release
tenants: on prod-1 the operator's dry-run failed (`chart … not found in octo-d808d9351847 index`) and,
on tenants that never had that entity, the import left a **dangling** `HelmRepository` row that the
typed GraphQL resolver hides, the multiplicity check counts, and neither `CREATE` nor `DELETE` through
the update mutation can remove (the mutation validates the target before touching the row). A raw
row delete was the only repair. With the fallback, app seeds carry no repository edge at all.

Read `Purpose` through `GetAttributeValueOrDefault` (see `ReadPurpose`), never the generated
property — the attribute is optional and new, and a repository written before it existed must read
as "no purpose" rather than throw inside a deploy.

Tests: `Services/DeploymentSiteServiceTests/HelmRepositoryFallbackTests`.


## CI/CD Workload Rollout E2E

Manual end-to-end validation of the Epic 3054 rollout flow on the
`test-2` cluster lives at `docs/E2E-CICD-WORKLOAD-ROLLOUT.md`. Run after
non-trivial changes in `WorkloadController`, `ICommunicationRepository`'s
workload methods, `octo-cli` workload commands, or the deploy-workload
pipeline template. Companion to the operator-side smoke test
(`octo-communication-operator/docs/E2E-SMOKE-TEST.md`); the two together
cover the whole pool→workload deploy path.
