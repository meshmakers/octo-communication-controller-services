#!/usr/bin/env bash
# Proves that a tenant admin cannot read the projected secret default of one tenant through any read path.
#   prove-no-reveal.sh <tenantId>      the sentinel (the default's value) is read from standard input
# Context: $OCTO_CONTEXT, else ${OCTO_CONTEXT_PREFIX:-local_}<tenantId>; log it in as a tenant admin of <tenantId>.
# Needs the blueprints SecretDefaultDemoCaptcha and SecretDefaultDemoProbe in the tenant and both data flows deployed.
# Prints one line per path (PASS/FAIL) and never a response body: a leaked value would end up in the terminal.
# Exit code 0 when every path refused, 1 when one did not, 2 on a usage or setup problem.
set -euo pipefail

usage='usage: prove-no-reveal.sh <tenantId>   (sentinel on standard input)'
tenant_id="${1:?$usage}"
octo_context="${OCTO_CONTEXT:-${OCTO_CONTEXT_PREFIX:-local_}${tenant_id}}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

secret_default_ck_type_id='System.Communication/SecretDefault'
probe_query_rt_id='5ecdef0000000000000000c1'
reveal_as_caller_pipeline_rt_id='5ecdef0000000000000000c3'
reveal_as_service_account_pipeline_rt_id='5ecdef0000000000000000c4'
pipeline_config_pipeline_rt_id='5ecdef0000000000000000c5'
verify_captcha_pipeline_rt_id='5ecdef0000000000000000c6'
execution_poll_attempts=30
execution_poll_interval_seconds=2

