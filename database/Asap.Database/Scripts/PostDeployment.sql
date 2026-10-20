ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;

IF NOT EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[SchemaVersion] ([Id], [Version], [UpdatedUtc])
    VALUES (1, 13, SYSUTCDATETIME());
END;
ELSE IF (SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1) < 7
BEGIN
    THROW 51000, 'Schema 7 is a pre-release reset boundary. Recreate the application database from this DACPAC.', 1;
END;
ELSE IF (SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1) > 13
BEGIN
    THROW 51000, 'The database schema is newer than this DACPAC.', 1;
END;

-- Materialize historical protection once at the upgrade boundary. From schema 10
-- onward event JSON is diagnostic history and cannot create runtime authority.
IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] IN (7, 8, 9))
BEGIN
    UPDATE request SET [LegacyHoldProtected] = 1
    FROM [asap].[TitleRequest] AS request
    WHERE EXISTS
    (
        SELECT 1 FROM [asap].[TitleRequestEvent] AS history
        WHERE history.[TitleRequestId] = request.[Id]
          AND EXISTS
          (
              SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(history.[MetadataJson]) = 1
                  THEN history.[MetadataJson] ELSE N'{}' END) AS marker
              WHERE marker.[key] COLLATE Latin1_General_100_BIN2 = N'legacyBibProtection'
                AND marker.[type] = 3 AND marker.[value] = N'true'
          )
    );
END;

-- Native schemas 7/8/9/10/11/12 upgrade in place.
UPDATE [asap].[SchemaVersion]
SET [Version] = 13, [UpdatedUtc] = SYSUTCDATETIME()
WHERE [Id] = 1 AND [Version] IN (7, 8, 9, 10, 11, 12);

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

-- Only byte-exact historical defaults are reconciled. SQL's normal text
-- comparison ignores case/trailing spaces and could overwrite a customization.
-- Apply equally to System and explicit library copies; NULL stays inherited.
DECLARE @OldPatronSuccess nvarchar(max) = N'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>';
DECLARE @OldPatronDuplicate nvarchar(max) = N'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library''s suggestion service.</div>';
DECLARE @OldPatronEbook nvarchar(max) = N'<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>';
DECLARE @OldPatronEaudiobook nvarchar(max) = N'<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>';

UPDATE [asap].[PatronSettings] SET [SuccessMessage] = @PatronSuccess, [UpdatedUtc] = SYSUTCDATETIME()
WHERE CONVERT(varbinary(max), [SuccessMessage]) = CONVERT(varbinary(max), @OldPatronSuccess);
UPDATE [asap].[PatronSettings] SET [AlreadySubmittedMessage] = @PatronDuplicate, [UpdatedUtc] = SYSUTCDATETIME()
WHERE CONVERT(varbinary(max), [AlreadySubmittedMessage]) = CONVERT(varbinary(max), @OldPatronDuplicate);
UPDATE [asap].[PatronSettings] SET [EbookMessage] = @PatronEbook, [UpdatedUtc] = SYSUTCDATETIME()
WHERE CONVERT(varbinary(max), [EbookMessage]) = CONVERT(varbinary(max), @OldPatronEbook);
UPDATE [asap].[PatronSettings] SET [EaudiobookMessage] = @PatronEaudiobook, [UpdatedUtc] = SYSUTCDATETIME()
WHERE CONVERT(varbinary(max), [EaudiobookMessage]) = CONVERT(varbinary(max), @OldPatronEaudiobook);

-- Stopped-source imports can retain the same seeds as format-specific copy.
-- Preserve the importer's forensic/default pins; deployment owns this cleanup.
UPDATE [asap].[MaterialFormat] SET [Message] = @PatronEbook, [UpdatedUtc] = SYSUTCDATETIME()
WHERE CONVERT(varbinary(max), [Message]) = CONVERT(varbinary(max), @OldPatronEbook);
UPDATE [asap].[MaterialFormat] SET [Message] = @PatronEaudiobook, [UpdatedUtc] = SYSUTCDATETIME()
WHERE CONVERT(varbinary(max), [Message]) = CONVERT(varbinary(max), @OldPatronEaudiobook);
UPDATE [asap].[MaterialFormatOverride] SET [Message] = @PatronEbook
WHERE CONVERT(varbinary(max), [Message]) = CONVERT(varbinary(max), @OldPatronEbook);
UPDATE [asap].[MaterialFormatOverride] SET [Message] = @PatronEaudiobook
WHERE CONVERT(varbinary(max), [Message]) = CONVERT(varbinary(max), @OldPatronEaudiobook);

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1)
BEGIN
    INSERT INTO [asap].[EmailSettings] ([OrganizationId], [UpdatedUtc])
    VALUES (1, SYSUTCDATETIME());
END;

-- The transport credential is System-owned. Retire obsolete library ciphertext
-- without reading it or changing the library's sender identity.
UPDATE [asap].[EmailSettings]
SET [ProtectedServerToken] = NULL
WHERE [OrganizationId] <> 1 AND [ProtectedServerToken] IS NOT NULL;

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
