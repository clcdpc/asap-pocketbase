# Current application qualification

PocketBase was an internal proof of concept with no production deployment or
historical data. Its migration executable, frozen source contracts, mapping
table and pre-production schema upgrades are retired. Git history is the archive.

## Mandatory populations

The built MTP application discovers the complete population, non-browser and
Browser partitions. Their disjoint union must exactly equal the complete
catalog. The reviewed browser methods are in reviewed-test-categories.json.
New unclassified tests default to non-browser. Exact native UIDs, execution
identities, counts, outcomes and counters must match; omissions, duplicates,
skips and equal-count substitutions fail qualification.

The Browser partition includes Chromium journeys and frontend asset,
publication and cache contracts. Every Chromium consumer belongs there.
PolarisProtocolTests retains real SQL configuration/protected-credential reads
while constructing providers directly, without a web host or Hangfire.
Shared-database fixtures remain serial. Every root frontend test file remains
mandatory; counts are derived from current discovery.

## Execution and required gate

Hosted validation-policy resolves current trusted PR state before runner
allocation. Draft PRs run bounded configuration/unit contracts and the complete
frontend suite. Ready PRs, main pushes and manual qualification use independent
Full CI execution and Browser qualification jobs on ephemeral clc-linux runners.
Each checks out the exact event SHA and independently creates SQL credentials
and a SQL Server 2022 container. They share no mutable state or databases.

Each job independently verifies its assigned suites and publishes only original
successful evidence. The reporting-only Build, test and package gate requires
both execution jobs to succeed. Missing, failed, cancelled or skipped jobs fail
that gate. There is no third test run for aggregation. Packaging always builds
and publishes the current commit; test-IIS activation still requires the
existing authorization controls and successful final qualification.

Browser and frontend source changes invalidate their evidence. Only the exact
audited JS/CSS paths listed in test-reuse.json can leave backend evidence valid.
HTML entry points, vendor assets, manifests, new paths, backend/schema/test
changes and execution/security/build/deployment tooling remain conservative.
Current build/publication checks and frontend-serving/browser contracts still
validate changed asset bytes.

## Events, provenance and evidence

PR selection verifies current base/head, exact synthetic-merge parents, a unique
merge base and both sides of renames. Unknown dependencies or ambiguous metadata
force fresh execution. Explicit force-full, release tags and deployment
qualification bypass reuse. Manual dispatch can reuse otherwise valid evidence.

Evidence schema 3 rejects prior populations/contracts. It binds suite, exact
identities, complete classification, source/toolchain/environment, originating
run/attempt/job, checkout logs, Git blob/tree identity, artifact digest, age and
successful original steps. Lookup is bounded and read-only. The final suite
verifier re-fetches original evidence; a cached plan cannot authorize success.
The full workflow, including browser execution and the required gate, is part
of the execution contract. Reused evidence never becomes new original evidence.

## Diagnostics and performance

ASAP_CI_FIXTURE_TIMINGS enables monotonic JSONL spans. Observations contain
operation identifiers, hashed test identity, process/span/parent identity,
timestamps and elapsed duration. Host build/start includes DI startup.
body_and_framework excludes TestInitialize and TestCleanup but includes any
nested host/provider work. Inclusive spans can overlap and must not be added
to wall time. Failed telemetry writes preserve test exceptions.

The normal execution uploads native TRX, resource/SQL telemetry, fixture spans,
discovered catalogs, per-file frontend receipts and verification decisions.
Compare equivalent cold populations separately from evidence reuse, and report
runner queueing/startup separately from test-command wall time. See
[the implementation and validation record](../../docs/implementation/ci-simplification.md).
