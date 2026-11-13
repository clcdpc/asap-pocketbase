#!/usr/bin/env bash
# Temporary issue-485 diagnostic branch only. Serial, fresh owned containers.
set -euo pipefail
root="$GITHUB_WORKSPACE/.artifacts/issue485"
mkdir -p "$root"
container=""
cleanup() {
  if [[ -n "$container" ]]; then
    docker logs "$container" > "$trial/sql-container.log" 2>&1 || true
    docker rm --force "$container" >/dev/null
    printf '%s\n' "$container removed" >> "$root/cleanup.txt"
    container=""
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
application=732dbcd973cbbd6eb5cfd4186144a5de2d79cbd5
test -z "$(git diff "$application" -- src database)"
git rev-parse HEAD > "$root/tooling-sha.txt"
printf '%s\n' "$application" > "$root/application-sha.txt"
git diff "$application" --stat > "$root/diagnostic-diff-stat.txt"
git diff "$application" -- src database > "$root/shipped-source-diff.txt"
dotnet --info > "$root/dotnet-info.txt"
uname -a > "$root/uname.txt"
lscpu > "$root/cpu.txt"
free --bytes > "$root/memory-before.txt"
df --block-size=1 > "$root/disk-before.txt"
docker version > "$root/docker-version.txt"
docker info --format '{"cpus":{{.NCPU}},"memory":{{.MemTotal}},"kernel":{{json .KernelVersion}},"os":{{json .OperatingSystem}},"architecture":{{json .Architecture}},"serverVersion":{{json .ServerVersion}}}' > "$root/docker-host.json"
docker pull mcr.microsoft.com/mssql/server:2022-latest
image="$(docker image inspect mcr.microsoft.com/mssql/server:2022-latest --format '{{index .RepoDigests 0}}')"
printf '%s\n' "$image" > "$root/sql-image.txt"
printf '%s\n' "$RUNNER_ENVIRONMENT/$RUNNER_OS/$RUNNER_ARCH" > "$root/runner.txt"
export ASAP_485_DIAGNOSTICS=1 ASAP_485_APPLICATION_SHA="$application"
failed=0
for specification in control-1 baseline-1 trace-1 baseline-2 trace-2 control-2 baseline-3 trace-3 baseline-4 regression; do
  trial="$root/$specification"
  mkdir -p "$trial"
  sql_password="Asap$(openssl rand -hex 32)Aa1!"
  echo "::add-mask::$sql_password"
  export MSSQL_SA_PASSWORD="$sql_password" ASAP_TEST_SQL_SA_PASSWORD="$sql_password"
  container="asap485-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT-$specification"
  docker run --detach --name "$container" --publish 127.0.0.1::1433 --env ACCEPT_EULA=Y --env MSSQL_SA_PASSWORD "$image" >/dev/null
  export ASAP_CI_SQL_CONTAINER="$container"
  address="$(docker port "$container" 1433/tcp)"
  port="${address##*:}"
  [[ "$port" =~ ^[0-9]+$ ]]
  export ASAP_TEST_SQL_CONNECTION_STRING="Server=localhost,$port;Database=master;User ID=sa;Password=$sql_password;Encrypt=True;TrustServerCertificate=True"
  ready=false
  for attempt in {1..45}; do
    if printf '%s\n' "$sql_password" | docker exec --interactive "$container" /bin/sh -c 'IFS= read -r SQLCMDPASSWORD || exit 1; export SQLCMDPASSWORD; exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -l 2 -t 3 -Q "SELECT 1"' >/dev/null 2>&1; then
      ready=true
      break
    fi
    sleep 2
  done
  test "$ready" = true
  docker inspect --format '{"image":{{json .Image}},"started":{{json .State.StartedAt}},"memoryLimit":{{.HostConfig.Memory}},"nanoCpus":{{.HostConfig.NanoCpus}}}' "$container" > "$trial/container.json"
  export ASAP_SEARCH_EVIDENCE_DIR="$trial/search" ASAP_CI_FIXTURE_TIMINGS="$trial/fixture-timings.jsonl"
  export ASAP_485_MODE="${specification%-*}"
  filter='FullyQualifiedName~SubmissionSearchIssue485Diagnostics'
  if [[ "$specification" == regression ]]; then
    filter='FullyQualifiedName~SubmissionSearchHttp|FullyQualifiedName~SubmissionSearchMeasuredSql'
  fi
  date --iso-8601=ns > "$trial/started.txt"
  set +e
  python3 -B scripts/ci/performance.py --output "$trial/resources" --interval 1 --docker-interval 5 --sql-interval 5 -- \
    dotnet tests/Asap.Tests/bin/Release/net10.0/Asap.Tests.dll --no-ansi --progress off --output Detailed \
    --filter "$filter" --minimum-expected-tests 1 --report-trx --report-trx-filename tests.trx --results-directory "$trial/results" > "$trial/test.log" 2>&1
  result=$?
  set -e
  date --iso-8601=ns > "$trial/finished.txt"
  printf '%s\n' "$result" > "$trial/exit-code.txt"
  tail -n 12 "$trial/test.log"
  cleanup
  if [[ "$result" -ne 0 ]]; then failed=1; break; fi
done
printf '%s\n' "$failed" > "$root/failure.txt"
exit "$failed"
