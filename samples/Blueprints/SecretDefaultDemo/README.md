# SecretDefaultDemo: a secret default shipped with an Application

A working sample of the secret-default pattern (Feature AB#7219): an operator supplies one secret
once, on an Application, and every tenant the Application serves can verify CAPTCHAs with it - while
a tenant can still set its own secret, and nobody in a tenant can read the default.

| Folder | What it is | Install on |
|---|---|---|
| `SecretDefaultDemo/` | Blueprint `SecretDefaultDemo-1.0.0`: the Application with one `SecretDefaults` entry, `demo-captcha-secret` | the tenant that hosts the Application (the parent) |
| `SecretDefaultDemoCaptcha/` | Blueprint `SecretDefaultDemoCaptcha-1.0.0`: the tenant override `CaptchaConfiguration` and the route `POST /secret-default-demo/verify-captcha` | every served tenant that verifies CAPTCHAs (child, grandchild) |
| `ConstructionKit/` | CK model `SecretDefaultDemo-1.0.0`: type `CaptchaConfiguration` with the Secret attribute `CaptchaSecret` | publish to a CK catalog; the second blueprint pulls it |
| `verify/` | The commands that prove the pattern on a local tenant tree | - |

Requires System.Communication 4.15.0 in every tenant of the tree, a Communication Controller with the
secret-default projection (AB#7244) and a Mesh Adapter with `VerifyCaptcha@1` (AB#7251).

## The pattern

```text
parent tenant                          child / grandchild (and the parent itself)
Application                            System.Communication/SecretDefault "demo-captcha-secret"
  SecretDefaults:                        Value (Secret, platform-only)   <- written by the controller
    Key: demo-captcha-secret           SecretDefaultDemo/CaptchaConfiguration
    SecretValue: (written after          CaptchaSecret (Secret)          <- the tenant's own override
                  the install)         pipeline: FromHttpRequest@2 -> VerifyCaptcha@1 -> { valid, source, reason }
  Values: turnstileSiteKey (Helm)
```

1. **Declare** the default on the Application, in `SecretDefaults` - not in `Values` or `ValuesYaml`.
   A `ValueOverride` reaches Helm values and the pod; a secret default never does. The site key is
   public (it goes to the browser), so it stays an ordinary Helm value.
2. **Supply the value after the install.** The seed carries the Key and no `SecretValue`. See below.
3. **Deploy the Application.** `DeployWorkload` projects each entry into a platform-owned
   `System.Communication/SecretDefault` (well-known name = Key) in the Application's own tenant and
   its whole subtree, never siblings; `AllowedTenantIds` on the entry can only narrow that set.
   A redeploy after a new value rotates it everywhere.
4. **Use it only through a platform node.** `VerifyCaptcha@1` takes the tenant's own secret when it is
   set, else the default named by `defaultKey`, calls Cloudflare's siteverify endpoint (fixed in code)
   and writes only `{ valid, source, reason }`. `source` says which key was used (`tenant`, `default`,
   `none`), never its value.

### How the secret value is supplied

There is no blueprint parameter for a secret, and there must not be one: a blueprint seed may leave a
Secret attribute empty or omit it, nothing else. The secret seed lint (`octo-bpm validate`, the
blueprint build) refuses a value, a `<...>` or `TODO_SET_...` placeholder and a `${...}` variable
alike ("Seeds may only leave Secret attributes empty or omit them ... Set the secret after
installation"). Blueprint variables come from the host's configuration (`OCTO_BLUEPRINTS__*`), which
would put the secret into Helm values - exactly what secret defaults exist to avoid.

So the value is written **after the install**, by an operator with write access to the Application in
the parent tenant:

- **Studio:** open the Application, edit the `SecretDefaults` entry, type the value into `SecretValue`.
- **GraphQL:** `systemCommunicationApplications.update` with the whole `secretDefaults` list, the
  value as `secretValue`; `verify/write-secret.sh default <tenant>` does this for the public test
  secrets and reads the value from standard input, never from an argument.

The engine encrypts it on write; reads return only `isSet`. Until it is written, `DeployWorkload`
refuses the Application ("the SecretDefaults entry 'demo-captcha-secret' has no SecretValue") and nothing is projected. A blueprint re-apply or
update keeps it: `SecretDefaults` is TenantOwned, and a Secret member of a record is carried over by
its record key. The value is never in git, a Helm values file, `ValuesYaml` or a pipeline.

### The platform-only rule

The projected default belongs to the platform, not to the tenant:

- `RevealSecret@1` refuses `System.Communication/SecretDefault` (and the Application's `SecretDefaults`)
  for every identity; `GetPipelineConfigByCkTypeId@1` refuses the type; the controller never ships it as
  adapter configuration. GraphQL, REST and MCP return only `isSet`.
- `VerifyCaptcha@1` reads it with an internal session, only in the executing tenant and only by
  `defaultKey`. The secret never enters the data context, a debug point, a log line or the response.
- A tenant that wants its own widget writes its own secret (here `CaptchaConfiguration.CaptchaSecret`).
  The projection never touches it; clearing it falls back to the default. A set tenant secret that
  cannot be decrypted is `verification-unavailable`, with no fallback.

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
parent/child/grandchild tree, write the test secret, deploy, call the route.
