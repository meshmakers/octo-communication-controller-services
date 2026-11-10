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
- `octo-cli` with the `GraphQl` command and its `-vars` option, one context per tenant (`<parent>`,
  `<child>`, `<grandchild>`). Logins are done by the developer, not by an agent. `jq` for
  `write-secret.sh`.
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

Create the tree before the deploy in step 4, so that the deploy itself projects into every tenant of
it. A tenant created later receives the default on its first load, or at once with
`POST {tenant}/v1/secretDefaults/reconcile` on the Communication Controller.

```bash
octo-cli --context "local_$parent_tenant" -c Create -tid "$child_tenant" -db "$child_tenant"
octo-cli --context "local_$child_tenant"  -c Create -tid "$grandchild_tenant" -db "$grandchild_tenant"

octo-cli --context "local_$parent_tenant" -c InstallBlueprint -b SecretDefaultDemo-1.0.0
for served_tenant in "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "local_$served_tenant" -c InstallBlueprint -b SecretDefaultDemoCaptcha-1.0.0
  octo-cli --context "local_$served_tenant" -c DeployDataFlow -id 5ecdef0000000000000000b2
done
```

## 3. Before the operator step: nothing to project

The seed declares no `SecretDefaults` entry, so nothing reaches a served tenant yet:

```bash
for served_tenant in "$parent_tenant" "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "local_$served_tenant" -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
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
octo-cli --context "local_$parent_tenant" -c DeployWorkload -id 5ecdef0000000000000000a1
for served_tenant in "$parent_tenant" "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "local_$served_tenant" -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
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
| Child clears its secret | `octo-cli --context "local_$child_tenant" -c GraphQl -tid "$child_tenant" -f "$sample_root/verify/clear-tenant-secret.graphql"` | `captchaSecret { isSet: false }` |
| Back to the default | `"$sample_root/verify/call-verify-captcha.sh" "$adapter_base_url" "$child_tenant"` | `{"valid":true,"source":"default"}` |

No answer, log line or GraphQL response may contain a secret: grep them for `1x0000` and `2x0000`.
`RevealSecret@1` on the `SecretDefault` is refused for every identity (proof story AB#7247).

## 6. Undeploy withdraws, clean up

```bash
octo-cli --context "local_$parent_tenant" -c UndeployWorkload -id 5ecdef0000000000000000a1 -y
for served_tenant in "$parent_tenant" "$child_tenant" "$grandchild_tenant"; do
  octo-cli --context "local_$served_tenant" -c GraphQl -tid "$served_tenant" -f "$sample_root/verify/read-secret-default.graphql"
done
```

Expected: `totalCount: 0` everywhere - an undeployed Application is no source - and
`"$sample_root/verify/call-verify-captcha.sh"` in a tenant without its own secret answers `"valid":false` with
`"source":"none"`. The Information event of the withdrawal is linked to the Application.

Then delete the child and grandchild tenants and remove the sample model and blueprints from the local
catalogs.
