#!/usr/bin/env bash
# Helper for the local federated stack (dev/federated/docker-compose.yml).
#
#   ./task.sh enqueue <dataset> <file.py>       queue a study version that runs the Python file on the dataset, prints its id
#   ./task.sh part <version-id> [tre]           print a hospital's task id in the version (default my-tre)
#   ./task.sh study <version-id>                show the version: open, canceling or ended, its outcome, and each hospital's phase
#   ./task.sh get <task-id>                     show a hospital's state, phase, phase times and result location
#   ./task.sh result <task-id>                  download and print the result file
#   ./task.sh run <dataset> <file.py>           enqueue, wait until my-tre's result waits for output approval
#   ./task.sh approve <task-id>                 approve the result in the Egress app, wait for the release
#   ./task.sh reject <task-id> "<reason>"       reject the result in the Egress app, wait for the report
#   ./task.sh cancel <version-id>               cancel the version as an admin, wait for my-tre to confirm
#
# Each enqueue is a new study unless STUDY_ID is set; the hospitals are SITES (default 'my-tre|other-tre'; a study
# needs at least 2). other-tre has no Agent, so its part stays queued.
#
# Examples:
#   ./task.sh run cohort_2024 code/analysis.py  -> RUNNING, phase waiting_for_output_approval
#   ./task.sh approve <task-id>                 -> COMPLETE, phase released, result_key <treId>/<taskId>/<sha256>.json
#   ./task.sh reject <task-id> "small cells"    -> CANCELED, phase rejected_at_output_approval, rejected_by_reviewer
#   ./task.sh run nope code/count_rows.py       -> CANCELED, phase rejected_by_input_checks, dataset_not_found
#   ./task.sh cancel <version-id>               -> CANCELED, phase canceled (review closed, result removed)
set -euo pipefail

. "$(dirname "$0")/lib.sh"

run() {
  local version id state seconds=600
  # A run spans several polls (start, check, finish) and may last up to MaxRunMinutes (5 on this stack).
  version=$(enqueue "$@")
  id=$(part "$version")
  echo "queued version $version, my-tre task $id; waiting for the agent (polls every minute)..."
  state=$(wait_while "$id" "$seconds" queued running) || return 1
  echo "$state"
  if [[ $state == *waiting_for_output_approval* ]]; then
    echo "review it at http://localhost:5280 (reviewer / Reviewer1!), or:"
    echo "  ./task.sh approve $id"
    echo "  ./task.sh reject $id \"<reason>\""
  fi
}

# Decides the task's Egress request, then waits for the Agent's report.
decide_and_wait() {
  decide "$@"
  echo "decision sent; waiting for the agent..."
  wait_while "$1" 150 queued running waiting_for_output_approval
}

case "${1:-}" in
  enqueue) shift; enqueue "$@" ;;
  part) shift; part "$@" ;;
  study) shift; study "$1" ;;
  get) shift; get "$1" ;;
  result) shift; result "$1" ;;
  run) shift; run "$@" ;;
  approve) shift; decide_and_wait "$1" 1 ;;
  reject) shift; [ -n "${2:-}" ] || { echo "a reason is required: ./task.sh reject <task-id> \"<reason>\"" >&2; exit 1; }; decide_and_wait "$1" 2 "$2" ;;
  cancel) shift; cancel "$1"; echo "cancel sent; waiting for my-tre to confirm..."; id=$(part "$1"); wait_while "$id" 150 canceling ;;
  *) sed -n '2,22p' "$0"; exit 1 ;;
esac
