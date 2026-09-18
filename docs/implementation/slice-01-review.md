# Slice 1 Independent Review Record

## Pass 1

Fresh Terra High reviewer: Cicero,
`01a09809-2eb9-7612-8ce6-ae499bccae94`. Reviewed the full 88-file
non-documentation dispatch inventory and surrounding source/contracts after
the completed Sol handoff and acceptance checks in `slice-01-evidence.md`.
Review scope/state is recorded in `slice-01-review-packet.md`. No reviewer
implementation edits, commit or deployment occurred.

| ID | Reported Finding | Astra Triage / Action |
| --- | --- | --- |
| S1-R1 | Login limiter defaults are hard-coded rather than configurable | Confirmed against document 01 and Program.cs; Sol to implement restart-loaded external settings and HTTP/default/invalid-config regressions |
| S1-R2 | Raw alternate pickup fields bypass the source-equivalent registered-branch fallback | Adjusted after source-chain triage; preserve positive raw Request, ignore raw aliases, then apply the actual PatronOrg fallback and allowed-branch selection |
| S1-R3 | Populated legacy patron/library settings are counted but not mapped or explicitly accounted | Confirmed legacy duplicate-label fallback; Sol to preserve current source precedence and account all populated legacy fields, including branding |
| S1-R4 | Initial config completing after successful login can overwrite the effective library configuration | Confirmed unguarded applyLoadedUiText and post-await DOM updates; Sol to fix and add the exact late-response order with distinctive config assertions |
| S1-R5 | Embed-origin migration admits remote HTTP and rejects valid HTTPS wildcards | Confirmed against pinned embed_security.js; Sol to preserve canonical trusted origin forms and reject invalid input atomically |

Terra reported all five as P1 and no additional confirmed issue in the
five-state outbox, fencing, recipient/domain checks or temporary file sender.
Focused reviewer tests passed: fourteen external-configuration cases and the
current authentication-race Node regression. Those tests do not cover the
reported failures. This is not a clean review or a completed slice.

### Pickup Finding Triage

The production chain matters: pinned `lib/polaris/auth.js` authenticatePatron
and `lookupPatron` both call `lib/polaris/patron.js` getPatronBasic. That
function reads raw RequestPickupBranchID only and assigns both normalized
CurrentPreferredPickupBranchID and PreferredPickupBranchID from that same
value. The later pickup_preference_context helper orders normalized object
fields, not independent raw PAPI preferences. The same reviewer withdrew the
original Current-before-Request claim after this source-chain check.

The complete login and refresh paths then call `orgs.attachPatronScope`, which
fills normalized PreferredPickupBranchID from PatronOrgID when Request is
absent. The current adapter instead inspects raw alternate preference fields
and can select a value the source discards. Astra independently verified the
revised narrower defect and assigned it to Sol: positive Request remains
authoritative; otherwise retain the source-equivalent registered-branch
fallback, with selection still requiring an allowed pickup branch. Test the
actual CLC response path with divergent aliases in both cases. This triage is
part of Pass 1, not the required later full Pass 2.

The same raw-field distinction applies to explicit zero: pinned
normalizePolarisId preserves numeric zero as the nonempty string `0`, so the
scope helper does not treat it as a missing preference. The CLC typed integer
alone conflates absent and zero. Preserve missing/null/blank fallback versus
an explicit nonselectable zero; do not silently choose a registered branch
for the latter or skip a required later preference update.

### Independent Legacy-Source Reproduction

Astra created a separate stopped synthetic PocketBase source variant through
Record/File APIs, adding a library legacy duplicate-label row with no modern
override and a dormant legacy branding row. The pre-fix published executable
exported/imported it and reported successful reconciliation, but independent
SQL assertions proved the two effective legacy library labels were absent.
Evidence is `.git/asap-migration-acceptance/runs/b477bc4640bd40dda55e0888f8269ac6/`;
the owned database was removed. The original pinned-shaped fixture remains
unchanged. Retest after Sol's fix; this is an expected failing reproduction.

Separate Record-API-created embed variants reproduced S1-R5 against the same
pre-fix executable. The valid HTTPS wildcard/local HTTP/exact HTTPS package
incorrectly blocks (`379d1d20518841f49e4fc6deb25ad397`); the remote-HTTP package
incorrectly imports and reconciles (`f7f503c1f4b6413cbb0f5bb201652ebb`). All owned
SQL databases were removed. A separate present-but-blank modern-override
variant is prepared to ensure the legacy label fallback is not revived when
the pinned source suppresses it. These are synthetic regression fixtures,
not production-source or release-rehearsal evidence.

The pre-fix executable already passes that companion modern-blank fixture:
library labels remain inherited, all original positive migration assertions
pass, and changed-package/target-drift rejection still holds. Evidence is
`acf9b0cec6064d5aae433df796503e0e`; its database was removed. The fix must make
the legacy-only case pass without regressing this existing behavior.

