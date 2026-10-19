namespace Asap.Tests.Email;

// Product copy is pinned independently of SQL and runtime fallbacks so tests
// catch an accidental edit to either implementation.
internal static class BuiltInEmailTemplateExpectations
{
    internal const string HistoricalSubmissionBody =
        "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection.";

    internal sealed record Template(string Key, string Name, string Subject, string Body, int SortOrder);

    internal static readonly Template[] All =
    [
        new("suggestion_submitted", "Suggestion submitted", "Suggestion received: {{title}}",
            "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nThank you for helping us shape the library collection.", 10),
        new("purchase_approved", "Purchase approved", "Purchase approved: {{title}}",
            "Hello {{name}},\n\nGood news. The library has approved your suggestion for purchase: {{title}} by {{author}} in {{format}} format.\n\nThis request is now awaiting ordering and cataloging.\n\nThank you for your suggestion.", 20),
        new("already_owned", "Already owned", "{{title}} is already available",
            "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order.\n\nThank you for using the library's suggestion service.", 30),
        new("hold_placed", "Hold placed", "Hold placed for {{title}}",
            "Hello {{name}},\n\n{{title}} by {{author}} in {{format}} format is now available in the catalog.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion.", 40),
        new("rejected", "Rejected", "Update on your suggestion: {{title}}",
            "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community.", 50)
    ];
}
