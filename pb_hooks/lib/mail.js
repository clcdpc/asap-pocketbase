const config = require(`${__hooks}/lib/config.js`);

function clean(value) {
  return String(value === undefined || value === null ? "" : value).replace(/[<>]/g, "");
}

function send(app, to, subject, text, html) {
  to = String(to || "").trim();
  if (!to) {
    return false;
  }

  var cfg = config.mail();
  var settings = app.settings();
  var fromAddress = cfg.from || settings.meta.senderAddress;
  if (!fromAddress) {
    app.logger().warn("Email skipped because no sender address is configured", "to", to, "subject", subject);
    return false;
  }

  var message = new MailerMessage({
    from: { address: fromAddress, name: cfg.fromName || settings.meta.senderName || "Library Collection Development" },
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
  var title = clean(record.get("title"));
  var author = clean(record.get("author"));
  var format = formatLabel(record.get("format"));
  var name = (firstName + " " + lastName).trim() || "Library Patron";
  var libraryOrgId = record.get("libraryOrgId");
  var tpl = config.libraryEmails(app, libraryOrgId).suggestion_submitted;
  var data = { 
    name: name, firstName: firstName, lastName: lastName, 
    title: title, author: author, format: format 
  };
  
  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion Has Been Submitted", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, record.get("email"), subject, text, html);
}

function alreadyOwned(app, record, patron) {
  var title = clean(record.get("title"));
  var author = clean(record.get("author"));
  var format = formatLabel(record.get("format"));
  var barcode = clean(record.get("barcode"));
  var name = patronName(record, patron);
  var firstName = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var lastName = clean((patron && patron.NameLast) || record.get("nameLast"));
  var libraryOrgId = record.get("libraryOrgId");
  var tpl = config.libraryEmails(app, libraryOrgId).already_owned;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format, barcode: barcode 
  };

  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, emailFor(record, patron), subject, text, html);
}

function rejected(app, record, patron) {
  var title = clean(record.get("title"));
  var author = clean(record.get("author"));
  var format = formatLabel(record.get("format"));
  var name = patronName(record, patron);
  var firstName = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var lastName = clean((patron && patron.NameLast) || record.get("nameLast"));
  var libraryOrgId = record.get("libraryOrgId");
  var tpl = config.libraryEmails(app, libraryOrgId).rejected;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format 
  };

  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, emailFor(record, patron), subject, text, html);
}

function holdPlaced(app, record, patron) {
  var title = clean(record.get("title"));
  var author = clean(record.get("author"));
  var format = formatLabel(record.get("format"));
  var barcode = clean(record.get("barcode"));
  var name = patronName(record, patron);
  var firstName = clean((patron && patron.NameFirst) || record.get("nameFirst"));
  var lastName = clean((patron && patron.NameLast) || record.get("nameLast"));
  var libraryOrgId = record.get("libraryOrgId");
  var tpl = config.libraryEmails(app, libraryOrgId).hold_placed;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format, barcode: barcode 
  };

  var subject = replacePlaceholders(tpl.subject || "Hold Placed for the Material You Suggested", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, emailFor(record, patron), subject, text, html);
}

function autoRejected(app, record) {
  var title = clean(record.get("title"));
  var author = clean(record.get("author"));
  var format = formatLabel(record.get("format"));
  var firstName = clean(record.get("nameFirst"));
  var lastName = clean(record.get("nameLast"));
  var name = (firstName + " " + lastName).trim() || "Library Patron";
  var libraryOrgId = record.get("libraryOrgId");
  var tpl = config.libraryEmails(app, libraryOrgId).rejected;
  var data = { 
    name: name, firstName: firstName, lastName: lastName,
    title: title, author: author, format: format 
  };

  var subject = replacePlaceholders(tpl.subject || "Your Material Purchase Suggestion", data);
  var text = replacePlaceholders(tpl.body || "", data);
  var html = text.replace(/\n/g, "<br>");

  return send(app, record.get("email"), subject, text, html);
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

