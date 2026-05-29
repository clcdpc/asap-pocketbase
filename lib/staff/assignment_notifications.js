const mail = require(`${__hooks}/../lib/mail.js`);

function sendAssignmentNotificationAfterCommit(app, result) {
  try {
    if (!result || !result.assigneeId || !result.record || !result.actor) {
      return;
    }

    var assignee = app.findRecordById("staff_users", result.assigneeId);

    mail.sendAssignmentNotification(app, assignee, result.record, result.actor, {
      type: result.type || "title_request"
    });
  } catch (mailErr) {
    app.logger().error(
      "Failed to send assignment notification",
      "recordId", result && result.record && result.record.id,
      "assigneeId", result && result.assigneeId,
      "type", result && result.type,
      "error", String(mailErr)
    );
  }
}

module.exports = {
  sendAssignmentNotificationAfterCommit: sendAssignmentNotificationAfterCommit
};
