# Temporary issue 485 investigation

Application baseline: `732dbcd973cbbd6eb5cfd4186144a5de2d79cbd5`.
Only test/diagnostic/workflow files differ. This branch is not intended to merge.
Its sole manually dispatched hosted job cannot deploy or invoke a self-hosted
runner. The original workflow path is retained to permit branch-ref dispatch
without changing main. Do not create a PR or merge as part of this investigation.

The runner builds once in Release, pins one SQL Server 2022 image digest, and
executes ten serial fresh-container trials: four baseline, two reduced-detail
controls, three heavyweight traces, and one original-search regression suite.
Each trial launches a fresh test process with a fresh DACPAC database and the
retained 10,035-title/2,003-copy synthetic seed. A process runs two fresh HTTP
application hosts; each host runs the seven retained classes in three rounds,
25-row pages. The first search follows host/auth/fixture initialization; it is
not total process startup or cold SQL. Seeding and state-hash reads warm SQL
buffers. There is no cache flush, SQL hint, query rewrite or plan replay in the
diagnostic trials. The unmodified original measured regression separately retains
its existing supplemental warm-page replay.

All trials retain first/warm HTTP timings, command text/typed synthetic parameters,
EF execute-reader duration and command-to-reader-disposal wall time. Baseline and
trace additionally collect SqlClient connection statistics and EF compilation/
context event timestamps. Those callbacks, allocations and SqlClient statistics
are instrumentation overhead; controls omit those details but retain command
capture/timing and outside-request process snapshots. Controls and baseline are
independent trials, not a precise subtraction of overhead. JSON/SQL file writes,
state snapshots and cached-plan retrieval are outside the HTTP timer.

Trace trials add database-ID-filtered Extended Events on the owned disposable
server: RPC/statement completion, query-post-compilation showplan and actual
query-post-execution showplan. This heavyweight observer can increase execution
cost; trace timings must not replace baseline budget observations. XE files are
flushed by stopping the session, read to individual XML files and the session is
dropped before the fixture/database cleanup. Event loss counters are retained.
These plans come from actual first executions, not query replay. Compile metadata
on a later reused plan does not mean it was compiled again; post-compilation
events establish observed compilations. Cached plan snapshots are supplemental.

EF execute-reader time ends when SqlClient returns a reader; it excludes earlier
LINQ translation/compilation and may exclude some row consumption. Reader lifetime
also includes consumption/materialization and interception. SqlClient statistics
have their own documented semantics and timer precision; they are not SQL CPU
time. EF query-compilation events concern client query generation, not SQL Server
compilation. Process CPU/JIT/GC counters are whole-process deltas, not exclusive
cost attribution. SQL actual plan QueryTimeStats and XE CPU/duration/reads provide
the independent server observations. QueryPlan CompileTime/CompileCPU fields are
milliseconds; XE completion duration is microseconds. Avoid conflating them.

The existing allow-listed performance collector samples Linux host/process data
every second, Docker/SQL aggregate data every five seconds. It creates a small
periodic background workload; peaks between samples and physical-host contention
remain unobserved. Each trial owns its container and loopback port; traps clean it
after success/failure/interruption. Credentials are ephemeral and masked, never
included in artifacts. Only `.artifacts/issue485` is uploaded, including search
JSON/SQL/XE XML, tests/TRX, resource/fixture telemetry, identities and cleanup log.

Assertions retain exact dataset/counts/facets, stable repeated paging, bounded two
search statements, read-only rowversion/audit/journal/job hashes, cancellation,
and unchanged deterministic-provider/dispatcher calls. Original HTTP scope,
literal/ranking/paging and active-identity regressions run separately. Targets
remain 2 seconds for first search and 1 second for warm; no human acceptance is
claimed. Raw evidence and temporary branch must be retained for review.
