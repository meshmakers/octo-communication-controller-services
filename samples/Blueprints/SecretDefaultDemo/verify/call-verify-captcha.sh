#!/usr/bin/env bash
# Posts a CAPTCHA token to the sample route of one tenant and prints the verdict.
#   call-verify-captcha.sh <adapterBaseUrl> <tenantId> [token]
# adapterBaseUrl: the tenant's Mesh Adapter, e.g. http://localhost:5020 (kubectl port-forward).
# The default token is Cloudflare's dummy token, accepted by the always-pass test secret only.
set -euo pipefail

adapter_base_url="${1:?usage: call-verify-captcha.sh <adapterBaseUrl> <tenantId> [token]}"
tenant_id="${2:?usage: call-verify-captcha.sh <adapterBaseUrl> <tenantId> [token]}"
captcha_token="${3:-XXXX.DUMMY.TOKEN.XXXX}"

request_body=$(printf '{"captchaToken":"%s"}' "$captcha_token")
curl --silent --show-error \
  --header 'Content-Type: application/json' \
  --data "$request_body" \
  "${adapter_base_url%/}/${tenant_id}/secret-default-demo/verify-captcha"
echo
