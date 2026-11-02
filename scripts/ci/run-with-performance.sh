#!/usr/bin/env bash
# Keep the workload's streams, arguments, environment and shell exit status intact.
if command -v python3 >/dev/null 2>&1; then
  collector="$(dirname -- "${BASH_SOURCE[0]}")/performance.py"
  # Check interpreter/import/syntax availability before handing it the workload.
  if python3 -c 'import pathlib,runpy,sys; sys.path.insert(0,str(pathlib.Path(sys.argv[1]).resolve().parent)); runpy.run_path(sys.argv[1], run_name="ci_performance_preflight")' "$collector" >/dev/null 2>&1; then
    output=()
    if [ -n "${ASAP_CI_PERFORMANCE_OUTPUT:-}" ]; then
      output=(--output "$ASAP_CI_PERFORMANCE_OUTPUT")
    fi
    exec python3 "$collector" "${output[@]}" -- "$@"
  fi
fi
echo '::warning::Performance collector/Python 3 is unavailable; running tests without telemetry.' >&2
exec "$@"
