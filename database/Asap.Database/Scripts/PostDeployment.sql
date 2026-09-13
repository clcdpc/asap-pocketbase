ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;

IF NOT EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[SchemaVersion] ([Id], [Version], [UpdatedUtc])
    VALUES (1, 3, SYSUTCDATETIME());
END;
ELSE IF (SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1) < 3
BEGIN
    UPDATE [asap].[SchemaVersion]
    SET [Version] = 3, [UpdatedUtc] = SYSUTCDATETIME()
    WHERE [Id] = 1;
END;
ELSE IF (SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1) > 3
BEGIN
    THROW 51000, 'The database schema is newer than this DACPAC.', 1;
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[DeploymentState] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[DeploymentState] ([Id]) VALUES (1);
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive], [LastSyncedUtc])
    VALUES (1, N'System', NULL, 1, NULL);
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
        ([OrganizationId], [AccessId], [WorkstationId], [SystemPolarisUserId],
         [OrganizationIdForRequests], [PickupOrganizationId], [UpdatedUtc])
    VALUES (1, N'SuggestAPI', 1, 1, 3, 0, SYSUTCDATETIME());
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
         N'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>',
         N'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library''s suggestion service.</div>',
         N'<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>',
         N'<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>',
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

IF NOT EXISTS (SELECT 1 FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted')
BEGIN
    INSERT INTO [asap].[EmailTemplate]
        ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
    VALUES
        (1, N'suggestion_submitted', N'Suggestion submitted',
         N'Suggestion received: {{title}}',
         N'Hello {{name}},' + NCHAR(10) + NCHAR(10) +
         N'Thank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.' + NCHAR(10) + NCHAR(10) +
         N'If we add this item, we will place a hold for you automatically and send another update.' + NCHAR(10) + NCHAR(10) +
         N'Thank you for helping us shape the library collection.',
         0, 0, 10);
END;
