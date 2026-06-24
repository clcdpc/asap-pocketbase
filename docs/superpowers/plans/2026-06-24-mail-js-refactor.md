# mail.js Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** split `lib/mail.js` (403 lines) into focused sub-modules under `lib/mail/`, keeping `lib/mail.js` as a barrel so existing consumers do not need import changes.

**Architecture:** extract pure formatting and transport utilities into leaf sub-modules. Keep the dispatch, convenience wrappers, reminders, and assignment notification in the barrel since they share cross-cutting state (template config, refresh, logging). The barrel re-exports everything so all 6 consumers (`lib/jobs/hold_placement.js`, `lib/jobs/weekly_summary.js`, `lib/jobs/timeouts.js`, `lib/route_utils.js`, `lib/patron_routes.js`, `lib/staff/title_request_side_effects.js`, `lib/staff/assignment_notifications.js`, `lib/staff/admin_routes.js`) continue to work unchanged.

**Tech Stack:** CommonJS backend modules, existing `require()` patterns, existing `MailerMessage` and `Record` globals from PocketBase runtime.

---

## File Structure

### Create
- `lib/mail/transport.js` — `send`, `recordEmailEvent`
- `lib/mail/templates.js` — `replacePlaceholders`, `escapeHtml`, `clean`, `getRealValue`, `formatLabel`, `formatDateTime`, `reminderValue`, `reminderLine`, `htmlReminderLine`, `isValidEmail`

### Modify
- `lib/mail.js` — replace implementation with barrel that re-exports from sub-modules plus the dispatch/notification code that stays in the main file

### No change
- All consumers that `require('lib/mail.js')` — barrel preserves every exported name
- `tests/mail.test.js` — barrel re-exports all names the test exercises

---

### Task 1: Extract Transport Module

**Files:**
- Create: `lib/mail/transport.js`

- [ ] **Step 1: Create the transport module**

Move `send` and `recordEmailEvent` into `lib/mail/transport.js`:

```js
const config = require(`${__hooks}/../lib/config.js`);

function send(app, to, subject, text, html, options) {
  to = String(to || "").trim();
  if (!to) {
    return false;
  }

  options = options || {};
  const settings = app.settings();
  const fromAddress = options.fromAddress || settings.meta.senderAddress;
  const fromName = options.fromName || settings.meta.senderName || "Library Collection Development";
  const smtp = config.mail();

  if (!String(smtp.host || "").trim() || !fromAddress) {
    app.logger().warn("Email skipped because notifications are not configured", "to", to, "subject", subject);
    recordEmailEvent(app, options.record, options.templateKey, to, subject, "skipped", "Email notifications are not configured.");
    return false;
  }

  try {
    const message = new MailerMessage({
      from: { address: fromAddress, name: fromName },
      to: [{ address: to, name: options.recipientName || "Library Patron" }],
      subject: subject,
      text: text,
      html: html,
    });
    app.newMailClient().send(message);
    recordEmailEvent(app, options.record, options.templateKey, to, subject, "sent", "");
    return true;
  } catch (err) {
    recordEmailEvent(app, options.record, options.templateKey, to, subject, "failed", err.message || String(err));
    throw err;
  }
}

function recordEmailEvent(app, record, templateKey, to, subject, status, error) {
  try {
    const event = new Record(app.findCollectionByNameOrId("email_delivery_events"));
    if (record && record.id) event.set("titleRequest", record.id);
    event.set("templateKey", templateKey || "");
    event.set("recipient", String(to || ""));
    event.set("subject", String(subject || ""));
    event.set("status", status || "sent");
    event.set("error", error || "");
    app.save(event);
  } catch (err) {}
}

module.exports = { send, recordEmailEvent };
```

- [ ] **Step 2: Remove `send` and `recordEmailEvent` from `lib/mail.js`**

Replace them with `const { send, recordEmailEvent } = require('./mail/transport.js');` at the top of `lib/mail.js`.

---

### Task 2: Extract Template Utilities Module

**Files:**
- Create: `lib/mail/templates.js`

- [ ] **Step 1: Create the templates module**

Move template and formatting helpers into `lib/mail/templates.js`:

