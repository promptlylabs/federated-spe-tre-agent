#!/bin/sh
# Usage: runner/test.sh [unit [pytest args] | image]
# With no argument, runs the unit tests and then the image checks.
set -eu
cd "$(dirname "$0")"

IMAGE=5s-tes/python-runner:1

unit() {
  docker build --platform linux/amd64 --target test -t 5s-tes/python-runner:test .
  docker run --rm --platform linux/amd64 --network none 5s-tes/python-runner:test \
    python3 -m pytest -q -p no:cacheprovider tests "$@"
}

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

# Runs the image locked down the way the Agent will run it.
# $1 = directory mounted read-only at /in, $2 = directory mounted at /outputs.
run_locked() {
  in=$1 outputs=$2
  shift 2
  docker run --rm --platform linux/amd64 --network none --read-only \
    -v "$in:/in:ro" -v "$outputs:/outputs" "$IMAGE" "$@"
}

image() {
  docker build --platform linux/amd64 -t "$IMAGE" .
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  examples=$(pwd)/examples

  echo "1. the example runs locked down and writes an ok result"
  mkdir -m 0777 "$tmp/1"
  run_locked "$examples" "$tmp/1" run-analysis /in/analysis.py /in/cohort /outputs/result.json \
    || fail "run-analysis exited non-zero"
  grep -q '"status":"ok"' "$tmp/1/result.json" || fail "result is not ok: $(cat "$tmp/1/result.json")"

  echo "2. pip and ensurepip are gone"
  for cmd in "pip --version" "pip3 --version" "python3 -m pip --version" "python3 -m ensurepip" \
    "/usr/bin/python3.11 -m pip --version" "/usr/bin/python3.11 -m ensurepip" "micropipenv --version"; do
    if docker run --rm --platform linux/amd64 --network none "$IMAGE" sh -c "$cmd" >/dev/null 2>&1; then
      fail "'$cmd' works in the image"
    fi
  done

  echo "3. runs as user 1001"
  uid=$(docker run --rm --platform linux/amd64 "$IMAGE" id -u)
  [ "$uid" = 1001 ] || fail "runs as user $uid"

  echo "4. Python is 3.11"
  docker run --rm --platform linux/amd64 "$IMAGE" python3 -c 'import sys; assert sys.version_info[:2] == (3, 11)' \
    || fail "Python is not 3.11"

  echo "5. code cannot open a network connection"
  mkdir -m 0777 "$tmp/5" "$tmp/5in"
  cp -r "$examples/cohort" "$tmp/5in/cohort"
  cat >"$tmp/5in/analysis.py" <<'EOF'
import socket


def site_analysis(df):
    socket.create_connection(("1.1.1.1", 53), timeout=2)
    return {"n": len(df)}
EOF
  run_locked "$tmp/5in" "$tmp/5" run-analysis /in/analysis.py /in/cohort /outputs/result.json
  grep -q '"code":"code_error"' "$tmp/5/result.json" || fail "network: $(cat "$tmp/5/result.json")"

  echo "6. code cannot change the dataset"
  mkdir -m 0777 "$tmp/6" "$tmp/6in"
  cp -r "$examples/cohort" "$tmp/6in/cohort"
  cat >"$tmp/6in/analysis.py" <<'EOF'
def site_analysis(df):
    with open("/in/cohort/cohort.csv", "w") as f:
        f.write("age,sex\n")
    return {"n": len(df)}
EOF
  run_locked "$tmp/6in" "$tmp/6" run-analysis /in/analysis.py /in/cohort /outputs/result.json
  grep -q '"code":"code_error"' "$tmp/6/result.json" || fail "dataset write: $(cat "$tmp/6/result.json")"
  cmp -s "$examples/cohort/cohort.csv" "$tmp/6in/cohort/cohort.csv" || fail "the dataset was changed"

  echo "image checks passed"
}

case "${1:-all}" in
  unit) shift; unit "$@" ;;
  image) image ;;
  all) unit && image ;;
  *) echo "usage: $0 [unit [pytest args] | image]" >&2; exit 2 ;;
esac
