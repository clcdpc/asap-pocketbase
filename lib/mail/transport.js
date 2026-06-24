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
