const config = require(`${__hooks}/../lib/config.js`);
const { send, recordEmailEvent } = require('./mail/transport.js');
const { escapeHtml, clean, getRealValue, replacePlaceholders, isValidEmail, formatDateTime, formatLabel, reminderValue, reminderLine, htmlReminderLine } = require('./mail/templates.js');

function dispatch(app, record, patron, templateKey, defaultSubject, templateId) {
  const refreshedPatronEmail = refreshPatronEmailBeforeSending(app, record);
  const rawTitle = getRealValue(record.get("title"));
  const rawAuthor = getRealValue(record.get("author"));
  const format = formatLabel(record.get("format"));
  const barcode = record.get("barcode");
  const firstName = (patron && patron.NameFirst) || record.get("nameFirst");
  const lastName = (patron && patron.NameLast) || record.get("nameLast");
  const name = (String(firstName || "") + " " + String(lastName || "")).trim() || "Library Patron";

  const libraryOrgId = record.get("libraryOrgId");
  const emailsConfig = config.librarySettings(app, libraryOrgId).emails;

  let tpl = emailsConfig[templateKey] || {};
  if (templateId && templateKey === "rejected" && emailsConfig.rejection_templates && Array.isArray(emailsConfig.rejection_templates)) {
    for (let i = 0; i < emailsConfig.rejection_templates.length; i++) {
      if (emailsConfig.rejection_templates[i].id === templateId) {
        tpl = emailsConfig.rejection_templates[i];
        break;
      }
    }
  }

  const data = {
    name: name, firstName: firstName, lastName: lastName,
    title: rawTitle, author: rawAuthor, format: format,
    barcode: barcode || ""
  };

  const subject = replacePlaceholders(tpl.subject || defaultSubject, data, false);
  const text = replacePlaceholders(tpl.body || "", data, false);

  const html = replacePlaceholders(tpl.body || "", data, true).replace(/\n/g, "<br>");

  return send(app, refreshedPatronEmail || record.get("email"), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName, record: record, templateKey: templateKey });
}

function refreshPatronEmailBeforeSending(app, record) {
  if (!app || !record) {
    return "";
  }
  const barcode = String(record.get("barcode") || "").trim();
  if (!barcode) {
    return "";
  }

  try {
    const polaris = require(`${__hooks}/../lib/polaris.js`);
    const patron = polaris.lookupPatron(polaris.adminStaffAuth(), barcode);
    try {
      const records = require(`${__hooks}/../lib/records.js`);
      records.cachePolarisPatronId(app, patron);
    } catch (cacheErr) {}
    const currentEmail = String(patron && patron.EmailAddress || "").trim();
    if (!isValidEmail(currentEmail)) {
      return "";
    }

    const storedEmail = String(record.get("email") || "").trim();
    if (storedEmail !== currentEmail) {
      record.set("email", currentEmail);
      try {
        const records = require(`${__hooks}/../lib/records.js`);
        records.appendSystemNote(record, "Patron email updated from Polaris before sending notification.");
      } catch (noteErr) {}
      app.save(record);
    }
    return currentEmail;
  } catch (err) {
    try {
      app.logger().warn("Could not refresh patron email from Polaris before sending notification", "recordId", record.id || "", "barcode", barcode, "error", String(err));
    } catch (logErr) {}
    return "";
  }
}

function noteSkipped(app, record) {
  if (!record) {
    return;
  }
  const records = require(`${__hooks}/../lib/records.js`);
  records.appendSystemNote(record, "Email not sent: email notifications are not configured.");
  app.save(record);
  recordEmailEvent(app, record, "", record.get("email"), "", "skipped", "Email notifications are not configured.");
}

function suggestionSubmitted(app, record) {
  return dispatch(app, record, null, "suggestion_submitted", "Your Material Purchase Suggestion Has Been Submitted");
}

function alreadyOwned(app, record, patron) {
  return dispatch(app, record, patron, "already_owned", "Your Material Purchase Suggestion");
}

function purchaseApproved(app, record, patron) {
  return dispatch(app, record, patron, "purchase_approved", "Purchase approved for the material you suggested");
}

function rejected(app, record, patron, templateId) {
  return dispatch(app, record, patron, "rejected", "Your Material Purchase Suggestion", templateId);
}

function holdPlaced(app, record, patron) {
  return dispatch(app, record, patron, "hold_placed", "Hold Placed for the Material You Suggested");
}

function autoRejected(app, record, templateId) {
  return dispatch(app, record, null, "rejected", "Your Material Purchase Suggestion", templateId);
}

function purchaseReminder(app, record, staff, toEmail, itemUrl) {
  return staffReminder(app, record, staff, toEmail, itemUrl, {
    intro: "You marked this item for purchase in ASAP.",
    subjectPrefix: "Purchase reminder",
    templateKey: "staff_purchase_reminder"
  });
}

function additionalCopyReminder(app, record, staff, toEmail, itemUrl) {
  return staffReminder(app, record, staff, toEmail, itemUrl, {
    intro: "You marked this item for an additional copy in ASAP.",
    subjectPrefix: "Additional copy reminder",
    templateKey: "staff_additional_copy_reminder"
  });
}

