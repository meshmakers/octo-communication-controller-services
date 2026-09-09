# Runbook — Recover MeshAdapter `DeploymentState` after blueprint re-apply regression

## When to run

Run this once on any cluster where the
`System.Communication.Release`/`MainLatest` blueprint bumped from a version
**older than the runtime-state preservation feature** (Release-1.5.0 /
MainLatest-1.4.0) to a newer one. Symptom:
`System.Communication/Adapter` entities seeded by the blueprint (rtId
`670000000000000000000002`) show `DeploymentState=Undeployed` in Studio
even though the corresponding helm release is `deployed` and the pod is
running.

Affected entities are exactly the ones whose attributes were upserted
by `BlueprintService.ApplySeedDataForBlueprintAsync` before the
preservation feature landed.

## Pre-flight

1. Confirm the affected cluster context:
   ```bash
   kubectl config current-context
   ```
2. List candidate tenants (those whose mesh-adapter helm release exists):
   ```bash
   helm list -A | grep -E '670000000000000000000002\s+octo' \
     | awk '{print $1}'
   ```
3. List actual broken entities (those whose DB `deploymentState=0` but
   `communicationState=1`, i.e. the operator is connected, so the
   helm release MUST be live):
   ```bash
   CONN=$(kubectl get secret -n mongodb octo-mongodb-admin-octo-system-admin \
     -o jsonpath='{.data.connectionString\.standard}' | base64 -d)

   kubectl exec -n mongodb octo-mongodb-0 -c mongod -- mongosh "$CONN" --quiet \
     --eval '
       db.getSiblingDB("octosystem")
         .RtEntity_SystemTenant.find({}, {_id:0, "attributes.tenantId":1})
         .toArray()
         .map(t => t.attributes.tenantId)
         .forEach(tid => {
           const e = db.getSiblingDB(tid)
             .RtEntity_SystemCommunicationDeployableEntity
             .findOne({_id: ObjectId("670000000000000000000002")});
           if (e && e.attributes.deploymentState === 0 && e.attributes.communicationState === 1) {
             print(`broken: ${tid}`);
           }
         });
     '
   ```
   Compare with the helm-list output above — every "broken" tenant must
   appear in the helm list. If a broken tenant has no helm release the
   `DeploymentState=Undeployed` is genuine; do **not** include it.

## Recovery

Run the update. The query guards ensure idempotency:
`deploymentState` is only flipped from `0`→`2` when `communicationState=1`
(operator currently connected), so a tenant that's genuinely undeployed
or whose operator is offline is left alone.

```bash
CONN=$(kubectl get secret -n mongodb octo-mongodb-admin-octo-system-admin \
  -o jsonpath='{.data.connectionString\.standard}' | base64 -d)

kubectl exec -n mongodb octo-mongodb-0 -c mongod -- mongosh "$CONN" --quiet --eval '
  const RT_ID = ObjectId("670000000000000000000002");
  const tenants = db.getSiblingDB("octosystem")
    .RtEntity_SystemTenant.find({}, {_id:0, "attributes.tenantId":1})
    .toArray()
    .map(t => t.attributes.tenantId);
  let total = 0;
  for (const tid of tenants) {
    const r = db.getSiblingDB(tid)
      .RtEntity_SystemCommunicationDeployableEntity
      .updateOne(
        { _id: RT_ID,
          "attributes.deploymentState": 0,
          "attributes.communicationState": 1 },
        { $set: {
            "attributes.deploymentState": 2,
            rtChangedDateTime: new Date()
          },
          $inc: { "rtVersion.low": 1 } }
      );
    print(`${tid}: matched=${r.matchedCount} modified=${r.modifiedCount}`);
    total += r.modifiedCount;
  }
  print(`total modified: ${total}`);
'
```

## After the update

The controller keeps its tenant state in an in-memory cache that is
populated from MongoDB during `StartTenantAsync`; a raw MongoDB update
does NOT invalidate that cache. Force a re-load by restarting the
controller pods:

```bash
kubectl rollout restart deployment octo-mesh-communication-controller-services -n octo
kubectl rollout status  deployment octo-mesh-communication-controller-services -n octo
```

If the rollout is restarting **into a newer image** (the typical case —
this runbook is part of the Phase 4 recovery alongside Phase 3's
blueprint bump), the new pod runs the blueprint re-apply path with
runtime-state preservation enabled. It reads back the now-correct
`deploymentState=2` from MongoDB and preserves it through the upsert —
the bug stays fixed even though we technically bump the blueprint
again.

## Verification

1. Studio UI shows the four mesh-adapters as `Deployed`.
2. Spot-check via MongoDB:
   ```bash
   kubectl exec -n mongodb octo-mongodb-0 -c mongod -- mongosh "$CONN" --quiet \
     --eval '
       ["sbeg","maco","meshtest","voest"].forEach(t => {
         const e = db.getSiblingDB(t)
           .RtEntity_SystemCommunicationDeployableEntity
           .findOne({_id: ObjectId("670000000000000000000002")});
         print(`${t}: deploymentState=${e?.attributes.deploymentState}`);
       });
     '
   ```
3. Cross-check helm releases stayed unchanged (recovery is database-only,
   no helm side effects):
   ```bash
   helm list -A | grep '670000000000000000000002'
   ```

## Why we don't drive recovery through the Studio "Deploy" button

That path is the obvious alternative but it goes through
`PoolService.DeployPoolAsync` → `NotifyWorkloadDeployedAsync` →
operator's `WorkloadReconciler.DeployAsync` → `helm upgrade --install`.
The helm upgrade is idempotent in principle, but the deploy may fail
for unrelated reasons (image-pull regression, registry credentials,
dry-run admission rejection from the operator's new Phase 1 pre-flight)
and that failure would surface as a new audit event chain even though
the actual cluster state never needed touching. A direct DB write is
strictly less invasive — no helm action, no operator round-trip, no
new audit noise.

The guards on the update statement (`deploymentState=0` AND
`communicationState=1`) keep this safe: we only flip entities that the
cluster reports as live (operator connected) but the controller's view
incorrectly marks as undeployed.
