ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;

IF NOT EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[SchemaVersion] ([Id], [Version], [UpdatedUtc])
    VALUES (1, 13, SYSUTCDATETIME());
END;
ELSE IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] <> 13)
BEGIN
    THROW 51000, 'This DACPAC requires schema 13.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[DeploymentState] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[DeploymentState] ([Id]) VALUES (1);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[Organization]
        ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive], [LastSyncedUtc])
    VALUES (1, N'System', NULL, NULL, NULL, 1, NULL);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[SystemSettings]
        ([OrganizationId], [SystemNotEnabledMessage], [MisconfiguredMessage], [UpdatedUtc])
    VALUES
        (1,
         N'{{library}} does not currently participate in this suggestion service.',
         N'The {{library}} suggestion system is currently misconfigured. Please contact staff.',
         SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[PolarisSettings]
        ([OrganizationId], [UpdatedUtc])
    VALUES (1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[WorkflowSettings]
        ([OrganizationId], [SuggestionLimit], [SuggestionLimitMessage],
         [OutstandingTimeoutEnabled], [OutstandingTimeoutDays], [OutstandingTimeoutSendEmail],
         [HoldPickupTimeoutEnabled], [HoldPickupTimeoutDays],
         [PendingHoldTimeoutEnabled], [PendingHoldTimeoutDays],
         [AdditionalCopyTimeoutEnabled], [AdditionalCopyTimeoutDays], [AutoPromote],
         [CommonAuthorsEnabled], [CommonAuthorsLabel], [CommonAuthorsHelp], [CommonAuthorsMessage],
         [AllowPatronAutoholdOptOut], [AllowAnyRegisteredCardLogin],
         [PatronCodeEligibilityEnabled], [PatronCodeEligibilityMessage], [UpdatedUtc])
    VALUES
        (1, 5, N'Weekly suggestion limit reached. You can try again after {{next_available_date}}.',
         0, 30, 0, 0, 14, 0, 14, 0, 14, 0,
         0, N'Popular Creators', N'See if this is a creator we already collect.',
         N'We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on ''On Order'' items.',
         1, 0, 0, N'Your patron account is not eligible to submit suggestions.', SYSUTCDATETIME());
END;

DECLARE @PatronSuccess nvarchar(max) = N'You have successfully submitted your material suggestion! Check your email inbox for status updates.' + NCHAR(10) + NCHAR(10) + N'Thank you for using our suggestion service.';
DECLARE @PatronDuplicate nvarchar(max) = N'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.' + NCHAR(10) + NCHAR(10) + N'Thank you for using this library''s suggestion service.';
DECLARE @PatronEbook nvarchar(max) = N'This is an eBook suggestion, please use Libby to notify us of your interest.' + NCHAR(10) + NCHAR(10) + N'Learn how to suggest a purchase using Libby here:' + NCHAR(10) + N'https://help.libbyapp.com/en-us/6260.htm';
DECLARE @PatronEaudiobook nvarchar(max) = N'This is an eAudiobook suggestion, please use Libby to notify us of your interest.' + NCHAR(10) + NCHAR(10) + N'Learn how to suggest a purchase using Libby here:' + NCHAR(10) + N'https://help.libbyapp.com/en-us/6260.htm';

IF NOT EXISTS (SELECT 1 FROM [asap].[PatronSettings] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[PatronSettings]
        ([OrganizationId], [PageTitle], [BarcodeLabel], [PinLabel], [LoginPrompt], [LoginNote],
         [SuggestionFormNote], [NoEmailMessage], [SuccessTitle], [SuccessMessage],
         [AlreadySubmittedMessage], [EbookMessage], [EaudiobookMessage],
         [SuggestionStatusLabel], [OutstandingPurchaseStatusLabel], [PendingHoldStatusLabel],
         [HoldPlacedStatusLabel], [ClosedStatusLabel], [RejectedStatusLabel],
         [HoldCompletedStatusLabel], [HoldNotPickedUpStatusLabel], [ManualStatusLabel],
         [SilentStatusLabel], [UpdatedUtc])
    VALUES
        (1, N'Material Suggestion', N'Library Card', N'Pin', N'', N'', N'',
         N'No email is specified on your library account, which means we won''t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.',
         N'Suggestion Submitted',
         @PatronSuccess, @PatronDuplicate, @PatronEbook, @PatronEaudiobook,
         N'Received', N'Under review', N'Being prepared', N'Hold placed', N'Completed',
         N'Not selected for purchase', N'Completed', N'Closed', N'Closed', N'Closed',
         SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[EmailSettings] ([OrganizationId], [UpdatedUtc])
    VALUES (1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = 1)
    INSERT INTO [asap].[CommonCreatorSet] ([OrganizationId]) VALUES (1);

IF NOT EXISTS (SELECT 1 FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = 1)
    INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (1);

IF NOT EXISTS (SELECT 1 FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[PublicationOptionSet] ([OrganizationId]) VALUES (1);
    INSERT INTO [asap].[PublicationOption]
        ([OrganizationId], [OptionKey], [Label], [IsEnabled], [SortOrder])
    VALUES
        (1, N'already_published', N'Already published', 1, 10),
        (1, N'coming_soon', N'Coming soon', 1, 20),
        (1, N'published_a_while_back', N'Published a while back', 1, 30);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[ExternalSearchProvider] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[ExternalSearchProvider]
        ([OrganizationId], [ProviderKey], [IsEnabled], [Label], [UrlTemplate], [SortOrder])
    VALUES
        (1, N'external_search_1', 1, N'Search Amazon', N'https://www.amazon.com/s?k={{title}}', 10),
        (1, N'external_search_2', 1, N'Search Goodreads', N'https://www.goodreads.com/search?q={{title}}', 20),
        (1, N'external_search_3', 1, N'Search WorldCat', N'https://www.worldcat.org/search?q={{title}}', 30),
        (1, N'external_search_4', 0, N'', N'', 40);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book')
    INSERT INTO [asap].[MaterialFormat]
        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior],
         [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel],
         [PublicationMode], [PublicationLabel], [CreatedUtc], [UpdatedUtc])
    VALUES (1, N'book', N'Book', 10, 1, N'none', N'required', N'Title', N'required', N'Author', N'optional', N'Identifier number', N'required', N'Publication Timing', SYSUTCDATETIME(), SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd')
    INSERT INTO [asap].[MaterialFormat]
        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior],
         [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel],
         [PublicationMode], [PublicationLabel], [CreatedUtc], [UpdatedUtc])
    VALUES (1, N'audiobook_cd', N'Audiobook (Physical CD)', 20, 1, N'none', N'required', N'Title', N'required', N'Author', N'optional', N'Identifier number', N'required', N'Publication Timing', SYSUTCDATETIME(), SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'dvd')
    INSERT INTO [asap].[MaterialFormat]
        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior],
         [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel],
         [PublicationMode], [PublicationLabel], [CreatedUtc], [UpdatedUtc])
    VALUES (1, N'dvd', N'DVD', 30, 1, N'none', N'required', N'Title', N'required', N'Director/Actors/Producer', N'hidden', N'UPC', N'required', N'Publication Timing', SYSUTCDATETIME(), SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'music_cd')
    INSERT INTO [asap].[MaterialFormat]
        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior],
         [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel],
         [PublicationMode], [PublicationLabel], [CreatedUtc], [UpdatedUtc])
    VALUES (1, N'music_cd', N'Music CD', 40, 1, N'none', N'required', N'Title', N'required', N'Artist', N'hidden', N'UPC', N'required', N'Publication Timing', SYSUTCDATETIME(), SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'ebook')
    INSERT INTO [asap].[MaterialFormat]
        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior],
         [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel],
         [PublicationMode], [PublicationLabel], [CreatedUtc], [UpdatedUtc])
    VALUES (1, N'ebook', N'eBook', 50, 1, N'ebookMessage', N'required', N'Title', N'required', N'Author', N'optional', N'Identifier number', N'required', N'Publication Timing', SYSUTCDATETIME(), SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook')
    INSERT INTO [asap].[MaterialFormat]
        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior],
         [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel],
         [PublicationMode], [PublicationLabel], [CreatedUtc], [UpdatedUtc])
    VALUES (1, N'eaudiobook', N'eAudiobook', 60, 1, N'eaudiobookMessage', N'required', N'Title', N'required', N'Author', N'optional', N'Identifier number', N'required', N'Publication Timing', SYSUTCDATETIME(), SYSUTCDATETIME());

