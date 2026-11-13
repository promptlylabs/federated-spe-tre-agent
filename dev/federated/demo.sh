#!/usr/bin/env bash
# Demo of the federated task loop with both hospital gates, on the local dev stack.
#
#   ./demo.sh                 live: pauses between cases, opens the review page, waits for the reviewer's decision
#   ./demo.sh --unattended    no pauses; decides through the Egress API; exits 1 if any case ends unexpectedly
#
# See dev/federated/README.md ("Demo") for the checklist before recording.
set -uo pipefail

. "$(dirname "$0")/lib.sh"

MODE=live
[ "${1:-}" = "--unattended" ] && MODE=unattended

# --- look -------------------------------------------------------------------------------------------------------------
B=$'\033[1m'; DIM=$'\033[2m'; G=$'\033[32m'; R=$'\033[31m'; A=$'\033[33m'; C=$'\033[36m'; X=$'\033[0m'
PICKUP_TIMEOUT=150   # seconds for the Agent to pick a task up (it polls once a minute)
ACT_TIMEOUT=60       # seconds for the Agent to act on a decision (it acts within seconds)
REVIEW_TIMEOUT=$(( ${DEMO_REVIEW_MINUTES:-15} * 60 ))   # live: how long to wait for the reviewer

FAILURES=0
SUMMARY=()

heading() { printf '\n%s━━ %s ━━%s\n' "$B" "$1" "$X"; }
say()     { printf '%s\n' "$1"; }
ok()      { printf '\r\033[K%s✓%s %s %s%s%s\n' "$G" "$X" "$1" "$DIM" "${2:-}" "$X"; }
stop()    { printf '\r\033[K%s■%s %s %s%s%s\n' "$R" "$X" "$1" "$DIM" "${2:-}" "$X"; }
# One line, rewritten in place. The hint after the counter is left out when the line would not fit the terminal:
# a wrapped line cannot be rewritten and would leave a copy on screen at every update.
COLS=$(stty size </dev/tty 2>/dev/null | cut -d' ' -f2); [ -n "$COLS" ] || COLS=80
waiting() {
  local room=$((COLS - ${#1} - 5)) detail=${2:-}
  [ "${#detail}" -gt "$room" ] && detail=${detail%%  *}
  [ "${#detail}" -gt "$room" ] && detail=""
  printf '\r\033[K%s⏳%s %s %s%s%s' "$A" "$X" "$1" "$DIM" "$detail" "$X"
}
pause()   { [ "$MODE" = live ] && { printf '\n%sPress Enter %s…%s' "$DIM" "$1" "$X"; read -r _; }; return 0; }

# --- task state -------------------------------------------------------------------------------------------------------
# Prints "STATE|step|error_code|error_message" for a task, or "UNKNOWN|||" if it cannot be read.
task_view() {
  task_json "$1" 2>/dev/null | python3 -c '
import sys, json
try:
    t = json.load(sys.stdin)
except Exception:
    print("UNKNOWN|||"); sys.exit()
m = (t.get("logs") or [{}])[0].get("metadata") or {}
print("|".join([t.get("state") or "UNKNOWN", m.get("step") or "", m.get("error_code") or "", (m.get("error_message") or "").replace("|", "/")]))'
}

# Polls the task every 2 s while its state is one of the given ones, showing a line whose counter moves every 10 s.
# Prints the final view.
# Returns 1 on timeout (0 = no limit).
wait_while_state() {
  local id=$1 limit=$2 label=$3 hint=$4
  shift 4
  local start=$SECONDS shown=-10 view state s waiting_now
  while :; do
    view=$(task_view "$id"); state=${view%%|*}
    waiting_now=false
    for s in "$@"; do [ "$state" = "$s" ] && waiting_now=true; done
    if [ "$waiting_now" = false ] && [ "$state" != UNKNOWN ]; then
      printf '\r\033[K' >&2; echo "$view"; return 0
    fi
    if [ "$limit" -gt 0 ] && [ $((SECONDS - start)) -ge "$limit" ]; then
      printf '\r\033[K' >&2; echo "$view"; return 1
    fi
    if [ $((SECONDS - start - shown)) -ge 10 ]; then
      shown=$(( (SECONDS - start) / 10 * 10 ))
      waiting "$label" "${shown}s  $hint" >&2
    fi
    sleep 2
  done
}

# The released value in plain words, e.g. "40 (from 3 records)".
result_words() {
  result "$1" 2>/dev/null | python3 -c '
import sys, json
try:
    r = json.load(sys.stdin).get("result") or {}
except Exception:
    sys.exit()
v = r.get("value")
if isinstance(v, float): v = round(v, 2)
n = r.get("rowCount")
print(f"{v} (from {n} records)" if n is not None else f"{v}")'
}

plain_reason() {
  case "$1" in
    dataset_not_found) echo "the hospital does not hold this dataset" ;;
    ambiguous_dataset) echo "the dataset is not clearly identified" ;;
    image_not_approved) echo "the hospital has not approved this analysis code" ;;
    *) echo "${2:-$1}" ;;
  esac
}

