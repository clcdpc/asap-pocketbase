const config = require(`${__hooks}/lib/config.js`);


function getRealValue(combinedStr) {
  if (!combinedStr) return combinedStr;
  var parenIndex = combinedStr.indexOf(" (");
  if (parenIndex > 0) {
      return combinedStr.substring(0, parenIndex).trim();
  }
  return combinedStr.trim();
}

function escapeHtml(str) {
  return String(str || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function send(app, to, subject, text, html, options) {
  to = String(to || "").trim();
  if (!to) {
    return false;
  }

  options = options || {};
  var settings = app.settings();
  var fromAddress = options.fromAddress || settings.meta.senderAddress;
  var fromName = options.fromName || settings.meta.senderName || "Library Collection Development";
  var smtp = config.mail();
  
  if (!String(smtp.host || "").trim() || !fromAddress) {
    app.logger().warn("Email skipped because notifications are not configured", "to", to, "subject", subject);
    recordEmailEvent(app, options.record, options.templateKey, to, subject, "skipped", "Email notifications are not configured.");
    return false;
  }

  try {
    var message = new MailerMessage({
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
    var event = new Record(app.findCollectionByNameOrId("email_delivery_events"));
    if (record && record.id) event.set("titleRequest", record.id);
    event.set("templateKey", templateKey || "");
    event.set("recipient", String(to || ""));
    event.set("subject", String(subject || ""));
    event.set("status", status || "sent");
    event.set("error", error || "");
    app.save(event);
  } catch (err) {}
}

function replacePlaceholders(template, data, escape) {
  if (!template) return "";
  return template.replace(/{{(\w+)}}/g, (match, key) => {
    var val = data[key] !== undefined ? data[key] : match;
    return escape ? escapeHtml(val) : val;
  });
}

function dispatch(app, record, patron, templateKey, defaultSubject, templateId) {
  var refreshedPatronEmail = refreshPatronEmailBeforeSending(app, record);
  var rawTitle = getRealValue(record.get("title"));
  var rawAuthor = getRealValue(record.get("author"));
  var format = formatLabel(record.get("format"));
  var barcode = record.get("barcode");
  var firstName = (patron && patron.NameFirst) || record.get("nameFirst");
  var lastName = (patron && patron.NameLast) || record.get("nameLast");
  var name = (String(firstName || "") + " " + String(lastName || "")).trim() || "Library Patron";
  
  var libraryOrgId = record.get("libraryOrgId");
  var emailsConfig = config.librarySettings(app, libraryOrgId).emails;

  var tpl = emailsConfig[templateKey] || {};
  if (templateId && templateKey === "rejected" && emailsConfig.rejection_templates && Array.isArray(emailsConfig.rejection_templates)) {
    for (var i = 0; i < emailsConfig.rejection_templates.length; i++) {
      if (emailsConfig.rejection_templates[i].id === templateId) {
        tpl = emailsConfig.rejection_templates[i];
        break;
      }
    }
  }

  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: rawTitle, author: rawAuthor, format: format,
    barcode: barcode || ""
  };

  var subject = replacePlaceholders(tpl.subject || defaultSubject, data, false);
  var text = replacePlaceholders(tpl.body || "", data, false);
  
  // For HTML, we escape the values but NOT the template (since templates may contain safe HTML like <br> or <div>)
  // Wait, if templates contain HTML, we can't just escape the whole thing.
  // The current implementation of replacePlaceholders with escape: true is correct for placeholders.
  var html = replacePlaceholders(tpl.body || "", data, true).replace(/\n/g, "<br>");

  return send(app, refreshedPatronEmail || record.get("email"), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName, record: record, templateKey: templateKey });
}

function refreshPatronEmailBeforeSending(app, record) {
  if (!app || !record) {
    return "";
  }
  var barcode = String(record.get("barcode") || "").trim();
  if (!barcode) {
    return "";
  }

  try {
    var polaris = require(`${__hooks}/lib/polaris.js`);
    var patron = polaris.lookupPatron(polaris.adminStaffAuth(), barcode);
    var currentEmail = String(patron && patron.EmailAddress || "").trim();
    if (!isValidEmail(currentEmail)) {
      return "";
    }

    var storedEmail = String(record.get("email") || "").trim();
    if (storedEmail !== currentEmail) {
      record.set("email", currentEmail);
      try {
        var records = require(`${__hooks}/lib/records.js`);
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

function isValidEmail(value) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(String(value || "").trim());
}

function noteSkipped(app, record) {
  if (!record) {
    return;
  }
  const records = require(`${__hooks}/lib/records.js`);
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

function rejected(app, record, patron, templateId) {
  return dispatch(app, record, patron, "rejected", "Your Material Purchase Suggestion", templateId);
}

function holdPlaced(app, record, patron) {
  return dispatch(app, record, patron, "hold_placed", "Hold Placed for the Material You Suggested");
}

function autoRejected(app, record, templateId) {
  return dispatch(app, record, null, "rejected", "Your Material Purchase Suggestion", templateId);
}

function emailFor(record, patron) {
  return (patron && patron.EmailAddress) || record.get("email");
}

function patronName(record, patron) {
  var first = (patron && patron.NameFirst) || record.get("nameFirst");
  var last = (patron && patron.NameLast) || record.get("nameLast");
  return (String(first || "") + " " + String(last || "")).trim() || "Library Patron";
}


function formatLabel(value) {
  var ui = config.uiText();
  var labels = ui.formatLabels || {
    book: "Book",
    ebook: "eBook",
    audiobook_cd: "Audiobook (Physical CD)",
    eaudiobook: "eAudiobook",
    dvd: "DVD",
    music_cd: "Music CD",
  };
  return labels[value] || clean(value);
}

module.exports = {
  alreadyOwned: alreadyOwned,
  autoRejected: autoRejected,
  holdPlaced: holdPlaced,
  noteSkipped: noteSkipped,
  rejected: rejected,
  recordEmailEvent: recordEmailEvent,
  refreshPatronEmailBeforeSending: refreshPatronEmailBeforeSending,
  send: send,
  suggestionSubmitted: suggestionSubmitted,
};
