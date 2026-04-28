const config = require(`${__hooks}/lib/config.js`);


function getRealValue(combinedStr) {
  if (!combinedStr) return combinedStr;
  var parenIndex = combinedStr.indexOf(" (");
  if (parenIndex > 0) {
      return combinedStr.substring(0, parenIndex).trim();
  }
  return combinedStr.trim();
}

function clean(value) {

  return String(value === undefined || value === null ? "" : value).replace(/[<>]/g, "");
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
  
  if (!fromAddress) {
    app.logger().warn("Email skipped because no sender address is configured", "to", to, "subject", subject);
    return false;
  }

  var message = new MailerMessage({
    from: { address: fromAddress, name: fromName },
    to: [{ address: to, name: "Library Patron" }],
    subject: subject,
    text: text,
    html: html,
  });
  app.newMailClient().send(message);
  return true;
}

function replacePlaceholders(template, data) {
  if (!template) return "";
  return template.replace(/{{(\w+)}}/g, (match, key) => {
    return data[key] !== undefined ? data[key] : match;
  });
}

function suggestionSubmitted(app, record) {
  var firstName = clean(record.get("nameFirst"));
  var lastName = clean(record.get("nameLast"));
  var title = getRealValue(clean(record.get("title")));
  var author = getRealValue(clean(record.get("author")));
  var format = formatLabel(record.get("format"));
  var name = (firstName + " " + lastName).trim() || "Library Patron";
  var libraryOrgId = record.get("libraryOrgId");
  var emailsConfig = config.librarySettings(app, libraryOrgId).emails;
  var tpl = emailsConfig.suggestion_submitted;
  var data = { 
    name: name, firstName: firstName, lastName: lastName, 
    title: title, author: author, format: format 
  };
  
  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion Has Been Submitted", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, record.get("email"), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName });
}

function alreadyOwned(app, record, patron) {
  var title = getRealValue(clean(record.get("title")));
  var author = getRealValue(clean(record.get("author")));
  var format = formatLabel(record.get("format"));
  var barcode = clean(record.get("barcode"));
  var name = patronName(record, patron);
  var firstName = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var lastName = clean((patron && patron.NameLast) || record.get("nameLast"));
  var libraryOrgId = record.get("libraryOrgId");
  var emailsConfig = config.librarySettings(app, libraryOrgId).emails;
  var tpl = emailsConfig.already_owned;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format, barcode: barcode 
  };

  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, emailFor(record, patron), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName });
}

function rejected(app, record, patron, templateId) {
  var title = getRealValue(clean(record.get("title")));
  var author = getRealValue(clean(record.get("author")));
  var format = formatLabel(record.get("format"));
  var name = patronName(record, patron);
  var firstName = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var lastName = clean((patron && patron.NameLast) || record.get("nameLast"));
  var libraryOrgId = record.get("libraryOrgId");
  var emailsConfig = config.librarySettings(app, libraryOrgId).emails;

  var tpl = emailsConfig.rejected;
  if (templateId && emailsConfig.rejection_templates && Array.isArray(emailsConfig.rejection_templates)) {
    for (var i = 0; i < emailsConfig.rejection_templates.length; i++) {
      if (emailsConfig.rejection_templates[i].id === templateId) {
        tpl = emailsConfig.rejection_templates[i];
        break;
      }
    }
  }

  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format 
  };

  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, emailFor(record, patron), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName });
}

function holdPlaced(app, record, patron) {
  var title = getRealValue(clean(record.get("title")));
  var author = getRealValue(clean(record.get("author")));
  var format = formatLabel(record.get("format"));
  var barcode = clean(record.get("barcode"));
  var name = patronName(record, patron);
  var firstName = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var lastName = clean((patron && patron.NameLast) || record.get("nameLast"));
  var libraryOrgId = record.get("libraryOrgId");
  var emailsConfig = config.librarySettings(app, libraryOrgId).emails;
  var tpl = emailsConfig.hold_placed;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format, barcode: barcode 
  };

  var subject = replacePlaceholders(tpl.subject || "Hold Placed for the Material You Suggested", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, emailFor(record, patron), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName });
}

function autoRejected(app, record) {
  var title = getRealValue(clean(record.get("title")));
  var author = getRealValue(clean(record.get("author")));
  var format = formatLabel(record.get("format"));
  var firstName = clean(record.get("nameFirst"));
  var lastName = clean(record.get("nameLast"));
  var name = (firstName + " " + lastName).trim() || "Library Patron";
  var libraryOrgId = record.get("libraryOrgId");
  var emailsConfig = config.librarySettings(app, libraryOrgId).emails;
  var tpl = emailsConfig.rejected;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format 
  };

  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, record.get("email"), subject, text, html, { fromAddress: emailsConfig.fromAddress, fromName: emailsConfig.fromName });
}

function emailFor(record, patron) {
  return clean((patron && patron.EmailAddress) || record.get("email"));
}

function patronName(record, patron) {
  var first = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var last = clean((patron && patron.NameLast) || record.get("nameLast"));
  return (first + " " + last).trim() || "Library Patron";
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
  rejected: rejected,
  send: send,
  suggestionSubmitted: suggestionSubmitted,
};