IFS= read -r -s sentinel || true
if [[ ${#sentinel} -lt 8 ]]; then
  echo "No sentinel of at least 8 characters on standard input." >&2
  exit 2
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
failed_paths=0

octo() {
  octo-cli --context "$octo_context" "$@"
}

holds_secret_material() {
  local file
  for file in "$@"; do
    [[ -f "$file" ]] || continue
    if grep -qF -- "$sentinel" "$file" || grep -qE 'enc:v[0-9]+:' "$file"; then
      return 0
    fi
  done
  return 1
}

report() {
  local outcome="$1" read_path="$2" detail="$3"
  printf '%-4s  %-44s %s\n' "$outcome" "$read_path" "$detail"
  if [[ "$outcome" == FAIL ]]; then
    failed_paths=$((failed_paths + 1))
  fi
}

last_json_line() {
  grep -E '^\{' "$1" | tail -n 1
}

find_secret_default_rt_id() {
  local response_file="$work_dir/read-secret-default.json"
  octo -c GraphQl -tid "$tenant_id" -f "$script_dir/read-secret-default.graphql" >"$response_file"
  jq -r '[.. | objects | select(has("rtWellKnownName")) | .rtId] | first // empty' "$response_file"
}

check_typed_value_field() {
  local output_file="$work_dir/typed.out" error_file="$work_dir/typed.err"
  if octo -c GraphQl -tid "$tenant_id" -f "$script_dir/probe-typed-value.graphql" >"$output_file" 2>"$error_file"; then
    report FAIL "GraphQL typed field value" "the request was answered"
  elif holds_secret_material "$output_file" "$error_file"; then
    report FAIL "GraphQL typed field value" "refused, but the answer holds the value"
  else
    report PASS "GraphQL typed field value" "refused: the schema has no field value"
  fi
}

check_generic_attributes() {
  local output_file="$work_dir/generic.json" value_attribute_count
  if ! octo -c GraphQl -tid "$tenant_id" -f "$script_dir/probe-generic-attributes.graphql" >"$output_file" 2>/dev/null; then
    report FAIL "GraphQL runtimeEntities, all attributes" "the query failed; nothing was checked"
    return
  fi
  value_attribute_count=$(jq '[.. | objects | select(has("attributeName")) | .attributeName
    | ascii_downcase | select(. == "value" or . == "secretdefaultvalue")] | length' "$output_file")
  if holds_secret_material "$output_file" || [[ "$value_attribute_count" != 0 ]]; then
    report FAIL "GraphQL runtimeEntities, all attributes" "the value attribute is in the answer"
  else
    report PASS "GraphQL runtimeEntities, all attributes" "answered without the value attribute"
  fi
}

check_export() {
  local read_path="$1" archive_file="$2" expected_rt_id="$3" content_file
  shift 3
  content_file="$archive_file.txt"
  if ! octo "$@" -f "$archive_file" >/dev/null 2>&1 || [[ ! -s "$archive_file" ]]; then
    report FAIL "$read_path" "the export failed; nothing was checked"
    return
  fi
  unzip -p "$archive_file" >"$content_file"
  if holds_secret_material "$content_file"; then
    report FAIL "$read_path" "the export holds the value or its encrypted envelope"
  elif ! grep -qF -- "$expected_rt_id" "$content_file"; then
    report FAIL "$read_path" "the SecretDefault is not in the export; nothing was checked"
  else
    report PASS "$read_path" "exported without value and without envelope"
  fi
}

run_probe_pipeline() {
  local pipeline_rt_id="$1" execution_file="$2" debug_points_file="$3" history_file="$4"
  local execution_id attempt latest_execution
  octo -c SetPipelineDebug -id "$pipeline_rt_id" -e true >/dev/null
  execution_id=$(octo -c ExecutePipeline -id "$pipeline_rt_id" \
    | grep -oE '[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}' | tail -n 1)
  : >"$execution_file"
  for ((attempt = 1; attempt <= execution_poll_attempts; attempt++)); do
    octo -c GetLatestPipelineExecution -id "$pipeline_rt_id" -j >"$work_dir/latest.out" 2>/dev/null || true
    latest_execution=$(last_json_line "$work_dir/latest.out" || true)
    if [[ -n "$latest_execution" ]] && jq -e --arg executionId "$execution_id" \
      '(.Id | ascii_downcase) == ($executionId | ascii_downcase) and ((.Status | tostring) | IN("0", "Running") | not)' \
      <<<"$latest_execution" >/dev/null; then
      printf '%s\n' "$latest_execution" >"$execution_file"
      break
    fi
    sleep "$execution_poll_interval_seconds"
  done
  octo -c GetPipelineDebugPoints -id "$pipeline_rt_id" -eid "$execution_id" -j >"$debug_points_file" 2>&1 || true
  octo -c GetPipelineExecutions -id "$pipeline_rt_id" -j >"$history_file" 2>&1 || true
}

check_refusing_pipeline() {
  local read_path="$1" pipeline_rt_id="$2" file_stem="$work_dir/$2"
  run_probe_pipeline "$pipeline_rt_id" "$file_stem.execution" "$file_stem.debug" "$file_stem.history"
  if holds_secret_material "$file_stem.execution" "$file_stem.debug" "$file_stem.history"; then
    report FAIL "$read_path" "the value is in the execution, its debug points or the history"
  elif [[ ! -s "$file_stem.execution" ]]; then
    report FAIL "$read_path" "the execution did not finish; nothing was checked"
  elif jq -e '((.Status | tostring) | IN("2", "Failed")) and ((.ErrorMessage // "") | contains("platform-only"))' \
    "$file_stem.execution" >/dev/null; then
    report PASS "$read_path" "refused as platform-only; no value in debug points or history"
  else
    report FAIL "$read_path" "the execution was not refused as platform-only"
  fi
}

check_verify_captcha_pipeline() {
  local read_path="VerifyCaptcha@1 via ExecutePipeline" file_stem="$work_dir/$verify_captcha_pipeline_rt_id"
  local verdict_source
  run_probe_pipeline "$verify_captcha_pipeline_rt_id" "$file_stem.execution" "$file_stem.debug" "$file_stem.history"
  verdict_source=$(grep -oE '"source"[[:space:]]*:[[:space:]]*"[a-z]+"' "$file_stem.debug" | head -n 1 \
    | grep -oE '"[a-z]+"$' | tr -d '"' || true)
  if holds_secret_material "$file_stem.execution" "$file_stem.debug" "$file_stem.history"; then
    report FAIL "$read_path" "the value is in the execution, its debug points or the history"
  elif [[ -z "$verdict_source" ]]; then
    report FAIL "$read_path" "no verdict in the debug points; nothing was checked"
  else
    report PASS "$read_path" "verdict source=$verdict_source; no value in debug points or history"
  fi
}

secret_default_rt_id=$(find_secret_default_rt_id)
if [[ -z "$secret_default_rt_id" ]]; then
  echo "No projected SecretDefault demo-captcha-secret in tenant '$tenant_id': deploy the Application first." >&2
  exit 2
fi

echo "Tenant $tenant_id, context $octo_context, SecretDefault $secret_default_rt_id"
check_typed_value_field
check_generic_attributes
check_export "ExportRtByDeepGraph" "$work_dir/deep-graph.zip" "$secret_default_rt_id" \
  -c ExportRtByDeepGraph -id "$secret_default_rt_id" -t "$secret_default_ck_type_id"
check_export "ExportRtByQuery" "$work_dir/by-query.zip" "$secret_default_rt_id" \
  -c ExportRtByQuery -q "$probe_query_rt_id"
check_refusing_pipeline "RevealSecret@1 as Caller" "$reveal_as_caller_pipeline_rt_id"
check_refusing_pipeline "RevealSecret@1 as ServiceAccount" "$reveal_as_service_account_pipeline_rt_id"
check_refusing_pipeline "GetPipelineConfigByCkTypeId@1" "$pipeline_config_pipeline_rt_id"
check_verify_captcha_pipeline

if [[ $failed_paths -gt 0 ]]; then
  echo "$failed_paths read path(s) did not refuse."
  exit 1
fi
echo "Every read path refused."
