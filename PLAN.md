# ASAP Development Plan

## Current batch

The reduced Slice 9 browser/accessibility CI work and Slice 11 cleanup/docs
work run as one final development-completion batch. The current branch is
`codex/final-development-completion`, based on the reviewed
`codex/csharp-port` baseline. The batch ends with a cleaned-tree validation,
one independent integrated review, and exact-milestone CI. It does not claim
production readiness or merge acceptance.

## Maintained development gates

1. Build the complete solution in Release with zero warnings and errors.
2. Run the full real-SQL .NET suite, including the three browser fixture
   methods, with a nonzero expected-test guard and zero skips.
3. Run every retained frontend test and the patron, staff, and legacy-link
   Playwright journeys with axe, origin, page-error, image, and overflow
   guards.
4. Publish Web and self-contained `win-x64` migration output, verify vendor
   hashes and exclusions, and run migration regression/oracle checks.
5. Validate the Slice 8 package contract, including exact identity, digest
   before extraction, runner isolation, and `-ValidateOnly` without IIS host
   mutation.
6. Check documentation links, removed imports/paths/scripts, secrets,
   generated output, and whitespace from the cleaned tree.

## Deferred work

Slice 10 synthetic seed/reset tooling is optional and deferred. Test-IIS
runner/host activation remains `pending_runner_setup`. Live providers,
production tags/deployments, cutover, operational recovery rehearsal, and
repository rename require a later production-readiness authorization.
