---
phase: 3
plan: 2
wave: 2
---

# Plan 3.2: Audit and Harden Save Payload Scope Safety

## Objective
Audit the frontend serialization and backend save handler to confirm that saving while in a library context cannot accidentally overwrite system-level SMTP, Polaris, Getting Started, or Staff Access data. Fix any identified gaps.

## Context
- pb_public/staff/js/settings.js — `_serializeSettingsState` (line 544), `saveSettings` (line 706), `libraryPayload` assembly (line 733)
- lib/staff_routes.js — `updateLibrarySettings` (line 1835), `saveSystemSettingsPayload` (line 1916), `saveLibraryScopedSettings` (line 1950)
- pb_public/staff/js/api.js — `systemOnlySections` (from Plan 3.1)

## Tasks

<task type="auto">
  <name>Audit and fix frontend payload assembly for library saves</name>
  <files>pb_public/staff/js/settings.js</files>
  <action>
    1. In `_serializeSettingsState()` (line 544-686), audit the `isSystemContext` guard:
       - Currently `isSystemContext` is `isSuperAdminStaff() && currentLibraryContextOrgId === 'system'` (line 545). This is correct.
       - SMTP fields (`smtp-host`, `smtp-port`, etc.) are only collected when `isSystemContext` is true (line 672-678). ✅ Already safe.
       - Polaris fields are only collected when `isSystemContext` is true (line 679). ✅ Already safe.
       - `staffUrl`, `leapBibUrlPattern`, `enabledLibraryOrgIds` are only collected when `isSystemContext` (lines 562-577, 680-682). ✅ Already safe.
       - `systemNotEnabledMessage` and `misconfiguredMessage` are conditionally `undefined` when not system context (lines 588-589). ✅ Already safe.

    2. In `saveSettings()` (line 706-808), audit the `libraryPayload` assembly (line 733-774):
       - Currently `libraryPayload.smtp` is set to `payload.smtp` (line 738). When `isSystemContext` is false, `payload.smtp` is `undefined`. This means the key IS present in the libraryPayload but its value is `undefined`.
       - Similarly `libraryPayload.polaris` is set to `payload.polaris` (line 739), which is `undefined` for library saves.
       - FIX: Explicitly guard these keys. Only include `smtp` and `polaris` in the libraryPayload when `currentLibraryContextOrgId === 'system'`:
         ```
         if (currentLibraryContextOrgId === 'system') {
           libraryPayload.smtp = payload.smtp;
           libraryPayload.polaris = payload.polaris;
           libraryPayload.staffUrl = payload.staffUrl;
           libraryPayload.leapBibUrlPattern = payload.leapBibUrlPattern;
         }
         ```
       - Remove the existing unconditional assignments for `smtp`, `polaris`, `staffUrl`, `leapBibUrlPattern` from the libraryPayload object literal (lines 735-739).
       - Keep `enabledLibraryOrgIds` inside workflow since the backend already handles that conditionally.

    3. Add a code comment above the libraryPayload explaining the scope safety rule:
       `// System-only fields (smtp, polaris, staffUrl, leapBibUrlPattern) are only`
       `// included when saving system defaults. Library saves must never send these.`

    AVOID:
    - Do not change the backend handler — the backend already strips these fields for non-super-admins (lines 1853-1863). The frontend fix is defense-in-depth.
    - Do not remove the backend guard — both layers should protect.
  </action>
  <verify>
    grep -n "System-only fields" pb_public/staff/js/settings.js
    # Should find the comment
    grep -c "libraryPayload.smtp = payload.smtp" pb_public/staff/js/settings.js
    # Should return 1 (inside the system guard)
  </verify>
  <done>
    - libraryPayload never includes smtp/polaris/staffUrl/leapBibUrlPattern when saving for a library
    - A comment documents the scope safety rule
    - Backend guard remains as defense-in-depth
  </done>
</task>

<task type="checkpoint:human-verify">
  <name>Manual verification of scope safety</name>
  <files>pb_public/staff/js/settings.js, pb_public/staff/js/api.js</files>
  <action>
    Open the staff app in a browser. Log in as super admin.

    Test 1 — Guard visibility:
    1. Select a library from the library context dropdown
    2. Click "Getting Started" — should see warning banner, all fields disabled
    3. Click "Polaris" — same guard
    4. Click "Email / SMTP" — same guard
    5. Click "Staff access" — same guard
    6. Click "Workflow" — guard should NOT appear, fields should be editable
    7. Click "Patron experience" — guard should NOT appear
    8. Click "Email templates" — guard should NOT appear
    9. Switch back to "System Defaults" — all sections should be fully editable

    Test 2 — Save safety:
    1. Select a library, go to Workflow, make a change (e.g. toggle a checkbox)
    2. Save
    3. Switch to "System Defaults"
    4. Verify SMTP fields still have their configured values (not blank)
    5. Verify Polaris fields still have their configured values (not blank)
    6. Verify Getting Started fields (Staff URL, Leap Bib URL) still have values
  </action>
  <verify>Visual inspection in browser</verify>
  <done>
    - All 9 navigation checks pass
    - Save from library context does not blank system fields
  </done>
</task>

## Success Criteria
- [ ] Frontend libraryPayload excludes smtp, polaris, staffUrl, leapBibUrlPattern when not in system context
- [ ] Backend guard remains intact as defense-in-depth
- [ ] Manual testing confirms no data loss when saving from library context
