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

-- Q1 presentation/navigation only: 24 extra open tasks plus the two existing
-- copy seeds cross the 25-row page boundary. No acceptance scenario mutates them.
UPDATE asap.SystemSettings SET LeapBibUrlPattern = N'https://catalog.example.org/bib/{{bibid}}'
WHERE OrganizationId = 1 AND NULLIF(LTRIM(RTRIM(LeapBibUrlPattern)), N'') IS NULL;
IF NOT EXISTS (SELECT 1 FROM asap.Organization WHERE Id = 3)
    INSERT INTO asap.Organization (Id, DisplayName, Abbreviation, IsActive, LastSyncedUtc, OrganizationCodeId, ParentOrganizationId)
    VALUES (3, N'Q1 Synthetic Library with a deliberately long display name for wrapping', N'Q1', 1, @now, 3, 1);
IF NOT EXISTS (SELECT 1 FROM asap.StaffUser WHERE NormalizedUserPrincipalName = N'Q1-CLAIMANT@EXAMPLE.ORG')
    INSERT INTO asap.StaffUser (UserPrincipalName, NormalizedUserPrincipalName, DisplayName, NotificationEmail, Role, OrganizationId, IsActive)
    VALUES (N'q1-claimant@example.org', N'Q1-CLAIMANT@EXAMPLE.ORG',
        N'Q1 Synthetic Claimant with a deliberately long staff display name', N'q1-claimant@example.org', N'staff', 2, 1);
DECLARE @q1Claimant bigint = (SELECT Id FROM asap.StaffUser WHERE NormalizedUserPrincipalName = N'Q1-CLAIMANT@EXAMPLE.ORG');
INSERT INTO asap.AdditionalCopyRequest
    (LegacyId, LibraryOrganizationId, LibraryNameSnapshot, BibId, Title, Author, Identifier, Publication,
     MaterialFormatId, FormatSnapshot, Status, Notes, CreatedByDisplayName, CreatedUtc, UpdatedUtc,
     ClaimedByStaffUserId, ClaimedByDisplayName, ClaimedAtUtc, ClaimType, ClosedUtc)
SELECT CONCAT(N'acceptance-q1-', n.value), CASE WHEN n.value = 26 THEN 3 ELSE 2 END,
    CASE WHEN n.value = 26 THEN N'Q1 Synthetic Library with a deliberately long display name for wrapping' ELSE N'Review Library' END,
    942000 + n.value,
    CONCAT(N'Q1 copy ', RIGHT(CONCAT(N'0', n.value), 2), CASE WHEN n.value = 1 THEN
        N' — The atlas of forthcoming stories and an exceptionally long working title for a compact additional copies row' ELSE N'' END),
    CASE WHEN n.value = 2 THEN NULL WHEN n.value = 1 THEN
        N'Synthetic Author with a deliberately long name and several collaborators contributing to this forthcoming anthology'
        ELSE CONCAT(N'Synthetic author ', 27 - n.value) END,
    CONCAT(N'Q1-IDENTIFIER-', n.value), CONCAT(N'Q1 publication ', n.value),
    CASE WHEN n.value = 2 THEN NULL ELSE @book END, CASE WHEN n.value = 2 THEN NULL ELSE N'Book' END,
    CASE WHEN n.value = 25 THEN N'closed' ELSE N'open' END,
    CASE WHEN n.value = 1 THEN N'Q1 synthetic notes remain available in the existing details view.' ELSE NULL END,
    CASE WHEN n.value = 2 THEN NULL ELSE N'Q1 Synthetic Creator with a deliberately long staff name' END,
    DATEADD(minute, n.value, CONVERT(datetime2(7), '2026-10-02T13:14:00')),
    DATEADD(minute, n.value, CONVERT(datetime2(7), '2026-10-02T13:14:00')),
    CASE WHEN n.value = 1 THEN @q1Claimant ELSE NULL END,
    CASE WHEN n.value = 1 THEN N'Q1 Synthetic Claimant with a deliberately long staff display name' ELSE NULL END,
    CASE WHEN n.value = 1 THEN CONVERT(datetime2(7), '2026-10-02T13:15:00') ELSE NULL END,
    CASE WHEN n.value = 1 THEN N'manual' ELSE NULL END,
    CASE WHEN n.value = 25 THEN CONVERT(datetime2(7), '2026-10-03T13:14:00') ELSE NULL END
FROM GENERATE_SERIES(1, 26) n
WHERE NOT EXISTS (SELECT 1 FROM asap.AdditionalCopyRequest c WHERE c.LegacyId = CONCAT(N'acceptance-q1-', n.value));
COMMIT;
