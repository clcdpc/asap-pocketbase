# .NET/SQL CI performance telemetry

Ready PR, push and manual CI suites can reuse verified original executions;
draft PRs run fast build/unit/frontend checks. See
[CI policy and test reuse](test-reuse.md) for the draft/ready matrix, required
full gate, PR cancellation groups, fingerprints, trust boundaries and forced
full CI. Performance collection below still wraps every fresh .NET/SQL
execution; reused runs link to original test timing instead of inventing samples.

The non-browser test step runs its existing command through
`bash scripts/ci/run-with-performance.sh dotnet test ...`. The existing filter,
minimum count (1439), configuration, no-build flag, streams, working directory
and exit behavior are preserved. Only TRX reporter arguments and the opt-in
`ASAP_CI_FIXTURE_TIMINGS` output path are added to the workload step.
Python 3.9+ and its standard library are sufficient on either Ubuntu runner.
The Bash launcher falls back to the command if Python or collector imports are
unavailable. Collector startup, sampling, storage and summary failures warn and
leave the command's exit status intact. The optional collector checks and artifact
upload use `continue-on-error: true`; the actual test step does not.

`performance.py` retains its guest/Docker sampling thread and starts a separate
SQL DMV thread so SQL cannot delay guest samples. It forwards SIGINT/SIGTERM to the
command's process group, permits five seconds of graceful termination, then
kills remaining members on cancellation. Signal termination uses the shell
convention `128 + signal`; an interrupted command returning zero also stays
unsuccessful. Normal command exit codes are preserved exactly. Docker clients
have separate process groups, four-second deadlines, and are killed/reaped on
timeout or shutdown. SQL calls have an eight-second client deadline, seven-second
in-container timeout (one-second kill grace), two-second login/three-second query
timeouts, and one-second SQL lock timeout. Both threads stop on shutdown; no final
SQL query is forced. Workload stdout, stderr and stdin are inherited directly.

## Output and cadence

The ignored `.artifacts/ci-performance/` directory contains:

- `environment.json`: UTC start, validated GitHub run/attempt/SHA, runner type,
  OS/kernel/architecture, CPU model/logical count/affinity, guest RAM,
  classified virtualization indicators, root/workspace filesystem type, block
  device and space, visible cgroup limits, Docker version and selected SQL limits.
- `samples.jsonl`: flushed host samples at startup, about every 15 seconds and
  shutdown; separate SQL observations about every 60 seconds. Timestamps are UTC;
  interval/rate calculations use the monotonic clock. A final partial line can be
  ignored. Missing values are absent or null, never fabricated as zero activity.
- `summary.md`: measured command duration and status, interval-weighted averages
  and sampled peaks, operation-weighted disk await, swap/fault totals, cgroup
  throttling/events, pressure and SQL observations, availability and limitations.
- `test-results.trx`: the existing invocation's native MTP TRX report.
- `test-timings.md`: 25 slowest results, class totals, median/p90/p95, summed
  execution time, wall-time coverage/concurrency and gaps, with UTC timestamps.
- `sql-waits.jsonl`: complete nonzero raw counters and interval deltas, idle
  classification, diagnostic family and reset/baseline reasons.
- `sql-io.jsonl`: raw per-file operations, bytes, stalls, deltas/latencies,
  database/file categories and hashed incarnation identities.
- `sql-activity.jsonl`: aggregate requests/current waits/blocking, scheduler
  queues/workers and SQL process memory indicators; no query text or session IDs.
- `fixture-timings.jsonl`: coarse test-infrastructure spans and success flags;
  no database names, SQL statements or configuration values.
- `diagnostic-summary.md`: standalone combined test, SQL, fixture and existing
  resource evidence, UTC correlation windows, limitations and hypotheses.

A compact health line is flushed to the Actions log about every minute, including
the last SQL DMV section availability (out of seven) and collection duration. The
reports are refreshed at that cadence and finalized on exit; a small final extract
goes to the log. An independent `always()` step rebuilds reports from available
evidence after the test/collector exits and appends the consolidated report to
`GITHUB_STEP_SUMMARY`. Upload follows with `if: always()` and seven-day retention under
`ci-performance-<run-id>-<attempt>`. GitHub may prevent upload/finalization after
a hard job timeout or SIGKILL; minute logs and already-flushed samples are the
remaining evidence. No telemetry can be recovered after destruction of an
unuploaded disposable VM.

