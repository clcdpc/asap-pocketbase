#!/usr/bin/env bash
set -euo pipefail

# Scope the workaround to this install. Do not edit DNS, global apt sources or
# trust settings. An optional sources path supports isolated setup tests.
if (( $# > 1 )); then
  printf 'Usage: %s [ubuntu.sources]\n' "$0" >&2
  exit 2
fi
source_file=${1:-/etc/apt/sources.list.d/ubuntu.sources}
setup=$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/asap-browser-install.XXXXXXXX")
cleanup() {
  local status=$?
  sudo -- rm -rf -- "$setup" || true
  exit "$status"
}
trap cleanup EXIT
chmod 755 "$setup"
mkdir -m 755 "$setup/lists"

python3 - "$source_file" "$setup/ubuntu.sources" <<'PY'
from pathlib import Path
import re
import sys

source = Path(sys.argv[1]).read_text()
uris = re.findall(r"^URIs:\s*(\S[^\n]*)$", source, re.MULTILINE)
allowed = {"mirror+file:/etc/apt/apt-mirrors.txt", "http://azure.archive.ubuntu.com/ubuntu"}
keys = re.findall(r"^Signed-By:\s*(\S[^\n]*)$", source, re.MULTILINE)
suites = re.findall(r"^Suites:\s*(\S[^\n]*)$", source, re.MULTILINE)
if (not uris or any(uri.strip().rstrip('/') not in allowed for uri in uris)
        or len(keys) != len(uris) or any(key.strip() != '/usr/share/keyrings/ubuntu-archive-keyring.gpg' for key in keys)
        or len(suites) != len(uris) or any(not suite.split() or not set(suite.split()) <= {
            'noble', 'noble-updates', 'noble-backports', 'noble-security'} for suite in suites)):
    raise SystemExit('Unsupported Ubuntu sources; refusing to change dependency source or trust policy.')
Path(sys.argv[2]).write_text(re.sub(r'^URIs:[^\n]*$', 'URIs: https://archive.ubuntu.com/ubuntu/', source, flags=re.MULTILINE))
PY
chmod 644 "$setup/ubuntu.sources"

# These are public-host probes, not a dump of proxy settings or environment
# variables. They distinguish host resolution from apt mirror transport failure.
probe() {
  if "$@"; then
    return 0
  else
    local status=$?
    if (( status >= 128 )); then exit "$status"; fi
    printf '::warning::Browser setup diagnostic failed (exit %s).\n' "$status"
  fi
}
for host in azure.archive.ubuntu.com archive.ubuntu.com security.ubuntu.com; do
  printf 'DNS diagnostic: %s\n' "$host"
  probe timeout 10 getent ahostsv4 "$host"
done
printf 'HTTPS diagnostic: direct Ubuntu archive InRelease\n'
probe curl --head --fail --silent --show-error --connect-timeout 5 --max-time 15 \
  --output /dev/null https://archive.ubuntu.com/ubuntu/dists/noble/InRelease

cat > "$setup/apt-get" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
exec /usr/bin/apt-get \
  -o "Dir::Etc::sourcelist=$ASAP_BROWSER_APT_ROOT/ubuntu.sources" \
  -o Dir::Etc::sourceparts=- \
  -o "Dir::State::lists=$ASAP_BROWSER_APT_ROOT/lists" \
  -o APT::Update::Error-Mode=any \
  "$@"
SH
chmod 755 "$setup/apt-get"
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
node=$(command -v node)
printf 'Installing complete Playwright system dependencies from direct signed Ubuntu sources\n'
sudo -- env "PATH=$setup:$PATH" "ASAP_BROWSER_APT_ROOT=$setup" \
  "$node" "$repo/node_modules/playwright/cli.js" install-deps chromium

# Keep the browser cache owned by the runner, not root. Never retry tests or
# continue after a failed OS dependency installation.
printf 'Installing Chromium as the runner user\n'
npx --no-install playwright install chromium
