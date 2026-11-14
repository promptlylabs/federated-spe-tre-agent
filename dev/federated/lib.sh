# Shared helpers for the local federated stack (dev/federated/docker-compose.yml).
# Sourced by task.sh and demo.sh; defines functions and URLs only.

SUBMISSION=http://localhost:5034
KEYCLOAK=http://localhost:8180/realms/Dare-Control
EGRESS=http://localhost:5279
EGRESS_KEYCLOAK=http://localhost:8180/realms/Data-Egress
EGRESS_UI=http://localhost:5280
TRE_STORE=http://localhost:9000       # the TRE's RustFS: datasets
RESULTS_STORE=http://localhost:9100   # the orchestrator's RustFS: released results

# Every call has a time limit, so a service that accepts the connection but never answers cannot hang a script.
curl() { command curl --connect-timeout 5 --max-time "${CURL_MAX_TIME:-20}" "$@"; }

# Logs in with a password grant and prints "<renew-at epoch>" then the token.
login() {
  curl -sf "$1/protocol/openid-connect/token" -d grant_type=password -d "client_id=$2" -d "client_secret=$3" \
    -d "username=$4" --data-urlencode "password=$5" |
    python3 -c 'import sys, json, time; t = json.load(sys.stdin); print(int(time.time()) + int(t.get("expires_in", 60)) - 30); print(t["access_token"])'
}

# Reuses a token until shortly before it expires, so a script that polls every 2 s does not log in every 2 s.
# The cache lives for one run of the script that sourced this file.
TOKEN_CACHE=$(mktemp -d "${TMPDIR:-/tmp}/federated-tokens.XXXXXX")
trap 'rm -rf "$TOKEN_CACHE"' EXIT
cached_login() {
  local file=$TOKEN_CACHE/$1 fresh
  shift
  if [ -s "$file" ] && [ "$(head -n 1 "$file")" -gt "$(date +%s)" ]; then tail -n 1 "$file"; return 0; fi
  fresh=$(login "$@") || return 1
  printf '%s\n' "$fresh" > "$file"
  tail -n 1 <<<"$fresh"
}

token() { cached_login submission "$KEYCLOAK" Dare-Control-API dev-dare-control-api-secret control-admin 'ControlAdmin1!'; }

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


egress_token() { cached_login egress "$EGRESS_KEYCLOAK" Data-Egress-API dev-data-egress-api-secret reviewer 'Reviewer1!'; }

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

# Lists a bucket in one of the dev RustFS stores: s3_list <store> <user:secret> <bucket> <query>.
s3_list() {
  curl -sf --aws-sigv4 "aws:amz:us-east-1:s3" -u "$2" "$1/$3?list-type=2&$4"
}

# Whether a result file of the task is in the orchestrator's result store: yes, no, or unknown (store not answering).
# Checked in the store itself: GetTaskResult answers 404 for every task that is not COMPLETE, whatever is stored.
stored_result() {
  local tres tre listing
  tres=$(s3_list "$RESULTS_STORE" orchadmin:orchadmin123 federated-results "delimiter=/") || { echo unknown; return; }
  # Keys are <tre>/<task>/<sha256>.json; list each TRE's folder for the task.
  for tre in $(grep -oE '<Prefix>[^<]+/</Prefix>' <<<"$tres" | sed -E 's#</?Prefix>##g'); do
    listing=$(s3_list "$RESULTS_STORE" orchadmin:orchadmin123 federated-results "prefix=${tre}$1/") || { echo unknown; return; }
    grep -q '<Key>' <<<"$listing" && { echo yes; return; }
  done
  echo no
}

# Prints what is missing when the seed steps have not finished (and the seed step to run), or nothing when seeded.
seed_problem() {
  s3_list "$TRE_STORE" rustfsadmin:rustfsadmin123 federated-datasets "prefix=dataset/cohort_2024/" | grep -q '\.csv</Key>' ||
    { echo "the dataset cohort_2024 is missing|seed-dataset"; return; }
  curl -sf "$EGRESS/api/TreCredentials/CheckCredentialsAreValid" -H "Authorization: Bearer $(egress_token)" |
    python3 -c 'import sys, json; sys.exit(0 if json.load(sys.stdin).get("result") is True else 1)' 2>/dev/null ||
    echo "the Egress app has no usable TRE login, so decisions cannot reach the Agent|seed-egress-creds"
}
