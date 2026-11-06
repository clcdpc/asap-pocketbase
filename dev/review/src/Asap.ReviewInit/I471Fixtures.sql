-- Only i471, after LocalReviewer. Each write scenario owns a fresh library.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
INSERT INTO asap.Organization (Id, DisplayName, Abbreviation, OrganizationCodeId, ParentOrganizationId, IsActive)
SELECT Id, N'I471 synthetic ' + Name, N'I471', 2, 1, 1
FROM (VALUES (47101, N'identities'), (47102, N'inheritance'), (47103, N'blockers'),
             (47104, N'conflicts'), (47105, N'compact rows'), (47106, N'rules'), (47107, N'comparison tools'),
             (47108, N'empty creator reset')) f(Id, Name);
DECLARE @code nvarchar(100) = N'custom_' + CONVERT(nvarchar(10), (SELECT COUNT(*) + 2 FROM asap.MaterialFormat WHERE OwnerOrganizationId = 1));
INSERT INTO asap.MaterialFormat (OwnerOrganizationId, Code, Label, SortOrder, IsEnabled, CreatedUtc, UpdatedUtc)
VALUES (47101, @code, N'Saved collision format', 9870, 1, '2026-01-01', '2026-01-01');
DECLARE @format bigint = SCOPE_IDENTITY();
INSERT INTO asap.PatronCustomField (LibraryOrganizationId, FieldKey, FieldType, Label, IsEnabled, SortOrder)
VALUES (47101, N'field_2', N'select', N'Original select label', 1, 9870);
DECLARE @field bigint = SCOPE_IDENTITY();
INSERT INTO asap.PatronCustomFieldOption (PatronCustomFieldId, OptionKey, Label, IsEnabled, SortOrder)
VALUES (@field, N'selected-choice', N'Original selected display', 1, 10), (@field, N'other-choice', N'Other choice', 1, 20);
INSERT INTO asap.MaterialFormatCustomFieldRule (LibraryOrganizationId, MaterialFormatId, PatronCustomFieldId, Mode, LabelOverride)
VALUES (47101, @format, @field, N'required', N'Saved rule witness');
DECLARE @staff bigint = (SELECT Id FROM asap.StaffUser WHERE NormalizedUserPrincipalName = N'REVIEWER@EXAMPLE.ORG');
INSERT INTO asap.FormatAutoClaimRule (LibraryOrganizationId, MaterialFormatId, StaffUserId, IsActive, CreatedUtc)
VALUES (47101, @format, @staff, 1, '2026-01-01');
DECLARE @claim bigint = SCOPE_IDENTITY();
INSERT INTO asap.PublicationOptionSet (OrganizationId) VALUES (47101);
INSERT INTO asap.PublicationOption (OrganizationId, OptionKey, Label, IsEnabled, SortOrder)
VALUES (47101, N'historical-publication', N'Historical publication label', 1, 10), (47101, N'other-publication', N'Other publication', 1, 20);
DECLARE @snapshot nvarchar(max) = N'{ "field_2" : { "label" : "Original select label", "type" : "select", "value" : "selected-choice", "displayValue" : "Original selected display" } }';
INSERT INTO asap.TitleRequest (LibraryOrganizationId, Barcode, Title, Publication, CustomFieldsJson, AutoHold, MaterialFormatId,
    Status, Notes, ClaimedByStaffUserId, ClaimedByDisplayName, ClaimedAtUtc, ClaimType, ClaimRuleId, CreatedUtc, UpdatedUtc)
VALUES (47101, N'ACCEPTANCE-I471-HISTORY', N'Synthetic identity history', N'Historical publication label', @snapshot, 0, @format,
    N'suggestion', N'Accepted notes', @staff, N'Synthetic selector', '2026-01-01', N'automatic_format_rule', @claim, '2026-01-01', '2026-01-01');
DECLARE @request bigint = SCOPE_IDENTITY();
INSERT INTO asap.TitleRequestEvent (TitleRequestId, EventType, Status, ActorType, ActorName, Message, MetadataJson, CreatedUtc)
VALUES (@request, N'request_submitted', N'suggestion', N'patron', N'Synthetic patron', N'Accepted history', @snapshot, '2026-01-01');
INSERT INTO asap.ExternalSearchProvider (OrganizationId, ProviderKey, Label, UrlTemplate, IsEnabled, SortOrder)
VALUES (1, N'audit_provider_inherited_i471', N'I471 system catalog', N'https://catalog.example.org?q={query}', 1, 9870);
-- Owned records prove scalar/list/template resets never delete library content.
INSERT INTO asap.MaterialFormat (OwnerOrganizationId, Code, Label, SortOrder, IsEnabled, CreatedUtc, UpdatedUtc)
VALUES (47102, N'i471_owned', N'I471 owned format', 9870, 1, '2026-01-01', '2026-01-01');
INSERT INTO asap.PatronCustomField (LibraryOrganizationId, FieldKey, FieldType, Label, IsEnabled, SortOrder)
VALUES (47102, N'i471_owned', N'text', N'I471 owned field', 1, 9870);
INSERT INTO asap.EmailTemplate (OrganizationId, TemplateKey, DisplayName, SubjectTemplate, BodyTemplate, IsHidden, IsCustom, SortOrder)
VALUES (47102, N'rejection:i471_owned', N'I471 owned rejection', N'Owned subject', N'Owned body', 0, 1, 9870);
COMMIT;
