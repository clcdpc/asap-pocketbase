
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);

const orgs = require(`${__hooks}/../lib/orgs.js`);
const identity = require(`${__hooks}/../lib/identity.js`);


function titleRequestActionContext(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = e.request.pathValue("id");
  var data = routeUtils.body(e);
  var action = String(data.action || "");
  var nextStatus = records.normalizeStatus(data.status);
  var record;

  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (findErr) {
    return {
      response: e.json(404, { message: "Suggestion not found: " + id })
    };
  }

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return { response: accessError };
  }

  var oldStatus = records.normalizeStatus(record.get("status"));
  var originalIdentifier = String(record.get("identifier") || "").trim();
  var nextIdentifier = data.identifier !== undefined && data.identifier !== null
    ? String(data.identifier).trim()
    : originalIdentifier;
  var originalFormat = records.normalizeFormat(record.get("format"));
  var nextFormat = data.format !== undefined && data.format !== null
    ? records.normalizeFormat(data.format)
    : originalFormat;
  var originalBibId = String(record.get("bibid") || "").trim();
  var nextBibId = data.bibid !== undefined && data.bibid !== null
    ? String(data.bibid).trim()
    : originalBibId;

  return {
    response: null,
    staff: staff,
    id: id,
    data: data,
    action: action,
    record: record,
    oldStatus: oldStatus,
    nextStatus: nextStatus,
    isClosingRequest: nextStatus === records.STATUS.CLOSED,
    isDuplicateClose: action === "closeDuplicate",
    isActiveHoldTarget: nextStatus === records.STATUS.PENDING_HOLD || nextStatus === records.STATUS.HOLD_PLACED || action === "alreadyOwn" || action === "additionalCopy",
    duplicateCloseNoteAdded: false,
    originalFormat: originalFormat,
    formatChanged: nextFormat !== originalFormat,
    shouldRunImmediatePromoter: (!!nextIdentifier && nextIdentifier !== originalIdentifier) || (!!nextBibId && nextBibId !== originalBibId)
  };
}



module.exports = {
  titleRequestActionContext
};
