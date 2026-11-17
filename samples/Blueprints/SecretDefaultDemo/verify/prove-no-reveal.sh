#!/usr/bin/env bash
# Proves that a tenant admin cannot read the projected secret default of one tenant through any read path.
#   prove-no-reveal.sh <tenantId>      the sentinel (the default's value) is read from standard input
# The sentinel uses only letters, digits, '.', '_' and '-', so no JSON or YAML escaping can hide it from the search.
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
if [[ ! "$sentinel" =~ ^[A-Za-z0-9._-]{8,}$ ]]; then
  echo "No sentinel on standard input: at least 8 of A-Z a-z 0-9 . _ -" >&2
  exit 2
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
sentinel_pattern_file="$work_dir/sentinel.pattern"
printf '%s\n' "$sentinel" >"$sentinel_pattern_file"
unset sentinel
failed_paths=0

octo() {
  octo-cli --context "$octo_context" "$@"
}

holds_secret_material() {
  local file
  for file in "$@"; do
    [[ -f "$file" ]] || continue
    if grep -qFf "$sentinel_pattern_file" "$file" || grep -qE 'enc:v[0-9]+:' "$file"; then
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
  grep -E '^[[{]' "$1" | tail -n 1
}

is_json_payload() {
  local payload
  payload=$(last_json_line "$1" || true)
  [[ -n "$payload" ]] && jq -e . >/dev/null 2>&1 <<<"$payload"
}

find_secret_default_rt_id() {
  local response_file="$work_dir/read-secret-default.json"
  octo -c GraphQl -tid "$tenant_id" -f "$script_dir/read-secret-default.graphql" >"$response_file"
  last_json_line "$response_file" \
    | jq -r '[.. | objects | select(has("rtWellKnownName")) | .rtId] | first // empty'
}

check_typed_value_field() {
  local output_file="$work_dir/typed.out" error_file="$work_dir/typed.err"
  if octo -c GraphQl -tid "$tenant_id" -f "$script_dir/probe-typed-value.graphql" >"$output_file" 2>"$error_file"; then
    report FAIL "GraphQL typed field value" "the request was answered"
  elif holds_secret_material "$output_file" "$error_file"; then
    report FAIL "GraphQL typed field value" "refused, but the answer holds the value"
  elif grep -qiE 'field[^A-Za-z]+value[^A-Za-z]+does not exist|cannot query field[^A-Za-z]+(u0027)?value' \
    "$output_file" "$error_file"; then
    report PASS "GraphQL typed field value" "refused: the schema has no field value"
  else
    report FAIL "GraphQL typed field value" "refused for another reason; nothing was checked"
  fi
}

check_generic_attributes() {
  local output_file="$work_dir/generic.json" value_attribute_count
  if ! octo -c GraphQl -tid "$tenant_id" -f "$script_dir/probe-generic-attributes.graphql" >"$output_file" 2>/dev/null; then
    report FAIL "GraphQL runtimeEntities, all attributes" "the query failed; nothing was checked"
    return
  fi
  if ! last_json_line "$output_file" | jq -e --arg rtId "$secret_default_rt_id" \
    '[.. | objects | select(.rtId? == $rtId)] | length > 0' >/dev/null 2>&1; then
    report FAIL "GraphQL runtimeEntities, all attributes" "the SecretDefault is not in the answer; nothing was checked"
    return
  fi
  value_attribute_count=$(last_json_line "$output_file" | jq '[.. | objects | select(has("attributeName"))
    | .attributeName | ascii_downcase | select(. == "value" or . == "secretdefaultvalue")] | length')
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
  : >"$execution_file"
  octo -c SetPipelineDebug -id "$pipeline_rt_id" -e true >/dev/null 2>&1 || return 1
  execution_id=$(octo -c ExecutePipeline -id "$pipeline_rt_id" 2>/dev/null \
    | grep -oE '[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}' | tail -n 1) || return 1
  [[ -n "$execution_id" ]] || return 1
  for ((attempt = 1; attempt <= execution_poll_attempts; attempt++)); do
    jq -cn --arg executionId "$execution_id" '{ executionId: $executionId }' \
      | octo -c GraphQl -tid "$tenant_id" -f "$script_dir/read-pipeline-execution.graphql" -vars @/dev/stdin \
        >"$work_dir/latest.out" 2>/dev/null || true
    latest_execution=$(last_json_line "$work_dir/latest.out" \
      | jq -c '.data.runtime.systemCommunicationPipelineExecution.items[0] // empty' 2>/dev/null || true)
    if [[ -n "$latest_execution" ]] && jq -e '.status | IN("QUEUED", "RUNNING") | not' \
      <<<"$latest_execution" >/dev/null; then
      printf '%s\n' "$latest_execution" >"$execution_file"
      break
    fi
    sleep "$execution_poll_interval_seconds"
  done
  octo -c GetPipelineDebugPoints -id "$pipeline_rt_id" -eid "$execution_id" -j >"$debug_points_file" 2>&1 \
    && is_json_payload "$debug_points_file" || : >"$debug_points_file.unreadable"
  octo -c GetPipelineExecutions -id "$pipeline_rt_id" -j >"$history_file" 2>&1 \
    && is_json_payload "$history_file" || : >"$history_file.unreadable"
}

