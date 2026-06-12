# Purchase Approved Patron Email Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an automatic patron email when a normal staff Purchase action first enters Pending purchase.

**Architecture:** Extend the existing fixed email-template path with a `purchase_approved` key. Send it from the staff title-request side-effect layer only when the persisted final status is `outstanding_purchase` and the previous status was not already `outstanding_purchase`.

**Tech Stack:** PocketBase JS hooks, plain browser JavaScript, Node-based regression tests.

---

### Task 1: Backend Trigger And Mail Helper

**Files:**
- Modify: `tests/staff_title_request_side_effects.test.js`
- Modify: `lib/staff/title_request_side_effects.js`
- Modify: `lib/mail.js`

- [ ] Add failing tests for first Pending purchase transition, repeat Pending purchase save, and non-Pending-purchase outcomes.
- [ ] Add `mail.purchaseApproved(app, record, patron)`.
- [ ] Add side-effect logic that sends `purchase_approved` only for `action === "purchase"`, `oldStatus !== "outstanding_purchase"`, and final status `outstanding_purchase`.
- [ ] Run `rtk node tests/staff_title_request_side_effects.test.js`.

### Task 2: Fixed Template Defaults And Settings Save

**Files:**
- Modify: `tests/mail.test.js`
- Modify: `tests/library_settings_save_scope.test.js`
- Modify: `lib/config/defaults.js`
- Modify: `lib/staff/settings_email.js`

- [ ] Add failing coverage that `purchase_approved` is available through defaults and saved like the other fixed templates.
- [ ] Add the new default subject/body.
- [ ] Add `purchase_approved` to the fixed template save key list.
- [ ] Run `rtk node tests/mail.test.js` and `rtk node tests/library_settings_save_scope.test.js`.

### Task 3: Settings UI Template Editor

**Files:**
- Modify: `pb_public/staff/index.html`
- Modify: `pb_public/staff/js/state.js`
- Modify: `pb_public/staff/js/settings-templates.js`
- Modify: `pb_public/staff/js/settings.js`
- Modify: existing UI static tests where needed

- [ ] Add failing static/UI coverage for the new accordion item and serialized payload.
- [ ] Add Purchase approved fields between Submission confirmation and Already owned.
- [ ] Include the new fields in defaults, summaries, placeholder tracking, population, and payload serialization.
- [ ] Run the affected UI/static tests.

### Task 4: Migration And Wording Alignment

**Files:**
- Create: `pb_migrations/202606120001_purchase_approved_email_template.js`
- Modify: `pb_migrations/0000000000_initial.js`
- Modify: `lib/config/ui_text.js`
- Modify: `pb_public/staff/js/settings.js`
- Modify: `pb_public/staff/js/state.js`

- [ ] Add failing/default coverage where practical for updated stock wording.
- [ ] Create the new system template in the migration when missing.
- [ ] Update stock Hold placed template and stock submission note only when exact old defaults are present.
- [ ] Update initial migration and runtime/UI fallback defaults for fresh installs.
- [ ] Run focused tests.

### Task 5: Verification

**Files:**
- No new files expected.

- [ ] Run all focused tests touched by the feature.
- [ ] Run the broader relevant test suite if practical.
- [ ] Check `rtk git diff` for unrelated changes.
