#!/bin/sh
# Usage: runner/test.sh [unit [pytest args] | image]
# With no argument, runs the unit tests and then the image checks.
set -eu
cd "$(dirname "$0")"

IMAGE=python-runner:1

unit() {
  docker build --platform linux/amd64 --target test -t python-runner:test .
  docker run --rm --platform linux/amd64 --network none python-runner:test \
    python3 -m pytest -q -p no:cacheprovider tests "$@"
}

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

# Runs the image locked down the way the pod will run it: no network, read-only root file system, the dataset
# read-only at /in, /outputs writable, and a scratch /tmp (an emptyDir in the pod).
# $1 = directory mounted read-only at /in, $2 = directory mounted at /outputs.
run_locked() {
  in=$1 outputs=$2
  shift 2
  docker run --rm --platform linux/amd64 --network none --read-only --tmpfs /tmp \
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
    "/usr/bin/python3.11 -m pip --version" "/usr/bin/python3.11 -m ensurepip" "micropipenv --version" \
    "uv --version" "uvx --version" "python3 -m virtualenv --version"; do
    if docker run --rm --platform linux/amd64 --network none "$IMAGE" sh -c "$cmd" >/dev/null 2>&1; then
      fail "'$cmd' works in the image"
    fi
  done

  echo "3. runs as user 1001 by default, and as a random user in group 0 (OpenShift)"
  uid=$(docker run --rm --platform linux/amd64 "$IMAGE" id -u)
  [ "$uid" = 1001 ] || fail "runs as user $uid"
  docker run --rm --platform linux/amd64 --network none --user 1000740000:0 \
    -v "$examples:/in:ro" "$IMAGE" \
    sh -c 'run-analysis /in/analysis.py /in/cohort /outputs/result.json && cat /outputs/result.json' \
    | grep -q '"status":"ok"' || fail "a random user in group 0 cannot run the example into /outputs"

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

  echo "7. the advertised libraries import under a read-only root file system"
  mkdir -m 0777 "$tmp/7" "$tmp/7in"
  cp -r "$examples/cohort" "$tmp/7in/cohort"
  cat >"$tmp/7in/analysis.py" <<'EOF'
import matplotlib.pyplot as plt
import numpy
import scipy.stats
import sklearn.linear_model


def site_analysis(df):
    plt.figure()
    return {"n": len(df)}
EOF
  run_locked "$tmp/7in" "$tmp/7" run-analysis /in/analysis.py /in/cohort /outputs/result.json
  grep -q '"status":"ok"' "$tmp/7/result.json" || fail "libraries: $(cat "$tmp/7/result.json")"

  echo "image checks passed"
}

case "${1:-all}" in
  unit) shift; unit "$@" ;;
  image) image ;;
  all) unit && image ;;
  *) echo "usage: $0 [unit [pytest args] | image]" >&2; exit 2 ;;
esac
