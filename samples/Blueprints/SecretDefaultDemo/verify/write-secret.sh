#!/usr/bin/env bash
# Writes a secret read from standard input into the sample, as a GraphQL variable.
#   write-secret.sh default <tenantId> [allowedTenantId ...]   the Application's SecretDefaults entry (parent tenant)
#   write-secret.sh tenant  <tenantId>                         the tenant's own CaptchaSecret (override)
# The secret never appears on a command line or in the document: jq turns standard input into the
# variables JSON, which octo-cli reads from its own standard input. The response carries only isSet.
# It always uses the context local_<tenantId>, never the active one, so it cannot write to a shared environment.
set -euo pipefail

usage='usage: write-secret.sh default|tenant <tenantId> [allowedTenantId ...]'
secret_target="${1:?$usage}"
tenant_id="${2:?$usage}"
shift 2
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

IFS= read -r -s secret_value || true
if [[ -z "$secret_value" ]]; then
  echo "No secret on standard input." >&2
  exit 2
fi

case "$secret_target" in
  default)
    document_file="$script_dir/write-secret-default.graphql"
    allowed_tenant_ids_json=$(jq -cn '$ARGS.positional' --args "$@")
    ;;
  tenant)
    document_file="$script_dir/write-tenant-secret.graphql"
    allowed_tenant_ids_json='[]'
    if [[ $# -gt 0 ]]; then
      echo "Allowed tenant ids apply only to the default." >&2
      exit 2
    fi
    ;;
  *)
    echo "Unknown target '$secret_target': use default or tenant." >&2
    exit 2
    ;;
esac

printf '%s' "$secret_value" \
  | jq -Rsc --argjson allowedTenantIds "$allowed_tenant_ids_json" --arg target "$secret_target" \
      'if $target == "default" then { secretValue: ., allowedTenantIds: $allowedTenantIds } else { secretValue: . } end' \
  | octo-cli --context "local_$tenant_id" -c GraphQl -tid "$tenant_id" -f "$document_file" -vars @/dev/stdin
