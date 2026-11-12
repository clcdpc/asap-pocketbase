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

-- Defaults are missing-row inserts only: preserve saved customizations, blanks,
-- hidden state and library lineage on every subsequent deployment. The three
-- hold promises omitted below are not true for every current workflow.
IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'suggestion_submitted', N'Suggestion submitted',
         N'Suggestion received: {{title}}',
         N'Hello {{name}},' + NCHAR(10) + NCHAR(10) +
         N'Thank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.' + NCHAR(10) + NCHAR(10) +
         N'Thank you for helping us shape the library collection.',
         0, 0, 10);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'purchase_approved')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'purchase_approved', N'Purchase approved',
         N'Purchase approved: {{title}}',
         N'Hello {{name}},' + NCHAR(10) + NCHAR(10) +
         N'Good news. The library has approved your suggestion for purchase: {{title}} by {{author}} in {{format}} format.' + NCHAR(10) + NCHAR(10) +
         N'This request is now awaiting ordering and cataloging.' + NCHAR(10) + NCHAR(10) +
         N'Thank you for your suggestion.',
         0, 0, 20);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'already_owned')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'already_owned', N'Already owned',
         N'{{title}} is already available',
         N'Hello {{name}},' + NCHAR(10) + NCHAR(10) +
         N'Thank you for suggesting {{title}} by {{author}} in {{format}} format.' + NCHAR(10) + NCHAR(10) +
         N'The library already owns this title or has it on order.' + NCHAR(10) + NCHAR(10) +
         N'Thank you for using the library''s suggestion service.',
         0, 0, 30);
END;

-- This editor default has no configured-template dispatch consumer. The hold
-- service continues to compose its existing notice from the frozen pickup.
IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'hold_placed')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'hold_placed', N'Hold placed',
         N'Hold placed for {{title}}',
         N'Hello {{name}},' + NCHAR(10) + NCHAR(10) +
         N'{{title}} by {{author}} in {{format}} format is now available in the catalog.' + NCHAR(10) + NCHAR(10) +
         N'We have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.' + NCHAR(10) + NCHAR(10) +
         N'Thank you for your suggestion.',
         0, 0, 40);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'rejected')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'rejected', N'Rejected',
         N'Update on your suggestion: {{title}}',
         N'Hello {{name}},' + NCHAR(10) + NCHAR(10) +
         N'Thank you for suggesting {{title}} by {{author}} in {{format}} format.' + NCHAR(10) + NCHAR(10) +
         N'After review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.' + NCHAR(10) + NCHAR(10) +
         N'Thank you for helping us build a collection that reflects our community.',
         0, 0, 50);
END;

