# Recent Suggestions Tracker Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sync the recent suggestions tracker when staff edit a suggestion.

**Architecture:** Implement `updateRecentSuggestion` helper and integrate it into save/action handlers in `pb_public/staff/js/modals.js`.

**Tech Stack:** JavaScript (ES6 Modules)

---

### Task 1: Create `updateRecentSuggestion` helper

**Files:**
- Modify: `pb_public/staff/js/recent-suggestions.js`

- [ ] **Step 1: Implement `updateRecentSuggestion`**

```javascript
export function updateRecentSuggestion(row, options = {}) {
  if (!row || !row.id) return;

  const storageKey = getStorageKey();
  if (!storageKey) return;

  let recent = getRecentSuggestions();
  const index = recent.findIndex(r => r.id === row.id);
  if (index < 0) return;

  const existing = recent[index];
  recent[index] = Object.assign({}, existing, {
    type: row.type || existing.type || 'title_request',
    title: row.title || existing.title || 'Unknown Title',
    author: row.author || existing.author || '',
    status: row.status || existing.status
  });

  if (options.bump) {
    const [item] = recent.splice(index, 1);
    item.accessedAt = new Date().toISOString();
    recent.unshift(item);
  }

  try {
    localStorage.setItem(storageKey, JSON.stringify(recent));
  } catch (e) {
    console.warn("Failed to update recent suggestions in localStorage", e);
  }
}
```

### Task 2: Integrate into Edit Form

**Files:**
- Modify: `pb_public/staff/js/modals.js`

- [ ] **Step 1: Import `updateRecentSuggestion`**
- [ ] **Step 2: Call `updateRecentSuggestion` in success path**

```javascript
// Inside edit form submit success path:
  const updatedRecord = await res.json();
  // ... existing logic
  updateRecentSuggestion(updatedRecord);
  renderRecentSuggestionsSwitcher();
```

### Task 3: Integrate into Immediate Action path

**Files:**
- Modify: `pb_public/staff/js/modals.js`

- [ ] **Step 1: Call `updateRecentSuggestion` in `performImmediateStaffAction`**

```javascript
// Inside performImmediateStaffAction success path:
  const updatedRecord = await res.json();
  // ... existing logic
  updateRecentSuggestion(updatedRecord);
  renderRecentSuggestionsSwitcher();
```

### Task 4: Add/Update tests

**Files:**
- Modify: `tests/recent_suggestions_ui.test.js`

- [ ] **Step 1: Add update test**

```javascript
test('updateRecentSuggestion updates existing record', async () => {
  // Clear localStorage
  // rememberRecentSuggestion({ id: "req1", title: "Old Title" })
  // updateRecentSuggestion({ id: "req1", title: "New Title" })
  // Assert localStorage has "New Title"
});
```

---

## Self-Review
1. **Spec coverage:** Covered.
2. **Placeholder scan:** None.
3. **Type consistency:** Matches existing JS structure.
