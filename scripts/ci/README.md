# .NET/SQL CI performance telemetry

The non-browser test step runs its existing command through
`bash scripts/ci/run-with-performance.sh dotnet test ...`. No test arguments,
filter, minimum count, output streams, environment or working directory change.
Python 3.9+ and its standard library are sufficient on either Ubuntu runner.
The Bash launcher falls back to the command if Python or collector imports are
unavailable. Collector startup, sampling, storage and summary failures warn and
leave the command's exit status intact. The optional collector checks and artifact
upload use `continue-on-error: true`; the actual test step does not.

`performance.py` owns one sampling thread. It forwards SIGINT/SIGTERM to the
command's process group, permits five seconds of graceful termination, then
kills remaining members on cancellation. Signal termination uses the shell
convention `128 + signal`; an interrupted command returning zero also stays
unsuccessful. Normal command exit codes are preserved exactly. Docker clients
have separate process groups, four-second deadlines, and are killed/reaped on
timeout or shutdown. Workload stdout, stderr and stdin are inherited directly.

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

A compact health line is flushed to the Actions log about every minute. The
report is refreshed at that cadence and finalized on exit; a small final extract
goes to the log and the full report to `GITHUB_STEP_SUMMARY`. Upload runs directly
after the test step with `if: always()` and seven-day retention under
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

No SQL queries, synthetic I/O benchmarks, services or monitoring dependencies
are added. The collector does not read command lines/environments, export host
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

## Focused local checks

```sh
python3 -B -m unittest discover -s scripts/ci -p 'test_*.py' -v
python3 scripts/ci/performance.py --output .artifacts/ci-performance/local --interval 1 -- python3 -c 'import time; time.sleep(3)'
bash scripts/ci/run-with-performance.sh python3 -c 'raise SystemExit(23)'
```

Tests use synthetic proc/cgroup/Docker inputs and short subprocesses; Linux
enables signal, device-mapping and helper cleanup checks. No SQL suite or live
provider is needed. Short custom intervals are for local validation only; CI
keeps the default 15-second host and 60-second Docker intervals.
