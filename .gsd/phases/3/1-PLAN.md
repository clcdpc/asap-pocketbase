---
phase: 3
plan: 1
wave: 1
---

# Plan 3.1: System-Only Section Guard UI

## Objective
When a super admin has a library selected in the settings context dropdown and navigates to a system-only section (Getting Started, Polaris, SMTP), all form controls in that section should be visually disabled and a prominent banner should explain they must switch to "System Defaults" to edit. When the user returns to system context or navigates to an overridable section, controls re-enable.

## Context
- pb_public/staff/js/api.js — `activateSettingsSection` (line 340), `overridableSections` (line 350)
- pb_public/staff/js/state.js — `currentLibraryContextOrgId`, `currentSettingsSection`
- pb_public/staff/js/settings.js — `applyLibrarySettingsToForm` (line 264), library selector change handler (line 227)
- pb_public/staff/index.html — settings section panels (lines 250–502)
- pb_public/staff/styles.css — settings styles

## Tasks

<task type="auto">
  <name>Add system-only section guard logic to activateSettingsSection</name>
  <files>pb_public/staff/js/api.js</files>
  <action>
    1. At the top of `activateSettingsSection` (near line 350), add a constant:
       `const systemOnlySections = ['start', 'polaris', 'smtp', 'staff'];`
       These are sections that only apply at the system level.

    2. After the existing `overridableSections` / `library-context-wrapper` toggle block (lines 350-358), add new logic:
       - Import `currentLibraryContextOrgId` from state.js (add to the existing import at line 1).
       - Determine if the user is in a library context: `const isLibraryContext = currentLibraryContextOrgId && currentLibraryContextOrgId !== 'system';`
       - For each settings panel matching a system-only section, toggle a CSS class `settings-panel-locked` based on whether `isLibraryContext && systemOnlySections.includes(targetSection)`.
       - When locked: find or create a `div.system-only-guard-banner` inside the panel's `.card-body` (as the first child). Set its textContent to explain: "These settings are system-wide. Switch to 'System Defaults' to edit." Add class `system-only-guard-banner` and make it visible.
       - When unlocked (system context or overridable section): remove the banner if it exists, remove `settings-panel-locked` class.
       - When locked: set `panel.querySelectorAll('input, select, textarea, button:not(.settings-nav-link)').forEach(el => el.disabled = true)` on the active panel.
       - When unlocked: re-enable them with `el.disabled = false`. BUT skip the save bar buttons (those are managed by `updateSaveBarState`).

    3. Export `systemOnlySections` so it can be used by other modules.

    AVOID:
    - Do not use innerHTML for the banner — use createElement + textContent (per AGENTS.md DOM safety rule).
    - Do not disable the settings sidebar nav links — only form controls inside the panel.
    - Do not disable save/discard buttons here — they're in the save bar outside sections.
  </action>
  <verify>
    grep -c "systemOnlySections" pb_public/staff/js/api.js
    # Should return >= 2 (definition + usage)
    grep -c "system-only-guard-banner" pb_public/staff/js/api.js
    # Should return >= 1
    grep -c "settings-panel-locked" pb_public/staff/js/api.js
    # Should return >= 1
  </verify>
  <done>
    - `systemOnlySections` array is defined and exported
    - `activateSettingsSection` toggles `settings-panel-locked` class on system-only panels when in library context
    - A guard banner is dynamically created/removed using safe DOM APIs
    - Form controls inside locked panels are disabled
  </done>
</task>

<task type="auto">
  <name>Re-apply guard when library context changes</name>
  <files>pb_public/staff/js/settings.js</files>
  <action>
    1. In the library selector `change` event handler (around line 227-243), after `await loadLibrarySettings(currentLibraryContextOrgId)`, call `activateSettingsSection(currentSettingsSection, { updateHash: false })` to re-evaluate the guard for the currently visible section.
       - Import `activateSettingsSection` from api.js (it's already imported on line 2 — verify it's in the import list).
       - Import `currentSettingsSection` from state.js (already imported on line 1 — verify).

    2. Similarly, in `applyLibrarySettingsToForm` (around line 346), after all form population is done, call `activateSettingsSection(currentSettingsSection, { updateHash: false })` to refresh the guard state. This ensures that after settings load completes, the guard is correctly applied.

    AVOID:
    - Do not add a second event listener — modify the existing one.
    - Do not call activateSettingsSection during the loading phase if settingsLoading is true — wrap in a check.
  </action>
  <verify>
    grep -c "activateSettingsSection(currentSettingsSection" pb_public/staff/js/settings.js
    # Should return >= 1
  </verify>
  <done>
    - Switching libraries re-evaluates the system-only guard on the current section
    - After settings load, the guard state is refreshed
  </done>
</task>

<task type="auto">
  <name>Add CSS for system-only guard styling</name>
  <files>pb_public/staff/styles.css</files>
  <action>
    1. Add a `.settings-panel-locked` class that:
       - Sets `position: relative` on the panel
       - Applies `opacity: 0.55` and `pointer-events: none` to all form controls inside: `.settings-panel-locked input, .settings-panel-locked select, .settings-panel-locked textarea, .settings-panel-locked .btn:not(.settings-nav-link)`

    2. Add a `.system-only-guard-banner` class that:
       - Uses a distinct alert-style appearance: `background: #fff3cd; border: 1px solid #ffc107; border-radius: 0.375rem; padding: 0.75rem 1rem; margin-bottom: 1rem; color: #856404; font-weight: 600; font-size: 0.9rem;`
       - Adds a left border accent: `border-left: 4px solid #ffc107;`
       - Includes an icon placeholder via `::before` with content like "⚠ " (or use Font Awesome if available)

    AVOID:
    - Do not use `pointer-events: none` on the entire panel — only on form controls, so scrolling and reading still works.
    - Do not override the save bar styles.
  </action>
  <verify>
    grep -c "settings-panel-locked" pb_public/staff/styles.css
    # Should return >= 1
    grep -c "system-only-guard-banner" pb_public/staff/styles.css
    # Should return >= 1
  </verify>
  <done>
    - Locked panels have visually reduced opacity on form controls
    - Guard banner has a distinct warning-style appearance
    - Panel remains scrollable and readable when locked
  </done>
</task>

## Success Criteria
- [ ] Navigating to SMTP/Polaris/Getting Started/Staff Access while a library is selected shows a warning banner and disables all form inputs
- [ ] Navigating back to Workflow/Patron/Templates while a library is selected removes the lock
- [ ] Switching library context back to "System Defaults" removes the lock on system-only sections
- [ ] Switching from system to a library re-evaluates the guard on the currently viewed section
