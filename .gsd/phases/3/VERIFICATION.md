## Phase 3 Verification

### Must-Haves
- [x] Navigating to SMTP while a library is selected shows a "switch to System Defaults to edit" message and all fields are disabled — VERIFIED (guard banner + `settings-panel-locked` class applied, inputs disabled in `activateSettingsSection`)
- [x] Navigating to Polaris while a library is selected shows the same system-only guard — VERIFIED (same logic via `systemOnlySections` list)
- [x] Navigating to Getting Started while a library is selected shows the same system-only guard — VERIFIED (same logic)
- [x] Navigating to Staff Access while a library is selected shows the same system-only guard — VERIFIED (same logic)
- [x] Saving settings while in library context does not send SMTP, Polaris, or Getting Started data — VERIFIED (`isSystemSave` guard in `saveSettings`, `libraryPayload` excludes system fields)
- [x] Switching back to System Defaults re-enables editing on all system-only sections — VERIFIED (`activateSettingsSection` re-evaluation hooks in both selector change handler and `applyLibrarySettingsToForm`)
- [x] Library-context saves preserve the correct system-level values in the database (no blanking) — VERIFIED (frontend excludes keys, backend strips remaining keys for non-super-admins as defense-in-depth)

### Verdict: PASS
