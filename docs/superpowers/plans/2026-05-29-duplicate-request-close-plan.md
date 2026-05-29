# Duplicate Request Close Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow staff to silently close duplicate requests when a `409 duplicate_open_request` occurs.

**Architecture:** Create a new confirmation helper in `pb_public/staff/js/modals.js` and use it within the existing error handling logic to trigger `closeDuplicateRequest`.

**Tech Stack:** JavaScript (ES6 Modules)

---

### Task 1: Create `confirmDuplicateOpenRequestClose` helper

**Files:**
- Modify: `pb_public/staff/js/modals.js`

- [ ] **Step 1: Add helper function to `pb_public/staff/js/modals.js`**

```javascript
export async function confirmDuplicateOpenRequestClose(err, id) {
  const confirmed = await showConfirm(
    'Duplicate request found',
    'This patron already has an open request or hold for this BIB ID. Close this request as a duplicate, or keep editing and choose another BIB.'
  );
  if (confirmed) {
    await closeDuplicateRequest(id);
    showToast('Duplicate request closed.', 'success');
    // Reload tab/close modal logic handled by actions.js
  }
  return confirmed;
}
```

### Task 2: Integrate into error handling

**Files:**
- Modify: `pb_public/staff/js/modals.js`

- [ ] **Step 1: Update catch block to use helper**

```javascript
// In the catch block of submission handlers
  } catch (err) {
    if (err && err.code === 'duplicate_open_request') {
      const confirmed = await confirmDuplicateOpenRequestClose(err, requestId);
      if (confirmed) {
        // Handle post-close UI state if needed
        return;
      }
    }
    await showAlert(err.message || 'Error updating suggestion');
  }
```

### Task 3: Test implementation

**Files:**
- Modify: `tests/staff_modal_duplicate_error.test.js`

- [ ] **Step 1: Add unit test**

```javascript
test('duplicate_open_request triggers confirmation', async () => {
  // Mock showConfirm, closeDuplicateRequest
  // Trigger handler with 409 duplicate_open_request
  // Assert showConfirm called
  // Assert closeDuplicateRequest called if confirmed
});
```

---

## Self-Review
1. **Spec coverage:** Covered.
2. **Placeholder scan:** None found.
3. **Type consistency:** Consistent with existing codebase.