## Review Gate

### Local Pass 3 Result

The same Terra High reviewer completed full Pass 3 with no confirmed
substantive defect. S1-R1 through S1-R6 are resolved, including the corrected
full-source-chain R2 position. The reviewer independently verified all 95
receipt files with zero mismatches, Web/migration hashes and file counts,
17 vendor and seven Hangfire hashes, and artifact exclusions. Five focused
real-SQL CSP tests passed with none skipped. No reviewer implementation edits
occurred. No nonblocking code cleanup was required to close this slice.

Slice 1's required tests and independent review/fix/re-review gate are
satisfied. Astra may now create its coherent milestone and verify remote CI
before dispatching Slice 2. Real Postmark/provider-webhook work remains the
explicit release/rehearsal blocker; this is not production-completion approval.

### Remote CI Correction

The locally accepted milestone `c1b86558ad3b2a7270c6cf1d1bfa7830911d7f44`
was pushed, then remote run 34730692517 failed: 77 passed, 41 failed, no skips.
One migration credential test opens unsupported Unix LocalMachine/My before
trying CurrentUser/My. Forty patron tests fail during factory setup because
the actual SQL-authenticated Linux CI connection is rejected by the external
configuration validator, leaving the asserted worker registration absent.
The latter assertion must not simply become optional.

The same Sol retains ownership for narrow correction and regressions. Preserve
Windows production certificate/private-key requirements and Integrated Security;
any CI SQL-authentication allowance must be governed only by the trusted Testing
host, never an external JSON environment label or IsNonProduction flag. Tests
remain real SQL without skips or a runner switch. Same-Terra closure review is
required before the correction commit; the nominal pass cap cannot waive a
known failure. No Slice 2 implementation starts until remote CI succeeds.

Sol's correction now uses the existing Web certificate lookup semantics in
migration and passes a Testing allowance from the trusted host only, defaulting
the loader/validator to strict Integrated Security. Two focused regressions
prove SQL authentication requires that caller-supplied allowance and cannot be
enabled by external JSON. The existing missing-worker assertion is unchanged.
Release build and complete 120-test suite pass; Astra independently reran
120/120 with no skips in 1m 39s. New source-built artifacts pass the recorded
browser/CSP/native-migration checks. The same Terra is requested for full
closure review against the 96-file receipt in the review packet. Linux CI is
still outstanding, not inferred from the Windows results.

The same Terra completed full Slice 1 closure review with no actionable defect
or regression. It verified all 96 receipt files with zero mismatches, the new
artifact hashes/counts/exclusions, 20 focused configuration tests and the real-
SQL current-user-certificate bootstrap migration test, all without skips. The
correction is cleared for commit/push. An actual green Linux CI run remains a
separate required gate before Slice 2; neither this review nor Windows tests
replace it. No reviewer edits occurred and no prior finding was waived.

The correction was committed/pushed as
`1e36761c771db70d0b669087d0a843b66cf5618b`. Actual Linux CI run 34731687718
passed all 120 .NET/SQL tests with no failures/skips, frontend tests and publish
checks. Slice 1 is now closed and Slice 2 may begin. The original failed run
and correction review remain recorded; no history was rewritten.

### Pass 2 Result

The same Terra High reviewer completed full Pass 2 over all 95 receipt files,
verified their hashes and the corrected migration DLL, and ran focused
frontend/pinned embed tests. S1-R1 through adjusted S1-R5 are resolved. One new
P1 remains: **S1-R6**, missing request-specific CSP wildcard expansion in
PatronContentSecurityPolicyMiddleware. The target emits stored origins only;
document 07 section 14 and pinned embed_security.js require adding the
normalized concrete request ancestor when the trusted system list permits it.

Astra confirmed the source contract. `patron_embed_routes.js` selects Referer
before Origin; the helper extracts/validates the origin, requires an exact or
strict wildcard-subdomain match with the pinned port semantics, and adds only
a permitted nonduplicate concrete origin. Sol is assigned the fix with actual
HTTP positive/negative, header-precedence and port regressions, followed by
full acceptance and a new correctly built publish. No other substantive issue
was confirmed by Pass 2. The same Terra must perform full Pass 3 afterward.

Astra independently ran the actual pinned `lib/embed_security.js` as the
expected-result oracle against real HTTP responses from the frozen pre-fix
Web publish. Of fourteen cases, generated and nested wildcard ancestors,
Origin fallback, and explicit-port wildcard expansion fail as reported; the
ten rejection/precedence/deduplication cases pass. Evidence is
`.artifacts/acceptance/patron-browser/0de76953bd3a4fd6b243d8ff9b35a929/csp-results.json`.
The isolated application process exited and its harness cleaned up the owned
SQL database/certificate. This is failing-before evidence, not fix clearance.

### Pass 3 Handoff