# --- the review step --------------------------------------------------------------------------------------------------
# Waits for the task's review request. Live: opens it in the browser for the reviewer. Unattended: decides through
# the Egress API as reviewer ($2: 1 approve, 2 reject; $3: reason). Prints the review page URL.
review() {
  local id=$1 status=$2 reason=${3:-} row="" start=$SECONDS
  while [ -z "$row" ]; do
    row=$(egress_row_id "$id" 2>/dev/null || true)
    [ -n "$row" ] && break
    [ $((SECONDS - start)) -ge 60 ] && { stop "The review request did not appear in the Egress app" "task $id" >&2; return 1; }
    waiting "Waiting for the hospital reviewer…" "registering the result for review" >&2
    sleep 2
  done
  local page="$EGRESS_UI/Data/GetEgress?id=$row"
  if [ "$MODE" = live ]; then
    open "$page" >/dev/null 2>&1 || true
    printf '\r\033[K%s→%s The review page is open in the browser %s%s%s\n' "$C" "$X" "$DIM" "$page" "$X" >&2
    echo "$page"
    return 0
  fi
  waiting "Waiting for the hospital reviewer…" >&2
  sleep 2
  decide "$id" "$status" "$reason" >&2 || { stop "The decision could not be sent" "task $id" >&2; return 1; }
  echo "$page"
}

# --- one case ---------------------------------------------------------------------------------------------------------
# run_case <n> <title> <researcher line> <expected: released|dataset_not_found|rejected_by_reviewer> <dataset> <op> [args...]
run_case() {
  local n=$1 title=$2 ask=$3 expected=$4
  shift 4
  local id view state step code message outcome left decision
  pause "to start case $n"
  heading "Case $n of 3 · $title"
  say "${DIM}${ask}${X}"
  echo
  if ! id=$(enqueue "$@" 2>/dev/null); then
    stop "The request could not be sent to the hospital"; FAILURES=$((FAILURES + 1))
    SUMMARY+=("$n|$title|request failed|no"); return
  fi
  ok "Request received by the hospital" "task ${id:0:8}"

  view=$(wait_while_state "$id" "$PICKUP_TIMEOUT" "Waiting for the hospital to pick it up…" \
    "the hospital checks for new requests once a minute" QUEUED) || true
  IFS='|' read -r state step code message <<<"$view"

  if [ "$step" = rejected_by_input_checks ]; then
    stop "Stopped by the hospital's checks: $(plain_reason "$code" "$message")" "$state · $step · $code"
    stop "Nothing ran, nothing left the hospital"
    outcome=$code; decision="stopped by the checks"
  elif [ "$state" = QUEUED ] || [ "$state" = UNKNOWN ]; then
    stop "The hospital did not pick the request up in time" "task $id · $state"
    FAILURES=$((FAILURES + 1)); SUMMARY+=("$n|$title|no answer|?"); return
  elif [ "$state" != RUNNING ] && [ "$state" != PAUSED ]; then
    # Ended before the analysis started, but not by the input checks (for example a system error).
    stop "The task ended unexpectedly" "task $id · $state · $step · $code"
    outcome="$state/$step/$code"; decision="$state"
  else
    ok "Hospital checks passed" "approved code, dataset held by the hospital"
    view=$(wait_while_state "$id" "$PICKUP_TIMEOUT" "Analysis running inside the hospital…" "" RUNNING) || true
    IFS='|' read -r state step code message <<<"$view"
    if [ "$step" = waiting_for_output_approval ]; then
      ok "Analysis ran inside the hospital" "the researcher sees nothing yet"
      local page limit=$ACT_TIMEOUT
      local reviewed=true
      if [ "$expected" = released ]; then page=$(review "$id" 1) || reviewed=false
      else page=$(review "$id" 2 "small cells") || reviewed=false; fi
      if [ "$reviewed" = false ]; then
        FAILURES=$((FAILURES + 1)); SUMMARY+=("$n|$title|no review|?"); return
      fi
      # A person decides in live mode: wait longer for them, but not for ever.
      [ "$MODE" = live ] && limit=$REVIEW_TIMEOUT
      if ! view=$(wait_while_state "$id" "$limit" "Waiting for the hospital reviewer…" "" PAUSED); then
        stop "No decision reached the hospital in $((limit / 60)) minute(s)" "task $id · check the Egress app and its logs"
        FAILURES=$((FAILURES + 1)); SUMMARY+=("$n|$title|no decision|?"); return
      fi
      IFS='|' read -r state step code message <<<"$view"
    fi
    case "$step" in
      released)
        ok "Approved by the hospital reviewer"
        ok "Result released to the researcher" "$state · $step"
        say "   ${C}${B}Answer: $(result_words "$id")${X}"
        outcome=released; decision="approved by the reviewer" ;;
      rejected_at_output_approval)
        stop "Rejected by the hospital reviewer: ${message}" "$state · $step · $code"
        stop "Nothing left the hospital"
        outcome=$code; decision="rejected: ${message}" ;;
      *)
        stop "The task ended unexpectedly" "task $id · $state · $step · $code"
        outcome="$state/$step/$code"; decision="$state" ;;
    esac
  fi

  # Checked in the result store itself. A result is stored only when it was released.
  case $(stored_result "$id") in
    yes) left="yes (1 file)" ;;
    no) left="no" ;;
    *) left="unknown" ;;
  esac
  if [ "$left" = unknown ]; then
    say "   ${R}✗ could not check the result store ($RESULTS_STORE)${X}"
    FAILURES=$((FAILURES + 1))
  elif { [ "$outcome" = released ] && [ "$left" = no ]; } || { [ "$outcome" != released ] && [ "$left" != no ]; }; then
    say "   ${R}✗ result stored: ${left}, but the task ended ${outcome}${X}"
    FAILURES=$((FAILURES + 1))
  elif [ "$outcome" != "$expected" ] && [ "$MODE" = unattended ]; then
    # Live: the reviewer may decide differently; the demo reports what really happened.
    say "   ${R}✗ expected ${expected}, got ${outcome}${X}"
    FAILURES=$((FAILURES + 1))
  fi
  SUMMARY+=("$n|$title|$decision|$left")
}

