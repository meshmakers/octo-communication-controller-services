# SecretDefaultDemo: a secret default shipped with an Application

A working sample of the secret-default pattern (Feature AB#7219): an operator supplies one secret
once, on an Application, and every tenant the Application serves can verify CAPTCHAs with it - while
a tenant can still set its own secret, and nobody in a tenant can read the default.

| Folder | What it is | Install on |
|---|---|---|
| `SecretDefaultDemo/` | Blueprint `SecretDefaultDemo-1.0.0`: the Application, without a `SecretDefaults` entry - the operator adds `demo-captcha-secret` after the install | the tenant that hosts the Application (the parent) |
| `SecretDefaultDemoCaptcha/` | Blueprint `SecretDefaultDemoCaptcha-1.0.0`: the tenant override `CaptchaConfiguration` and the route `POST /secret-default-demo/verify-captcha` | every served tenant that verifies CAPTCHAs (child, grandchild) |
| `ConstructionKit/` | CK model `SecretDefaultDemo-1.0.0`: type `CaptchaConfiguration` with the Secret attribute `CaptchaSecret` | publish to a CK catalog; the second blueprint pulls it |
| `verify/` | The commands that prove the pattern on a local tenant tree | - |

Requires System.Communication `[4.16,5.0)` (4.16.0 or later, below 5.0.0) in the CK catalog the asset
repository reads and in every tenant of the tree, a Communication Controller with the secret-default
projection (AB#7244, AB#7245), and a Mesh Adapter with `VerifyCaptcha@1` (AB#7251), outbound HTTPS to
`challenges.cloudflare.com` and the key ring the asset repository encrypts Secret attributes with (without
it the default cannot be decrypted and every check answers `verification-unavailable`). The projection itself needs 4.15.0, but only from 4.16.0 on is the projected value `Hidden`: on 4.15.0 a tenant
identity with generic write access could overwrite it (the next reconcile writes the projected value back).
Both blueprints require `[4.16,5.0)`; the parent's blueprint cannot constrain the descendant tenants, so
install `SecretDefaultDemoCaptcha` (or bring System.Communication into that range) in each of them.

## The pattern

```text
parent tenant                          child / grandchild (and the parent itself)
Application                            System.Communication/SecretDefault "demo-captcha-secret"
  SecretDefaults: (added by the          Value (Secret, platform-only)   <- written by the controller
    operator after the install)        SecretDefaultDemo/CaptchaConfiguration
    Key: demo-captcha-secret             CaptchaSecret (Secret)          <- the tenant's own override
    SecretValue: ...                   pipeline: FromHttpRequest@2 -> VerifyCaptcha@1 -> { valid, source, reason }
  Values: turnstileSiteKey (Helm)
```

1. **Declare nothing in the seed.** The blueprint installs the Application without a `SecretDefaults`
   entry: a seed cannot carry a secret, and an entry without `SecretValue` would make the deploy refuse
   the Application. The site key is public (it goes to the browser), so it stays an ordinary Helm value
   (`Values`). A secret default is never a `ValueOverride` or `ValuesYaml`: those reach Helm values and
   the pod; a secret default never does.
2. **The operator adds the entry after the install** - key, value, optionally `AllowedTenantIds` and a
   description - and deploys. See below.
3. **Deploy the Application.** `DeployWorkload` projects each entry into a platform-owned
   `System.Communication/SecretDefault` (well-known name = Key) in the Application's own tenant and
   its whole subtree, never siblings. `AllowedTenantIds` empty or absent means that whole set; a
   non-empty list narrows it and never widens it (a listed tenant outside the subtree is ignored).
   Only a deployed Application is a source: a tenant created or loaded later receives the default on
   its load, `POST {tenant}/v1/secretDefaults/reconcile` brings a tenant and its subtree up to date
   (e.g. after deleting an Application), a redeploy after a new value rotates it, and **undeploying the
   Application withdraws its defaults** from every tenant. So does a redeploy the deploy guard refuses
   (state Error), even after an earlier successful deploy: the next reconcile removes them. The
   projection is best effort per tenant: the deploy succeeds even when a tenant is skipped (no
   System.Communication 4.15.0, not resolvable) or its write fails, and each such tenant is a Warning or
   Error event linked to the Application. Read those events before you revoke an old secret at the
   provider.
4. **Use it only through a platform node.** `VerifyCaptcha@1` takes the tenant's own secret when it is
   set, else the default named by `defaultKey`, calls Cloudflare's siteverify endpoint (fixed in code)
   and writes only `{ valid, source, reason }`. `source` says which key was used (`tenant`, `default`,
   `none`), never its value.

### How the operator supplies the secret

There is no blueprint parameter for a secret, and there must not be one: a blueprint seed may leave a
Secret attribute empty or omit it, nothing else. The secret seed lint (`octo-bpm validate`, the
blueprint build) refuses a value, a `<...>` or `TODO_SET_...` placeholder and a `${...}` variable
alike ("Seeds may only leave Secret attributes empty or omit them ... Set the secret after
installation"). Blueprint variables come from the host's configuration (`OCTO_BLUEPRINTS__*`), which
would put the secret into Helm values - exactly what secret defaults exist to avoid. So the seed
declares no entry at all, and an operator with write access to the Application in the parent tenant
adds it after the install:

- **Refinery Studio:** open the parent tenant -> **Communication** -> **Applications** ->
  `secret-default-demo`. In **Further attributes** -> `SecretDefaults` add a row: `Key`
  `demo-captcha-secret`, `SecretValue` the secret (write-only; leaving it empty on a later edit keeps
  the stored value), optionally `AllowedTenantIds` (empty = own tenant and whole subtree) and
  `Description`. **Save**, then **Deploy** the Application.
- **GraphQL:** `systemCommunicationApplications.update` with the whole `secretDefaults` list (it
  replaces the Application's list: use it while the Application carries no other entry, otherwise the
  Studio form) and the secret passed as a **GraphQL variable**, never inline in the document:
  [`verify/write-secret-default.graphql`](verify/write-secret-default.graphql).
  `verify/write-secret.sh default <tenant> [allowedTenantId ...]` sends it (always through the context
  `local_<tenant>`) with the value read from standard input; the variables reach `octo-cli` on its standard input (`-vars @/dev/stdin`), so the
  secret is on no command line, in no file and in no shell history. Then `DeployWorkload`.
- **A CD from a secret store**, as the Energy Community app does on test-2 (Vault -> `SecretDefaults`
  before the deploy). Then the store is the source of truth and a Studio edit is overwritten by the
  next run.

The engine encrypts the value on write; reads of the Application return only `isSet`. A blueprint
re-apply or update keeps the entry: `SecretDefaults` is TenantOwned, and a Secret member of a record is
carried over by its record key. Rotate by writing the new value and deploying again. The value is never
in git, a Helm values file, `ValuesYaml` or a pipeline.

**Is the default set in a tenant?** `SecretDefault.Value` is `Hidden` (System.Communication 4.16.0 and
later, so also 4.18.0): GraphQL, REST and MCP can neither read nor write it, not even its `isSet`
marker. The projected entity is the signal: when `verify/read-secret-default.graphql` finds the
`SecretDefault` `demo-captcha-secret` in a tenant, the default reaches that tenant (`sourceTenantId`
and `sourceWorkloadRtId` name the Application); the controller never projects an entry without a value.

### The platform-only rule

The projected default belongs to the platform, not to the tenant:

- `RevealSecret@1` refuses `System.Communication/SecretDefault` (and the Application's `SecretDefaults`)
  for every identity; `GetPipelineConfigByCkTypeId@1` refuses the type; the controller never ships it as
  adapter configuration. GraphQL, REST and MCP cannot read the projected value at all (Hidden), and
  return only `isSet` for the Application's `SecretValue`.
- `VerifyCaptcha@1` reads it with an internal session, only in the executing tenant and only by
  `defaultKey`. The secret never enters the data context, a debug point, a log line or the response.
- A tenant that wants its own widget writes its own secret (here `CaptchaConfiguration.CaptchaSecret`).
  The projection never touches it; clearing it falls back to the default. A tenant secret encrypted
  with a key the adapter's key ring does not hold counts as not set (logged as an error naming the key
  id) and falls back to the default; any other tenant secret that cannot be decrypted is
  `verification-unavailable`, with no fallback.

## Test keys only

The sample uses Cloudflare's public Turnstile test keys and never a real key:

| Key | Value |
|---|---|
| Site key, always passes | `1x00000000000000000000AA` (seeded as the Helm value `turnstileSiteKey`) |
| Secret, always passes | `1x0000000000000000000000000000000AA` |
| Secret, always fails | `2x0000000000000000000000000000000AA` |
| Dummy token | `XXXX.DUMMY.TOKEN.XXXX` |

## Run it

See [`verify/README.md`](verify/README.md): publish the model, install both blueprints on a
parent/child/grandchild tree, add the entry with the test secret, deploy, call the route.