Sol fixed S1-R6 and added real HTTP regressions. Its full Release build,
118-test .NET run, npm/browser and formatting gates pass. Astra independently
reran all 118 tests with zero failures/skips in 1m 43s. The fresh source-built
publish is `.artifacts/slice-01-handoff-20260912-pass2-verified/`; Web DLL is
`0dc74a704bfba3c91c97e45bef90e9eefaa58e4f423b8817febfdf968d97c76f`.
The migration DLL and DACPAC remain byte-identical to their verified fixed
pre-R6 versions. Vendor/Hangfire hashes, file inventory and exclusions pass.

All fourteen executable-pinned-source/real-HTTP CSP cases pass against the
new publish (`b5069b1770c24b1ead5a42b7fbdd495a`), and its full published
desktop/mobile/SQL journey passes (`e30f3cc1685e410d84e459cf9c02ce95`). The same
Terra reviewer is requested for full Pass 3 using the frozen 95-file receipt
in the review packet. No implementation writer is active and no milestone
has been created. These acceptance results are not independent-review closure.

### Fix Acceptance Before Pass 2

Astra's focused check of the first S1-R4 fix found an adjacent regression:
holding initial configuration, completing a failed login, then releasing the
configuration leaves the generic default title instead of the library title.
The new guard used any authentication-operation increment even when no newer
configuration had been installed. The independent JSDOM probe
`.git/asap-bootstrap-failed-login.cjs` reproduced it against an ignored source
snapshot (`.git/asap-failed-login-aePvsm`). Sol must retain the login error and
load the initial library config in this case while still preventing a late
response from replacing a successful login's configuration. This is parent
fix acceptance before the required formal Pass 2, not a new review pass.

The failed-login probe passed after Sol's follow-up fix, preserving both the
library title and login error (`.git/asap-failed-login-mtG900`). Sol then
reported a clean Release build, 114/114 .NET tests with none skipped, legacy
tests, browser checks and a new post-fix publish. However, Astra's native CLI
legacy-fixture retest still failed (`62dd36a49ae147479d363432f83186ac`). The
post-fix publish contained the unchanged pre-fix migration DLL.

Diagnosis: default-build and test migration DLL SHA-256 is
`4b914a35554019facfda0b34640c8bb2fa03b1d086d6caab7974af3e1ce90b2e`,
but the win-x64 build/publish still contains pre-fix
`745c2e099f67d2136c1083d91fa1991ae507d97bd8f68238dd09e86b55281527`.
The fixes are present in source; stale RID output was published. Apphost hash
and version/contract smoke checks alone cannot prove current CLI behavior.
Sol must build/publish the actual RID into a new directory and verify the
published executable's behavior before Pass 2. The failed publication at
`.artifacts/slice-01-handoff-20260912-pass1-final/` is retained as evidence,
not treated as the accepted fixed artifact. Its owned test database was removed.

### Accepted Fix Handoff

Sol rebuilt without `--no-build` into
`.artifacts/slice-01-handoff-20260912-pass1-verified/`. The corrected migration
DLL SHA-256 is `cd9b585d532ab20d447c564125f116bd27aee2b1f8385e152def629f237115ec`;
Web DLL is `9ce0d77f0edd97b0085d53645a9bee1804b991a59ecd08810381ec45a6fd952d`.
DACPAC remains the verified schema-2 artifact. The same native executable now
passes the independent legacy-only, wildcard and modern-blank source fixtures
and rejects remote HTTP with zero-row rollback. Evidence runs are respectively
`a6be37547cbc4c5481bdbfd83dfbb67f`, `4572781a7d6041d7a1c9528a1abe6c2f`,
`1ddce1e9697542999271e287dd5a4bc5` and `e83cde7580bf437782ab0c1d42677caf`.
The positive cases retain package-binding and same-count drift rejection.
Dormant legacy logo/logoAlt fields have explicit source-identified intentional
drop entries, not silent omission or resurrection over current branding.

Release build: zero warnings/errors. Sol's full suite: 114/114, none skipped;
npm, checked-in SQL/Kestrel/Playwright and format checks pass. Astra independently
reran all 114 tests successfully in 1m 41s and inspected the corrected artifact
hashes/restricted CLI reports. No owned acceptance process/database remains.
The same Terra context is now requested for full Pass 2 across all 95 current
changed/new non-documentation files and surrounding code. No milestone yet.

The same Sol XHigh implementer fixes confirmed substantive findings and runs
focused regressions plus complete build, .NET/legacy/browser and publish gates.
Retain the same Terra context for full-slice Pass 2, not just a patch check.
If Pass 2 finds a new substantive issue, Sol fixes it and Terra performs Pass 3.
Do not waive blocking correctness/security/migration issues at the nominal cap.
No Slice 1 milestone or Slice 2 implementation until this gate passes.

Real Postmark transport and provider webhook work remain the explicit deferred
release/rehearsal blocker, not a waived outbox requirement or review finding.
