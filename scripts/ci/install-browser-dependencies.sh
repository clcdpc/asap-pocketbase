#!/usr/bin/env bash
set -euo pipefail

# Retry only dependency setup after transient DNS failures. Never retry tests or
# accept a failed install. The workflow bounds both attempts to 25 minutes total.
log=$(mktemp "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/asap-browser-install.XXXXXXXX")
trap 'rm -f -- "$log"' EXIT

for attempt in 1 2; do
  printf 'Installing Chromium and system dependencies (attempt %s/2)\n' "$attempt"
  set +e
  npx --no-install playwright install --with-deps chromium 2>&1 | tee "$log"
  statuses=("${PIPESTATUS[@]}")
  set -e
  # A logging failure must not turn an incomplete install into success.
  if (( statuses[1] != 0 )); then
    exit "${statuses[1]}"
  fi
  status=${statuses[0]}
  if (( status == 0 )); then
    exit 0
  fi
  if (( attempt == 2 || status >= 128 )) ||
      ! grep -Eq 'Temporary failure resolving|Could not resolve host|EAI_AGAIN' "$log"; then
    exit "$status"
  fi
  printf '::warning::Browser dependency installation failed with a transient DNS error; retrying once in 30 seconds.\n'
  sleep 30
done