IF NOT EXISTS (SELECT 1 FROM [asap].[WorkflowTag] WHERE [Code] = N'duplicate_suggestion')
    INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) VALUES (N'duplicate_suggestion', N'Duplicate suggestion', 10);

IF NOT EXISTS (SELECT 1 FROM [asap].[WorkflowTag] WHERE [Code] = N'polaris_bib_found')
    INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) VALUES (N'polaris_bib_found', N'Polaris BIB found', 20);

IF NOT EXISTS (SELECT 1 FROM [asap].[WorkflowTag] WHERE [Code] = N'polaris_bib_not_found')
    INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) VALUES (N'polaris_bib_not_found', N'Polaris BIB not found', 30);

IF NOT EXISTS (SELECT 1 FROM [asap].[WorkflowTag] WHERE [Code] = N'polaris_multiple_matches')
    INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) VALUES (N'polaris_multiple_matches', N'Multiple Polaris matches', 40);

-- Refresh only a complete, byte-for-byte match to a previously shipped system row.
-- Hold placed is an editor default. The hold workflow composes its own plain-text notice.
DECLARE @lf nchar(1) = NCHAR(10), @paragraph nchar(2) = NCHAR(10) + NCHAR(10);
DECLARE @emailDefaults TABLE (
    TemplateKey nvarchar(100) PRIMARY KEY, DisplayName nvarchar(256), SortOrder int,
    PriorSubject nvarchar(max), PriorBody nvarchar(max), Subject nvarchar(max), Body nvarchar(max)
);
INSERT INTO @emailDefaults (TemplateKey, DisplayName, SortOrder, PriorSubject, PriorBody, Subject, Body)
VALUES
    (N'suggestion_submitted', N'Suggestion submitted', 10,
     N'Suggestion received: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'Thank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.' + @paragraph +
        N'Thank you for helping us shape the library collection.',
     N'Suggestion received: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'Thank you for your suggestion. We received your request for the following item.' + @paragraph +
        N'Title: {{title}} by {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'Our collection development team will review your suggestion.'),
    (N'purchase_approved', N'Purchase approved', 20,
     N'Purchase approved: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'Good news. The library has approved your suggestion for purchase: {{title}} by {{author}} in {{format}} format.' + @paragraph +
        N'This request is now awaiting ordering and cataloging.' + @paragraph +
        N'Thank you for your suggestion.',
     N'Purchase approved: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'The library has approved your suggestion for purchase.' + @paragraph +
        N'Title: {{title}} by {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'The item is awaiting ordering and cataloging.'),
    (N'already_owned', N'Already owned', 30,
     N'{{title}} is already available',
     N'Hello {{name}},' + @paragraph +
        N'Thank you for suggesting {{title}} by {{author}} in {{format}} format.' + @paragraph +
        N'The library already owns this title or has it on order.' + @paragraph +
        N'Thank you for using the library''s suggestion service.',
     N'Already owned or on order: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'Thank you for your suggestion. The library already owns this title or has it on order.' + @paragraph +
        N'Title: {{title}} by {{author}}' + @lf + N'Format: {{format}}'),
    (N'hold_placed', N'Hold placed', 40,
     N'Hold placed for {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'{{title}} by {{author}} in {{format}} format is now available in the catalog.' + @paragraph +
        N'We have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.' + @paragraph +
        N'Thank you for your suggestion.',
     N'Hold placed for {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'A hold has been placed on your card ({{barcode}}).' + @paragraph +
        N'Title: {{title}} by {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'Please wait for your pickup notice before visiting the library.'),
    (N'rejected', N'Rejected', 50,
     N'Update on your suggestion: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'Thank you for suggesting {{title}} by {{author}} in {{format}} format.' + @paragraph +
        N'After review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.' + @paragraph +
        N'Thank you for helping us build a collection that reflects our community.',
     N'Update on your suggestion: {{title}}',
     N'Hello {{name}},' + @paragraph +
        N'Thank you for suggesting this item for the collection.' + @paragraph +
        N'Title: {{title}} by {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'After review, we are unable to add it at this time. We appreciate your suggestion.'),
    (N'staff_title_assignment', N'Title request assigned', 60,
     N'ASAP request assigned',
     N'Hello {{recipientName}},' + @paragraph +
        N'{{title}} has been assigned to you.' + @paragraph +
        N'Assigned by: {{actorName}}' + @lf + N'Assignee: {{assigneeName}}' + @lf + N'Library: {{libraryName}}' + @lf + N'Title request: {{requestId}}' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author/creator: {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'View request: {{requestUrl}}',
     N'Title request assigned: {{title}}',
     N'Hello {{recipientName}},' + @paragraph +
        N'A title request at {{libraryName}} has been assigned to you.' + @paragraph +
        N'Assigned by: {{actorName}}' + @lf + N'Assignee: {{assigneeName}}' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author / creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'Request ID: {{requestId}}' + @paragraph +
        N'View request in ASAP: {{requestUrl}}'),
    (N'staff_purchase_reminder', N'Purchase reminder', 70,
     N'ASAP purchase reminder',
     N'Hello {{recipientName}},' + @paragraph +
        N'Purchase requested for {{title}}.' + @paragraph +
        N'Requested by: {{actorName}}' + @lf + N'Library: {{libraryName}}' + @lf + N'Title request: {{requestId}}' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author/creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'BIB: {{bibId}}' + @paragraph +
        N'View request: {{requestUrl}}',
     N'Purchase reminder: {{title}}',
     N'Hello {{recipientName}},' + @paragraph +
        N'Please follow up on this purchase for {{libraryName}}.' + @paragraph +
        N'Requested by: {{actorName}}' + @lf + N'Title: {{title}}' + @lf + N'Author / creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'BIB: {{bibId}}' + @lf + N'Request ID: {{requestId}}' + @paragraph +
        N'View request in ASAP: {{requestUrl}}'),
    (N'staff_additional_copy_reminder', N'Additional-copy reminder', 80,
     N'ASAP additional-copy reminder',
     N'Hello {{recipientName}},' + @paragraph +
        N'Additional-copy task {{taskId}} for {{title}} (BIB {{bibId}}) was created.' + @paragraph +
        N'Created by: {{actorName}}' + @lf + N'Library: {{libraryName}}' + @lf + N'Title request: {{requestId}}' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author/creator: {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'View task: {{requestUrl}}',
     N'Additional-copy reminder: {{title}}',
     N'Hello {{recipientName}},' + @paragraph +
        N'An additional-copy task was created for {{libraryName}}. Please review the purchase details.' + @paragraph +
        N'Created by: {{actorName}}' + @lf + N'Title: {{title}}' + @lf + N'Author / creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'BIB: {{bibId}}' + @lf + N'Task ID: {{taskId}}' + @lf + N'Request ID: {{requestId}}' + @paragraph +
        N'View additional-copy task in ASAP: {{requestUrl}}'),
    (N'staff_additional_copy_assignment', N'Additional-copy task assigned', 90,
     N'ASAP additional-copy task assigned',
     N'Hello {{recipientName}},' + @paragraph +
        N'Additional-copy task {{taskId}} for {{title}} has been assigned to you.' + @paragraph +
        N'Assigned by: {{actorName}}' + @lf + N'Assignee: {{assigneeName}}' + @lf + N'Library: {{libraryName}}' + @lf + N'Title request: {{requestId}}' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author/creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'BIB: {{bibId}}' + @paragraph +
        N'View task: {{requestUrl}}',
     N'Additional-copy task assigned: {{title}}',
     N'Hello {{recipientName}},' + @paragraph +
        N'An additional-copy task at {{libraryName}} has been assigned to you.' + @paragraph +
        N'Assigned by: {{actorName}}' + @lf + N'Assignee: {{assigneeName}}' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author / creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'BIB: {{bibId}}' + @lf + N'Task ID: {{taskId}}' + @lf + N'Request ID: {{requestId}}' + @paragraph +
        N'View additional-copy task in ASAP: {{requestUrl}}'),
    (N'staff_new_patron_request', N'New patron purchase request', 65,
     N'New purchase request: {{title}}',
     N'Hello {{recipientName}},' + @paragraph +
        N'A new patron purchase request was submitted to {{libraryName}}.' + @paragraph +
        N'Title request: {{requestId}}' + @lf + N'Title: {{title}}' + @lf + N'Author/creator: {{author}}' + @lf + N'Format: {{format}}' + @paragraph +
        N'View request: {{requestUrl}}',
     N'New purchase request — {{title}}',
     N'Hello {{recipientName}},' + @paragraph +
        N'A patron submitted a new purchase request to {{libraryName}}.' + @paragraph +
        N'Title: {{title}}' + @lf + N'Author / creator: {{author}}' + @lf + N'Format: {{format}}' + @lf + N'Request ID: {{requestId}}' + @paragraph +
        N'View request in ASAP: {{requestUrl}}'),
    (N'staff_weekly_summary', N'Weekly action summary', 100,
     N'Weekly ASAP action summary: {{submissionCount}} new, {{purchaseCount}} awaiting bibs, {{additionalCopyCount}} additional copies',
     N'Hello {{recipientName}},' + @paragraph +
        N'Weekly ASAP action summary for {{libraryName}}.' + @lf + N'Business period: {{periodStart}} through {{periodEnd}}' + @paragraph +
        N'New submissions' + @lf + N'{{submissionCount}} active requests' + @lf + N'Five most recent:' + @lf + N'{{submissionItems}}' + @lf + N'View new submissions: {{submissionQueueUrl}}' + @paragraph +
        N'Approved purchases without bibs' + @lf + N'{{purchaseCount}} active requests' + @lf + N'Five most recent:' + @lf + N'{{purchaseItems}}' + @lf + N'View purchases awaiting BIBs: {{purchaseQueueUrl}}' + @paragraph +
        N'Additional copies' + @lf + N'{{additionalCopyCount}} open tasks' + @lf + N'Five most recent:' + @lf + N'{{additionalCopyItems}}' + @lf + N'View additional copies: {{additionalCopiesQueueUrl}}',
     N'Weekly ASAP summary: {{submissionCount}} new, {{purchaseCount}} awaiting BIBs, {{additionalCopyCount}} additional copies',
     N'Hello {{recipientName}},' + @paragraph +
        N'Here is the weekly action summary for {{libraryName}}.' + @lf + N'Business period: {{periodStart}} through {{periodEnd}}' + @paragraph +
        N'New submissions' + @lf + N'Active requests: {{submissionCount}}' + @lf + N'Recent items (up to five):' + @lf + N'{{submissionItems}}' + @lf + N'View new submissions: {{submissionQueueUrl}}' + @paragraph +
        N'Purchases awaiting BIBs' + @lf + N'Active requests: {{purchaseCount}}' + @lf + N'Recent items (up to five):' + @lf + N'{{purchaseItems}}' + @lf + N'View purchases awaiting BIBs: {{purchaseQueueUrl}}' + @paragraph +
        N'Additional copies' + @lf + N'Open tasks: {{additionalCopyCount}}' + @lf + N'Recent items (up to five):' + @lf + N'{{additionalCopyItems}}' + @lf + N'View Additional copies: {{additionalCopiesQueueUrl}}');

UPDATE t SET SubjectTemplate = d.Subject, BodyTemplate = d.Body
FROM [asap].[EmailTemplate] AS t
JOIN @emailDefaults AS d ON t.TemplateKey = d.TemplateKey
CROSS APPLY (
    SELECT d.PriorBody
    UNION ALL
    SELECT N'Hello {{name}},' + @paragraph +
        N'Thank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.' + @paragraph +
        N'If we add this item, we will place a hold for you automatically and send another update.' + @paragraph +
        N'Thank you for helping us shape the library collection.'
    WHERE d.TemplateKey = N'suggestion_submitted'
) AS historical(Body)
WHERE t.OrganizationId = 1 AND t.SourceTemplateId IS NULL AND t.IsCustom = 0 AND t.IsHidden = 0
  AND t.SortOrder = d.SortOrder
  AND CONVERT(varbinary(max), t.TemplateKey) = CONVERT(varbinary(max), d.TemplateKey)
  AND CONVERT(varbinary(max), t.DisplayName) = CONVERT(varbinary(max), d.DisplayName)
  AND CONVERT(varbinary(max), t.SubjectTemplate) = CONVERT(varbinary(max), d.PriorSubject)
  AND CONVERT(varbinary(max), t.BodyTemplate) = CONVERT(varbinary(max), historical.Body)
  AND DATALENGTH(t.TemplateKey) = DATALENGTH(d.TemplateKey)
  AND DATALENGTH(t.DisplayName) = DATALENGTH(d.DisplayName)
  AND DATALENGTH(t.SubjectTemplate) = DATALENGTH(d.PriorSubject)
  AND DATALENGTH(t.BodyTemplate) = DATALENGTH(historical.Body);

INSERT INTO [asap].[EmailTemplate]
    (OrganizationId, TemplateKey, DisplayName, SubjectTemplate, BodyTemplate, IsHidden, IsCustom, SortOrder)
SELECT 1, d.TemplateKey, d.DisplayName, d.Subject, d.Body, 0, 0, d.SortOrder
FROM @emailDefaults AS d
WHERE NOT EXISTS (
    SELECT 1 FROM [asap].[EmailTemplate] AS t WHERE t.OrganizationId = 1 AND t.TemplateKey = d.TemplateKey
)
ORDER BY d.SortOrder;
