# Verifying the secret-default sample

The steps the proof story (AB#7247) runs on a **local** tenant tree. Never on a shared environment
without that environment's own go.

## Prerequisites

- Local stack with a Communication Controller that contains the secret-default projection (AB#7244,
  AB#7245) and a Mesh Adapter that contains `VerifyCaptcha@1` (AB#7251); System.Communication in the
  range `[4.16,5.0)` (4.16.0 or later, below 5.0.0) in the CK catalog the asset repository reads and in
  every tenant of the tree.
- Outbound HTTPS from the Mesh Adapter to `challenges.cloudflare.com` (Turnstile siteverify, even for
  the test keys); without it every check answers `verification-unavailable`.
- The Mesh Adapter holds the key ring the asset repository encrypts Secret attributes with (a local
  stack shares it); without any key ring the projected default cannot be decrypted and every check
  answers `verification-unavailable`, and a key ring that lacks the default's key id treats the default as
  not set, so a tenant without its own secret gets `not-configured`.
- `octo-cli` with the `GraphQl` command and its `-vars` option, one context per tenant, named
  `<prefix><tenant>`: `local_<tenant>` on the local stack, `<installation>_<tenant>` elsewhere (see
  [On a shared environment](#8-on-a-shared-environment)). Logins are done by the developer, not by an agent.
  `jq` for `write-secret.sh` and `prove-no-reveal.sh`, `unzip` for `prove-no-reveal.sh`.
- The served tenants' Mesh Adapter reachable from the shell, e.g. `http://localhost:5020` through
  `kubectl port-forward`.

Variables used below (bash; replace the example tenant ids with your own):

```bash
sample_root=samples/Blueprints/SecretDefaultDemo
parent_tenant=sddemo-parent   child_tenant=sddemo-child   grandchild_tenant=sddemo-grandchild
adapter_base_url=http://localhost:5020
export OCTO_CONTEXT_PREFIX=local_
```

`write-secret.sh` and `prove-no-reveal.sh` read `OCTO_CONTEXT_PREFIX` too (default `local_`).

## 1. Publish the model and the blueprints locally

```bash
octo-ckc -c Compile -p "$sample_root/ConstructionKit" -o ./ck-out
octo-ckc -c Publish -f ./ck-out/ck-secretdefaultdemo.yaml -r
catalog_root=~/.octo/local-blueprint-catalog/blueprints/v1
for blueprint_name in SecretDefaultDemo SecretDefaultDemoCaptcha SecretDefaultDemoProbe; do
  mkdir -p "$catalog_root/$blueprint_name/1.0.0"
  cp -r "$sample_root/$blueprint_name/." "$catalog_root/$blueprint_name/1.0.0/"
done
rm -f ~/.octo/blueprint-catalog/cache/local-blueprint-catalog-cache.json
```

The local catalogs are shared by every session on the machine: remove the sample model and
blueprints again when the run is over.

## 2. Tenant tree, install, data flows

Create the tree before the deploy in step 4, so that the deploy itself projects into every tenant of
it. A tenant created later receives the default on its first load, or at once with
`POST {tenant}/v1/secretDefaults/reconcile` on the Communication Controller.

```bash
octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c Create -tid "$child_tenant" -db "$child_tenant"
octo-cli --context "${OCTO_CONTEXT_PREFIX}$child_tenant"  -c Create -tid "$grandchild_tenant" -db "$grandchild_tenant"

octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c InstallBlueprint -b SecretDefaultDemo-1.0.0
for served_tenant in "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$served_tenant" -c InstallBlueprint -b SecretDefaultDemoCaptcha-1.0.0
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$served_tenant" -c DeployDataFlow -id 5ecdef0000000000000000b2
done
```

## 3. Before the operator step: nothing to project

The seed declares no `SecretDefaults` entry, so nothing reaches a served tenant yet:

```bash
for served_tenant in "$parent_tenant" "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$served_tenant" -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
done
```

Expected: `totalCount: 0` in every tenant.

## 4. Operator step: add the entry with the always-pass test secret, deploy

This is the step an operator does in Refinery Studio (Application `secret-default-demo` -> Further
attributes -> `SecretDefaults`: Key `demo-captcha-secret`, SecretValue, optional AllowedTenantIds and
Description -> Save -> Deploy). The script does the same through
`write-secret-default.graphql`, with the secret as the GraphQL variable `secretValue`:

```bash
printf '%s\n' 1x0000000000000000000000000000000AA \
  | "$sample_root/verify/write-secret.sh" default "$parent_tenant"
octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c DeployWorkload -id 5ecdef0000000000000000a1
for served_tenant in "$parent_tenant" "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$served_tenant" -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
done
```

Expected: the mutation answers `secretValue { isSet: true }` and `allowedTenantIds: []` (empty = the
parent and its whole subtree); each tenant has one `SecretDefault` `demo-captcha-secret` with
`sourceTenantId` = the parent and `sourceWorkloadRtId` = `5ecdef0000000000000000a1`. Its value is
Hidden (System.Communication 4.16.0 and later), so the entity is the proof that the default is set.

To narrow the set, list the tenants after the tenant id, e.g.
`write-secret.sh default "$parent_tenant" "$child_tenant"`: after the deploy only the child holds the
default, and the parent and grandchild have none.

## 5. Acceptance checks

| Step | Command | Expected answer |
|---|---|---|
| Default in the child | `"$sample_root/verify/call-verify-captcha.sh" "$adapter_base_url" "$child_tenant"` | `{"valid":true,"source":"default"}` |
| Default in the grandchild | `"$sample_root/verify/call-verify-captcha.sh" "$adapter_base_url" "$grandchild_tenant"` | `{"valid":true,"source":"default"}` |
| Child sets the always-fail secret | `printf '%s\n' 2x0000000000000000000000000000000AA \| "$sample_root/verify/write-secret.sh" tenant "$child_tenant"` | `captchaSecret { isSet: true }` |
| Override in the child | `"$sample_root/verify/call-verify-captcha.sh" "$adapter_base_url" "$child_tenant"` | `{"valid":false,"source":"tenant","reason":"invalid-token"}` |
| Grandchild untouched | `"$sample_root/verify/call-verify-captcha.sh" "$adapter_base_url" "$grandchild_tenant"` | `{"valid":true,"source":"default"}` |
| Child clears its secret | `octo-cli --context "${OCTO_CONTEXT_PREFIX}$child_tenant" -c GraphQl -tid "$child_tenant" -f "$sample_root/verify/clear-tenant-secret.graphql"` | `captchaSecret { isSet: false }` |
| Back to the default | `"$sample_root/verify/call-verify-captcha.sh" "$adapter_base_url" "$child_tenant"` | `{"valid":true,"source":"default"}` |

No answer, log line or GraphQL response may contain a secret: grep them for `1x0000` and `2x0000`.
`RevealSecret@1` on the `SecretDefault` is refused for every identity: step 6 proves it.

## 6. Prove that no read path reveals the default

As a **tenant admin** of a served tenant (every role of that tenant), with the default deployed (step 4).
Install the probe blueprint in that tenant and deploy its data flow, then pipe the default's value into
the script as the sentinel. It is read from standard input like in `write-secret.sh`, so it is on no
command line, and it must be at least 8 characters of `A-Z a-z 0-9 . _ -`, so no JSON or YAML escaping can
hide it from the search. The script prints one line per read path and never a response body. A random value is the
stronger sentinel (write it with `write-secret.sh default` and deploy again before the run). Turnstile then
answers HTTP 400 for the unknown secret and the Mesh Adapter logs it as verification unavailable, so the
CAPTCHA probe's verdict is not valid; its `source` stays `default`, which is what the script checks.

```bash
octo-cli --context "${OCTO_CONTEXT_PREFIX}$child_tenant" -c InstallBlueprint -b SecretDefaultDemoProbe-1.0.0
octo-cli --context "${OCTO_CONTEXT_PREFIX}$child_tenant" -c DeployDataFlow -id 5ecdef0000000000000000c2
printf '%s\n' 1x0000000000000000000000000000000AA \
  | "$sample_root/verify/prove-no-reveal.sh" "$child_tenant"
```

`OCTO_CONTEXT` names a context of its own, e.g. one in which the tenant-admin test user is logged in
(`OCTO_CONTEXT=local_sddemo-child-admin`); without it the script uses `${OCTO_CONTEXT_PREFIX}<tenant>`.

| Read path | How the script tries it | Expected |
|---|---|---|
| GraphQL, typed root | `probe-typed-value.graphql` selects `value` on `systemCommunicationSecretDefault` | refused: the field does not exist (Hidden) |
| GraphQL, generic root | `probe-generic-attributes.graphql`: `runtimeEntities` with `attributes` and no `attributeNames` (all attributes) | answered, no attribute `value` |
| Export by deep graph | `ExportRtByDeepGraph` of the `SecretDefault` | the entity is exported, without the value and without an `enc:v…:` envelope |
| Export by query | `ExportRtByQuery` of the probe query `5ecdef0000000000000000c1` | as above |
| `RevealSecret@1`, caller | `ExecutePipeline` of `5ecdef0000000000000000c3` | execution failed, message names the type platform-only |
| `RevealSecret@1`, service account | `ExecutePipeline` of `5ecdef0000000000000000c4` | as above |
| `GetPipelineConfigByCkTypeId@1` | `ExecutePipeline` of `5ecdef0000000000000000c5` | as above |
| `VerifyCaptcha@1` without a route | `ExecutePipeline` of `5ecdef0000000000000000c6` | completed; the verdict and its `source` are the execution's result (`SetPipelineExecutionResult@1`) |

For each pipeline the script turns debug on (`SetPipelineDebug`) and starts it. It reads the outcome from the
persisted `System.Communication/PipelineExecution` entity (`read-pipeline-execution.graphql`, by execution id:
`status`, `errorMessage`, `outputData`) until the status is no longer `QUEUED` or `RUNNING`. It does not use
`GetLatestPipelineExecution` or `GetPipelineExecutions` for that: while debug is on the controller answers both from
its in-memory debug cache, which carries no status and no error message. A refusal passes when the status is
`FAILED` and the error message says platform-only; the CAPTCHA probe passes when the status is `COMPLETED` and the
result's `source` is `default`.

It then searches the persisted execution, the debug points (`GetPipelineDebugPoints`) and the execution list
(`GetPipelineExecutions`) for the sentinel and for an `enc:v…:` envelope. The debug-point search covers what
`octo-cli` fetches, which is the node tree of the execution: the input and output documents of the nodes are not
part of that answer, so the script does not search them. Expected output, with the always-pass test secret:

```text
Tenant sddemo-child, context local_sddemo-child, SecretDefault <rtId>
PASS  GraphQL typed field value                    refused: the schema has no field value
PASS  GraphQL runtimeEntities, all attributes      answered without the value attribute
PASS  ExportRtByDeepGraph                          exported without value and without envelope
PASS  ExportRtByQuery                              exported without value and without envelope
PASS  RevealSecret@1 as Caller                     refused as platform-only; no value in debug points or history
PASS  RevealSecret@1 as ServiceAccount             refused as platform-only; no value in debug points or history
PASS  GetPipelineConfigByCkTypeId@1                refused as platform-only; no value in debug points or history
PASS  VerifyCaptcha@1 via ExecutePipeline          verdict source=default; no value in debug points or history
Every read path refused.
```

A path that could not be checked (another error than the expected one, unreadable debug points or history,
an execution that did not finish, a verdict whose `source` is not `default`) is a FAIL as well.
Exit code 0 when every path refused, 1 when one did not (the line says which, never the value), 2 when the
tenant has no projected default or no sentinel was given. The script asserts refusals only: the dump and
restore of a tenant and the log sweep are separate (a restored `SecretDefault` without a providing
Application is removed by the next reconcile, which the controller's unit tests
`RestoredSecretDefaultReconcileTests` cover; for the logs see step 9).

## 7. Undeploy withdraws, clean up

```bash
octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c UndeployWorkload -id 5ecdef0000000000000000a1 -y
for served_tenant in "$parent_tenant" "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$served_tenant" -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
done
```

Expected: `totalCount: 0` everywhere - an undeployed Application is no source - and
`"$sample_root/verify/call-verify-captcha.sh"` in a tenant without its own secret answers `"valid":false` with
`"source":"none"`. The Information event of the withdrawal is linked to the Application.

Then delete the child and grandchild tenants and remove the sample model and blueprints from the local
catalogs.

## 8. On a shared environment

Only with that environment's own go. The steps are the same, with these differences:

- **Contexts.** One octo-cli context per tenant, named `<installation>_<tenant>` (e.g. `test-2_sddemo-child`), and
  `export OCTO_CONTEXT_PREFIX=test-2_` before the steps; every command and both scripts use it. Nothing in
  the steps names `local_` any more.
- **Catalogs.** The model and the blueprints come from the environment's catalogs, not from the local folders
  of step 1 (publish them there, then refresh the catalogs).
- **Deployment site.** A tenant created for the run has no running deployment site, and a workload deploy
  there does nothing. Deploy the site once before step 4:

  ```bash
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c GraphQl -tid "$parent_tenant" \
    -f "$sample_root/verify/read-deployment-site.graphql"
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c DeployDeploymentSite -id <deploymentSiteRtId>
  ```

- **No ingress for the adapter.** Where the tenant's Mesh Adapter has no ingress, `call-verify-captcha.sh` cannot
  reach the route. Use the probe pipeline instead: install `SecretDefaultDemoProbe`, deploy its data flow and
  run it with `ExecutePipeline`; the verdict is the result of the persisted execution (`outputData`).

  ```bash
  octo-cli --context "${OCTO_CONTEXT_PREFIX}$child_tenant" -c ExecutePipeline -id 5ecdef0000000000000000c6
  jq -cn --arg executionId <executionId> '{ executionId: $executionId }' \
    | octo-cli --context "${OCTO_CONTEXT_PREFIX}$child_tenant" -c GraphQl -tid "$child_tenant" \
      -f "$sample_root/verify/read-pipeline-execution.graphql" -vars @/dev/stdin
  ```

  `prove-no-reveal.sh` uses this pipeline anyway, so it needs no ingress.

## 9. Trace sweep: raise the log levels, then restore them

To show that no log line carries the sentinel, raise the levels before step 4 (projection) and step 5
(verification), grep the logs for the sentinel, then restore the levels.

- **Communication Controller** (and the asset repository, which stores the value) at run time:

  ```bash
  for service_name in CommunicationController AssetRepository; do
    for logger_name in 'Meshmakers.*' '*'; do
      octo-cli --context "${OCTO_CONTEXT_PREFIX}$parent_tenant" -c ReconfigureLogLevel \
        -n "$service_name" -ln "$logger_name" -minL Trace -maxL Fatal
    done
  done
  ```

  Restore with the same loop and `-minL Info` (or the level the deployment sets with `OCTO_LOG_LEVEL` /
  `OCTO_LOG_LEVEL_ROOT`). The change lives in memory only: a restart of the service restores the configured level
  as well.
- **Communication Operator** reads its level at start from `OCTO_LOG_LEVEL` (our code) and `OCTO_LOG_LEVEL_ROOT`
  (frameworks), default `Info`; its NLog provider drops the `Logging:LogLevel` filters of `appsettings.json`, so these
  two variables decide alone. Local stack: set both to `Trace` in the service's environment and restart it. kind:
  `kubectl -n <namespace> set env deployment/<name> OCTO_LOG_LEVEL=Trace OCTO_LOG_LEVEL_ROOT=Trace`, restore with
  `kubectl -n <namespace> set env deployment/<name> OCTO_LOG_LEVEL- OCTO_LOG_LEVEL_ROOT-`.
- **Mesh Adapter** needs two more variables. Its host keeps the `Logging:LogLevel` filters of `appsettings.json`
  (`Default` = `Information`) in front of NLog, so `OCTO_LOG_LEVEL` and `OCTO_LOG_LEVEL_ROOT` alone still produce no
  Debug or Trace line. The SDK's `OCTO_ADAPTER__MINIMUMLOGLEVEL` does not help either: the `appsettings.json` rule takes
  precedence over that minimum. Set `Logging__LogLevel__Default=Trace` as well, and
  `Logging__LogLevel__Microsoft.AspNetCore=Trace`, because `appsettings.json` holds ASP.NET Core's own category at
  `Warning` and its Debug lines would otherwise escape the sweep. Local stack: all four to `Trace` in the service's
  environment, then restart it. kind:

  ```bash
  kubectl -n <namespace> set env deployment/<name> \
    OCTO_LOG_LEVEL=Trace OCTO_LOG_LEVEL_ROOT=Trace Logging__LogLevel__Default=Trace \
    Logging__LogLevel__Microsoft.AspNetCore=Trace
  ```

  Restore all four (the configured defaults apply again):

  ```bash
  kubectl -n <namespace> set env deployment/<name> \
    OCTO_LOG_LEVEL- OCTO_LOG_LEVEL_ROOT- Logging__LogLevel__Default- \
    Logging__LogLevel__Microsoft.AspNetCore-
  ```

- On kind, do not count on the next deploy of the release to remove these variables: Helm 3 keeps environment variables
  its chart does not render. Always run the restore commands above.
- **Search** the logs for the sentinel with `grep -cF` and report only the count; never print a matching line.
  Then restore every level before you leave the environment.
