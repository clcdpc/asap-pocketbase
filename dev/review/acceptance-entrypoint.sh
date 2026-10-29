#!/bin/bash
set -euo pipefail
umask 077
children=()
runner_finished=false
blocker="Acceptance startup did not complete."

start() {
    local log=$1
    shift
    setsid "$@" >"/review/logs/$log.log" 2>&1 &
    task=$!
    children+=("$task")
}
cleanup() {
    local code=$?
    trap - EXIT INT TERM
    set +e
    for child in "${children[@]}"; do kill -TERM -- "-$child" 2>/dev/null; done
    # SQL and Chromium normally exit promptly. Bound shutdown even after a crash.
    local deadline=$((SECONDS + 10))
    while (( SECONDS < deadline )); do
        local alive=false
        for child in "${children[@]}"; do
            if kill -0 -- "-$child" 2>/dev/null; then alive=true; fi
        done
        if [[ $alive == false ]]; then break; fi
        sleep 0.2
    done
    for child in "${children[@]}"; do kill -KILL -- "-$child" 2>/dev/null; done
    wait 2>/dev/null
    if [[ $runner_finished == false ]]; then
        code=2
        node /review/runtime.cjs blocked "$blocker"
    fi
    node /review/runtime.cjs diagnostics "$code"
    exit "$code"
}
trap cleanup EXIT
trap 'blocker="Acceptance run interrupted."; exit 2' INT TERM

# Wait for a finite task while also detecting loss of an essential child.
wait_task() {
    local expected=$1 finished code
    set +e
    wait -n -p finished "${children[@]}"
    code=$?
    set -e
    if [[ ${finished:-} != "$expected" || $code != 0 ]]; then return 1; fi
    local remaining=()
    for child in "${children[@]}"; do
        if [[ $child != "$expected" ]]; then remaining+=("$child"); fi
    done
    children=("${remaining[@]}")
}
check_children() {
    for child in "${children[@]}"; do
        if ! kill -0 "$child" 2>/dev/null; then return 1; fi
    done
}

mkdir -p /artifacts /review/logs
node /review/runtime.cjs prepare "$@"
source /review/runtime.env
export REVIEW_PROJECT_NAME="asap-acceptance-${REVIEW_RUN_ID}"
export REVIEW_SQL_SERVER=localhost,1433 REVIEW_DATABASE_NAME=AsapReview
export REVIEW_SA_PASSWORD
export REVIEW_DACPAC_PATH=/app/Database/Asap.Database.dacpac
export REVIEW_HANGFIRE_SCRIPT=/app/Database/Hangfire/1.8.25/install.sql

echo 'Starting disposable SQL Server 2022...'
blocker="SQL Server did not accept authenticated connections."
start sql env ACCEPT_EULA=Y MSSQL_PID=Developer MSSQL_MEMORY_LIMIT_MB=2048 \
    MSSQL_SA_PASSWORD="$REVIEW_SA_PASSWORD" /opt/mssql/bin/sqlservr
deadline=$((SECONDS + 120))
until SQLCMDPASSWORD="$REVIEW_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost,1433 -U sa -C -l 2 -b -Q 'SET NOCOUNT ON; SELECT 1' >/dev/null 2>&1; do
    check_children
    if (( SECONDS >= deadline )); then exit 2; fi
    sleep 1
done

echo 'Deploying DACPAC, Hangfire and deterministic fixtures...'
blocker="Review database initialization failed. See initializer and SQL diagnostics."
start initializer timeout --kill-after=5s 300s dotnet /review/init/Asap.ReviewInit.dll
wait_task "$task"
unset REVIEW_SA_PASSWORD

blocker="Review Data Protection setup failed."
start bootstrap dotnet /review/bootstrap/Asap.ReviewBootstrap.dll
wait_task "$task"
export ASPNETCORE_Kestrel__Certificates__Default__Path=/review/tls/localhost.pfx
export ASPNETCORE_Kestrel__Certificates__Default__Password="$(cat /review/tls/password.txt)"
export NODE_EXTRA_CA_CERTS=/review/tls-public/root.crt
export DOTNET_ADDITIONAL_DEPS=/review/providers/Asap.ReviewProviders.deps.json
echo 'Starting ASAP in Testing at https://localhost:8080...'
blocker="ASAP did not become ready. See web diagnostics."
cd /app
start web dotnet /app/Asap.Web.dll
unset ASPNETCORE_Kestrel__Certificates__Default__Password DOTNET_ADDITIONAL_DEPS
deadline=$((SECONDS + 120))
until node /review/runtime.cjs health; do
    check_children
    if (( SECONDS >= deadline )); then exit 2; fi
    sleep 1
done

blocker="Chromium virtual display or local certificate trust could not start."
mkdir -p "$HOME/.pki/nssdb"
certutil -N --empty-password -d "sql:$HOME/.pki/nssdb" >/dev/null 2>&1
certutil -A -d "sql:$HOME/.pki/nssdb" -n asap-review -t 'C,,' -i "$NODE_EXTRA_CA_CERTS"
start display Xvfb :99 -screen 0 1440x1000x24 -nolisten tcp -ac
deadline=$((SECONDS + 15))
until [[ -S /tmp/.X11-unix/X99 ]]; do
    check_children
    if (( SECONDS >= deadline )); then exit 2; fi
    sleep 0.1
done

echo 'Running acceptance with synthetic local authentication...'
blocker="An essential process stopped during browser acceptance."
start playwright node /runner/tests/acceptance/run.cjs "$@"
runner=$task
set +e
wait -n -p finished "${children[@]}"
code=$?
set -e
if [[ ${finished:-} != "$runner" || $code -gt 2 ]]; then exit 2; fi
runner_finished=true
cat /review/logs/playwright.log
exit "$code"
