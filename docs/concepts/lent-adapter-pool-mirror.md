# Mirroring lent adapter pools into the borrower tenant (AB#5271)

## 1. The problem, stated precisely

A borrower tenant cannot see which adapter pools it may use.

The `AdapterPool` entity lives in the **lender's** tenant database. GraphQL is tenant-scoped, so
`runtime.systemCommunicationAdapterPool` in a child returns nothing — not because of a permission
rule, but because the entity is not there. Observed while wiring `accounting` (lender) to
`meshmakers` (borrower): the Adapter Pools list in `meshmakers` is empty while the pool exists and
lends correctly.

Today the operator has to know the lender tenant id and the pool RtId and type both into the
borrower's adapter:

```
LifecycleMode          = Leased
LentFromTenantId       = "accounting"
LentFromAdapterPoolRtId = "6aab…"
```

🔴 Those are **plain strings, and deliberately so**: a CK association cannot cross a tenant
database, so AB#4924 had no other way to express the reference. Everything that follows from that
choice is the cost this work item pays off — no navigation, no filtering, no display name, nothing
for backup to carry, and no validation beyond "is this 24 hex characters".

## 2. Why a local entity rather than a cross-tenant query

The obvious alternative is a borrower-facing REST endpoint that walks up the tenant tree and lists
what the ancestors lend. That answers the question and nothing else. A local entity answers it
*and* rejoins the platform's own mechanisms:

| | cross-tenant query | local mirrored entity |
|---|---|---|
| Adapter → pool reference | stays two opaque strings | real CK association |
| Studio list, filter, sort | bespoke REST view | ordinary entity list |
| tenant backup / restore | not carried | carried |
| re-link after restore | manual re-typing | an attribute on the entity |
| permissions | new surface to define | the tenant's existing entity permissions |

The upward walk is still needed — as the **backfill** for tenants that already exist, not as the
read path.

## 3. Precedent: this is how client mirroring already works

`octo-identity-services` mirrors `ClientCredentials` clients from a parent into its children
(`IClientMirrorProvisioningService`). The shape is directly reusable:

- the **lender pushes**; the borrower never polls upwards
- provision on tenant create, re-check idempotently on startup
- updates and deletes on the source fan out to every mirror
- a backfill endpoint for tenants that predate the feature
- `PreDeleteTenant` drops tracking rows

Adopting it means the lifecycle questions below already have answers that are running in
production, rather than being invented here.

## 4. 🔴 The mirror is never authoritative

This is the constraint that decides the design, and it is easy to get wrong because the mirror
looks exactly like a normal entity.

A tenant can edit entities in its own database — that is what a tenant is. If the lease decision
ever read the local mirror, a borrower could grant itself a pool by editing its own copy, or widen
`SharingMode`, or add itself to an allow-list. **The mirror is display and association only.**
`MayLendAsync` against the lender's own `AdapterPool` stays the gate at lease time, exactly as it
is today.

The client-mirror precedent has the same property: the mirrored client exists in the child, but
identity still validates centrally.

Consequence for the implementation: nothing in `LeaseService`, `LeaseSchedulerService` or
`AdapterService` may read `LentAdapterPool` to make a decision. It is legitimate to read it to
*render* something, or to resolve a display name.

## 5. 🔴 The mirror is runtime state

The entity must be marked `isRuntimeState`. A blueprint re-apply resets non-runtime attributes to
their seeded values, which for a mirror means wiping or recreating it — this is the mechanism
behind the prod-1 redirect-URI outage and the fix in AB#5210. A mirror is by definition not
authored, so it is runtime state.

## 6. Lifecycle

| event on the lender | effect on every borrower in scope |
|---|---|
| pool created with a lending `SharingMode` | mirror created |
| `SharingMode` / `LendingAllowedTenantIds` widened | mirror created in newly-in-scope tenants |
| `SharingMode` / allow-list narrowed | mirror removed where no longer in scope |
| name, replica range, lifecycle state changed | mirror updated |
| pool deleted | mirror removed |
| lender tenant deleted | mirrors removed |
| borrower tenant created | mirrors provisioned for every in-scope ancestor pool |

All operations idempotent, so the startup re-check and the backfill are the same code path.

🔴 **Removal must not cascade into the borrower's adapters.** A narrowed scope leaves adapters
pointing at a pool they may no longer use; that is already a defined state (AB#4924 concept §6,
"parent tenant deleted while lending") and it surfaces as a refused deploy with a named reason.
Deleting the borrower's adapters because the lender changed its mind would be a far worse failure
than a blocked deploy.

## 7. Open decisions

1. **Association vs the existing string fields.** Add the association and keep `LentFrom*` as the
   wire-level truth, or migrate to the association and derive the strings? Coexistence is safer for
   one release; a migration is cleaner and the migration machinery now exists (`RenameAttribute`,
   `SetWellKnownName`).
2. **What the mirror carries.** Name, sharing mode, replica range and deployment state are
   uncontroversial. Chart name/version and values are lender-side deployment detail and probably
   should not be copied into another tenant's database.
3. **Who may see it.** Which role in the borrower reads the mirror — and does the parent-tenant
   administration boundary (AB#5060/5068/5070) have anything to say about a child displaying an
   ancestor's resource name?

## 8. Not mirrored

Pool members and the lease queue. Both are live lender-side state, change constantly, and have no
meaning in the borrower's database. The queue is already reachable through
`GET {tenantId}/v1/adapterPool/{id}/queue` on the lender.
