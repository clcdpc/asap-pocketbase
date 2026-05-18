const normalization = require("./normalization.js");
const cloneJson = normalization.cloneJson;

const DEFAULT_DUPLICATE_STATUS_LABELS = {
  suggestion: "Received",
  outstanding_purchase: "Under review",
  pending_hold: "Being prepared",
  hold_placed: "Hold placed",
  closed: "Completed",
  rejected: "Not selected for purchase",
  hold_completed: "Completed",
  hold_not_picked_up: "Closed",
  manual: "Closed",
  silent: "Closed",
  "Silently Closed": "Closed"
};

const DEFAULT_EMAILS = {
  suggestion_submitted: {
    subject: "Suggestion received: {{title}}",
    body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection."
  },
  already_owned: {
    subject: "{{title}} is already available",
    body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order. We have placed a hold on card {{barcode}} so you will be notified when it is ready.\n\nThank you for using the library's suggestion service."
  },
  rejected: {
    subject: "Update on your suggestion: {{title}}",
    body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community."
  },
  hold_placed: {
    subject: "Hold placed for {{title}}",
    body: "Hello {{name}},\n\nGood news. The library plans to add {{title}} by {{author}} in {{format}} format.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion."
  }
};

const EMAIL_TEMPLATE_FIELDS = ["subject", "body"];

function defaultDuplicateStatusLabels() {
  return cloneJson(DEFAULT_DUPLICATE_STATUS_LABELS);
}

function defaultEmailTemplates() {
  return cloneJson(DEFAULT_EMAILS);
}

module.exports = {
  DEFAULT_DUPLICATE_STATUS_LABELS: DEFAULT_DUPLICATE_STATUS_LABELS,
  DEFAULT_EMAILS: DEFAULT_EMAILS,
  EMAIL_TEMPLATE_FIELDS: EMAIL_TEMPLATE_FIELDS,
  defaultDuplicateStatusLabels: defaultDuplicateStatusLabels,
  defaultEmailTemplates: defaultEmailTemplates,
};
