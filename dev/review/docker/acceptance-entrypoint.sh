#!/bin/bash
set -euo pipefail
children=()
cleanup() {
    trap - EXIT INT TERM
    for child in "${children[@]}"; do kill "$child" 2>/dev/null || true; done
    wait 2>/dev/null || true
}
trap cleanup EXIT
trap 'exit 2' INT TERM

# Trust only this environment's public root. Private keys/SQL config/Docker
# socket are never available to this container. NSS is local to this one-off run.
certutil -N --empty-password -d sql:"$HOME/.pki/nssdb"
certutil -A -d sql:"$HOME/.pki/nssdb" -n asap-review -t 'C,,' -i /review/tls-public/root.crt
node /runner/localhost-forwarder.cjs & children+=("$!")
Xvfb :99 -screen 0 1440x1000x24 -nolisten tcp -ac >/tmp/display.log 2>&1 & children+=("$!")
for attempt in {1..100}; do
    [[ -S /tmp/.X11-unix/X99 ]] && break
    sleep 0.1
done
[[ -S /tmp/.X11-unix/X99 ]]
fluxbox >/tmp/window-manager.log 2>&1 & children+=("$!")
# Raw VNC is container-local; websockify requires a random 256-bit per-run token.
# Authentication traffic and viewer access logs remain in ephemeral /tmp only.
x11vnc -display :99 -localhost -rfbport 5900 -forever -shared -nopw >/tmp/vnc.log 2>&1 & children+=("$!")
websockify --web /usr/share/novnc --token-plugin TokenFile \
    --token-source /run/review/viewer/tokens 6080 >/tmp/viewer.log 2>&1 & children+=("$!")
for attempt in {1..100}; do
    if node -e "require('http').get('http://127.0.0.1:6080/vnc.html', r => process.exit(r.statusCode === 200 ? 0 : 1)).on('error', () => process.exit(1))"; then
        if node -e "require('net').connect(5900, '127.0.0.1').once('connect', () => process.exit(0)).once('error', () => process.exit(1))"; then
            touch /tmp/viewer-ready
            break
        fi
    fi
    sleep 0.1
done
[[ -f /tmp/viewer-ready ]]
node - <<'NODE'
const fs = require('node:fs/promises');
(async () => {
    for (let attempt = 0; attempt < 150; attempt++) {
        try {
            const proof = JSON.parse(await fs.readFile('/run/review/control/proof.json', 'utf8'));
            if (proof.acceptanceRunning === true && Date.now() - Date.parse(proof.observedUtc) < 15000) return;
        } catch {}
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    process.exitCode = 2;
})();
NODE
node /runner/tests/acceptance/run.cjs "$@" & runner=$!; children+=("$runner")
set +e
wait "$runner"
code=$?
exit "$code"
