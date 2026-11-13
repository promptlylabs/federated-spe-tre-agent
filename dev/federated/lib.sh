# Shared helpers for the local federated stack (dev/federated/docker-compose.yml).
# Sourced by task.sh and demo.sh; defines functions and URLs only.

SUBMISSION=http://localhost:5034
KEYCLOAK=http://localhost:8180/realms/Dare-Control
EGRESS=http://localhost:5279
EGRESS_KEYCLOAK=http://localhost:8180/realms/Data-Egress
EGRESS_UI=http://localhost:5280

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

# The task as GetTask returns it (JSON).
task_json() {
  curl -sf "$SUBMISSION/api/FederatedTask/GetTask?id=$1" -H "Authorization: Bearer $(token)"
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


egress_token() {
  curl -sf "$EGRESS_KEYCLOAK/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=Data-Egress-API -d client_secret=dev-data-egress-api-secret \
    -d username=reviewer -d 'password=Reviewer1!' |
    python3 -c 'import sys, json; print(json.load(sys.stdin)["access_token"])'
}

# Prints the id of the task's undecided Egress request, or nothing if there is none (yet).
egress_row_id() {
  local id=$1 token
  token=${2:-$(egress_token)}
  curl -sf "$EGRESS/api/DataEgress/GetAllEgresses?unprocessedonly=true" -H "Authorization: Bearer $token" |
    python3 -c '
import sys, json
rows = [r["id"] for r in json.load(sys.stdin) if r.get("submissionId") == sys.argv[1]]
print(rows[0] if rows else "")' "$id"
}

# Decides every file of the task's Egress request (1 approved, 2 rejected) as reviewer. Does not wait.
decide() {
  local id=$1 status=$2 reason=${3:-} token row egress body code response
  token=$(egress_token)
  row=$(egress_row_id "$id" "$token")
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
}

