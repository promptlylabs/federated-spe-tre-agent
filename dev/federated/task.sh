#!/usr/bin/env bash
# Helper for the local federated stack (dev/federated/docker-compose.yml).
#
#   ./task.sh enqueue <dataset> [operation] [extra args...]   queue a task, prints its id
#   ./task.sh get <task-id>                                   show state and result
#   ./task.sh run <dataset> [operation] [extra args...]       enqueue, wait for the agent, show the result
#
# Examples:
#   ./task.sh run cohort_2024 mean --column age --filter sex:eq:F    -> COMPLETE, value 35, row_count 2
#   ./task.sh run cohort_2024 count                                  -> COMPLETE, value 3
#   ./task.sh run nope count                                         -> EXECUTOR_ERROR, dataset_not_found
set -euo pipefail

SUBMISSION=http://localhost:5034
KEYCLOAK=http://localhost:8180/realms/Dare-Control

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

run() {
  local id
  id=$(enqueue "$@")
  echo "queued $id; waiting for the agent (polls every minute)..."
  for _ in $(seq 1 30); do
    local result
    result=$(get "$id")
    if [[ $result != QUEUED* ]]; then
      echo "$result"
      return 0
    fi
    sleep 5
  done
  echo "still QUEUED after 150s; check: docker compose -f dev/federated/docker-compose.yml logs agent-api" >&2
  return 1
}

case "${1:-}" in
  enqueue) shift; enqueue "$@" ;;
  get) shift; get "$1" ;;
  run) shift; run "$@" ;;
  *) sed -n '2,12p' "$0"; exit 1 ;;
esac
