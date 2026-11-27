#!/bin/sh
# Runs GA4GH's TES compliance suite (ga4gh/openapi-test-runner) against the dev stack's TES API, twice: as published
# ("stock"), and with each submitted task replaced by a valid federated study ("federated"). See README.md.
# Usage, from the repo root with the dev stack running: dev/federated/tes-conformance/run.sh
set -eu
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
git clone -q https://github.com/ga4gh/openapi-test-runner.git "$work/suite"
git -C "$work/suite" checkout -q bc3eb911e877521409348e0225852d072d4de196
# A federated study completes only after hospital reviewers approve it: the suite's waits for COMPLETE would run for an
# hour each, so they are cut to a few minutes (the suite counts neither a pass nor a fail for them; see README.md).
sed -i.bak -e 's/polling_timeout_value: 3600/polling_timeout_value: 60/' -e 's/timeout: 3600/timeout: 180/' "$work"/suite/tests/*.yml
mkdir -p "$work/out"
docker run --rm -v "$work/suite:/suite" -v "$here:/harness:ro" -v "$work/out:/out" python:3.11-slim sh -c '
  pip install -q /suite >/dev/null 2>&1
  cd /suite
  for mode in stock federated; do
    mkdir -p /out/report-$mode
    MODE=$mode CREATED=/out/created-$mode.txt python3 /harness/proxy.py & P=$!; sleep 2
    openapi-test-runner report --server http://127.0.0.1:8000/ --version 1.1.0 --output_path /out/report-$mode > /out/log-$mode.txt 2>&1 || true
    kill $P
  done'
for mode in stock federated; do
  python3 -c 'import json,sys; s=json.load(open(sys.argv[1]))["summary"]; print(sys.argv[2], s)' "$work/out/report-$mode/report.json" "$mode"
done
# Cancel every study the run created: each one is real work for the dev stack's hospitals.
. "$here/../lib.sh"
for id in $(cat "$work"/out/created-*.txt 2>/dev/null | sort -u); do
  curl -s -o /dev/null -X POST "$SUBMISSION/v1/tasks/${id}:cancel" -H "Authorization: Bearer $(token)"
done
echo "Reports: $work/out. The studies the run created are cancelled."
