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
            "Hello {{name}},\n\nThank you for your suggestion. We received your request for the following item.\n\nTitle: {{title}} by {{author}}\nFormat: {{format}}\n\nOur collection development team will review your suggestion.", 10),
        new("purchase_approved", "Purchase approved", "Purchase approved: {{title}}",
            "Hello {{name}},\n\nThe library has approved your suggestion for purchase.\n\nTitle: {{title}} by {{author}}\nFormat: {{format}}\n\nThe item is awaiting ordering and cataloging.", 20),
        new("already_owned", "Already owned", "Already owned or on order: {{title}}",
            "Hello {{name}},\n\nThank you for your suggestion. The library already owns this title or has it on order.\n\nTitle: {{title}} by {{author}}\nFormat: {{format}}", 30),
        new("hold_placed", "Hold placed", "Hold placed for {{title}}",
            "Hello {{name}},\n\nA hold has been placed on your card ({{barcode}}).\n\nTitle: {{title}} by {{author}}\nFormat: {{format}}\n\nPlease wait for your pickup notice before visiting the library.", 40),
        new("rejected", "Rejected", "Update on your suggestion: {{title}}",
            "Hello {{name}},\n\nThank you for suggesting this item for the collection.\n\nTitle: {{title}} by {{author}}\nFormat: {{format}}\n\nAfter review, we are unable to add it at this time. We appreciate your suggestion.", 50)
    ];

    internal static readonly Template[] Staff =
    [
        new("staff_title_assignment", "Title request assigned", "Title request assigned: {{title}}",
            "Hello {{recipientName}},\n\nA title request at {{libraryName}} has been assigned to you.\n\nAssigned by: {{actorName}}\nAssignee: {{assigneeName}}\n\nTitle: {{title}}\nAuthor / creator: {{author}}\nFormat: {{format}}\nRequest ID: {{requestId}}\n\nView request in ASAP: {{requestUrl}}", 60),
        new("staff_purchase_reminder", "Purchase reminder", "Purchase reminder: {{title}}",
            "Hello {{recipientName}},\n\nPlease follow up on this purchase for {{libraryName}}.\n\nRequested by: {{actorName}}\nTitle: {{title}}\nAuthor / creator: {{author}}\nFormat: {{format}}\nBIB: {{bibId}}\nRequest ID: {{requestId}}\n\nView request in ASAP: {{requestUrl}}", 70),
        new("staff_additional_copy_reminder", "Additional-copy reminder", "Additional-copy reminder: {{title}}",
            "Hello {{recipientName}},\n\nAn additional-copy task was created for {{libraryName}}. Please review the purchase details.\n\nCreated by: {{actorName}}\nTitle: {{title}}\nAuthor / creator: {{author}}\nFormat: {{format}}\nBIB: {{bibId}}\nTask ID: {{taskId}}\nRequest ID: {{requestId}}\n\nView additional-copy task in ASAP: {{requestUrl}}", 80),
        new("staff_additional_copy_assignment", "Additional-copy task assigned", "Additional-copy task assigned: {{title}}",
            "Hello {{recipientName}},\n\nAn additional-copy task at {{libraryName}} has been assigned to you.\n\nAssigned by: {{actorName}}\nAssignee: {{assigneeName}}\n\nTitle: {{title}}\nAuthor / creator: {{author}}\nFormat: {{format}}\nBIB: {{bibId}}\nTask ID: {{taskId}}\nRequest ID: {{requestId}}\n\nView additional-copy task in ASAP: {{requestUrl}}", 90),
        new("staff_new_patron_request", "New patron purchase request", "New purchase request — {{title}}",
            "Hello {{recipientName}},\n\nA patron submitted a new purchase request to {{libraryName}}.\n\nTitle: {{title}}\nAuthor / creator: {{author}}\nFormat: {{format}}\nRequest ID: {{requestId}}\n\nView request in ASAP: {{requestUrl}}", 65),
        new("staff_weekly_summary", "Weekly action summary", "Weekly ASAP summary: {{submissionCount}} new, {{purchaseCount}} awaiting BIBs, {{additionalCopyCount}} additional copies",
            "Hello {{recipientName}},\n\nHere is the weekly action summary for {{libraryName}}.\nBusiness period: {{periodStart}} through {{periodEnd}}\n\nNew submissions\nActive requests: {{submissionCount}}\nRecent items (up to five):\n{{submissionItems}}\nView new submissions: {{submissionQueueUrl}}\n\nPurchases awaiting BIBs\nActive requests: {{purchaseCount}}\nRecent items (up to five):\n{{purchaseItems}}\nView purchases awaiting BIBs: {{purchaseQueueUrl}}\n\nAdditional copies\nOpen tasks: {{additionalCopyCount}}\nRecent items (up to five):\n{{additionalCopyItems}}\nView Additional copies: {{additionalCopiesQueueUrl}}", 100)
    ];
}