-- Staff notification defaults are system-owned templates with optional library
-- lineage rows. Keep these inserts missing-row-only so deployments preserve
-- configured content, explicit blanks, hidden state and rowversions.
IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'staff_title_assignment')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'staff_title_assignment', N'Title request assigned',
         N'ASAP request assigned',
         N'Hello {{recipientName}},' + NCHAR(10) + NCHAR(10) +
         N'{{title}} has been assigned to you.' + NCHAR(10) + NCHAR(10) +
         N'Assigned by: {{actorName}}' + NCHAR(10) + N'Assignee: {{assigneeName}}' + NCHAR(10) + N'Library: {{libraryName}}' + NCHAR(10) + N'Title request: {{requestId}}' + NCHAR(10) + NCHAR(10) +
         N'Title: {{title}}' + NCHAR(10) + N'Author/creator: {{author}}' + NCHAR(10) + N'Format: {{format}}' + NCHAR(10) + NCHAR(10) + N'View request: {{requestUrl}}',
         0, 0, 60);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'staff_purchase_reminder')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'staff_purchase_reminder', N'Purchase reminder',
         N'ASAP purchase reminder',
         N'Hello {{recipientName}},' + NCHAR(10) + NCHAR(10) +
         N'Purchase requested for {{title}}.' + NCHAR(10) + NCHAR(10) +
         N'Requested by: {{actorName}}' + NCHAR(10) + N'Library: {{libraryName}}' + NCHAR(10) + N'Title request: {{requestId}}' + NCHAR(10) + NCHAR(10) +
         N'Title: {{title}}' + NCHAR(10) + N'Author/creator: {{author}}' + NCHAR(10) + N'Format: {{format}}' + NCHAR(10) + N'BIB: {{bibId}}' + NCHAR(10) + NCHAR(10) + N'View request: {{requestUrl}}',
         0, 0, 70);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'staff_additional_copy_reminder')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'staff_additional_copy_reminder', N'Additional-copy reminder',
         N'ASAP additional-copy reminder',
         N'Hello {{recipientName}},' + NCHAR(10) + NCHAR(10) +
         N'Additional-copy task {{taskId}} for {{title}} (BIB {{bibId}}) was created.' + NCHAR(10) + NCHAR(10) +
         N'Created by: {{actorName}}' + NCHAR(10) + N'Library: {{libraryName}}' + NCHAR(10) + N'Title request: {{requestId}}' + NCHAR(10) + NCHAR(10) +
         N'Title: {{title}}' + NCHAR(10) + N'Author/creator: {{author}}' + NCHAR(10) + N'Format: {{format}}' + NCHAR(10) + NCHAR(10) + N'View task: {{requestUrl}}',
         0, 0, 80);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'staff_additional_copy_assignment')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'staff_additional_copy_assignment', N'Additional-copy task assigned',
         N'ASAP additional-copy task assigned',
         N'Hello {{recipientName}},' + NCHAR(10) + NCHAR(10) +
         N'Additional-copy task {{taskId}} for {{title}} has been assigned to you.' + NCHAR(10) + NCHAR(10) +
         N'Assigned by: {{actorName}}' + NCHAR(10) + N'Assignee: {{assigneeName}}' + NCHAR(10) + N'Library: {{libraryName}}' + NCHAR(10) + N'Title request: {{requestId}}' + NCHAR(10) + NCHAR(10) +
         N'Title: {{title}}' + NCHAR(10) + N'Author/creator: {{author}}' + NCHAR(10) + N'Format: {{format}}' + NCHAR(10) + N'BIB: {{bibId}}' + NCHAR(10) + NCHAR(10) + N'View task: {{requestUrl}}',
         0, 0, 90);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'staff_weekly_summary')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'staff_weekly_summary', N'Weekly action summary',
         N'Weekly ASAP action summary: {{submissionCount}} new, {{purchaseCount}} awaiting bibs, {{additionalCopyCount}} additional copies',
         N'Hello {{recipientName}},' + NCHAR(10) + NCHAR(10) +
         N'Weekly ASAP action summary for {{libraryName}}.' + NCHAR(10) +
         N'Business period: {{periodStart}} through {{periodEnd}}' + NCHAR(10) + NCHAR(10) +
         N'New submissions' + NCHAR(10) + N'{{submissionCount}} active requests' + NCHAR(10) + N'Five most recent:' + NCHAR(10) + N'{{submissionItems}}' + NCHAR(10) +
         N'View new submissions: {{submissionQueueUrl}}' + NCHAR(10) + NCHAR(10) +
         N'Approved purchases without bibs' + NCHAR(10) + N'{{purchaseCount}} active requests' + NCHAR(10) + N'Five most recent:' + NCHAR(10) + N'{{purchaseItems}}' + NCHAR(10) +
         N'View purchases awaiting BIBs: {{purchaseQueueUrl}}' + NCHAR(10) + NCHAR(10) +
         N'Additional copies' + NCHAR(10) + N'{{additionalCopyCount}} open tasks' + NCHAR(10) + N'Five most recent:' + NCHAR(10) + N'{{additionalCopyItems}}' + NCHAR(10) +
         N'View additional copies: {{additionalCopiesQueueUrl}}',
         0, 0, 100);
END;
