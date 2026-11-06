#!/usr/bin/env bash
# Writes a secret read from standard input into the sample, without putting it on a command line.
#   write-secret.sh default <tenantId>   the SecretDefaults entry of the Application (parent tenant)
#   write-secret.sh tenant  <tenantId>   the tenant's own CaptchaSecret (override)
# The value reaches octo-cli inside the GraphQL document on standard input; the response carries
# only isSet. Use it for Cloudflare's public test secrets; type a real secret in the Studio.
set -euo pipefail

secret_target="${1:?usage: write-secret.sh default|tenant <tenantId>}"
tenant_id="${2:?usage: write-secret.sh default|tenant <tenantId>}"

IFS= read -r -s secret_value || true
if [[ -z "$secret_value" ]]; then
  echo "No secret on standard input." >&2
  exit 2
fi
if [[ ! "$secret_value" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "This helper accepts only letters, digits, '.', '_' and '-'; type other secrets in the Studio." >&2
  exit 2
fi

case "$secret_target" in
  default)
    mutation_document=$(printf 'mutation writeSecretDefault { runtime { systemCommunicationApplications { update(entities: [{ rtId: "5ecdef0000000000000000a1", item: { secretDefaults: [{ key: "demo-captcha-secret", secretValue: "%s", description: "Turnstile secret every served tenant verifies CAPTCHAs with unless it sets its own." }] } }]) { rtId secretDefaults { key secretValue { isSet } } } } } }' "$secret_value")
    ;;
  tenant)
    mutation_document=$(printf 'mutation writeTenantSecret { runtime { secretDefaultDemoCaptchaConfigurations { update(entities: [{ rtId: "5ecdef0000000000000000b1", item: { captchaSecret: "%s" } }]) { rtId captchaSecret { isSet } } } } }' "$secret_value")
    ;;
  *)
    echo "Unknown target '$secret_target': use default or tenant." >&2
    exit 2
    ;;
esac

printf '%s' "$mutation_document" | octo-cli -c GraphQl -tid "$tenant_id" -si