# --- screens ----------------------------------------------------------------------------------------------------------
PROMISES=("The researcher never sees the patient data."
          "The hospital checks every request before it runs."
          "Nothing leaves the hospital unless a hospital reviewer approves exactly what is leaving.")

opening() {
  clear 2>/dev/null || true
  heading "Federated analysis with the hospital in control"
  echo
  local p
  for p in "${PROMISES[@]}"; do say "  • $p"; done
  echo
  say "${DIM}Three requests follow: one the hospital approves, one its checks stop, one its reviewer rejects.${X}"
}

summary() {
  heading "What happened"
  echo
  printf '  %-4s %-48s %-28s %s\n' "" "Request" "Hospital decision" "Left the hospital?"
  printf '  %s\n' "${DIM}──────────────────────────────────────────────────────────────────────────────────────────${X}"
  local row n title decision left colour
  for row in "${SUMMARY[@]}"; do
    IFS='|' read -r n title decision left <<<"$row"
    case $left in no) colour=$R ;; unknown|\?) colour=$A ;; *) colour=$G ;; esac
    printf '  %-4s %-48s %-28s %s%s%s\n' "$n" "$title" "$decision" "$colour" "$left" "$X"
  done
  echo
  local p
  for p in "${PROMISES[@]}"; do ok "$p"; done
  echo
  say "${B}Only the result the hospital approved left the hospital.${X}"
}

preflight() {
  local problem=""
  curl -sf -o /dev/null "$SUBMISSION/health" 2>/dev/null || curl -s -o /dev/null --max-time 5 "$SUBMISSION" || problem="Submission ($SUBMISSION) is not reachable"
  [ -z "$problem" ] && { curl -s -o /dev/null --max-time 5 "$EGRESS" || problem="the Egress API ($EGRESS) is not reachable"; }
  [ -z "$problem" ] && { token >/dev/null 2>&1 || problem="cannot log in as control-admin (Keycloak at http://localhost:8180)"; }
  [ -z "$problem" ] && { egress_token >/dev/null 2>&1 || problem="cannot log in to the Egress app as reviewer"; }
  if [ -n "$problem" ]; then
    printf '%sThe dev stack is not ready: %s.%s\n' "$R" "$problem" "$X" >&2
    printf 'Start it with: docker compose -f dev/federated/docker-compose.yml up -d --build\n' >&2
    exit 1
  fi
  # Reachable but not seeded: name the seed step that has not finished.
  local seed
  seed=$(seed_problem)
  if [ -n "$seed" ]; then
    printf '%sThe dev stack is not seeded: %s.%s\n' "$R" "${seed%|*}" "$X" >&2
    printf 'Run: docker compose -f dev/federated/docker-compose.yml up %s\n' "${seed#*|}" >&2
    exit 1
  fi
}

# --- run --------------------------------------------------------------------------------------------------------------
preflight
opening
run_case 1 "A safe result leaves the hospital" \
  "A researcher asks for the average age in the cohort_2024 dataset." released cohort_2024 mean --column age
run_case 2 "A request for data the hospital does not hold" \
  "A researcher asks to count records in a dataset this hospital does not have." dataset_not_found no_such_dataset count
run_case 3 "The hospital reviewer says no" \
  "A researcher asks to count the records in cohort_2024." rejected_by_reviewer cohort_2024 count
pause "for the summary"
summary

if [ "$FAILURES" -gt 0 ]; then
  printf '\n%s%d case(s) did not end as expected.%s\n' "$R" "$FAILURES" "$X"
  [ "$MODE" = unattended ] && exit 1
fi
exit 0
