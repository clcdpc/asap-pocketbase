# Slice 9: Focused Development CI And Browser Completion

## Current Reduced Scope

Slice 9 is future work and is not implemented by reduced Slice 8. Start only
after the accepted Slice 8 milestone, following [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md)
and the current status record. It does not have to wait for
`test_cd_activation` unless a chosen browser check explicitly targets the live
test IIS host.

The development-completion scope is:

- make important existing browser journeys run in normal hosted CI using the
  retained plain Node/Playwright runners;
- preserve the full .NET/real-SQL suite, frontend unit suite, and meaningful
  test-count/skip guards;
- preserve the serious/critical `axe-core` accessibility gate;
- fill concrete development-completion coverage gaps in the owning feature,
  including patron login/options/submission/expiry/pickup and staff anonymous
  shell, roles, queue/scope/deep-link, edit/claim/action, stale conflict,
  AdditionalCopy, profile preferences, settings inheritance/reset and
  email/operator recovery journeys;
- cover keyboard reachability, visible focus, modal/tab/view focus, labels,
  live regions and supported Escape behavior at desktop and mobile sizes;
- optionally add a small post-deployment browser smoke after the test runner is
  activated.

Normal CI remains independent of the IIS runner and live Polaris/Postmark.
Testing-only identity evidence, deterministic provider boundaries and real SQL
authorization/concurrency assertions remain required where applicable. Node and
browser dependencies stay development/CI-only and never enter the application
publish or deployment artifact.

## Explicitly Deferred

Slice 9 does not implement production release artifact promotion, protected
live-Polaris canaries, production release/tag workflows, production provider
correlation evidence, production rehearsal machinery or other operational proof.
Those contracts remain in the deferred production-readiness backlog and the
preserved historical porting pack.

## Acceptance Boundary

Use one cohesive implementation context where practical, complete integrated
validation, one fresh Terra High review and focused re-review of confirmed
findings under the three-cycle cap. Do not create a production tag or alter a
protected environment to exercise this slice. `test_cd_activation:
pending_runner_setup` may remain unchanged while development CI/browser work
proceeds.

The prior production-oriented Slice 9 packet is retained in Git at
`88fd92cf1fdd856dccba6ef3538182d80325e98a` and remains reference material for
future production readiness; it is not the current execution contract.