```js
function escapeHtml(str) {
  return String(str || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function clean(value) {
  return String(value || "").trim();
}

function getRealValue(combinedStr) {
  if (!combinedStr) return combinedStr;
  const parenIndex = combinedStr.indexOf(" (");
  if (parenIndex > 0) {
    return combinedStr.substring(0, parenIndex).trim();
  }
  return combinedStr.trim();
}

function replacePlaceholders(template, data, escape) {
  if (!template) return "";
  return template.replace(/{{(\w+)}}/g, (match, key) => {
    const val = data[key] !== undefined ? data[key] : match;
    return escape ? escapeHtml(val) : val;
  });
}

function isValidEmail(value) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(String(value || "").trim());
}

function formatDateTime(value) {
  let date = value ? new Date(value) : new Date();
  if (isNaN(date.getTime())) {
    date = new Date();
  }
  return date.toLocaleString("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
    timeZoneName: "short"
  });
}

function formatLabel(value) {
  const config = require(`${__hooks}/../lib/config.js`);
  const ui = config.uiText();
  const labels = ui.formatLabels || {
    book: "Book",
    ebook: "eBook",
    audiobook_cd: "Audiobook (Physical CD)",
    eaudiobook: "eAudiobook",
    dvd: "DVD",
    music_cd: "Music CD",
  };
  return labels[value] || clean(value);
}

function reminderValue(value) {
  const text = clean(value);
  return text || "Not provided";
}

function reminderLine(label, value) {
  return label + ": " + reminderValue(value);
}

function htmlReminderLine(label, value) {
  return "<p><strong>" + escapeHtml(label) + ":</strong> " + escapeHtml(reminderValue(value)) + "</p>";
}

module.exports = {
  escapeHtml, clean, getRealValue,
  replacePlaceholders, isValidEmail, formatDateTime, formatLabel,
  reminderValue, reminderLine, htmlReminderLine
};
```

Note: `formatLabel` has a lazy `require()` inside to avoid a circular dependency at module load time — keep that pattern as-is.

- [ ] **Step 2: Remove the extracted functions from `lib/mail.js`**

Delete the matching function definitions from `lib/mail.js` and add `const { escapeHtml, clean, getRealValue, replacePlaceholders, isValidEmail, formatDateTime, formatLabel, reminderValue, reminderLine, htmlReminderLine } = require('./mail/templates.js');` at the top.

---

### Task 3: Convert `lib/mail.js` to Barrel + Business Logic

**Files:**
- Modify: `lib/mail.js`

- [ ] **Step 1: Verify all extracted names are documented**

After Tasks 1 and 2, `lib/mail.js` retains: `dispatch`, `refreshPatronEmailBeforeSending`, `noteSkipped`, `suggestionSubmitted`, `alreadyOwned`, `purchaseApproved`, `rejected`, `holdPlaced`, `autoRejected`, `purchaseReminder`, `additionalCopyReminder`, `staffReminder`, `sendAssignmentNotification`.

- [ ] **Step 2: Remove the duplicate `escapeHtml` definition**

`lib/mail.js` currently has `escapeHtml` defined twice (lines 13-20 and lines 319-326). After importing from `templates.js`, remove both local definitions and use the shared one.

---

- [ ] **Step 3: Update `module.exports`**

Create the new barrel export block at the bottom of `lib/mail.js`:

```js
module.exports = {
  // Re-exported from transport
  send,
  recordEmailEvent,
  
  // Kept in mail.js
  suggestionSubmitted,
  alreadyOwned,
  purchaseApproved,
  rejected,
  holdPlaced,
  autoRejected,
  purchaseReminder,
  additionalCopyReminder,
  sendAssignmentNotification,
  noteSkipped,
  refreshPatronEmailBeforeSending
};
```
*Note: Do not export `dispatch` or `staffReminder`, as they were private helpers in the original file.*

---

### Task 4: Verify

**Files:**
- No additional edits.

- [ ] **Step 1: Run the mail test**

Run:

```bash
node tests/mail.test.js
```

Expected: `All mail.js tests passed!`

- [ ] **Step 2: Run the full suite**

Run:

```bash
npm test
```

Expected: All tests passed. *(Note: `node tests/module_import_paths.test.js` is omitted here because it only scans frontend `pb_public` directories for ES Modules, not backend CommonJS modules like `lib/mail.js`.)*
