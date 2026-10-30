-- Independent synthetic scenarios. Never repair/reset a consumed fixture on restart.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @book bigint =
    (SELECT Id FROM asap.MaterialFormat WHERE OwnerOrganizationId = 1 AND Code = N'book');
DECLARE @now datetime2(7) = SYSUTCDATETIME();

INSERT INTO asap.TitleRequest
    (LegacyId, LibraryOrganizationId, PatronOrganizationId, Barcode, Email,
     NameFirst, NameLast, PatronCodeId, PatronCodeDescription, PreferredPickupBranchId,
     PreferredPickupBranchName, LibraryNameSnapshot, Title, Author, AutoHold,
     MaterialFormatId, Status, BibId, BibIdStaffVerified, Notes, CreatedUtc, UpdatedUtc, PatronIdSnapshot)
SELECT N'acceptance-pr410-' + f.Scenario, 2, 2, N'ACCEPTANCE' + f.Suffix,
    N'acceptance@example.org', N'Synthetic', N'Reviewer', 1, N'Adult', 101,
    N'Main Library', N'Review Library', N'Acceptance ' + f.Scenario, N'Synthetic author',
    f.AutoHold, @book, N'suggestion', f.BibId, f.Verified,
    N'Acceptance fixture draft baseline', @now, @now, CONVERT(int, f.Suffix)
FROM (VALUES
    (N'purchase-reminder-on',  N'41001', 1, NULL, 0),
    (N'purchase-reminder-off', N'41002', 1, NULL, 0),
    (N'purchase-hold',         N'41003', 1, 941003, 1),
    (N'purchase-no-hold',      N'41004', 0, 941004, 1),
    (N'purchase-duplicate',    N'41005', 1, NULL, 0),
    (N'reject',                N'41006', 1, NULL, 0),
    (N'draft',                 N'41007', 1, NULL, 0),
    (N'close-reopen',          N'41008', 1, NULL, 0),
    (N'stale-action',          N'41009', 1, NULL, 0),
    (N'unverified',            N'41010', 1, 941010, 0),
    (N'stale-consent',         N'41011', 1, NULL, 0)
) f(Scenario, Suffix, AutoHold, BibId, Verified)
WHERE NOT EXISTS (SELECT 1 FROM asap.TitleRequest r WHERE r.LegacyId = N'acceptance-pr410-' + f.Scenario);

-- D1 read-only states: ordinary closure, retained legacy protection, and a
-- deterministic history with bigint ties. Existing PR410 fixtures stay pristine.
INSERT INTO asap.TitleRequest
    (LegacyId, LibraryOrganizationId, PatronOrganizationId, Barcode, Email,
     NameFirst, NameLast, PreferredPickupBranchId, PreferredPickupBranchName,
     LibraryNameSnapshot, Title, Author, AutoHold, MaterialFormatId, Status,
     CloseReason, LegacyHoldProtected, Notes, CreatedUtc, UpdatedUtc, PatronIdSnapshot)
SELECT N'acceptance-pr410-' + f.Scenario, 2, 2, N'ACCEPTANCE' + f.Suffix,
    N'acceptance@example.org', N'Synthetic', N'Reviewer with a long patron name for wrapping', 101,
    N'Main Library', N'Review Library', N'Acceptance ' + f.Scenario, N'Synthetic author',
    1, @book, f.Status, f.CloseReason, f.Protected,
    N'Acceptance fixture draft baseline', '2026-10-21T12:00:00', '2026-10-23T12:00:00', CONVERT(int, f.Suffix)
FROM (VALUES
    (N'd1-closed', N'41013', N'closed', N'rejected', 0),
    (N'd1-protected', N'41014', N'closed', N'hold_completed', 1),
    (N'd1-history', N'41015', N'suggestion', NULL, 0)
) f(Scenario, Suffix, Status, CloseReason, Protected)
WHERE NOT EXISTS (SELECT 1 FROM asap.TitleRequest r WHERE r.LegacyId = N'acceptance-pr410-' + f.Scenario);

IF NOT EXISTS (SELECT 1 FROM asap.TitleRequestEvent e JOIN asap.TitleRequest r ON r.Id = e.TitleRequestId
               WHERE r.LegacyId = N'acceptance-pr410-d1-history')
BEGIN
    SET IDENTITY_INSERT asap.TitleRequestEvent ON;
    INSERT INTO asap.TitleRequestEvent (Id, TitleRequestId, EventType, ActorType, ActorName, Message, CreatedUtc)
    SELECT f.Id, r.Id, f.EventType, N'system', N'Synthetic acceptance', f.Message, f.CreatedUtc
    FROM asap.TitleRequest r CROSS JOIN (VALUES
        (CONVERT(bigint, 9007199254740992), N'created', N'D1 created', CONVERT(datetime2(7), '2026-10-21T12:00:00')),
        (CONVERT(bigint, 9007199254740993), N'edited', N'D1 earlier tied event', CONVERT(datetime2(7), '2026-10-23T12:00:00')),
        (CONVERT(bigint, 9007199254740994), N'edited', N'D1 newest tied event', CONVERT(datetime2(7), '2026-10-23T12:00:00'))
    ) f(Id, EventType, Message, CreatedUtc)
    WHERE r.LegacyId = N'acceptance-pr410-d1-history';
    SET IDENTITY_INSERT asap.TitleRequestEvent OFF;
END;

IF NOT EXISTS (SELECT 1 FROM asap.AdditionalCopyRequest WHERE LegacyId = N'acceptance-pr410-copy')
    INSERT INTO asap.AdditionalCopyRequest
        (LegacyId, LibraryOrganizationId, LibraryNameSnapshot, BibId, Title, Author,
         MaterialFormatId, FormatSnapshot, Status, CreatedByDisplayName, CreatedUtc, UpdatedUtc)
    VALUES (N'acceptance-pr410-copy', 2, N'Review Library', 941012, N'Acceptance additional copy',
        N'Synthetic author', @book, N'Book', N'open', N'Acceptance seed', @now, @now);

IF NOT EXISTS (SELECT 1 FROM asap.EmailTemplate
               WHERE OrganizationId = 2 AND TemplateKey = N'rejection:acceptance-pr410-rejection')
    INSERT INTO asap.EmailTemplate
        (OrganizationId, TemplateKey, DisplayName, SubjectTemplate, BodyTemplate, IsHidden, IsCustom, SortOrder)
    VALUES (2, N'rejection:acceptance-pr410-rejection', N'Acceptance rejection',
        N'Review decision for {{title}}', N'Hello {{name}}, this synthetic review request was declined.', 0, 1, 999);

-- A library-only synthetic sender. Do not overwrite existing reviewer settings.
IF NOT EXISTS (SELECT 1 FROM asap.EmailSettings WHERE OrganizationId = 2)
    INSERT INTO asap.EmailSettings (OrganizationId, FromAddress, FromName, UpdatedUtc)
    VALUES (2, N'review@example.org', N'ASAP review', @now);

-- Settings discard edits an existing library override through the real editor.
-- Inherited fields are deliberately disabled by the application.
IF NOT EXISTS (SELECT 1 FROM asap.PatronSettings WHERE OrganizationId = 2)
    INSERT INTO asap.PatronSettings (OrganizationId, SuccessTitle, UpdatedUtc)
    VALUES (2, N'Acceptance suggestion received', @now);
COMMIT;
