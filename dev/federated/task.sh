#!/usr/bin/env bash
# Helper for the local federated stack (dev/federated/docker-compose.yml).
#
#   ./task.sh enqueue <dataset> [operation] [extra args...]   queue a task, prints its id
#   ./task.sh get <task-id>                                   show state, step, step times and result location
#   ./task.sh result <task-id>                                download and print the result file
#   ./task.sh run <dataset> [operation] [extra args...]       enqueue, wait until the result waits for output approval
#   ./task.sh approve <task-id>                               approve the result in the Egress app, wait for the release
#   ./task.sh reject <task-id> "<reason>"                     reject the result in the Egress app, wait for the report
#
# Examples:
#   ./task.sh run cohort_2024 mean --column age --filter sex:eq:F    -> PAUSED, step waiting_for_output_approval
#   ./task.sh approve <task-id>                                      -> COMPLETE, step released, result_key <treId>/<taskId>/<sha256>.json
#   ./task.sh reject <task-id> "small cells"                         -> CANCELED, step rejected_at_output_approval, rejected_by_reviewer
#   ./task.sh run nope count                                         -> CANCELED, step rejected_by_input_checks, dataset_not_found
set -euo pipefail

SUBMISSION=http://localhost:5034
KEYCLOAK=http://localhost:8180/realms/Dare-Control
EGRESS=http://localhost:5279
EGRESS_KEYCLOAK=http://localhost:8180/realms/Data-Egress

token() {
  curl -sf "$KEYCLOAK/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=Dare-Control-API -d client_secret=dev-dare-control-api-secret \
    -d username=control-admin -d 'password=ControlAdmin1!' |
    python3 -c 'import sys, json; print(json.load(sys.stdin)["access_token"])'
}

enqueue() {
  local dataset=$1 operation=${2:-count}
  shift $(( $# >= 2 ? 2 : 1 ))
  local command
  command=$(python3 -c 'import sys, json; print(json.dumps(sys.argv[1:]))' "$operation" "$@")
  local body
  body=$(cat <<EOF
{"executors":[{"image":"5s-tes/federated-ops:1","command":$command}],
 "inputs":[{"url":"dataset://$dataset","path":"/data/$dataset","type":"DIRECTORY"}],
 "outputs":[],"tags":{"tres":"my-tre","study_id":"study-001"}}
EOF
)
  local response code
  response=$(curl -s -w '\n%{http_code}' -X POST "$SUBMISSION/api/FederatedTask/Enqueue" \
    -H "Authorization: Bearer $(token)" -H 'Content-Type: application/json' -d "$body")
  code=${response##*$'\n'}
  response=${response%$'\n'*}
  if [ "$code" != 200 ]; then
    echo "Enqueue rejected (HTTP $code): $response" >&2
    return 1
  fi
  python3 -c 'import sys, json; print(json.load(sys.stdin)["id"])' <<<"$response"
}

get() {
  curl -s "$SUBMISSION/api/FederatedTask/GetTask?id=$1" -H "Authorization: Bearer $(token)" |
    python3 -c '
import sys, json
t = json.load(sys.stdin)
meta = dict(t["logs"][0]["metadata"]) if t.get("logs") else {}
meta.pop("$id", None)
print(t["state"], json.dumps(meta))'
}

result() {
  local tmp code
  tmp=$(mktemp)
  code=$(curl -s -o "$tmp" -w '%{http_code}' "$SUBMISSION/api/FederatedTask/GetTaskResult?id=$1" \
    -H "Authorization: Bearer $(token)") || code=000
  if [ "$code" != 200 ]; then
    echo "HTTP $code" >&2
    cat "$tmp" >&2
    echo >&2
    rm -f "$tmp"
    return 1
  fi
  python3 -m json.tool "$tmp"
  rm -f "$tmp"
}

# Polls the task every 5 s while its state is one of the given prefixes; prints the first other state.
wait_while() {
  local id=$1 seconds=$2
  shift 2
  local state prefix waiting
  for _ in $(seq 1 $(( seconds / 5 ))); do
    state=$(get "$id")
    waiting=false
    for prefix in "$@"; do
      [[ $state == "$prefix"* ]] && waiting=true
    done
    if [ "$waiting" = false ]; then
      echo "$state"
      return 0
    fi
    sleep 5
  done
  echo "still $state after ${seconds}s; check: docker compose -f dev/federated/docker-compose.yml logs agent-api" >&2
  return 1
}

run() {
  local id state
  id=$(enqueue "$@")
  echo "queued $id; waiting for the agent (polls every minute)..."
  state=$(wait_while "$id" 150 QUEUED RUNNING) || return 1
  echo "$state"
  if [[ $state == *waiting_for_output_approval* ]]; then
    echo "review it at http://localhost:5280 (reviewer / Reviewer1!), or:"
    echo "  ./task.sh approve $id"
    echo "  ./task.sh reject $id \"<reason>\""
  fi
}

egress_token() {
  curl -sf "$EGRESS_KEYCLOAK/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=Data-Egress-API -d client_secret=dev-data-egress-api-secret \
    -d username=reviewer -d 'password=Reviewer1!' |
    python3 -c 'import sys, json; print(json.load(sys.stdin)["access_token"])'
}

# Decides every file of the task's Egress request (1 approved, 2 rejected), then waits for the Agent's report.
decide() {
  local id=$1 status=$2 reason=${3:-} token row egress body code response
  token=$(egress_token)
  row=$(curl -sf "$EGRESS/api/DataEgress/GetAllEgresses?unprocessedonly=true" -H "Authorization: Bearer $token" |
    python3 -c '
import sys, json
rows = [r["id"] for r in json.load(sys.stdin) if r.get("submissionId") == sys.argv[1]]
print(rows[0] if rows else "")' "$id")
  if [ -z "$row" ]; then
    echo "no undecided Egress request for task $id" >&2
    return 1
  fi
  egress=$(curl -sf "$EGRESS/api/DataEgress/GetEgress?id=$row" -H "Authorization: Bearer $token")
  body=$(python3 -c '
import sys, json
e = json.loads(sys.argv[1])
for f in e["files"]:
    f["status"] = int(sys.argv[2])
e["decisionReason"] = sys.argv[3] or None
print(json.dumps(e))' "$egress" "$status" "$reason")
  response=$(curl -s -w '\n%{http_code}' -X POST "$EGRESS/api/DataEgress/CompleteEgress" \
    -H "Authorization: Bearer $token" -H 'Content-Type: application/json' -d "$body")
  code=${response##*$'\n'}
  if [ "$code" != 200 ]; then
    echo "CompleteEgress failed (HTTP $code): ${response%$'\n'*}" >&2
    return 1
  fi
  echo "decision sent; waiting for the agent (polls every minute)..."
  wait_while "$id" 150 QUEUED RUNNING PAUSED
}

case "${1:-}" in
  enqueue) shift; enqueue "$@" ;;
  get) shift; get "$1" ;;
  result) shift; result "$1" ;;
  run) shift; run "$@" ;;
  approve) shift; decide "$1" 1 ;;
  reject) shift; [ -n "${2:-}" ] || { echo "a reason is required: ./task.sh reject <task-id> \"<reason>\"" >&2; exit 1; }; decide "$1" 2 "$2" ;;
  *) sed -n '2,15p' "$0"; exit 1 ;;
esac
