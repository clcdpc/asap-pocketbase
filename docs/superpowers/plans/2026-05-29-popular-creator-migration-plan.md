# Settings Migration: Popular Creator Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move "Popular creator notifications" section from "Workflow" to "Patron Experience" in staff settings.

**Architecture:** Update HTML DOM structure to relocate the settings section. JS logic relies on static element IDs, so functionality remains intact.

**Tech Stack:** HTML/CSS

---

### Task 1: Relocate Settings Section

**Files:**
- Modify: `pb_public/staff/index.html`

- [ ] **Step 1: Identify and copy the section**
  - Locate the section for "Popular creator notifications" (lines 687-720).
- [ ] **Step 2: Relocate the section**
  - Remove the section from its current location.
  - Insert it into the "Patron Experience" accordion (`#patron-experience-accordion`) near similar settings.
- [ ] **Step 3: Preserve structure**
  - Ensure the copied HTML includes the surrounding `div`s and classes to maintain styling.

### Task 2: Verify and Test

**Files:**
- None (verify via browser/UI)

- [ ] **Step 1: Verify layout**
  - Load the staff settings page in a browser.
  - Confirm the "Popular creator notifications" section now appears within the "Patron Experience" accordion.
- [ ] **Step 2: Verify functionality**
  - Confirm the settings in this section populate correctly from `settings.js`.
  - Toggle settings and confirm values persist correctly on save.

---

## Self-Review
1. **Spec coverage:** Covered.
2. **Placeholder scan:** None.
3. **Type consistency:** N/A (HTML change only).