## Measurement definitions

- Guest CPU percentages use the first eight `/proc/stat` counter deltas.
  Busy = user + nice + system + irq + softirq; idle, I/O wait and steal are
  separate percentages of the same interval total. Guest/guest_nice are already
  included in user/nice. Decreases (including the kernel's approximate I/O wait)
  invalidate that interval. Load averages and CPU counts provide context.
- Memory used = total - available, not total - free. Swap capacity/use comes
  from `/proc/meminfo`; swap traffic uses `/proc/vmstat` page deltas times the
  guest page size. Major page faults are also interval deltas/rates.
- Pressure includes the kernel's 10/60/300-second percentages and a separate
  interval stall percentage from the cumulative microsecond counter.
- Disk sectors always mean 512 bytes. Reads/writes and bytes are divided by
  interval seconds; await = delta read/write milliseconds / completed operations.
  Busy = delta I/O milliseconds / interval milliseconds; average queue depth =
  delta weighted I/O milliseconds / interval milliseconds. In-flight I/O is a
  gauge. Resets or newly appearing counters establish a new baseline.
- Root/workspace backing partitions map to their whole guest device. Other
  independent top-level guest devices are included for Docker storage; loop,
  RAM and partition devices and lower stacked layers are excluded. Devices are
  never summed. Overlay, remote Docker storage, container mounts and nested
  virtual disks may prevent attribution to workspace/SQL/physical storage.
- Visible cgroup v2 leaf and ancestor levels have numeric labels, not exported
  paths. CPU quota, effective cpuset, usage, throttled periods/time, memory
  current/high/max, swap current/max and memory events are recorded. `max` means
  unlimited; missing controllers are marked unavailable. The most restrictive
  visible ancestor can govern the workload. Cgroup v1 and hidden ancestors are
  explicitly unsupported/unavailable. The runner cgroup may exclude SQL.
- Docker sampling targets only `asap-sql` with `stats --no-stream` and selected
  `inspect` fields: state/start time, CPU quota/period (microseconds), NanoCpus/cpuset, memory/swap
  limit and block-I/O weight. CPU/memory are sparse observations; SQL CPU mean
  is the mean of observations, not a full-run average. Docker's memory display
  subtracts cache. CLI byte quantities are rounded; block rates/totals use deltas
  between observations of the same container start, omitting resets/restarts.
  Configured zero limits mean Docker imposed no explicit limit, while stats may
  display guest capacity as the effective limit.
- Process-name groups (`dotnet`, `sqlservr`, `testhost`/`Asap.Tests`) use stat
  counters matched by PID and start time. CPU uses clock ticks and elapsed time;
  100% means one logical CPU, so process/cgroup/container CPU can exceed 100%.
  RSS is summed pages, which can double-count shared memory. Exiting/short-lived
  processes between samples can be missed; these are guest-wide name groups.

No synthetic I/O benchmarks, tracing, services or monitoring dependencies
are added. The collector does not read process command lines/environments, export host
names, mount/cgroup/workspace paths, emit raw CLI errors, or inspect container
configuration/environment. Exceptions export their type only. Metadata is an
allow-list; the workload itself retains its existing output policy.

Guest measurements cannot independently establish Windows physical disk latency
or Hyper-V scheduling contention. Zero steal does not exclude host contention.
Compare equivalent runs of the same test partition: CPU busy plus pressure and
cgroup throttling; available memory plus swap/major faults and memory pressure;
disk await/queue/throughput plus I/O wait/pressure; SQL CPU/memory plus its configured
limits. Correlation suggests the next measurement; it does not establish cause.

Kernel definitions: [CPU/memory/process counters](https://docs.kernel.org/filesystems/proc.html),
[disk statistics](https://docs.kernel.org/admin-guide/iostats.html),
[pressure](https://docs.kernel.org/accounting/psi.html),
[cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html).
Docker's [stats documentation](https://docs.docker.com/reference/cli/docker/container/stats/)
describes the CLI's memory and counter presentation.

## Native test reporting and fixtures

Verified against **installed MSTest.Sdk 4.4.0 / Microsoft.Testing.Platform 2.4.0**
application help before changing the workflow, then exercised on two existing
tests using the actual .NET 10 test driver:

```sh
dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --no-build --minimum-expected-tests 2 --filter 'FullyQualifiedName~PublicPatronLinkBuilderTests' --report-trx --report-trx-filename test-results.trx --results-directory .artifacts/ci-performance/reporter-validation
```

CI uses those same three reporter options with its original filter/minimum.
There is no second test invocation and no VSTest `--logger trx` option.
Percentiles use linear interpolation. Framework durations and UTC spans can
differ because of setup/overhead. Summed execution times and class totals may
overlap; the report separately unions intervals and calculates peak concurrency.
Completion gaps show recorded active-test coverage. Uncovered/completion gaps
of at least five seconds do not prove idle CPU. Missing timestamps are not guessed.
Completed elements from partial XML and complete JSONL lines remain usable.
MTP may only persist TRX on graceful exit; hard termination may leave no TRX.

The test-only `FixtureTiming` helper is disabled without its output variable.
It wraps shared baseline/journey initialization, their initial DACPAC deployments
(including database creation), Hangfire setup, the shared library seed and the
migration tests' DACPAC helper. These operations issue exactly the same SQL.
Only explicit completion sets `succeeded=true`; failed/cancelled scopes retain
false. Writes are serialized and I/O errors cannot replace fixture exceptions.
Nested spans must not be added to wall time. Scattered per-test seeds, repeated
upgrade deployments and migration subprocess internals remain individually
unmeasured to avoid invasive infrastructure changes.

## SQL DMV snapshots

One fixed, read-only `sql-diagnostics.sql` batch runs approximately every 60
seconds plus collection time, only via `docker exec` into **asap-sql** at
localhost/master. No server/connection-string override or remote fallback exists.
The existing ephemeral SA password travels through stdin into `SQLCMDPASSWORD`
inside the container, never through arguments or telemetry. Only SELECT/SET
statements run; wait statistics are never cleared. No resource allocation changes.
The SQL bootstrap/readiness steps also forward passwords through environment
inheritance/stdin, rather than secret-bearing Docker/sqlcmd arguments.

Each JSON section has completion markers: completed sections survive a later
timeout, unavailable DMV or permission error. Missing sections are explicit.
CLI errors are discarded; Python exports only numeric fields, validated wait
types, categories, UTC times and hashed identities. SQL metadata never includes
absolute file paths, database/server names, query text/parameters/results,
connection strings, session IDs or patron data. Native TRX retains the framework's
usual synthetic test output, runner identity and path metadata; it is not an
anonymized SQL report.

- Wait task count, total milliseconds and signal milliseconds use deltas from
  complete snapshots of the same hashed engine start. First samples are baselines.
  Newly nonzero waits use zero only after a complete previous snapshot. Any
  observed counter decrease/disappearance invalidates the whole wait interval.
  Missing engine identity prevents deltas. Resets that overtake prior counters
  between samples cannot always be detected. Background/idle waits are omitted
  only from rankings; raw counters/classification remain. Wait time includes
  signal time, accumulates across workers and can exceed wall time.
- File identity hashes database GUID/creation timestamp, file GUID/create LSN and
  open handle; numeric IDs alone are insufficient. New/reopened files, missing
  identity, engine restarts or decreasing counters/sample clocks establish fresh
  baselines. Deleted files are evicted after complete snapshots. Failed samples
  can extend valid intervals but cannot bridge unidentified incarnations. User
  files without a database GUID have no deltas. Categories distinguish tempdb,
  system, synthetic ASAP tests, other databases and data/log files. Latency is
  delta stall / delta operations; aggregates are operation-weighted. Peaks are
  per-file interval means, not individual-I/O percentiles. Short-lived databases
  and work after the last sample can be missed.
- Activity aggregates user requests, excluding the diagnostic session.
  `active_requests` includes running/runnable/waiting; separate fields show state.
  Blocking counts positive blocker IDs; maximum duration is the current request
  wait, not the full episode. Negative IDs/changing waits are a gap. Visible online
  schedulers expose runnable tasks, work queues and workers. Process-memory use
  and low-memory flags come from SQL; CPU reuses existing guest/Docker collectors.
  Sequential DMV sections are not an atomic snapshot.

Rows include host sample start/end UTC, SQL UTC, collection duration, engine epoch
and each delta's interval start/length. UTC correlations weight guest averages by
interval overlap. Clock differences, sparse observations and different SQL/guest
I/O layers limit attribution. Raw counters permit independent review.

Definitions: [SQL waits](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-os-wait-stats-transact-sql?view=sql-server-ver16),
[file counters](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-io-virtual-file-stats-transact-sql?view=sql-server-ver16),
[file identity metadata](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-master-files-transact-sql?view=sql-server-ver16).

## Optional Windows host monitoring

Before CI, run locally in PowerShell 7 from this branch:

```powershell
pwsh -File ./scripts/ci/Watch-HyperVPerformance.ps1 -SampleIntervalSeconds 5 -DurationSeconds 7200
```

The standalone script discovers accessible paths before sampling: Hyper-V virtual
processor run time, wait/contention per dispatch and dispatches; virtual-storage
throughput/operations/latency; physical-disk latency/queue/throughput; memory and
CPU queue. Unsupported/localized/inaccessible sets appear in `report.json`.
Invalid statuses/nonfinite values stay blank. Cooked values retain native units
and counter types (PhysicalDisk Avg. Disk sec/* is seconds); Hyper-V definitions
depend on Windows version.

CSV/JSON stream to `%LOCALAPPDATA%/AsapDiagnostics/hyperv-<UTC>/`, or a new explicit
`-OutputDirectory`. Ctrl+C finalizes the report and retains completed samples.
No network, services, permissions, resource metering or VM changes are introduced.
Run the same command separately on the outer physical host where practical.
**Keep reports local**: counter paths/instances can identify infrastructure; CI
never uploads them. Synchronize UTC clocks. VM-to-physical-volume mapping is not
automatically inferred. See [Microsoft's Hyper-V measurement guidance](https://learn.microsoft.com/en-us/windows-server/administration/performance-tuning/role/hyper-v-server/detecting-virtualized-environment-bottlenecks).

## Focused local checks

```sh
python3 -B -m unittest discover -s scripts/ci -p 'test_*.py' -v
pwsh -NoProfile -File scripts/ci/Test-HyperVPerformance.ps1
python3 scripts/ci/performance.py --output .artifacts/ci-performance/local --interval 1 -- python3 -c 'import time; time.sleep(3)'
python3 -B scripts/ci/diagnostic_report.py --output .artifacts/ci-performance/local
bash scripts/ci/run-with-performance.sh python3 -c 'raise SystemExit(23)'
```

Tests use synthetic proc/cgroup/Docker/SQL DMV/TRX inputs and short subprocesses.
Linux enables signals, device mapping and helper cleanup. SQL checks cover resets,
larger counters after restarts, reused IDs/handles, deletion, missing identities,
credentials/DMVs, partial output, idle filtering and weighted latency. Report
checks cover overlap, gaps, UTC correlation and missing evidence. The PowerShell
check mocks counters; no Hyper-V access or Pester is needed. No full SQL suite or
live provider is needed. Short intervals are for local validation only; CI keeps
15-second guest and 60-second Docker/SQL defaults. Configure independently with
`performance.py --interval N --docker-interval N --sql-interval N -- ...`.

Validated October 29, 2026: all 52 Python checks pass on Linux (no skips);
PowerShell mock-counter checks, real local counters and Ctrl+C partial-report
retention pass; Release
build passes without warnings/errors. Native reporting passed the two-test
unit selection above. A separate disposable SQL Server 2022 WSLC container
passed every live DMV section, file creation/deletion/recreated identity and
sanitized-output checks, plus two focused existing SQL/journey tests exercising
the initialization/seed spans. The Windows-built fixture manifest was adapted
only in ignored build output for that Linux validation and restored afterward.
Five live SQL snapshots took 0.070 s mean / 0.074 s peak in that idle validation;
this is a local observation, not a loaded-CI overhead guarantee. A focused .NET
harness checked opt-in behavior, UTC/durations, cancellation flags and preservation
of original exceptions with a broken timing sink. No full suite was run locally.