traces_unreadable() {
  [[ -e "$1.debug.unreadable" || -e "$1.history.unreadable" ]]
}

check_refusing_pipeline() {
  local read_path="$1" pipeline_rt_id="$2" file_stem="$work_dir/$2"
  if ! run_probe_pipeline "$pipeline_rt_id" "$file_stem.execution" "$file_stem.debug" "$file_stem.history"; then
    report FAIL "$read_path" "the probe pipeline could not be started; nothing was checked"
    return
  fi
  if holds_secret_material "$file_stem.execution" "$file_stem.debug" "$file_stem.history"; then
    report FAIL "$read_path" "the value is in the execution, its debug points or the history"
  elif [[ ! -s "$file_stem.execution" ]]; then
    report FAIL "$read_path" "the execution did not finish; nothing was checked"
  elif traces_unreadable "$file_stem"; then
    report FAIL "$read_path" "debug points or history could not be read; nothing was checked"
  elif jq -e '(.status == "FAILED") and ((.errorMessage // "") | contains("platform-only"))' \
    "$file_stem.execution" >/dev/null; then
    report PASS "$read_path" "refused as platform-only; no value in debug points or history"
  else
    report FAIL "$read_path" "the execution was not refused as platform-only"
  fi
}

check_verify_captcha_pipeline() {
  local read_path="VerifyCaptcha@1 via ExecutePipeline" file_stem="$work_dir/$verify_captcha_pipeline_rt_id"
  local verdict_source
  if ! run_probe_pipeline "$verify_captcha_pipeline_rt_id" "$file_stem.execution" "$file_stem.debug" \
    "$file_stem.history"; then
    report FAIL "$read_path" "the probe pipeline could not be started; nothing was checked"
    return
  fi
  verdict_source=$(jq -r '.outputData // empty
    | if type == "string" then (fromjson? // empty) else . end | .source? // empty' "$file_stem.execution" 2>/dev/null || true)
  if holds_secret_material "$file_stem.execution" "$file_stem.debug" "$file_stem.history"; then
    report FAIL "$read_path" "the value is in the execution, its debug points or the history"
  elif [[ ! -s "$file_stem.execution" ]] || traces_unreadable "$file_stem"; then
    report FAIL "$read_path" "no finished execution or unreadable debug points; nothing was checked"
  elif ! jq -e '.status == "COMPLETED"' "$file_stem.execution" >/dev/null; then
    report FAIL "$read_path" "the execution did not complete"
  elif [[ "$verdict_source" != default ]]; then
    report FAIL "$read_path" "verdict source=${verdict_source:-none found}, expected default"
  else
    report PASS "$read_path" "verdict source=default; no value in debug points or history"
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