function staffReminder(app, record, staff, toEmail, itemUrl, options) {
  options = options || {};
  toEmail = clean(toEmail);
  if (!toEmail) {
    return false;
  }

  const title = getRealValue(record.get("title"));
  const author = getRealValue(record.get("author"));
  const format = formatLabel(record.get("format"));
  const publicationDate = clean(record.get("exactPublicationDate")) || clean(record.get("publication"));
  const publisher = clean(record.get("publisher"));
  const staffName = clean(staff && (staff.get("displayName") || staff.get("username"))) || "Library Staff";
  const generatedAt = formatDateTime(new Date());
  const bibId = clean(record.get("bibid"));
  const controlNumber = clean(record.get("controlNumber"));
  const bibLine = bibId || controlNumber || "";
  const publisherDate = [publisher, publicationDate].filter(Boolean).join(" / ");
  const notes = clean(record.get("notes"));

  const lines = [
    options.intro || "You marked this item for purchase in ASAP.",
    "",
    reminderLine("Title", title),
    reminderLine("Author", author),
    reminderLine("ISBN", record.get("identifier")),
    reminderLine("Format", format),
    reminderLine("Publisher/date", publisherDate),
    reminderLine("Bib ID", bibLine),
    reminderLine("Notes", notes),
    "",
    reminderLine("Staff member", staffName),
    reminderLine("Reminder generated", generatedAt)
  ];
  if (itemUrl) {
    lines.push("", "Open in ASAP: " + itemUrl);
  }

  const html = [
    "<p>" + escapeHtml(options.intro || "You marked this item for purchase in ASAP.") + "</p>",
    htmlReminderLine("Title", title),
    htmlReminderLine("Author", author),
    htmlReminderLine("ISBN", record.get("identifier")),
    htmlReminderLine("Format", format),
    htmlReminderLine("Publisher/date", publisherDate),
    htmlReminderLine("Bib ID", bibLine),
    htmlReminderLine("Notes", notes),
    "<hr>",
    htmlReminderLine("Staff member", staffName),
    htmlReminderLine("Reminder generated", generatedAt)
  ];
  if (itemUrl) {
    html.push('<p><strong>Open in ASAP:</strong> <a href="' + escapeHtml(itemUrl) + '">' + escapeHtml(itemUrl) + "</a></p>");
  }

  return send(app, toEmail, (options.subjectPrefix || "Purchase reminder") + ": " + reminderValue(title), lines.join("\n"), html.join("\n"), {
    recipientName: staffName,
    record: record,
    templateKey: options.templateKey || "staff_purchase_reminder"
  });
}

function sendAssignmentNotification(app, staff, record, actor, options) {
  options = options || {};
  var type = options.type || "title_request";
  var toEmail = String(staff.get("weekly_action_summary_email") || staff.get("email") || "").trim();
  if (!toEmail) return null;

  var title = String(record.get("title") || "Unknown Title").trim();
  var author = String(record.get("author") || "N/A").trim();
  var format = String(record.get("format") || "N/A").trim();
  var actorName = String(actor.get("displayName") || actor.get("username") || "Another staff member").trim();
  var recipientName = String(staff.get("displayName") || staff.get("username") || "Staff").trim();

  var typeLabel = type === "additional_copy" ? "additional-copy task" : "suggestion";
  var subject = "Assigned " + typeLabel + ": " + title;

  var lines = [
    "Hello " + recipientName + ",",
    "",
    actorName + " has assigned an open " + typeLabel + " to you:",
    "",
    "Title: " + title,
    "Author: " + author,
    "Format: " + format,
    ""
  ];

  var html = [
    "<p>Hello " + escapeHtml(recipientName) + ",</p>",
    "<p><strong>" + escapeHtml(actorName) + "</strong> has assigned an open " + escapeHtml(typeLabel) + " to you:</p>",
    "<ul>",
    "<li><strong>Title:</strong> " + escapeHtml(title) + "</li>",
    "<li><strong>Author:</strong> " + escapeHtml(author) + "</li>",
    "<li><strong>Format:</strong> " + escapeHtml(format) + "</li>",
    "</ul>"
  ];

  try {
    var base = config.staffUrl(app);
    var stage = "suggestion";
    if (type === "additional_copy") {
      stage = "additional_copies";
    } else {
      const records = require(`${__hooks}/../lib/records.js`);
      stage = records.normalizeStatus(record.get("status"));
    }
    var sep = base.indexOf("?") >= 0 ? "&" : "?";
    var itemUrl = base + sep + "stage=" + stage + "&request=" + record.id;

    lines.push("Open in ASAP: " + itemUrl, "");
    html.push("<p><strong>Open in ASAP:</strong> <a href=\"" + escapeHtml(itemUrl) + "\">" + escapeHtml(itemUrl) + "</a></p>");
  } catch (err) {}

  lines.push("You can view and process this item in the staff portal.");
  html.push("<p>You can view and process this item in the staff portal.</p>");

  return send(app, toEmail, subject, lines.join("\n"), html.join("\n"), {
    actor: actor,
    record: record
  });
}

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
