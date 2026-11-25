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

# Prints the TES body of a code task: the Python file inline, fed.code_sha256 the SHA-256 of its bytes.
# The hospitals are $SITES (default "my-tre|other-tre": a study needs at least 2). The study is $STUDY_ID, by default a
# new one per call, so one run never waits for another's open version.
code_task_body() {
  local dataset=$1 file=$2
  [ -f "$file" ] || { echo "no such file: $file" >&2; return 1; }
  python3 - "$dataset" "$file" "${SITES:-my-tre|other-tre}" "${STUDY_ID:-}" <<'PY'
import hashlib, json, sys, uuid
dataset, path, sites = sys.argv[1], sys.argv[2], sys.argv[3]
study = sys.argv[4] or f"study-{uuid.uuid4().hex[:12]}"
raw = open(path, "rb").read()
print(json.dumps({
    "executors": [{"image": "python-runner:1",
                   "command": ["run-analysis", "/code/analysis.py", f"/data/{dataset}", "/outputs/result.json"]}],
    "inputs": [{"path": "/code/analysis.py", "type": "FILE", "content": raw.decode("utf-8")},
               {"url": f"dataset://{dataset}", "path": f"/data/{dataset}", "type": "DIRECTORY"}],
    "outputs": [{"path": "/outputs/result.json", "url": "result://", "type": "FILE"}],
    "tags": {"fed.sites": sites, "fed.study_id": study, "fed.code_sha256": hashlib.sha256(raw).hexdigest()},
}))
PY
}

# enqueue <dataset> <file.py>: queues a study version that runs the file on the dataset at each hospital; prints the
# version ID. `part` gives a hospital's task ID.
enqueue() {
  [ $# -eq 2 ] || { echo "usage: enqueue <dataset> <file.py>" >&2; return 1; }
  local body
  body=$(code_task_body "$1" "$2") || return 1
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

# part <version-id> [tre]: prints that hospital's task ID in the study version (default my-tre).
part() {
  [ $# -ge 1 ] || { echo "usage: part <version-id> [tre]" >&2; return 1; }
  curl -sf "$SUBMISSION/api/FederatedTask/GetStudyVersion?id=$1" -H "Authorization: Bearer $(token)" |
    python3 -c '
import sys, json
ids = [h["task_id"] for h in json.load(sys.stdin)["hospitals"] if h["tre"] == sys.argv[1]]
if not ids:
    sys.exit(f"no part for {sys.argv[1]} in this version")
print(ids[0])' "${2:-my-tre}"
}

# study <version-id>: prints the study version (state open, canceling or ended, its outcome, and each hospital's phase) as JSON.
study() {
  [ $# -eq 1 ] || { echo "usage: study <version-id>" >&2; return 1; }
  curl -sf "$SUBMISSION/api/FederatedTask/GetStudyVersion?id=$1" -H "Authorization: Bearer $(token)" | python3 -m json.tool
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

# cancel <version-id>: cancels the study version as an orchestrator admin. A queued part is canceled at once; a part a
# hospital picked up is canceling until that hospital confirms.
cancel() {
  local response code
  response=$(curl -s -w '\n%{http_code}' -X POST "$SUBMISSION/api/FederatedTask/Cancel?id=$1" -H "Authorization: Bearer $(token)") ||
    response=$'Submission is not reachable\n000'
  code=${response##*$'\n'}
  if [ "$code" != 200 ]; then
    echo "Cancel refused (HTTP $code): ${response%$'\n'*}" >&2
    return 1
  fi
}

# Polls the task every 5 s while its phase is one of the given ones; prints the first other state, as get prints it.
# The phase, not the TES state, tells running from waiting for review: both are RUNNING.
wait_while() {
  local id=$1 seconds=$2
  shift 2
  local state phase s waiting
  for _ in $(seq 1 $(( seconds / 5 ))); do
    state=$(get "$id")
    phase=$(sed -nE 's/.*"phase": "([^"]*)".*/\1/p' <<<"$state")
    waiting=false
    for s in "$@"; do
      [ "$phase" = "$s" ] && waiting=true
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
