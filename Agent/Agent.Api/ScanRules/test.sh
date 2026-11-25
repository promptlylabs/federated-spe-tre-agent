#!/usr/bin/env bash
# Runs Semgrep's rule tests: every "# ruleid:" / "# ok:" annotation in default.py against default.yaml.
# Uses the Semgrep version pinned in requirements.txt (the one the Agent image installs), offline.
set -euo pipefail
cd "$(dirname "$0")"
[ -f default.yaml ] || { echo "default.yaml not found" >&2; exit 1; }

version=$(sed -n 's/^semgrep==\([0-9][0-9.]*\).*/\1/p' requirements.txt)
[ -n "$version" ] || { echo "requirements.txt has no 'semgrep==<version>' line" >&2; exit 1; }

docker run --rm --network none -v "$PWD:/rules:ro" -w /rules "semgrep/semgrep:$version" \
  semgrep --test --metrics=off --disable-version-check --config default.yaml default.py
