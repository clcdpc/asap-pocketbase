const PURCHASE_APPROVED_SUBJECT = "Purchase approved: {{title}}";
const PURCHASE_APPROVED_BODY = "Hello {{name}},\n\nGood news. The library has approved your suggestion for purchase: {{title}} by {{author}} in {{format}} format.\n\nThis request is now awaiting ordering and cataloging. Once the item is available in the catalog, ASAP will place a hold automatically when possible and send another update.\n\nThank you for your suggestion.";

const OLD_HOLD_PLACED_BODY = "Hello {{name}},\n\nGood news. The library plans to add {{title}} by {{author}} in {{format}} format.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion.";
const NEW_HOLD_PLACED_BODY = "Hello {{name}},\n\n{{title}} by {{author}} in {{format}} format is now available in the catalog.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion.";

const OLD_SUGGESTION_FORM_NOTE = "If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don't see the email.";
const NEW_SUGGESTION_FORM_NOTE = "If the library approves your suggestion for purchase, we will email you while it is awaiting ordering and cataloging. Once the item is available in the catalog, we will automatically place a hold when possible and send another update.";

function findSystemTemplate(app, key) {
  try {
    return app.findFirstRecordByFilter("email_templates", "scope = 'system' && templateKey = {:key}", { key: key });
  } catch (err) {
    return null;
  }
}

function findSystemUiSettings(app) {
  try {
    return app.findFirstRecordByFilter("ui_settings", "scope = 'system'", {});
  } catch (err) {
    try {
      return app.findRecordById("ui_settings", "uisettings00010");
    } catch (idErr) {
      return null;
    }
  }
}

migrate((app) => {
  var purchaseApproved = findSystemTemplate(app, "purchase_approved");
  if (!purchaseApproved) {
    purchaseApproved = new Record(app.findCollectionByNameOrId("email_templates"));
    purchaseApproved.set("scope", "system");
    purchaseApproved.set("templateKey", "purchase_approved");
    purchaseApproved.set("name", "Purchase approved");
    purchaseApproved.set("subject", PURCHASE_APPROVED_SUBJECT);
    purchaseApproved.set("body", PURCHASE_APPROVED_BODY);
    purchaseApproved.set("enabled", true);
    app.save(purchaseApproved);
  }

  var holdPlaced = findSystemTemplate(app, "hold_placed");
  if (holdPlaced && String(holdPlaced.get("body") || "") === OLD_HOLD_PLACED_BODY) {
    holdPlaced.set("body", NEW_HOLD_PLACED_BODY);
    app.save(holdPlaced);
  }

  var uiSettings = findSystemUiSettings(app);
  if (uiSettings && String(uiSettings.get("suggestionFormNote") || "") === OLD_SUGGESTION_FORM_NOTE) {
    uiSettings.set("suggestionFormNote", NEW_SUGGESTION_FORM_NOTE);
    app.save(uiSettings);
  }
}, (app) => {
  var purchaseApproved = findSystemTemplate(app, "purchase_approved");
  if (
    purchaseApproved &&
    String(purchaseApproved.get("subject") || "") === PURCHASE_APPROVED_SUBJECT &&
    String(purchaseApproved.get("body") || "") === PURCHASE_APPROVED_BODY
  ) {
    app.delete(purchaseApproved);
  }

  var holdPlaced = findSystemTemplate(app, "hold_placed");
  if (holdPlaced && String(holdPlaced.get("body") || "") === NEW_HOLD_PLACED_BODY) {
    holdPlaced.set("body", OLD_HOLD_PLACED_BODY);
    app.save(holdPlaced);
  }

  var uiSettings = findSystemUiSettings(app);
  if (uiSettings && String(uiSettings.get("suggestionFormNote") || "") === NEW_SUGGESTION_FORM_NOTE) {
    uiSettings.set("suggestionFormNote", OLD_SUGGESTION_FORM_NOTE);
    app.save(uiSettings);
  }
});
