---
phase: 2
plan: 1
wave: 1
---

# Plan 2.1: Update Workflow Descriptions

## Objective
Update UI text across the staff interface to clarify that a BIB ID is required to move suggestions to the "Pending hold" phase.

## Context
- .gsd/SPEC.md
- pb_public/staff/js/state.js
- pb_public/staff/js/grid.js
- pb_public/staff/js/modals.js

## Tasks

<task type="auto">
  <name>Update Data Descriptions</name>
  <files>
    <file>pb_public/staff/js/state.js</file>
  </files>
  <action>
    Update the `outstanding_purchase` description in the `descriptions` object to:
    "Pending purchase contains approved suggestions that are waiting to appear in Polaris. Staff can add a BIB ID to move a suggestion to the Pending hold phase."
  </action>
  <verify>grep "move a suggestion to the Pending hold phase" pb_public/staff/js/state.js</verify>
  <done>The description in state.js matches the new text.</done>
</task>

<task type="auto">
  <name>Update UI Component Descriptions</name>
  <files>
    <file>pb_public/staff/js/grid.js</file>
    <file>pb_public/staff/js/modals.js</file>
  </files>
  <action>
    1. In `pb_public/staff/js/grid.js`, update the hardcoded `tabDesc.textContent` for `outstanding_purchase` to match the new text in `state.js`.
    2. In `pb_public/staff/js/modals.js`, update `bibHint.textContent` in `setBibIdRequirement` to: "Required before moving this suggestion to the Pending hold phase."
  </action>
  <verify>
    grep "move a suggestion to the Pending hold phase" pb_public/staff/js/grid.js
    grep "Required before moving this suggestion to the Pending hold phase" pb_public/staff/js/modals.js
  </verify>
  <done>Both grid.js and modals.js use the updated descriptive text.</done>
</task>

## Success Criteria
- [ ] The "Pending purchase" tab description explicitly mentions moving to the "Pending hold" phase via BIB ID.
- [ ] The BIB ID input hint in the edit modal clearly states it is required for the "Pending hold" phase.
