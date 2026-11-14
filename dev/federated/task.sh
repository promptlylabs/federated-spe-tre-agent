#!/usr/bin/env bash
# Helper for the local federated stack (dev/federated/docker-compose.yml).
#
#   ./task.sh enqueue <dataset> [operation] [extra args...]   queue a task, prints its id
#   ./task.sh get <task-id>                                   show state, phase, phase times and result location
#   ./task.sh result <task-id>                                download and print the result file
#   ./task.sh run <dataset> [operation] [extra args...]       enqueue, wait until the result waits for output approval
#   ./task.sh approve <task-id>                               approve the result in the Egress app, wait for the release
#   ./task.sh reject <task-id> "<reason>"                     reject the result in the Egress app, wait for the report
#   ./task.sh cancel <task-id>                                cancel the task as an admin, wait for the hospital to confirm
#
# Examples:
#   ./task.sh run cohort_2024 mean --column age --filter sex:eq:F    -> RUNNING, phase waiting_for_output_approval
#   ./task.sh approve <task-id>                                      -> COMPLETE, phase released, result_key <treId>/<taskId>/<sha256>.json
#   ./task.sh reject <task-id> "small cells"                         -> CANCELED, phase rejected_at_output_approval, rejected_by_reviewer
#   ./task.sh run nope count                                         -> CANCELED, phase rejected_by_input_checks, dataset_not_found
#   ./task.sh cancel <task-id>                                       -> CANCELED, phase canceled (review closed, result removed)
set -euo pipefail

. "$(dirname "$0")/lib.sh"

run() {
  local id state
  id=$(enqueue "$@")
  echo "queued $id; waiting for the agent (polls every minute)..."
  state=$(wait_while "$id" 150 queued running) || return 1
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
  get) shift; get "$1" ;;
  result) shift; result "$1" ;;
  run) shift; run "$@" ;;
  approve) shift; decide_and_wait "$1" 1 ;;
  reject) shift; [ -n "${2:-}" ] || { echo "a reason is required: ./task.sh reject <task-id> \"<reason>\"" >&2; exit 1; }; decide_and_wait "$1" 2 "$2" ;;
  cancel) shift; cancel "$1"; echo "cancel sent; waiting for the hospital to confirm..."; wait_while "$1" 150 canceling ;;
  *) sed -n '2,17p' "$0"; exit 1 ;;
esac
