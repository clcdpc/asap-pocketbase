#!/usr/bin/env bash
set -euo pipefail

# Pin both release and archive bytes; never execute a mutable download script.
tool_root=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/asap-actionlint.XXXXXXXX")
trap 'rm -rf -- "$tool_root"' EXIT
curl --fail --silent --show-error --location --retry 3 \
  https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz \
  --output "$tool_root/actionlint.tar.gz"
printf '%s  %s\n' \
  '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8' \
  "$tool_root/actionlint.tar.gz" | sha256sum --check --status
tar -xzf "$tool_root/actionlint.tar.gz" -C "$tool_root" actionlint
# ShellCheck/pyflakes are separate tools, unavailable on some clc-linux images.
"$tool_root/actionlint" -shellcheck= -pyflakes=
