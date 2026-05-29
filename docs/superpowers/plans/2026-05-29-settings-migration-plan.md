# Settings Migration: Autohold Opt-Out Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move "Allow patron autohold opt-out" setting from "Workflow" to "Patron Experience".

**Architecture:** Update HTML structure to relocate DOM element. Functionality relies on DOM ID, so JS logic remains unchanged.

**Tech Stack:** HTML/CSS

---

### Task 1: Identify and Relocate Setting

**Files:**
- Modify: `pb_public/staff/index.html`

- [ ] **Step 1: Locate `allow-patron-autohold-opt-out` container in "Workflow" section**
- [ ] **Step 2: Move the entire container element to the "Patron Experience" section**
- [ ] **Step 3: Ensure labels and formatting remain consistent**

### Task 2: Verify and Test

**Files:**
- None (verify via browser/UI)

- [ ] **Step 1: Verify layout**
  - Load the staff settings page in a browser.
  - Confirm the "Allow patron autohold opt-out" checkbox is in the "Patron Experience" section.
- [ ] **Step 2: Verify functionality**
  - Confirm the checkbox populates with the correct value on load.
  - Toggle the checkbox and confirm the value persists on save.

---

## Self-Review
1. **Spec coverage:** Covered.
2. **Placeholder scan:** None.
3. **Type consistency:** N/A (HTML change only).
