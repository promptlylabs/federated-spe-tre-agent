#!/bin/sh
set -eu
cd "$(dirname "$0")"

unit() {
  docker build --platform linux/amd64 --target test -t 5s-tes/python-runner:test .
  docker run --rm --platform linux/amd64 --network none 5s-tes/python-runner:test \
    python3 -m pytest -q -p no:cacheprovider tests "$@"
}

case "${1:-unit}" in
  unit) [ $# -gt 0 ] && shift; unit "$@" ;;
  *) unit "$@" ;;
esac
