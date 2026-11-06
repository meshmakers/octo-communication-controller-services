# Verifying the secret-default sample

The steps the proof story (AB#7247) runs on a **local** tenant tree. Never on a shared environment
without that environment's own go.

## Prerequisites

- Local stack with a Communication Controller that contains the secret-default projection (AB#7244)
  and a Mesh Adapter that contains `VerifyCaptcha@1` (AB#7251); System.Communication 4.15.0 in the
  CK catalog the asset repository reads.
- `octo-cli` with the `GraphQl` command, one context per tenant (`<parent>`, `<child>`, `<grandchild>`).
  Logins are done by the developer, not by an agent.
- The served tenants' Mesh Adapter reachable from the shell, e.g. `http://localhost:5020` through
  `kubectl port-forward`.

Variables used below (bash; replace the example tenant ids with your own):

```bash
sample_root=samples/Blueprints/SecretDefaultDemo
parent_tenant=sddemo-parent   child_tenant=sddemo-child   grandchild_tenant=sddemo-grandchild
adapter_base_url=http://localhost:5020
```

## 1. Publish the model and the blueprints locally

```bash
octo-ckc -c Compile -p "$sample_root/ConstructionKit" -o ./ck-out
octo-ckc -c Publish -f ./ck-out/ck-secretdefaultdemo.yaml -r
catalog_root=~/.octo/local-blueprint-catalog/blueprints/v1
for blueprint_name in SecretDefaultDemo SecretDefaultDemoCaptcha; do
  mkdir -p "$catalog_root/$blueprint_name/1.0.0"
  cp -r "$sample_root/$blueprint_name/." "$catalog_root/$blueprint_name/1.0.0/"
done
rm -f ~/.octo/blueprint-catalog/cache/local-blueprint-catalog-cache.json
```

The local catalogs are shared by every session on the machine: remove the sample model and
blueprints again when the run is over.

## 2. Tenant tree, install, data flows

Create the tree **before** the Application is deployed, so the deploy in step 4 projects into every
tenant of it.

```bash
octo-cli --context "local_$parent_tenant" -c Create -tid "$child_tenant" -db "$child_tenant"
octo-cli --context "local_$child_tenant"  -c Create -tid "$grandchild_tenant" -db "$grandchild_tenant"

octo-cli --context "local_$parent_tenant" -c InstallBlueprint -b SecretDefaultDemo-1.0.0
for served_tenant in "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "local_$served_tenant" -c InstallBlueprint -b SecretDefaultDemoCaptcha-1.0.0
  octo-cli --context "local_$served_tenant" -c DeployDataFlow -id 5ecdef0000000000000000b2
done
```

## 3. No value, no deploy

```bash
octo-cli --context "local_$parent_tenant" -c DeployWorkload -id 5ecdef0000000000000000a1
```

Expected: refused with "the SecretDefaults entry 'demo-captcha-secret' has no SecretValue", and
`read-secret-default.graphql` finds no `SecretDefault` in the child.

## 4. Write the always-pass test secret and deploy

```bash
printf '%s\n' 1x0000000000000000000000000000000AA \
  | "$sample_root/verify/write-secret.sh" default "$parent_tenant"
octo-cli --context "local_$parent_tenant" -c DeployWorkload -id 5ecdef0000000000000000a1
for served_tenant in "$child_tenant" "$grandchild_tenant"; do
  octo-cli -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
done
```

Expected: the mutation answers `secretValue { isSet: true }`; each served tenant has one
`SecretDefault` `demo-captcha-secret` with `value { isSet: true }` and `sourceTenantId` = the parent.

## 5. Acceptance checks

| Step | Command | Expected answer |
|---|---|---|
| Default in the child | `verify/call-verify-captcha.sh "$adapter_base_url" "$child_tenant"` | `{"valid":true,"source":"default"}` |
| Default in the grandchild | `verify/call-verify-captcha.sh "$adapter_base_url" "$grandchild_tenant"` | `{"valid":true,"source":"default"}` |
| Child sets the always-fail secret | `printf '%s\n' 2x0000000000000000000000000000000AA \| verify/write-secret.sh tenant "$child_tenant"` | `captchaSecret { isSet: true }` |
| Override in the child | `verify/call-verify-captcha.sh "$adapter_base_url" "$child_tenant"` | `{"valid":false,"source":"tenant","reason":"invalid-token"}` |
| Grandchild untouched | `verify/call-verify-captcha.sh "$adapter_base_url" "$grandchild_tenant"` | `{"valid":true,"source":"default"}` |
| Child clears its secret | `octo-cli -c GraphQl -tid "$child_tenant" -f verify/clear-tenant-secret.graphql` | `captchaSecret { isSet: false }` |
| Back to the default | `verify/call-verify-captcha.sh "$adapter_base_url" "$child_tenant"` | `{"valid":true,"source":"default"}` |

No answer, log line or GraphQL response may contain a secret: grep them for `1x0000` and `2x0000`.
`RevealSecret@1` on the `SecretDefault` is refused for every identity (proof story AB#7247).

## 6. Clean up

Undeploy the Application (`UndeployWorkload -id 5ecdef0000000000000000a1 -y`), delete the child and
grandchild tenants, remove the sample model and blueprints from the local catalogs.
