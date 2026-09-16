# Slice 9: Development Browser And Accessibility Completion

## Current combined-batch scope

Reduced Slice 9 and Slice 11 completed as one final development-completion
batch, merged through PR #272 into `codex/csharp-port`. See the
[completion record](final-development-completion.md) and its live journal for
the exact milestone CI and acceptance. No required development slice remains.
The batch preserves
the accepted Slices 0-8 behavior and runs against the current .NET tree. Slice
9 is not a production-release exercise and does not require test-IIS activation.

The maintained development contract is:

- run the retained plain Node/Playwright wrapper in normal hosted CI;
- preserve the full .NET/real-SQL suite, current frontend tests, and test-count
  and zero-skip guards;
- keep serious/critical `axe-core`, origin, page-error, missing-image, and
  overflow gates;
- cover the retained patron, staff, and legacy request-link journeys at
  desktop and mobile sizes, including keyboard/focus/dialog/tab/live-region
  behavior and the authentication/session races;
- keep deterministic Polaris/Postmark boundaries, real SQL assertions, and
  Node/npm strictly development and CI only.

The current browser inventory is 10 patron states plus 3 race scenarios, 20
staff states, and 18 legacy-link states. The three browser fixture methods are
ordinary .NET tests selected by `npm run test:browser`; they are not optional
or skipped tests.

## Explicitly deferred

Slice 9 does not activate a runner or IIS host, send live Polaris/Postmark
requests, promote a production artifact, create a production tag, or perform
cutover/rehearsal work. `test_cd_activation: pending_runner_setup` remains
valid. Those contracts remain in
[deferred production readiness](deferred-production-readiness.md) and the
preserved `docs/dotnet-port` pack.

## Acceptance boundary

The combined batch ends with complete integrated validation, one fresh
independent Terra High review, focused re-review of confirmed findings when
needed, and exact-milestone CI. The implementer does not merge the draft PR or
claim supervisor acceptance. Slice 10 synthetic seed/reset convenience
tooling remains optional and deferred.

The prior production-oriented Slice 9 packet is retained in Git at
`88fd92cf1fdd856dccba6ef3538182d80325e98a`; it remains reference material for
future production readiness and does not override this development scope.
