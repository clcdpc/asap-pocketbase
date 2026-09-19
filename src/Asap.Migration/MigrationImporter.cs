using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

public sealed record MigrationImportOptions(
    string PackagePath,
    string ConnectionString,
    IReadOnlySet<Guid> AllowedTenantIds,
    string ReportPath,
    string? ExternalConfigurationPath = null,
    string? PostmarkTokenEnvironmentName = null);

public sealed record MigrationImportResult(
    IReadOnlyDictionary<string, int> ImportedCounts,
    bool ReconciliationPassed);

public static class MigrationImporter
{
    private static readonly IReadOnlySet<string> HoldTerminalReasons = new HashSet<string>(
        ["hold_completed", "hold_not_picked_up", "hold_unclaimed", "hold_cancelled", "hold_expired"],
        StringComparer.Ordinal);

    private static readonly string[] PlacementEvidenceKinds =
    [
        "current_status",
        "terminal_close_reason",
        "dedicated_event",
        "transition_to_placed",
        "transition_from_placed",
        "event_terminal_reason"
    ];

    private static readonly IReadOnlySet<string> KnownEventTypes = new HashSet<string>(
        [
            "created", "status_changed", "system_note", "promoted", "hold_placed", "hold_skipped",
            "fulfilled", "timeout_closed", "pickup_preference_changed", "claim_manual_assigned",
            "claim_manual_transferred", "claim_manual_cleared", "claim_auto_assigned",
            "claim_auto_reassigned", "claim_auto_cleared", "claim_auto_skipped"
        ],
        StringComparer.Ordinal);

    public static MigrationImportResult Import(MigrationImportOptions options)
    {
        var package = MigrationPackageValidator.Validate(options.PackagePath);
        var operationalConfiguration = MigrationOperationalConfiguration.ValidateRequired(
            package,
            options.ExternalConfigurationPath);
        ValidateOptions(options, package);
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var staffUsers = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var authenticationEmails = ValidateStaffAuthenticationEmails(staffUsers);
        ValidateSourceIdentityRows(package);
        var postmarkToken = ReadOptionalSecretEnvironment(
            options.PostmarkTokenEnvironmentName,
            "postmark_token_missing");
        var credentialProtector = RequiresCredentialProtection(package, postmarkToken)
            ? MigrationCredentialProtector.Load(options.ExternalConfigurationPath)
            : null;
        var importedCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var transformations = new List<object>();
        var claimTransformations = new List<ClaimTransformation>();
        var additionalCopyClaimTransformations = new List<ClaimTransformation>();
        var placementTransformations = new List<PlacementTransformation>();
        MigrationSemanticReconciliation semanticReconciliation;
        transformations.AddRange(ValidateConfigurationSourceFields(package));
        transformations.Add(new
        {
            entity = "operational_configuration",
            status = "matched",
            matchedSchedules = operationalConfiguration.MatchedSchedules,
            matchedQueues = operationalConfiguration.MatchedQueues,
            retiredObsoleteOverrides = operationalConfiguration.RetiredObsoleteOverrides
        });

        using (var connection = new SqlConnection(options.ConnectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
            VerifyFreshTarget(connection, transaction);
            NormalizeFreshTargetSeedTimestamps(
                connection,
                transaction,
                package.Manifest.ExportedAtUtc.UtcDateTime);
            var organizationIds = ImportOrganizations(
                connection,
                transaction,
                organizations,
                importedCounts);
            var emailTemplateIds = ImportEmailTemplates(
                connection,
                transaction,
                MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates"),
                MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates"),
                organizationIds,
                importedCounts,
                transformations);
            var formatIds = ImportMaterialFormats(
                connection,
                transaction,
                MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats"),
                organizationIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts);
            MigrationConfigurationImporter.Import(
                connection,
                transaction,
                package,
                organizationIds,
                formatIds,
                emailTemplateIds,
                credentialProtector,
                postmarkToken,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations);
            var staffIds = ImportStaffUsers(
                connection,
                transaction,
                staffUsers,
                authenticationEmails,
                organizationIds,
                options.AllowedTenantIds,
                importedCounts);
            var bootstrapMutatedStaffUserId = EnsureUsableSuperAdministrator(
                connection,
                transaction,
                options.ExternalConfigurationPath,
                options.AllowedTenantIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations);
            AddStaffRecipientTransformations(
                connection,
                transaction,
                staffUsers,
                authenticationEmails,
                staffIds,
                options.AllowedTenantIds,
                transformations);
            var tagIds = ImportWorkflowTags(
                connection,
                transaction,
                MigrationPackageReader.ReadRows(package, "workflow-tags.json", "workflow_tags"),
                importedCounts);
            var requestStatusRows = MigrationPackageReader.ReadRowsOrEmpty(
                package,
                "title-request-events.json",
                "request_statuses");
            var requestCloseReasonRows = MigrationPackageReader.ReadRowsOrEmpty(
                package,
                "title-request-events.json",
                "request_close_reasons");
            var requestStatuses = BuildStatusMap(requestStatusRows);
            var requestCloseReasons = BuildCloseReasonMap(requestCloseReasonRows);
            var claimRuleIds = ImportFormatAutoClaimRules(
                connection,
                transaction,
                MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules"),
                staffIds,
                organizationIds,
                options.AllowedTenantIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations);
            var configurationReconciliation = MigrationConfigurationImporter.Reconcile(
                connection,
                transaction,
                package,
                organizationIds,
                formatIds,
                emailTemplateIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                postmarkToken is not null);
            var titleRequestRows = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
            var requestIds = ImportTitleRequests(
                connection,
                transaction,
                titleRequestRows,
                formatIds,
                staffIds,
                claimRuleIds,
                organizationIds.Values.ToHashSet(),
                tagIds,
                options.AllowedTenantIds,
                requestStatuses,
                requestCloseReasons,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations,
                claimTransformations);
            var additionalCopyRows = MigrationPackageReader.ReadRows(
                package,
                "additional-copy-requests.json",
                "additional_copy_requests");
            var additionalCopyIds = ImportAdditionalCopies(
                connection,
                transaction,
                additionalCopyRows,
                requestIds,
                organizationIds,
                formatIds,
                staffIds,
                options.AllowedTenantIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations,
                additionalCopyClaimTransformations);
            var deletedAuditRows = MigrationPackageReader.ReadRows(
                package,
                "deleted-request-audit.json",
                "deleted_request_audit");
            ImportDeletedRequestAudit(
                connection,
                transaction,
                deletedAuditRows,
                staffIds,
                organizationIds.Values.ToHashSet(),
                importedCounts,
                transformations);
            ImportTitleRequestTags(
                connection,
                transaction,
                MigrationPackageReader.ReadRows(package, "title-request-tags.json", "title_request_tags"),
                requestIds,
                tagIds,
                importedCounts);
            ImportTitleRequestEventsAndPlacementProtection(
                connection,
                transaction,
                titleRequestRows,
                MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events"),
                requestStatusRows,
                requestCloseReasonRows,
                requestIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations,
                placementTransformations);
            ImportHistoricalEmailDeliveryEvents(
                connection,
                transaction,
                MigrationPackageReader.ReadRows(package, "email-delivery-events.json", "email_delivery_events"),
                requestIds,
                emailTemplateIds,
                importedCounts);
            semanticReconciliation = ReconcileImportedSourceState(
                connection,
                transaction,
                package,
                organizations,
                staffUsers,
                authenticationEmails,
                titleRequestRows,
                requestIds,
                tagIds,
                emailTemplateIds,
                additionalCopyRows,
                deletedAuditRows,
                organizationIds,
                formatIds,
                staffIds,
                claimRuleIds,
                options.AllowedTenantIds,
                requestStatuses,
                requestCloseReasons,
                bootstrapMutatedStaffUserId,
                configurationReconciliation,
                placementTransformations);
            VerifyUsableSuperAdministrator(
                connection,
                transaction,
                options.AllowedTenantIds);
            transaction.Commit();

            if (staffIds.Count != staffUsers.Count)
            {
                throw new MigrationOperationException("reconciliation_failed", "Imported StaffUser mapping count changed after commit.");
            }
        }

        var targetCounts = ReconcileTarget(options.ConnectionString, importedCounts, options.AllowedTenantIds);
        var targetFingerprint = MigrationReconciler.ComputeTargetFingerprint(options.ConnectionString);
        var packageIdentity = MigrationPackageValidator.ComputePackageIdentitySha256(package);
        WriteReport(
            options.ReportPath,
            package,
            importedCounts,
            targetCounts,
            targetFingerprint,
            packageIdentity,
            transformations,
            claimTransformations,
            additionalCopyClaimTransformations,
            placementTransformations,
            semanticReconciliation);
        return new MigrationImportResult(importedCounts, true);
    }

    private static IReadOnlyList<object> ValidateConfigurationSourceFields(ValidatedMigrationPackage package)
    {
        var definitions = new[]
        {
            new ConfigurationSourceFields(
                "system-settings.json",
                "system_settings",
                Fields(
                    "id", "created", "updated", "settingsKey", "allowedStaffUsers", "staffUrl",
                    "leapBibUrlPattern", "leapPatronUrlPattern", "formatIconUrlPattern",
                    "patronEmbedAllowedOrigins", "enabledLibraries", "organizationsSyncStatus",
                    "organizationsLastSynced", "organizationsSyncMessage", "organizationsSyncError",
                    "patronCodesSyncStatus", "patronCodesLastSynced", "patronCodesSyncMessage",
                    "patronCodesSyncError"),
                Fields(
                    "allowedStaffUsers", "enabledLibraries", "organizationsSyncStatus", "organizationsLastSynced",
                    "organizationsSyncMessage", "organizationsSyncError", "patronCodesSyncStatus",
                    "patronCodesLastSynced", "patronCodesSyncMessage", "patronCodesSyncError")),
            new ConfigurationSourceFields(
                "polaris-settings.json",
                "polaris_settings",
                Fields(
                    "id", "created", "updated", "settingsKey", "host", "accessId", "apiKey", "staffDomain",
                    "adminUser", "adminPassword", "overridePassword", "langId", "appId", "orgId", "pickupOrgId",
                    "requestingOrgId", "workstationId", "userId", "autoPromote", "firstSuccessfulSaveAt",
                    "materialTypesCache", "materialTypesCacheUpdated"),
                Fields(
                    "settingsKey", "overridePassword", "langId", "appId", "orgId", "autoPromote",
                    "firstSuccessfulSaveAt", "materialTypesCache", "materialTypesCacheUpdated", "pickupOrgId",
                    "requestingOrgId")),
            new ConfigurationSourceFields(
                "email-settings.json",
                "smtp_settings",
                Fields(
                    "id", "created", "updated", "settingsKey", "host", "port", "username", "password", "tls",
                    "fromAddress", "fromName"),
                Fields("settingsKey", "host", "port", "username", "password", "tls")),
            new ConfigurationSourceFields(
                "workflow-settings.json",
                "workflow_settings",
                Fields(
                    "id", "created", "updated", "scope", "libraryOrganization", "suggestionLimit",
                    "suggestionLimitMessage", "outstandingTimeoutEnabled", "outstandingTimeoutDays",
                    "outstandingTimeoutSendEmail", "outstandingTimeoutRejectionTemplate", "holdPickupTimeoutEnabled",
                    "holdPickupTimeoutDays", "pendingHoldTimeoutEnabled", "pendingHoldTimeoutDays",
                    "additionalCopyTimeoutEnabled", "additionalCopyTimeoutDays", "autoPromote", "commonAuthorsEnabled",
                    "commonAuthorsList", "commonAuthorsLabel", "commonAuthorsHelp", "commonAuthorsMessage",
                    "allowPatronAutoholdOptOut", "allowAnyRegisteredCardLogin", "patronCodeEligibilityEnabled",
                    "allowedPatronCodeIds", "patronCodeEligibilityMessage", "externalSearch1Enabled",
                    "externalSearch1Label", "externalSearch1UrlTemplate", "externalSearch2Enabled", "externalSearch2Label",
                    "externalSearch2UrlTemplate", "externalSearch3Enabled", "externalSearch3Label",
                    "externalSearch3UrlTemplate", "externalSearch4Enabled", "externalSearch4Label",
                    "externalSearch4UrlTemplate"),
                Fields()),
            new ConfigurationSourceFields(
                "patron-settings.json",
                "ui_settings",
                Fields(
                    "id", "created", "updated", "scope", "libraryOrganization", "logo", "logoAlt", "pageTitle",
                    "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote", "successTitle",
                    "successMessage", "alreadySubmittedMessage", "duplicateLabelSuggestion",
                    "duplicateLabelOutstandingPurchase", "duplicateLabelPendingHold", "duplicateLabelHoldPlaced",
                    "duplicateLabelClosed", "duplicateLabelRejected", "duplicateLabelHoldCompleted",
                    "duplicateLabelHoldNotPickedUp", "duplicateLabelManual", "duplicateLabelSilent", "noEmailMessage",
                    "systemNotEnabledMessage", "ebookMessage", "eaudiobookMessage", "publicationOptions"),
                Fields()),
            new ConfigurationSourceFields(
                "patron-settings.json",
                "patron_settings_overrides",
                Fields(
                    "id", "created", "updated", "orgId", "duplicateStatusLabels", "publicationOptions",
                    "patronFormatRules", "additionalFieldDefinitions", "ebookMessage", "eaudiobookMessage"),
                Fields()),
            new ConfigurationSourceFields(
                "patron-settings.json",
                "patron_library_settings",
                Fields("id", "created", "updated", "libraryOrganization", "duplicateRequestStatusLabels"),
                Fields()),
            new ConfigurationSourceFields(
                "patron-settings.json",
                "library_settings",
                Fields("id", "created", "updated", "libraryOrganization", "logo", "logoAlt"),
                Fields("logo", "logoAlt")),
            new ConfigurationSourceFields(
                "material-formats.json",
                "material_formats",
                Fields(
                    "id", "created", "updated", "scope", "libraryOrganization", "code", "label", "enabled",
                    "sortOrder", "messageBehavior", "titleMode", "titleLabel", "authorMode", "authorLabel",
                    "identifierMode", "identifierLabel", "publicationMode", "publicationLabel"),
                Fields()),
            new ConfigurationSourceFields(
                "format-auto-claim-rules.json",
                "format_claim_rules",
                Fields(
                    "id", "created", "updated", "libraryOrganization", "libraryOrgId", "format", "staffUser",
                    "staffUserId", "active", "createdBy", "updatedBy"),
                Fields("createdBy", "updatedBy")),
            new ConfigurationSourceFields(
                "email-templates.json",
                "email_templates",
                Fields(
                    "id", "created", "updated", "scope", "libraryOrganization", "templateKey", "name",
                    "subject", "body", "fromAddress", "fromName", "enabled"),
                Fields("created", "updated")),
            new ConfigurationSourceFields(
                "email-templates.json",
                "rejection_templates",
                Fields(
                    "id", "created", "updated", "scope", "libraryOrganization", "name", "subject", "body",
                    "enabled", "sortOrder", "sourceTemplateId"),
                Fields("created", "updated"))
        };

        var accounting = new List<object>();
        foreach (var definition in definitions)
        {
            foreach (var row in MigrationPackageReader.ReadRowsOrEmpty(package, definition.File, definition.Collection))
            {
                var unknown = row.Names
                    .Where(name => !definition.Known.Contains(name) && row.HasValue(name))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (unknown.Length != 0)
                {
                    throw new MigrationOperationException(
                        "source_field_unaccounted",
                        $"Source setting collection {definition.Collection} contains unaccounted populated field(s): {string.Join(", ", unknown)}.");
                }

                accounting.Add(new
                {
                    entity = "source_field_accounting",
                    collection = definition.Collection,
                    sourceId = row.String("id"),
                    intentionallyDroppedFields = definition.IntentionalDrops
                        .Where(row.HasValue)
                        .Order(StringComparer.Ordinal)
                        .ToArray()
                });
            }
        }
        return accounting;
    }

    private static void ValidateSourceIdentityRows(ValidatedMigrationPackage package)
    {
        var definitions = new[]
        {
            ("organizations.json", "polaris_organizations", "id"),
            ("staff-users.json", "staff_users", "id"),
            ("system-settings.json", "system_settings", "id"),
            ("polaris-settings.json", "polaris_settings", "id"),
            ("workflow-settings.json", "workflow_settings", "id"),
            ("patron-settings.json", "ui_settings", "id"),
            ("patron-settings.json", "patron_settings_overrides", "id"),
            ("patron-settings.json", "patron_library_settings", "id"),
            ("patron-settings.json", "library_settings", "id"),
            ("email-settings.json", "smtp_settings", "id"),
            ("material-formats.json", "material_formats", "id"),
            ("format-auto-claim-rules.json", "format_claim_rules", "id"),
            ("workflow-tags.json", "workflow_tags", "id"),
            ("title-requests.json", "title_requests", "id"),
            ("title-request-tags.json", "title_request_tags", "id"),
            ("title-request-events.json", "request_statuses", "id"),
            ("title-request-events.json", "request_close_reasons", "id"),
            ("title-request-events.json", "title_request_events", "id"),
            ("email-templates.json", "email_templates", "id"),
            ("email-templates.json", "rejection_templates", "id"),
            ("email-delivery-events.json", "email_delivery_events", "id"),
            ("deleted-request-audit.json", "deleted_request_audit", "id"),
            ("additional-copy-requests.json", "additional_copy_requests", "id")
        };
        foreach (var (file, collection, key) in definitions)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in MigrationPackageReader.ReadRowsOrEmpty(package, file, collection))
            {
                if (!ids.Add(row.RequiredString(key)))
                {
                    throw new MigrationOperationException(
                        "source_record_duplicate",
                        $"Source collection {collection} contains a duplicate {key}.");
                }
            }
        }

        var brandingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in MigrationPackageReader.ReadRowsOrEmpty(package, "branding.json", "branding"))
        {
            if (!brandingIds.Add(row.RequiredString("sourceRecordId")))
            {
                throw new MigrationOperationException(
                    "source_record_duplicate",
                    "Source branding collection contains a duplicate sourceRecordId.");
            }
        }
    }

    private static HashSet<string> Fields(params string[] names) =>
        names.ToHashSet(StringComparer.Ordinal);

    private static void ValidateOptions(MigrationImportOptions options, ValidatedMigrationPackage package)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new MigrationOperationException("target_connection_missing", "The target SQL connection environment value is missing.");
        }
        if (options.PostmarkTokenEnvironmentName is not null && options.ExternalConfigurationPath is null)
        {
            throw new MigrationOperationException(
                "credential_protection_configuration_missing",
                "Target Data Protection configuration is required when a Postmark token environment input is named.");
        }
        if (options.AllowedTenantIds.Count == 0 || options.AllowedTenantIds.Contains(Guid.Empty))
        {
            throw new MigrationOperationException("allowed_tenant_invalid", "At least one non-empty allowed Entra tenant ID is required.");
        }

        var packageRoot = package.RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? package.RootPath
            : package.RootPath + Path.DirectorySeparatorChar;
        var report = Path.GetFullPath(options.ReportPath);
        if (report.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException("report_path_invalid", "The import report must be written outside the immutable migration package.");
        }
    }

    private static string? ReadOptionalSecretEnvironment(string? environmentName, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(environmentName)) return null;
        var value = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MigrationOperationException(errorCode, "The named secure process-environment input is missing.");
        }
        return value.Trim();
    }

    private static bool RequiresCredentialProtection(
        ValidatedMigrationPackage package,
        string? postmarkToken)
    {
        if (postmarkToken is not null) return true;
        return MigrationPackageReader.ReadRowsOrEmpty(package, "polaris-settings.json", "polaris_settings")
            .Any(row => row.String("apiKey") is not null || row.String("adminPassword") is not null);
    }

    private static Dictionary<string, string?> ValidateStaffAuthenticationEmails(
        IReadOnlyList<SourceRow> staffUsers)
    {
        var bySource = new Dictionary<string, string?>(StringComparer.Ordinal);
        var normalizedEmails = new HashSet<string>(StringComparer.Ordinal);
        foreach (var staff in staffUsers)
        {
            var sourceId = staff.RequiredString("id");
            var email = RealEmail(staff.String("email"));
            if (staff.Bool("active") && email is null)
            {
                throw new MigrationOperationException(
                    "active_staff_email_invalid",
                    $"Active staff user {sourceId} has no valid real authentication email.");
            }
            if (email is not null && !normalizedEmails.Add(email.ToUpperInvariant()))
            {
                throw new MigrationOperationException(
                    "duplicate_staff_email",
                    $"Staff user {sourceId} duplicates another normalized authentication email.");
            }
            bySource.Add(sourceId, email);
        }
        return bySource;
    }

    private static void VerifyFreshTarget(SqlConnection connection, SqlTransaction transaction)
    {
        using var command = new SqlCommand(
            """
            SELECT
                (SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1),
                (SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] <> 1),
                (SELECT COUNT(*) FROM [asap].[StaffUser]),
                (SELECT COUNT(*) FROM [asap].[TitleRequest]),
                (SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest]),
                (SELECT COUNT(*) FROM [asap].[DeletedRequestAudit]),
                (SELECT COUNT(*) FROM [asap].[AdministrativeAudit]),
                (SELECT COUNT(*) FROM [asap].[HoldPlacementOperation]),
                (SELECT COUNT(*) FROM [asap].[PatronSession]),
                (SELECT COUNT(*) FROM [asap].[EmailOutbox]),
                (SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent]),
                (SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping]),
                (SELECT COUNT(*) FROM [asap].[QueueProgress]),
                (SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin]),
                (SELECT COUNT(*) FROM [asap].[Branding]),
                (SELECT COUNT(*) FROM [asap].[WorkflowSettings] WHERE [OrganizationId] <> 1),
                (SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] <> 1),
                (SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] <> 1),
                (SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] <> 1),
                (SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] <> 1),
                (SELECT COUNT(*) FROM [asap].[MaterialFormatOverride]),
                (SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule]),
                (SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule]),
                (SELECT COUNT(*) FROM [asap].[ExternalSearchProviderOverride]),
                (SELECT COUNT(*) FROM [asap].[PatronCustomField]),
                (SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption]),
                (SELECT COUNT(*) FROM [asap].[CommonCreatorTerm]),
                (SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember]),
                (SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] <> 1),
                (SELECT COUNT(*) FROM [asap].[WorkflowTag]
                   WHERE [Code] NOT IN
                     (N'duplicate_suggestion', N'polaris_bib_found',
                      N'polaris_bib_not_found', N'polaris_multiple_matches'));
            """,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != MigrationContract.ExpectedSchemaVersion)
        {
            throw new MigrationOperationException("target_schema_version_mismatch", "Target application schema version is incompatible.");
        }
        if (Enumerable.Range(1, reader.FieldCount - 1).Any(index => reader.GetInt32(index) != 0))
        {
            throw new MigrationOperationException(
                "target_not_fresh",
                "Target contains runtime, business, configuration, asset, or prior migration rows outside the permitted DACPAC seeds.");
        }
    }

    private static void NormalizeFreshTargetSeedTimestamps(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTime exportedAtUtc)
    {
        using var command = new SqlCommand(
            """
            UPDATE [asap].[SchemaVersion] SET [UpdatedUtc] = @exportedAtUtc WHERE [Id] = 1;
            UPDATE [asap].[SystemSettings] SET [UpdatedUtc] = @exportedAtUtc WHERE [OrganizationId] = 1;
            UPDATE [asap].[PolarisSettings] SET [UpdatedUtc] = @exportedAtUtc WHERE [OrganizationId] = 1;
            UPDATE [asap].[WorkflowSettings] SET [UpdatedUtc] = @exportedAtUtc WHERE [OrganizationId] = 1;
            UPDATE [asap].[PatronSettings] SET [UpdatedUtc] = @exportedAtUtc WHERE [OrganizationId] = 1;
            UPDATE [asap].[EmailSettings] SET [UpdatedUtc] = @exportedAtUtc WHERE [OrganizationId] = 1;
            UPDATE [asap].[MaterialFormat]
            SET [CreatedUtc] = @exportedAtUtc, [UpdatedUtc] = @exportedAtUtc
            WHERE [OwnerOrganizationId] = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@exportedAtUtc", exportedAtUtc);
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, int> ImportOrganizations(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IDictionary<string, int> importedCounts)
    {
        var mapped = new Dictionary<string, int>(StringComparer.Ordinal);
        var targetOrganizations = new HashSet<int>();
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var organizationId = row.Int32("organizationId")
                ?? throw new MigrationOperationException("organization_id_missing", $"Organization {sourceId} has no Polaris ID.");
            if (organizationId <= 0 ||
                !mapped.TryAdd(sourceId, organizationId) ||
                !targetOrganizations.Add(organizationId))
            {
                throw new MigrationOperationException("organization_id_invalid", $"Organization {sourceId} has an invalid or duplicate source identity.");
            }

            var displayName = row.String("displayName") ?? row.String("name") ?? $"Organization {organizationId}";
            using (var command = new SqlCommand(
                """
                IF @id = 1
                    UPDATE [asap].[Organization]
                    SET [DisplayName] = @displayName,
                        [Abbreviation] = @abbreviation,
                        [IsActive] = 1,
                        [LastSyncedUtc] = @lastSyncedUtc
                    WHERE [Id] = 1;
                ELSE
                    INSERT INTO [asap].[Organization]
                        ([Id], [DisplayName], [Abbreviation], [IsActive], [LastSyncedUtc])
                    VALUES
                        (@id, @displayName, @abbreviation, @isActive, @lastSyncedUtc);
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@id", organizationId);
                command.Parameters.AddWithValue("@displayName", displayName);
                command.Parameters.AddWithValue("@abbreviation", (object?)row.String("abbreviation") ?? DBNull.Value);
                command.Parameters.AddWithValue("@isActive", row.Bool("enabledForPatrons"));
                command.Parameters.AddWithValue("@lastSyncedUtc", (object?)row.UtcDateTime("lastSynced") ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
            InsertMapping(connection, transaction, "organization", sourceId, organizationId);
        }
        importedCounts["polaris_organizations"] = rows.Count;
        return mapped;
    }

    private static Dictionary<string, long> ImportStaffUsers(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, string?> authenticationEmails,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IDictionary<string, int> importedCounts)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var role = row.RequiredString("role").ToLowerInvariant();
            if (role is not ("staff" or "admin" or "super_admin"))
            {
                throw new MigrationOperationException("staff_role_invalid", $"Staff user {sourceId} has an unknown role.");
            }

            var organizationId = role == "super_admin"
                ? 1
                : row.Int32("libraryOrgId")
                    ?? throw new MigrationOperationException("staff_organization_missing", $"Staff user {sourceId} has no library organization.");
            if ((role == "super_admin" && organizationId != 1) ||
                (role != "super_admin" && organizationId == 1) ||
                !OrganizationExists(connection, transaction, organizationId))
            {
                throw new MigrationOperationException("staff_organization_invalid", $"Staff user {sourceId} has an invalid role/organization scope.");
            }

            var active = row.Bool("active");
            var sourceEmail = authenticationEmails[sourceId];
            var weeklyEmail = RealEmail(row.String("weekly_action_summary_email"));
            var notificationEmail = sourceEmail ?? weeklyEmail;
            var weeklyEnabled = row.Bool("weekly_action_summary_enabled");
            var userPrincipalName = sourceEmail;
            var displayName = row.String("displayName") ?? row.String("username");

            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive],
                     [WeeklyActionSummaryEnabled], [WeeklyActionSummaryEmail], [PurchaseReminderDefault],
                     [AdditionalCopyReminderDefault], [DefaultMineUnclaimedFilter], [LastLoginUtc])
                OUTPUT inserted.[Id]
                VALUES
                    (@tenantId, @objectId, @upn, @normalizedUpn,
                     @displayName, @notificationEmail, @role, @organizationId, @isActive,
                     @weeklyEnabled, @weeklyEmail, @purchaseDefault,
                     @additionalCopyDefault, @mineDefault, @lastLoginUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@tenantId", DBNull.Value);
            command.Parameters.AddWithValue("@objectId", DBNull.Value);
            command.Parameters.AddWithValue("@upn", (object?)userPrincipalName ?? DBNull.Value);
            command.Parameters.AddWithValue("@normalizedUpn", (object?)userPrincipalName?.ToUpperInvariant() ?? DBNull.Value);
            command.Parameters.AddWithValue("@displayName", (object?)displayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@notificationEmail", (object?)notificationEmail ?? DBNull.Value);
            command.Parameters.AddWithValue("@role", role);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@isActive", active);
            command.Parameters.AddWithValue("@weeklyEnabled", weeklyEnabled);
            command.Parameters.AddWithValue("@weeklyEmail", (object?)weeklyEmail ?? DBNull.Value);
            command.Parameters.AddWithValue("@purchaseDefault", row.Bool("purchase_reminder_default"));
            command.Parameters.AddWithValue("@additionalCopyDefault", row.Bool("additional_copy_reminder_default"));
            command.Parameters.AddWithValue("@mineDefault", row.Bool("default_mine_unclaimed_filter"));
            command.Parameters.AddWithValue("@lastLoginUtc", (object?)row.UtcDateTime("lastLogin") ?? DBNull.Value);
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            mapped.Add(sourceId, targetId);
            InsertMapping(connection, transaction, "staff_user", sourceId, targetId);
        }
        importedCounts["staff_users"] = rows.Count;
        return mapped;
    }

    private static void AddStaffRecipientTransformations(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, string?> authenticationEmails,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlySet<Guid> allowedTenantIds,
        ICollection<object> transformations)
    {
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var targetId = staffIds[sourceId];
            var sourceEmailValue = row.Text("email");
            var sourceWeeklyValue = row.Text("weekly_action_summary_email");
            var sourceAssignmentRecipient = LegacyTransportRecipient(
                IsLegacyTruthy(sourceWeeklyValue) ? sourceWeeklyValue : sourceEmailValue);
            var sourcePurchaseReminderRecipient = LegacyTransportRecipient(sourceWeeklyValue);
            var sourceAdditionalCopyReminderRecipient = LegacyTransportRecipient(sourceWeeklyValue);
            var sourceWeeklyRecipient = LegacyTransportRecipient(sourceWeeklyValue);
            var weeklyEnabled = row.Bool("weekly_action_summary_enabled");
            var sourceWeeklyEligible = weeklyEnabled && row.Bool("verified") && sourceWeeklyRecipient is not null;

            string? targetNotificationEmail;
            string? targetWeeklyEmail;
            bool targetWeeklyEnabled;
            bool targetActive;
            string targetRole;
            int targetOrganizationId;
            string? targetAuthenticationEmail;
            string? targetNormalizedAuthenticationEmail;
            using (var command = new SqlCommand(
                       """
                       SELECT [NotificationEmail], [WeeklyActionSummaryEmail], [WeeklyActionSummaryEnabled],
                              [IsActive], [Role], [OrganizationId], [UserPrincipalName], [NormalizedUserPrincipalName]
                       FROM [asap].[StaffUser]
                       WHERE [Id] = @id;
                       """,
                       connection,
                       transaction))
            {
                command.Parameters.AddWithValue("@id", targetId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    throw new MigrationOperationException("reconciliation_failed", $"Imported staff user {sourceId} is missing from the target.");
                }
                targetNotificationEmail = reader.IsDBNull(0) ? null : reader.GetString(0);
                targetWeeklyEmail = reader.IsDBNull(1) ? null : reader.GetString(1);
                targetWeeklyEnabled = reader.GetBoolean(2);
                targetActive = reader.GetBoolean(3);
                targetRole = reader.GetString(4);
                targetOrganizationId = reader.GetInt32(5);
                targetAuthenticationEmail = reader.IsDBNull(6) ? null : reader.GetString(6);
                targetNormalizedAuthenticationEmail = reader.IsDBNull(7) ? null : reader.GetString(7);
            }

            var targetAssignmentRecipient = targetNotificationEmail;
            var targetPurchaseReminderRecipient = targetNotificationEmail;
            var targetAdditionalCopyReminderRecipient = targetNotificationEmail;
            var targetWeeklyRecipient = targetWeeklyEmail ?? targetNotificationEmail;
            var targetScopeValid = targetRole == "super_admin"
                ? targetOrganizationId == 1
                : targetRole is "staff" or "admin" && targetOrganizationId != 1;
            var targetWeeklyEligible = targetWeeklyEnabled && targetWeeklyRecipient is not null && targetActive &&
                RealEmail(targetAuthenticationEmail) is { } validAuthenticationEmail &&
                string.Equals(validAuthenticationEmail.ToUpperInvariant(), targetNormalizedAuthenticationEmail, StringComparison.Ordinal) &&
                targetScopeValid &&
                OrganizationIsActive(connection, transaction, targetOrganizationId);

            var sourceEmail = authenticationEmails[sourceId];
            var weeklyEmail = RealEmail(row.String("weekly_action_summary_email"));
            var notificationSource = sourceEmail is not null
                ? "staff_email"
                : weeklyEmail is not null
                    ? "weekly_action_summary_email"
                    : "none";

            transformations.Add(new
            {
                entity = "staff_user",
                sourceId,
                notificationEmailSource = notificationSource,
                sourceAssignmentRecipient,
                sourcePurchaseReminderRecipient,
                sourceAdditionalCopyReminderRecipient,
                sourceWeeklyRecipient,
                targetAssignmentRecipient,
                targetPurchaseReminderRecipient,
                targetAdditionalCopyReminderRecipient,
                targetWeeklyRecipient,
                assignmentRecipientChanged = !RecipientEquals(sourceAssignmentRecipient, targetAssignmentRecipient),
                purchaseReminderRecipientChanged = !RecipientEquals(sourcePurchaseReminderRecipient, targetPurchaseReminderRecipient),
                additionalCopyReminderRecipientChanged = !RecipientEquals(sourceAdditionalCopyReminderRecipient, targetAdditionalCopyReminderRecipient),
                weeklyRecipientChanged = !RecipientEquals(sourceWeeklyRecipient, targetWeeklyRecipient),
                sourceWeeklyEligible,
                targetWeeklyEligible,
                newlyWeeklyEligible = !sourceWeeklyEligible && targetWeeklyEligible,
                targetHasNotificationEmail = targetNotificationEmail is not null,
                targetHasWeeklyRecipient = targetWeeklyRecipient is not null
            });
        }
    }

    private static bool IsLegacyTruthy(string? value) => value is not null && value.Length > 0;

    private static string? LegacyTransportRecipient(string? value) => Clean(value);

    private static bool RecipientEquals(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, long> ImportEmailTemplates(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> emailRows,
        IReadOnlyList<SourceRow> rejectionRows,
        IReadOnlyDictionary<string, int> organizationIds,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var sourceRows = emailRows.Select(row => new SourceTemplate(row, false))
            .Concat(rejectionRows.Select(row => new SourceTemplate(row, true)))
            .OrderBy(item => string.Equals(item.Row.String("scope"), "system", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => item.Row.RequiredString("id"), StringComparer.Ordinal)
            .ToArray();
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var source in sourceRows)
        {
            var row = source.Row;
            var sourceId = row.RequiredString("id");
            var scope = row.RequiredString("scope").ToLowerInvariant();
            var organizationId = scope switch
            {
                "system" => 1,
                "library" => ResolveOrganizationId(row, "libraryOrganization", organizationIds),
                _ => throw new MigrationOperationException("email_template_scope_invalid", $"Email template {sourceId} has an invalid scope.")
            };
            if (scope == "library" && organizationId == 1)
            {
                throw new MigrationOperationException("email_template_scope_invalid", $"Email template {sourceId} has system ownership in library scope.");
            }
            var templateKey = source.IsRejection
                ? "rejection:" + (row.String("sourceTemplateId") ?? sourceId)
                : row.RequiredString("templateKey");
            var sourceTemplateId = scope == "library"
                ? ResolveSourceTemplateId(connection, transaction, row, source.IsRejection, templateKey, mapped)
                : null;
            var isCustom = scope == "library" && sourceTemplateId is null;
            var subject = NormalizeTemplateText(row.Text("subject"));
            var body = NormalizeTemplateText(row.Text("body"));
            if (sourceTemplateId is null && (subject is null || body is null))
            {
                throw new MigrationOperationException("email_template_content_missing", $"Email template {sourceId} has no complete content.");
            }

            long targetId;
            var existingId = scope == "system" ? FindSystemTemplateId(connection, transaction, templateKey) : null;
            if (existingId is not null)
            {
                targetId = existingId.Value;
                using var update = new SqlCommand(
                    """
                    UPDATE [asap].[EmailTemplate]
                    SET [DisplayName] = @displayName, [SubjectTemplate] = @subject,
                        [BodyTemplate] = @body, [IsHidden] = @isHidden, [SortOrder] = @sortOrder
                    WHERE [Id] = @id;
                    """,
                    connection,
                    transaction);
                AddTemplateParameters(update, row, targetId, organizationId, templateKey, sourceTemplateId, isCustom, subject, body);
                update.ExecuteNonQuery();
            }
            else
            {
                using var insert = new SqlCommand(
                    """
                    INSERT INTO [asap].[EmailTemplate]
                        ([OrganizationId], [TemplateKey], [SourceTemplateId], [DisplayName],
                         [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
                    OUTPUT inserted.[Id]
                    VALUES
                        (@organizationId, @templateKey, @sourceTemplateId, @displayName,
                         @subject, @body, @isHidden, @isCustom, @sortOrder);
                    """,
                    connection,
                    transaction);
                AddTemplateParameters(insert, row, null, organizationId, templateKey, sourceTemplateId, isCustom, subject, body);
                targetId = Convert.ToInt64(insert.ExecuteScalar());
            }
            mapped.Add(sourceId, targetId);
            InsertMapping(connection, transaction, "email_template", sourceId, targetId);
            if (row.String("fromAddress") is not null || row.String("fromName") is not null)
            {
                transformations.Add(new
                {
                    entity = "email_template",
                    sourceId,
                    transformation = "template_sender_fields_moved_to_scoped_email_settings"
                });
            }
        }
        importedCounts["email_templates"] = emailRows.Count;
        importedCounts["rejection_templates"] = rejectionRows.Count;
        return mapped;
    }

    private static void AddTemplateParameters(
        SqlCommand command,
        SourceRow row,
        long? id,
        int organizationId,
        string templateKey,
        long? sourceTemplateId,
        bool isCustom,
        string? subject,
        string? body)
    {
        command.Parameters.AddWithValue("@id", DbValue(id));
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@templateKey", templateKey);
        command.Parameters.AddWithValue("@sourceTemplateId", DbValue(sourceTemplateId));
        command.Parameters.AddWithValue("@displayName", DbString(row.Text("name")));
        command.Parameters.AddWithValue("@subject", DbString(subject));
        command.Parameters.AddWithValue("@body", DbString(body));
        command.Parameters.AddWithValue("@isHidden", !row.Bool("enabled", true));
        command.Parameters.AddWithValue("@isCustom", isCustom);
        command.Parameters.AddWithValue("@sortOrder", row.Int32("sortOrder") ?? 0);
    }

    private static string? NormalizeTemplateText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static long? ResolveSourceTemplateId(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        bool isRejection,
        string templateKey,
        IReadOnlyDictionary<string, long> mapped)
    {
        var explicitSourceId = row.String("sourceTemplateId");
        if (explicitSourceId is not null)
        {
            if (mapped.TryGetValue(explicitSourceId, out var targetId)) return targetId;
            throw new MigrationOperationException("email_template_source_unresolved", $"Email template {row.RequiredString("id")} has an unresolved source template.");
        }
        if (isRejection) return null;
        return FindSystemTemplateId(connection, transaction, templateKey);
    }

    private static long? FindSystemTemplateId(
        SqlConnection connection,
        SqlTransaction transaction,
        string templateKey)
    {
        using var command = new SqlCommand(
            "SELECT [Id] FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = @key;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@key", templateKey);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static Dictionary<string, long> ImportMaterialFormats(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, int> organizationIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var scope = row.RequiredString("scope").ToLowerInvariant();
            var code = NormalizeFormatCode(row.RequiredString("code"));
            var ownerId = scope switch
            {
                "system" => 1,
                "library" => ResolveOrganizationId(row, "libraryOrganization", organizationIds),
                _ => throw new MigrationOperationException("format_scope_invalid", $"Material format {sourceId} has an unknown scope.")
            };
            if (scope == "library" && ownerId == 1)
            {
                throw new MigrationOperationException("format_scope_invalid", $"Material format {sourceId} has system ownership in library scope.");
            }

            var systemFormatId = scope == "library"
                ? FindFormatId(connection, transaction, 1, code)
                : null;
            long targetId;
            if (systemFormatId is not null)
            {
                targetId = systemFormatId.Value;
                using var overrideCommand = new SqlCommand(
                    """
                    INSERT INTO [asap].[MaterialFormatOverride]
                        ([LibraryOrganizationId], [MaterialFormatId], [Label], [SortOrder], [IsEnabled],
                         [MessageBehavior], [Message], [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel],
                         [IdentifierMode], [IdentifierLabel], [PublicationMode], [PublicationLabel])
                    OUTPUT inserted.[Id]
                    VALUES
                        (@organizationId, @formatId, @label, @sortOrder, @enabled,
                         @messageBehavior, @message, @titleMode, @titleLabel, @authorMode, @authorLabel,
                         @identifierMode, @identifierLabel, @publicationMode, @publicationLabel);
                    """,
                    connection,
                    transaction);
                AddFormatParameters(
                    overrideCommand,
                    row,
                    ownerId,
                    targetId,
                    includeOwnerAndDates: false,
                    sparseOverride: true,
                    exportedAtUtc);
                var overrideId = Convert.ToInt64(overrideCommand.ExecuteScalar());
                InsertMapping(connection, transaction, "material_format_override", sourceId, overrideId);
            }
            else
            {
                var existingId = FindFormatId(connection, transaction, ownerId, code);
                if (existingId is not null)
                {
                    targetId = existingId.Value;
                    using var update = new SqlCommand(
                        """
                        UPDATE [asap].[MaterialFormat]
                        SET [Label] = @label, [SortOrder] = @sortOrder, [IsEnabled] = @enabled,
                            [MessageBehavior] = @messageBehavior, [Message] = @message,
                            [TitleMode] = @titleMode, [TitleLabel] = @titleLabel,
                            [AuthorMode] = @authorMode, [AuthorLabel] = @authorLabel,
                            [IdentifierMode] = @identifierMode, [IdentifierLabel] = @identifierLabel,
                            [PublicationMode] = @publicationMode, [PublicationLabel] = @publicationLabel,
                            [CreatedUtc] = @createdUtc, [UpdatedUtc] = @updatedUtc
                        WHERE [Id] = @formatId;
                        """,
                        connection,
                        transaction);
                    AddFormatParameters(
                        update,
                        row,
                        ownerId,
                        targetId,
                        includeOwnerAndDates: true,
                        sparseOverride: false,
                        exportedAtUtc);
                    update.ExecuteNonQuery();
                }
                else
                {
                    using var insert = new SqlCommand(
                        """
                        INSERT INTO [asap].[MaterialFormat]
                            ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled],
                             [MessageBehavior], [Message], [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel],
                             [IdentifierMode], [IdentifierLabel], [PublicationMode], [PublicationLabel],
                             [CreatedUtc], [UpdatedUtc])
                        OUTPUT inserted.[Id]
                        VALUES
                            (@organizationId, @code, @label, @sortOrder, @enabled,
                             @messageBehavior, @message, @titleMode, @titleLabel, @authorMode, @authorLabel,
                             @identifierMode, @identifierLabel, @publicationMode, @publicationLabel,
                             @createdUtc, @updatedUtc);
                        """,
                        connection,
                        transaction);
                    AddFormatParameters(
                        insert,
                        row,
                        ownerId,
                        null,
                        includeOwnerAndDates: true,
                        sparseOverride: false,
                        exportedAtUtc);
                    targetId = Convert.ToInt64(insert.ExecuteScalar());
                }
                InsertMapping(connection, transaction, "material_format", sourceId, targetId);
            }
            mapped.Add(sourceId, targetId);
        }
        importedCounts["material_formats"] = rows.Count;
        return mapped;
    }

    private static void AddFormatParameters(
        SqlCommand command,
        SourceRow row,
        int organizationId,
        long? formatId,
        bool includeOwnerAndDates,
        bool sparseOverride,
        DateTime exportedAtUtc)
    {
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@formatId", (object?)formatId ?? DBNull.Value);
        command.Parameters.AddWithValue("@code", NormalizeFormatCode(row.RequiredString("code")));
        command.Parameters.AddWithValue("@label", sparseOverride ? DbString(row.String("label")) : row.RequiredText("label"));
        command.Parameters.AddWithValue("@sortOrder", sparseOverride ? DbValue(row.Int32("sortOrder")) : row.Int32("sortOrder") ?? 0);
        command.Parameters.AddWithValue("@enabled", sparseOverride ? DbValue(row.NullableBool("enabled")) : row.Bool("enabled", true));
        command.Parameters.AddWithValue("@messageBehavior", DbString(NormalizeOptionalEnum(
            row.String("messageBehavior"),
            ["none", "message", "ebookMessage", "eaudiobookMessage"],
            "format_message_behavior_invalid")));
        command.Parameters.AddWithValue("@message", DbString(sparseOverride ? row.String("message") : row.Text("message")));
        var titleMode = NormalizeOptionalEnum(
            row.String("titleMode"),
            ["required"],
            "format_title_mode_invalid");
        command.Parameters.AddWithValue("@titleMode", DbString(sparseOverride ? titleMode : titleMode ?? "required"));
        command.Parameters.AddWithValue("@titleLabel", DbString(sparseOverride ? row.String("titleLabel") : row.Text("titleLabel")));
        command.Parameters.AddWithValue("@authorMode", DbString(NormalizeOptionalEnum(
            row.String("authorMode"), ["required", "optional", "hidden"], "format_author_mode_invalid")));
        command.Parameters.AddWithValue("@authorLabel", DbString(sparseOverride ? row.String("authorLabel") : row.Text("authorLabel")));
        command.Parameters.AddWithValue("@identifierMode", DbString(NormalizeOptionalEnum(
            row.String("identifierMode"), ["required", "optional", "hidden"], "format_identifier_mode_invalid")));
        command.Parameters.AddWithValue("@identifierLabel", DbString(sparseOverride ? row.String("identifierLabel") : row.Text("identifierLabel")));
        command.Parameters.AddWithValue("@publicationMode", DbString(NormalizeOptionalEnum(
            row.String("publicationMode"), ["required", "optional", "hidden"], "format_publication_mode_invalid")));
        command.Parameters.AddWithValue("@publicationLabel", DbString(sparseOverride ? row.String("publicationLabel") : row.Text("publicationLabel")));
        if (includeOwnerAndDates)
        {
            command.Parameters.AddWithValue("@createdUtc", row.UtcDateTime("created") ?? exportedAtUtc);
            command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
        }
    }

    private static Dictionary<string, long> ImportWorkflowTags(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IDictionary<string, int> importedCounts)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        var normalizedCodes = new HashSet<string>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var code = NormalizeWorkflowTagCode(row.RequiredString("code"));
            if (!mapped.TryAdd(sourceId, 0) || !normalizedCodes.Add(code))
            {
                throw new MigrationOperationException(
                    "workflow_tag_conflict",
                    $"Workflow tag {sourceId} duplicates a source identity or normalized code.");
            }
            var id = FindTagId(connection, transaction, code);
            var label = row.String("label") ?? code;
            var sortOrder = row.Int32("sortOrder") ?? 1000 + ordinal;
            if (id is null)
            {
                using var insert = new SqlCommand(
                    "INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) OUTPUT inserted.[Id] VALUES (@code, @label, @sortOrder);",
                    connection,
                    transaction);
                insert.Parameters.AddWithValue("@code", code);
                insert.Parameters.AddWithValue("@label", label);
                insert.Parameters.AddWithValue("@sortOrder", sortOrder);
                id = Convert.ToInt64(insert.ExecuteScalar());
            }
            else
            {
                using var update = new SqlCommand(
                    "UPDATE [asap].[WorkflowTag] SET [Label] = @label, [SortOrder] = @sortOrder WHERE [Id] = @id;",
                    connection,
                    transaction);
                update.Parameters.AddWithValue("@id", id.Value);
                update.Parameters.AddWithValue("@label", label);
                update.Parameters.AddWithValue("@sortOrder", sortOrder);
                update.ExecuteNonQuery();
            }
            mapped[sourceId] = id.Value;
            InsertMapping(connection, transaction, "workflow_tag", sourceId, id.Value);
            ordinal++;
        }
        importedCounts["workflow_tags"] = rows.Count;
        return mapped;
    }

    private static Dictionary<string, long> ImportFormatAutoClaimRules(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlySet<Guid> allowedTenantIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        var activeScopes = new HashSet<(int LibraryId, long FormatId)>();
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var libraryId = row.Int32("libraryOrgId")
                ?? throw new MigrationOperationException("claim_rule_library_missing", $"Format claim rule {sourceId} has no library.");
            if (row.HasValue("libraryOrganization") && ResolveOrganizationId(row, "libraryOrganization", organizationIds) != libraryId)
            {
                throw new MigrationOperationException(
                    "claim_rule_organization_conflict",
                    $"Format claim rule {sourceId} has conflicting library organization references.");
            }
            if (libraryId == 1 || !OrganizationExists(connection, transaction, libraryId))
            {
                throw new MigrationOperationException("claim_rule_library_invalid", $"Format claim rule {sourceId} has an invalid library.");
            }
            var formatCode = NormalizeFormatCode(row.RequiredString("format"));
            var formatId = FindFormatId(connection, transaction, libraryId, formatCode) ??
                FindFormatId(connection, transaction, 1, formatCode) ??
                throw new MigrationOperationException("claim_rule_format_unresolved", $"Format claim rule {sourceId} has no resolvable format.");
            var sourceStaffId = ReadConsistentReference(
                row,
                "staffUserId",
                "staffUser",
                "claim_rule_staff_reference_conflict");
            var staffId = ResolveOptionalMapping(sourceStaffId, staffIds);
            var requestedActive = row.Bool("active");
            var eligible = staffId is not null && IsStaffEligibleForLibrary(
                connection,
                transaction,
                staffId.Value,
                libraryId,
                allowedTenantIds);
            var active = requestedActive && eligible;
            DateTime? deactivatedUtc = active ? null : row.UtcDateTime("updated") ?? exportedAtUtc;
            var reason = !requestedActive
                ? "source_inactive"
                : staffId is null
                    ? "assignee_unmapped"
                    : !eligible
                        ? "assignee_ineligible"
                        : "eligible";
            if (active && !activeScopes.Add((libraryId, formatId)))
            {
                throw new MigrationOperationException(
                    "configuration_scope_conflict",
                    "Effective format auto-claim rules contain duplicate active scoped configuration.");
            }

            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[FormatAutoClaimRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc], [DeactivatedUtc])
                OUTPUT inserted.[Id]
                VALUES (@libraryId, @formatId, @staffId, @active, @createdUtc, @deactivatedUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@libraryId", libraryId);
            command.Parameters.AddWithValue("@formatId", formatId);
            command.Parameters.AddWithValue("@staffId", DbValue(staffId));
            command.Parameters.AddWithValue("@active", active);
            command.Parameters.AddWithValue("@createdUtc", row.UtcDateTime("created") ?? exportedAtUtc);
            command.Parameters.AddWithValue("@deactivatedUtc", DbValue(deactivatedUtc));
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            mapped.Add(sourceId, targetId);
            InsertMapping(connection, transaction, "format_auto_claim_rule", sourceId, targetId);
            transformations.Add(new
            {
                entity = "format_auto_claim_rule",
                sourceId,
                sourceStaffUserId = sourceStaffId,
                targetStaffUserId = staffId,
                sourceActive = requestedActive,
                targetActive = active,
                reason
            });
        }
        importedCounts["format_claim_rules"] = rows.Count;
        return mapped;
    }

    private static Dictionary<string, long> ImportTitleRequests(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> claimRuleIds,
        IReadOnlySet<int> validOrganizationIds,
        IDictionary<string, long> tagIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        IReadOnlyDictionary<string, string> requestCloseReasons,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations,
        ICollection<ClaimTransformation> claimTransformations)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var status = ResolveRequestStatus(row, requestStatuses);
            var closeReason = ResolveRequestCloseReason(row, requestCloseReasons);
            if ((status == "closed") != (closeReason is not null))
            {
                throw new MigrationOperationException("request_close_state_invalid", $"Title request {sourceId} has inconsistent status and close reason.");
            }
            var libraryId = row.Int32("libraryOrgId") ?? throw new MigrationOperationException(
                "request_library_missing",
                $"Title request {sourceId} has no library.");
            if (libraryId == 1 || !validOrganizationIds.Contains(libraryId))
            {
                throw new MigrationOperationException(
                    "request_library_unresolved",
                    $"Title request {sourceId} references an unknown or system library.");
            }
            ValidateOptionalOrganizationReference(row.Int32("patronOrgId"), validOrganizationIds, sourceId, "patron organization");
            ValidateOptionalOrganizationReference(row.Int32("staffLibraryOrgIdCreatedBy"), validOrganizationIds, sourceId, "staff library organization");
            var identifier = row.String("identifier");
            var bibId = row.String("bibid");
            var sourceIsbnStatus = row.String("isbnCheckStatus");
            var targetIsbnStatus = NormalizeIsbnStatus(sourceId, sourceIsbnStatus, identifier, bibId);
            var retryCount = targetIsbnStatus == "skipped_no_isbn"
                ? 0
                : row.Int32("isbnCheckRetryCount") ?? 0;
            if (retryCount < 0)
            {
                throw new MigrationOperationException("request_isbn_retry_invalid", $"Title request {sourceId} has a negative identifier retry count.");
            }

            var formatId = ResolveRequestFormatId(connection, transaction, row, formatIds);
            var claim = ResolveRequestClaim(
                connection,
                transaction,
                row,
                status,
                formatId,
                staffIds,
                claimRuleIds,
                allowedTenantIds);

            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[TitleRequest]
                    ([LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [StaffLibraryOrganizationIdCreatedBy],
                     [Barcode], [Email], [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                     [Title], [Author], [Identifier], [Publication], [ExactPublicationDate], [CustomFieldsJson],
                     [AutoHold], [MaterialFormatId], [Status], [CloseReason], [BibId], [Notes],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClaimRuleId],
                     [LastPromoterCheckUtc], [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount],
                     [IsbnCheckLastErrorCode], [LastCheckedUtc], [CreatedUtc], [UpdatedUtc])
                OUTPUT inserted.[Id]
                VALUES
                    (@legacyId, @libraryId, @patronOrganizationId, @staffLibraryId,
                     @barcode, @email, @nameFirst, @nameLast, @patronCodeId, @patronCodeDescription,
                     @pickupId, @pickupName, @libraryName,
                     @title, @author, @identifier, @publication, @exactPublicationDate, @customFields,
                     @autoHold, @formatId, @status, @closeReason, @bibId, @notes,
                     @claimedById, @claimedDisplay, @claimedAt, @claimType, @claimRuleId,
                     @lastPromoterCheck, @isbnStatus, @isbnResult, @retryCount,
                     @lastErrorCode, @lastChecked, @createdUtc, @updatedUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@legacyId", DbString(row.String("legacyId")));
            command.Parameters.AddWithValue("@libraryId", libraryId);
            command.Parameters.AddWithValue("@patronOrganizationId", DbValue(row.Int32("patronOrgId")));
            command.Parameters.AddWithValue("@staffLibraryId", DbValue(row.Int32("staffLibraryOrgIdCreatedBy")));
            command.Parameters.AddWithValue("@barcode", row.RequiredString("barcode"));
            command.Parameters.AddWithValue("@email", DbString(row.String("email")));
            command.Parameters.AddWithValue("@nameFirst", DbString(row.Text("nameFirst")));
            command.Parameters.AddWithValue("@nameLast", DbString(row.Text("nameLast")));
            command.Parameters.AddWithValue("@patronCodeId", DbString(row.String("patronCodeId")));
            command.Parameters.AddWithValue("@patronCodeDescription", DbString(row.Text("patronCodeDescription")));
            command.Parameters.AddWithValue("@pickupId", DbValue(row.Int32("preferredPickupBranchId")));
            command.Parameters.AddWithValue("@pickupName", DbString(row.Text("preferredPickupBranchName")));
            command.Parameters.AddWithValue("@libraryName", DbString(row.Text("libraryOrgName")));
            command.Parameters.AddWithValue("@title", row.RequiredText("title"));
            command.Parameters.AddWithValue("@author", DbString(row.Text("author")));
            command.Parameters.AddWithValue("@identifier", DbString(identifier));
            command.Parameters.AddWithValue("@publication", DbString(row.Text("publication")));
            command.Parameters.AddWithValue("@exactPublicationDate", DbValue(ParseDate(row.String("exactPublicationDate"), "request_publication_date_invalid")));
            command.Parameters.AddWithValue("@customFields", DbString(row.JsonText("customFields")));
            command.Parameters.AddWithValue("@autoHold", row.Bool("autohold"));
            command.Parameters.AddWithValue("@formatId", formatId);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@closeReason", DbString(closeReason));
            command.Parameters.AddWithValue("@bibId", DbString(bibId));
            command.Parameters.AddWithValue("@notes", DbString(row.Text("notes")));
            command.Parameters.AddWithValue("@claimedById", DbValue(claim.StaffUserId));
            command.Parameters.AddWithValue("@claimedDisplay", DbString(claim.DisplayName));
            command.Parameters.AddWithValue("@claimedAt", DbValue(claim.ClaimedAtUtc));
            command.Parameters.AddWithValue("@claimType", DbString(claim.ClaimType));
            command.Parameters.AddWithValue("@claimRuleId", DbValue(claim.ClaimRuleId));
            command.Parameters.AddWithValue("@lastPromoterCheck", DbValue(row.UtcDateTime("lastPromoterCheck")));
            command.Parameters.AddWithValue("@isbnStatus", DbString(targetIsbnStatus));
            command.Parameters.AddWithValue("@isbnResult", DbString(row.Text("isbnCheckResult")));
            command.Parameters.AddWithValue("@retryCount", retryCount);
            command.Parameters.AddWithValue("@lastErrorCode", DbString(targetIsbnStatus == "error_max_retries" ? "legacy_retry_exhausted" : null));
            command.Parameters.AddWithValue("@lastChecked", DbValue(row.UtcDateTime("lastChecked")));
            command.Parameters.AddWithValue("@createdUtc", row.UtcDateTime("created") ?? throw new MigrationOperationException("request_created_missing", $"Title request {sourceId} has no creation timestamp."));
            command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? throw new MigrationOperationException("request_updated_missing", $"Title request {sourceId} has no update timestamp."));
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            mapped.Add(sourceId, targetId);
            InsertMapping(connection, transaction, "title_request", sourceId, targetId);

            if (claim.RequiresMigrationAnnotation)
            {
                InsertClaimMigrationAnnotation(
                    connection,
                    transaction,
                    targetId,
                    sourceId,
                    row,
                    claim,
                    exportedAtUtc);
                importedCounts.TryGetValue("claim_migration_annotations", out var annotationCount);
                importedCounts["claim_migration_annotations"] = annotationCount + 1;
            }

            if (targetIsbnStatus == "found")
            {
                var foundTagId = EnsureTag(connection, transaction, "polaris_bib_found", "Polaris BIB found", 20);
                InsertRequestTag(connection, transaction, targetId, foundTagId);
            }
            transformations.Add(new
            {
                entity = "title_request_isbn_status",
                sourceId,
                sourceStatus = sourceIsbnStatus,
                targetStatus = targetIsbnStatus,
                reason = IsbnTransformationReason(sourceIsbnStatus, targetIsbnStatus)
            });
            if (claim.HasSourceAttribution)
            {
                var claimTransformation = new ClaimTransformation(
                    sourceId,
                    row.Int32("libraryOrgId") ?? 0,
                    status,
                    claim.SourceClaimantId,
                    claim.MappedStaffUserId,
                    claim.StaffUserId,
                    row.String("claimedByDisplayName"),
                    row.UtcDateTime("claimedAt"),
                    row.String("claimType"),
                    row.String("claimRuleId"),
                    claim.Reason,
                    claim.RequiresMigrationAnnotation);
                claimTransformations.Add(claimTransformation);
                transformations.Add(new
                {
                    entity = "title_request_claim",
                    sourceId,
                    sourceClaimantId = claim.SourceClaimantId,
                    mappedStaffUserId = claim.MappedStaffUserId,
                    effectiveStaffUserId = claim.StaffUserId,
                    sourceDisplayName = claimTransformation.SourceDisplayName,
                    sourceClaimedAtUtc = claimTransformation.SourceClaimedAtUtc,
                    sourceClaimType = claimTransformation.SourceClaimType,
                    sourceClaimRuleId = claimTransformation.SourceClaimRuleId,
                    reason = claim.Reason,
                    migrationAnnotationInserted = claim.RequiresMigrationAnnotation
                });
            }
        }
        importedCounts.TryAdd("claim_migration_annotations", 0);
        importedCounts["title_requests"] = rows.Count;
        return mapped;
    }

    private static Dictionary<string, long> ImportAdditionalCopies(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlySet<Guid> allowedTenantIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations,
        ICollection<ClaimTransformation> claimTransformations)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        var validOrganizationIds = organizationIds.Values.ToHashSet();
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var libraryId = row.Int32("libraryOrgId") ?? throw new MigrationOperationException(
                "additional_copy_library_missing",
                $"Additional-copy request {sourceId} has no library.");
            if (!validOrganizationIds.Contains(libraryId))
            {
                throw new MigrationOperationException(
                    "additional_copy_library_unresolved",
                    $"Additional-copy request {sourceId} references an unknown library.");
            }
            var status = NormalizeAdditionalCopyStatus(row.RequiredString("status"));
            var createdUtc = row.UtcDateTime("created") ?? throw new MigrationOperationException(
                "additional_copy_created_missing",
                $"Additional-copy request {sourceId} has no creation timestamp.");
            var updatedUtc = row.UtcDateTime("updated") ?? createdUtc;
            var closedUtc = row.UtcDateTime("closedAt");
            var closedDisplayName = row.String("closedByUsername");
            if ((status == "closed") != closedUtc.HasValue)
            {
                throw new MigrationOperationException(
                    "additional_copy_close_state_invalid",
                    $"Additional-copy request {sourceId} has inconsistent close attribution.");
            }
            var sourceTitleRequestId = ResolveRequiredMapping(
                row.String("sourceTitleRequest"),
                requestIds,
                "additional_copy_request_reference_invalid",
                "additional-copy source title request");
            var materialFormatId = ResolveAdditionalCopyFormatId(connection, transaction, row);
            var claim = ResolveAdditionalCopyClaim(
                connection,
                transaction,
                row,
                status,
                staffIds,
                allowedTenantIds);
            var notes = AdditionalCopyNotes(row, claim, exportedAtUtc);
            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[AdditionalCopyRequest]
                    ([LegacyId], [SourceTitleRequestId], [LibraryOrganizationId], [LibraryNameSnapshot],
                     [BibId], [Title], [Author], [Identifier], [Publication], [MaterialFormatId], [FormatSnapshot],
                     [Status], [Notes], [CreatedByStaffUserId], [CreatedByDisplayName], [CreatedUtc], [UpdatedUtc],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClaimRuleId],
                     [ClosedByStaffUserId], [ClosedByDisplayName], [ClosedUtc])
                OUTPUT inserted.[Id]
                VALUES
                    (@legacyId, @sourceTitleRequestId, @libraryId, @libraryName,
                     @bibId, @title, @author, @identifier, @publication, @materialFormatId, @formatSnapshot,
                     @status, @notes, @createdByStaffUserId, @createdByDisplayName, @createdUtc, @updatedUtc,
                     @claimedByStaffUserId, @claimedByDisplayName, @claimedAtUtc, NULL, NULL,
                     @closedByStaffUserId, @closedByDisplayName, @closedUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@legacyId", DbString(row.String("legacyId")));
            command.Parameters.AddWithValue("@sourceTitleRequestId", DbValue(sourceTitleRequestId));
            command.Parameters.AddWithValue("@libraryId", libraryId);
            command.Parameters.AddWithValue("@libraryName", DbString(row.Text("libraryOrgName")));
            command.Parameters.AddWithValue("@bibId", row.RequiredString("bibid"));
            command.Parameters.AddWithValue("@title", row.RequiredText("title"));
            command.Parameters.AddWithValue("@author", DbString(row.Text("author")));
            command.Parameters.AddWithValue("@identifier", DbString(row.String("identifier")));
            command.Parameters.AddWithValue("@publication", DbString(row.Text("publication")));
            command.Parameters.AddWithValue("@materialFormatId", DbValue(materialFormatId));
            command.Parameters.AddWithValue("@formatSnapshot", DbString(row.String("format")));
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@notes", DbString(notes));
            command.Parameters.AddWithValue(
                "@createdByStaffUserId",
                DbValue(ResolveRequiredMapping(
                    row.String("createdByStaff"),
                    staffIds,
                    "additional_copy_creator_reference_invalid",
                    "additional-copy creator staff")));
            command.Parameters.AddWithValue("@createdByDisplayName", DbString(row.String("createdByUsername")));
            command.Parameters.AddWithValue("@createdUtc", createdUtc);
            command.Parameters.AddWithValue("@updatedUtc", updatedUtc);
            command.Parameters.AddWithValue("@claimedByStaffUserId", DbValue(claim.StaffUserId));
            command.Parameters.AddWithValue("@claimedByDisplayName", DbString(claim.DisplayName));
            command.Parameters.AddWithValue("@claimedAtUtc", DbValue(claim.ClaimedAtUtc));
            command.Parameters.AddWithValue(
                "@closedByStaffUserId",
                DbValue(ResolveRequiredMapping(
                    row.String("closedByStaff"),
                    staffIds,
                    "additional_copy_closer_reference_invalid",
                    "additional-copy closer staff")));
            command.Parameters.AddWithValue("@closedByDisplayName", DbString(closedDisplayName));
            command.Parameters.AddWithValue("@closedUtc", DbValue(closedUtc));
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            mapped.Add(sourceId, targetId);
            InsertMapping(connection, transaction, "additional_copy", sourceId, targetId);

            if (claim.RequiresMigrationAnnotation)
            {
                importedCounts.TryGetValue("additional_copy_claim_migration_annotations", out var annotationCount);
                importedCounts["additional_copy_claim_migration_annotations"] = annotationCount + 1;
            }
            if (claim.HasSourceAttribution)
            {
                var transformation = new ClaimTransformation(
                    sourceId,
                    libraryId,
                    status,
                    claim.SourceClaimantId,
                    claim.MappedStaffUserId,
                    claim.StaffUserId,
                    row.String("claimedByDisplayName"),
                    row.UtcDateTime("claimedAt"),
                    null,
                    null,
                    claim.Reason,
                    claim.RequiresMigrationAnnotation);
                claimTransformations.Add(transformation);
                transformations.Add(new
                {
                    entity = "additional_copy_claim",
                    sourceId,
                    sourceClaimantId = claim.SourceClaimantId,
                    mappedStaffUserId = claim.MappedStaffUserId,
                    effectiveStaffUserId = claim.StaffUserId,
                    sourceDisplayName = transformation.SourceDisplayName,
                    sourceClaimedAtUtc = transformation.SourceClaimedAtUtc,
                    reason = claim.Reason,
                    migrationAnnotationInserted = claim.RequiresMigrationAnnotation
                });
            }
            if (!row.HasValue("updated"))
            {
                transformations.Add(new
                {
                    entity = "additional_copy_updated_timestamp",
                    sourceId,
                    sourceUpdatedUtc = (DateTime?)null,
                    targetUpdatedUtc = updatedUtc,
                    reason = "missing_updated_uses_created"
                });
            }
        }
        importedCounts.TryAdd("additional_copy_claim_migration_annotations", 0);
        importedCounts["additional_copy_requests"] = rows.Count;
        return mapped;
    }

    private static void ImportDeletedRequestAudit(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlySet<int> validOrganizationIds,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var originalKey = row.RequiredString("titleRequestId");
            var barcode = row.String("barcode");
            var requestType = barcode is null ? "additional_copy" : "title_request";
            var libraryId = row.Int32("libraryOrgId") ?? throw new MigrationOperationException(
                "deleted_request_library_missing",
                $"Deleted-request audit {sourceId} has no library.");
            if (libraryId == 1 || !validOrganizationIds.Contains(libraryId))
            {
                throw new MigrationOperationException(
                    "deleted_request_library_unresolved",
                    $"Deleted-request audit {sourceId} references an unknown or system library.");
            }
            var status = NormalizeStatus(row.RequiredString("status"));
            var closeReason = NormalizeCloseReason(row.String("closeReason"));
            var createdUtc = ParseUtcText(
                row.JsonPropertyString("snapshot", "created"),
                "deleted_request_created_invalid");
            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[DeletedRequestAudit]
                    ([RequestType], [OriginalRequestKey], [LibraryOrganizationId], [Title], [Author], [Identifier],
                     [BibId], [Status], [CloseReason], [MaskedBarcode], [CreatedUtc], [DeletedUtc],
                     [DeletedByStaffUserId], [DeletedByDisplayName])
                OUTPUT inserted.[Id]
                VALUES
                    (@requestType, @originalKey, @libraryId, @title, @author, @identifier,
                     @bibId, @status, @closeReason, @maskedBarcode, @createdUtc, @deletedUtc,
                     @deletedByStaffUserId, @deletedByDisplayName);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestType", requestType);
            command.Parameters.AddWithValue("@originalKey", originalKey);
            command.Parameters.AddWithValue("@libraryId", libraryId);
            command.Parameters.AddWithValue("@title", DbString(row.Text("title")));
            command.Parameters.AddWithValue("@author", DbString(row.Text("author")));
            command.Parameters.AddWithValue("@identifier", DbString(row.String("identifier")));
            command.Parameters.AddWithValue("@bibId", DbString(row.String("bibid")));
            command.Parameters.AddWithValue("@status", DbString(status));
            command.Parameters.AddWithValue("@closeReason", DbString(closeReason));
            command.Parameters.AddWithValue("@maskedBarcode", DbString(MaskBarcode(barcode)));
            command.Parameters.AddWithValue("@createdUtc", DbValue(createdUtc));
            command.Parameters.AddWithValue("@deletedUtc", row.UtcDateTime("deletedAt") ?? throw new MigrationOperationException(
                "deleted_request_timestamp_missing",
                $"Deleted-request audit {sourceId} has no deletion timestamp."));
            command.Parameters.AddWithValue(
                "@deletedByStaffUserId",
                DbValue(ResolveRequiredMapping(
                    row.String("deletedByStaff"),
                    staffIds,
                    "deleted_request_actor_reference_invalid",
                    "deleted-request actor staff")));
            command.Parameters.AddWithValue("@deletedByDisplayName", DbString(row.String("deletedByUsername")));
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            InsertMapping(connection, transaction, "deleted_request_audit", sourceId, targetId);
        }
        importedCounts["deleted_request_audit"] = rows.Count;
        transformations.Add(new
        {
            entity = "deleted_request_audit",
            sourceRows = rows.Count,
            targetRows = rows.Count,
            reason = "reduced_audit_excludes_sensitive_and_freeform_fields"
        });
    }

    private static ImportedClaim ResolveAdditionalCopyClaim(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        string status,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        var sourceClaimantId = row.String("claimedByStaffUserId");
        var mappedStaffId = ResolveOptionalMapping(sourceClaimantId, staffIds);
        var displayName = row.String("claimedByDisplayName");
        var claimedAt = row.UtcDateTime("claimedAt");
        var hasAttribution = sourceClaimantId is not null || displayName is not null || claimedAt.HasValue;
        if (!hasAttribution)
        {
            return new(false, false, null, null, null, null, null, null, null, "unclaimed");
        }
        if (status == "closed")
        {
            if (displayName is null || !claimedAt.HasValue)
            {
                return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "closed_attribution_incomplete");
            }
            return new(
                true,
                false,
                sourceClaimantId,
                mappedStaffId,
                mappedStaffId,
                displayName,
                claimedAt,
                null,
                null,
                mappedStaffId.HasValue ? "closed_history_preserved" : "closed_claimant_unmapped");
        }
        if (sourceClaimantId is null || !mappedStaffId.HasValue)
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "claimant_unmapped");
        }
        if (displayName is null || !claimedAt.HasValue)
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "claim_metadata_incomplete");
        }
        var reason = StaffEligibilityReason(
            connection,
            transaction,
            mappedStaffId.Value,
            row.Int32("libraryOrgId") ?? 0,
            allowedTenantIds);
        return reason == "eligible"
            ? new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, displayName, claimedAt, null, null, reason)
            : new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, reason);
    }

    private static string? AdditionalCopyNotes(SourceRow row, ImportedClaim claim, DateTime exportedAtUtc)
    {
        var notes = row.Text("notes");
        if (!claim.RequiresMigrationAnnotation)
        {
            return notes;
        }
        var claimantId = claim.SourceClaimantId ?? "unmapped";
        var displayName = row.String("claimedByDisplayName") ?? "unknown";
        var claimedAt = row.UtcDateTime("claimedAt")?.ToString("O") ?? "unknown";
        var status = NormalizeAdditionalCopyStatus(row.RequiredString("status"));
        var action = status == "closed"
            ? "Retained incomplete closed attribution as historical notes"
            : $"Cleared open claim ({claim.Reason})";
        var annotation = $"[{exportedAtUtc:O}] [ASAP migration:additional_copy_claim_v1] {action}. Previous claimant ID: {claimantId}; display: {displayName.Replace('\r', ' ').Replace('\n', ' ')}; claimed at: {claimedAt}.";
        return string.IsNullOrWhiteSpace(notes) ? annotation : $"{notes.TrimEnd()}\n{annotation}";
    }

    private static long? ResolveAdditionalCopyFormatId(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row)
    {
        var sourceFormat = row.String("format");
        if (sourceFormat is null)
        {
            return null;
        }
        var code = NormalizeFormatCode(sourceFormat);
        var libraryId = row.Int32("libraryOrgId") ?? 0;
        return FindFormatId(connection, transaction, libraryId, code) ??
            FindFormatId(connection, transaction, 1, code) ??
            throw new MigrationOperationException(
                "additional_copy_format_unresolved",
                $"Additional-copy request {row.RequiredString("id")} has no resolvable material format.");
    }

    private static string NormalizeAdditionalCopyStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "open" => "open",
        "closed" => "closed",
        var invalid => throw new MigrationOperationException(
            "additional_copy_status_invalid",
            $"Unknown additional-copy status: {invalid}")
    };

    private static DateTime? ParseUtcText(string? value, string errorCode)
    {
        if (value is null) return null;
        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.UtcDateTime;
        }
        throw new MigrationOperationException(errorCode, $"Invalid source UTC timestamp: {value}");
    }

    private static string? MaskBarcode(string? value) => value is null
        ? null
        : value.Length <= 4 ? new string('*', value.Length) : $"***{value[^4..]}";

    private static void InsertClaimMigrationAnnotation(
        SqlConnection connection,
        SqlTransaction transaction,
        long targetRequestId,
        string sourceRequestId,
        SourceRow row,
        ImportedClaim claim,
        DateTime exportedAtUtc)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            transform = "claim_attribution_normalization_v1",
            sourceCollection = "title_requests",
            sourceRecordId = sourceRequestId,
            sourceClaimantId = claim.SourceClaimantId,
            mappedStaffUserId = claim.MappedStaffUserId,
            sourceDisplayName = row.String("claimedByDisplayName"),
            sourceClaimedAtUtc = row.UtcDateTime("claimedAt"),
            sourceClaimType = row.String("claimType"),
            sourceClaimRuleId = row.String("claimRuleId"),
            reason = claim.Reason
        });
        using var command = new SqlCommand(
            """
            INSERT INTO [asap].[TitleRequestEvent]
                ([TitleRequestId], [EventType], [ActorType], [ActorName], [Message], [MetadataJson], [CreatedUtc])
            VALUES
                (@requestId, N'legacy', N'system', N'migration',
                 N'Legacy claim attribution was normalized during migration.', @metadata, @createdUtc);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@requestId", targetRequestId);
        command.Parameters.AddWithValue("@metadata", metadata);
        command.Parameters.AddWithValue("@createdUtc", exportedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void ImportTitleRequestTags(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> tagIds,
        IDictionary<string, int> importedCounts)
    {
        var relationships = new HashSet<(long RequestId, long TagId)>();
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            if (!requestIds.TryGetValue(row.RequiredString("titleRequest"), out var requestId) ||
                !tagIds.TryGetValue(row.RequiredString("tag"), out var tagId) ||
                !relationships.Add((requestId, tagId)))
            {
                throw new MigrationOperationException(
                    "request_tag_reference_invalid",
                    $"Title request tag {sourceId} has an unresolved or duplicate relationship.");
            }
            InsertRequestTag(connection, transaction, requestId, tagId);
            InsertMapping(connection, transaction, "title_request_tag", sourceId, requestId);
        }
        importedCounts["title_request_tags"] = rows.Count;
    }

    private static void ImportTitleRequestEventsAndPlacementProtection(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> requestRows,
        IReadOnlyList<SourceRow> eventRows,
        IReadOnlyList<SourceRow> statusRows,
        IReadOnlyList<SourceRow> closeReasonRows,
        IReadOnlyDictionary<string, long> requestIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations,
        ICollection<PlacementTransformation> placementTransformations)
    {
        var statuses = BuildStatusMap(statusRows);
        var closeReasons = BuildCloseReasonMap(closeReasonRows);
        var eventsByRequest = new Dictionary<string, List<SourceEvent>>(StringComparer.Ordinal);

        foreach (var row in eventRows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var sourceRequestId = row.RequiredString("titleRequest");
            if (!requestIds.TryGetValue(sourceRequestId, out var requestId))
            {
                throw new MigrationOperationException("request_event_reference_invalid", $"Title request event {sourceId} has an unresolved request.");
            }
            var actorType = row.RequiredString("actorType").ToLowerInvariant();
            if (actorType is not ("system" or "staff" or "patron"))
            {
                throw new MigrationOperationException("request_event_actor_invalid", $"Title request event {sourceId} has an invalid actor type.");
            }
            var sourceEventType = row.RequiredString("eventType");
            var normalizedEventType = sourceEventType.Trim().ToLowerInvariant();
            var eventType = KnownEventTypes.Contains(normalizedEventType) ? normalizedEventType : "legacy";
            var fromStatus = ResolveEventStatus(row.String("fromStatus"), statuses);
            var toStatus = ResolveEventStatus(row.String("toStatus"), statuses);
            var closeReason = ResolveEventCloseReason(row.String("closeReason"), closeReasons);
            var metadata = BuildImportedEventMetadata(row, sourceId, sourceEventType);

            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [CloseReason], [ActorType], [StaffUserId],
                     [ActorName], [Message], [MetadataJson], [CreatedUtc])
                OUTPUT inserted.[Id]
                VALUES
                    (@requestId, @eventType, @status, @closeReason, @actorType, NULL,
                     @actorName, @message, @metadata, @createdUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestId", requestId);
            command.Parameters.AddWithValue("@eventType", eventType);
            command.Parameters.AddWithValue("@status", DbString(toStatus));
            command.Parameters.AddWithValue("@closeReason", DbString(closeReason));
            command.Parameters.AddWithValue("@actorType", actorType);
            command.Parameters.AddWithValue("@actorName", DbString(row.Text("actorName")));
            command.Parameters.AddWithValue("@message", DbString(row.Text("message")));
            command.Parameters.AddWithValue("@metadata", metadata);
            command.Parameters.AddWithValue("@createdUtc", row.UtcDateTime("created") ?? throw new MigrationOperationException("request_event_created_missing", $"Title request event {sourceId} has no creation timestamp."));
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            InsertMapping(connection, transaction, "title_request_event", sourceId, targetId);

            if (!eventsByRequest.TryGetValue(sourceRequestId, out var requestEvents))
            {
                requestEvents = [];
                eventsByRequest.Add(sourceRequestId, requestEvents);
            }
            var bibSources = ReadEventBibSources(row, sourceId);
            requestEvents.Add(new SourceEvent(
                sourceId,
                normalizedEventType,
                fromStatus,
                toStatus,
                closeReason,
                bibSources));
        }

        var markerCount = 0;
        foreach (var request in requestRows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceRequestId = request.RequiredString("id");
            var evidence = new List<PlacementEvidence>();
            var currentStatus = ResolveRequestStatus(request, statuses);
            var currentCloseReason = ResolveRequestCloseReason(request, closeReasons);
            var requestBib = request.String("bibid");
            if (currentStatus == "hold_placed")
            {
                evidence.Add(new(
                    "current_status",
                    "title_requests",
                    sourceRequestId,
                    request.String("statusRef") is null ? "status" : "statusRef",
                    currentStatus));
            }
            if (currentCloseReason is not null && HoldTerminalReasons.Contains(currentCloseReason))
            {
                evidence.Add(new(
                    "terminal_close_reason",
                    "title_requests",
                    sourceRequestId,
                    request.String("closeReasonRef") is null ? "closeReason" : "closeReasonRef",
                    currentCloseReason));
            }

            eventsByRequest.TryGetValue(sourceRequestId, out var requestEvents);
            var sourceEvents = requestEvents ?? [];
            foreach (var sourceEvent in sourceEvents)
            {
                if (sourceEvent.EventType == "hold_placed")
                {
                    evidence.Add(new("dedicated_event", "title_request_events", sourceEvent.Id, "eventType", "hold_placed"));
                }
                if (sourceEvent.ToStatus == "hold_placed")
                {
                    evidence.Add(new("transition_to_placed", "title_request_events", sourceEvent.Id, "toStatus", "hold_placed"));
                }
                if (sourceEvent.FromStatus == "hold_placed")
                {
                    evidence.Add(new("transition_from_placed", "title_request_events", sourceEvent.Id, "fromStatus", "hold_placed"));
                }
                if (sourceEvent.CloseReason is not null && HoldTerminalReasons.Contains(sourceEvent.CloseReason))
                {
                    evidence.Add(new("event_terminal_reason", "title_request_events", sourceEvent.Id, "closeReason", sourceEvent.CloseReason));
                }
            }
            var libraryOrganizationId = request.Int32("libraryOrgId") ?? 0;
            var hints = new List<PlacementEvidence>();
            if (evidence.Count == 0 &&
                currentStatus == "closed" &&
                currentCloseReason is not "rejected" &&
                requestBib is not null)
            {
                hints.Add(new(
                    "recorded_bib_hint",
                    "title_requests",
                    sourceRequestId,
                    "bibid",
                    requestBib));
            }
            foreach (var sourceEvent in sourceEvents)
            {
                if (evidence.Any(item => item.SourceRecordId == sourceEvent.Id)) continue;
                hints.AddRange(sourceEvent.BibSources.Select(source => new PlacementEvidence(
                    "event_bib_hint",
                    source.SourceCollection,
                    source.SourceRecordId,
                    source.SourceField,
                    source.BibId)));
            }
            var sortedHints = hints.Distinct()
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.SourceCollection, StringComparer.Ordinal)
                .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
                .ThenBy(item => item.SourceField, StringComparer.Ordinal)
                .ThenBy(item => item.Value, StringComparer.Ordinal)
                .ToArray();
            if (evidence.Count == 0 && sortedHints.Length > 0)
            {
                placementTransformations.Add(new(
                    sourceRequestId,
                    libraryOrganizationId,
                    currentStatus,
                    null,
                    [],
                    [],
                    "placement_history_ambiguous",
                    sortedHints));
                transformations.Add(new
                {
                    entity = "placed_bib_protection",
                    sourceId = sourceRequestId,
                    libraryOrganizationId,
                    status = currentStatus,
                    bibId = (string?)null,
                    evidence = Array.Empty<PlacementEvidence>(),
                    hints = sortedHints.Select(item => new
                    {
                        kind = item.Kind,
                        sourceCollection = item.SourceCollection,
                        sourceRecordId = item.SourceRecordId,
                        sourceField = item.SourceField,
                        value = item.Value
                    }),
                    action = "placement_history_ambiguous"
                });
                var references = string.Join(", ", sortedHints.Select(item =>
                    $"{item.SourceCollection}/{item.SourceRecordId}/{item.SourceField}"));
                throw new MigrationOperationException(
                    "placement_history_ambiguous",
                    $"Title request {sourceRequestId} has hint-only placement history ({references}); source correction is required.");
            }
            if (evidence.Count == 0)
            {
                placementTransformations.Add(new(
                    sourceRequestId,
                    libraryOrganizationId,
                    currentStatus,
                    null,
                    [],
                    [],
                    "no_placement_evidence",
                    []));
                transformations.Add(new
                {
                    entity = "placed_bib_protection",
                    sourceId = sourceRequestId,
                    libraryOrganizationId,
                    status = currentStatus,
                    bibId = (string?)null,
                    evidence = Array.Empty<PlacementEvidence>(),
                    action = "no_placement_evidence"
                });
                continue;
            }

            var bibSources = new List<PlacementBibSource>();
            var bibIds = new HashSet<string>(StringComparer.Ordinal);
            if (requestBib is not null)
            {
                bibIds.Add(requestBib);
                bibSources.Add(new("title_requests", sourceRequestId, "bibid", requestBib));
            }
            foreach (var sourceEvent in requestEvents ?? [])
            {
                if (!evidence.Any(item => item.SourceRecordId == sourceEvent.Id)) continue;
                foreach (var sourceBib in sourceEvent.BibSources)
                {
                    bibIds.Add(sourceBib.BibId);
                    bibSources.Add(sourceBib);
                }
            }
            if (bibIds.Count > 1)
            {
                throw new MigrationOperationException("placed_bib_conflict", $"Title request {sourceRequestId} has conflicting placed-history BIB values.");
            }
            var bibId = bibIds.SingleOrDefault();
            var sortedEvidence = evidence.Distinct().OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.SourceCollection, StringComparer.Ordinal)
                .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
                .ThenBy(item => item.SourceField, StringComparer.Ordinal)
                .ThenBy(item => item.Value, StringComparer.Ordinal)
                .ToArray();
            var sortedBibSources = bibSources
                .Distinct()
                .OrderBy(item => item.SourceCollection, StringComparer.Ordinal)
                .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
                .ThenBy(item => item.SourceField, StringComparer.Ordinal)
                .ThenBy(item => item.BibId, StringComparer.Ordinal)
                .ToArray();
            var markerMetadata = JsonSerializer.Serialize(new
            {
                legacyBibProtection = true,
                bibId,
                transform = "placed_bib_protection_v1",
                sourceTitleRequestId = sourceRequestId,
                evidence = sortedEvidence.Select(item => new
                {
                    kind = item.Kind,
                    sourceCollection = item.SourceCollection,
                    sourceRecordId = item.SourceRecordId,
                    sourceField = item.SourceField,
                    value = item.Value
                }),
                bibSources = sortedBibSources.Select(item => new
                {
                    sourceCollection = item.SourceCollection,
                    sourceRecordId = item.SourceRecordId,
                    sourceField = item.SourceField,
                    bibId = item.BibId
                })
            });
            using var marker = new SqlCommand(
                """
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [CloseReason], [ActorType], [ActorName],
                     [Message], [MetadataJson], [CreatedUtc])
                VALUES
                    (@requestId, N'legacy', NULL, NULL, N'system', N'migration',
                     N'Legacy placed-state history protection.', @metadata, @createdUtc);
                """,
                connection,
                transaction);
            marker.Parameters.AddWithValue("@requestId", requestIds[sourceRequestId]);
            marker.Parameters.AddWithValue("@metadata", markerMetadata);
            marker.Parameters.AddWithValue("@createdUtc", exportedAtUtc);
            marker.ExecuteNonQuery();
            markerCount++;
            placementTransformations.Add(new(
                sourceRequestId,
                libraryOrganizationId,
                currentStatus,
                bibId,
                sortedEvidence,
                sortedBibSources,
                "inserted",
                []));
            transformations.Add(new
            {
                entity = "placed_bib_protection",
                sourceId = sourceRequestId,
                libraryOrganizationId,
                status = currentStatus,
                bibId,
                evidence = sortedEvidence.Select(item => new
                {
                    kind = item.Kind,
                    sourceCollection = item.SourceCollection,
                    sourceRecordId = item.SourceRecordId,
                    sourceField = item.SourceField,
                    value = item.Value
                }),
                bibSources = sortedBibSources.Select(item => new
                {
                    sourceCollection = item.SourceCollection,
                    sourceRecordId = item.SourceRecordId,
                    sourceField = item.SourceField,
                    bibId = item.BibId
                }),
                action = "inserted"
            });
        }
        importedCounts["title_request_events"] = eventRows.Count;
        importedCounts["placed_bib_protection_markers"] = markerCount;
    }

    private static IReadOnlyList<PlacementBibSource> ReadEventBibSources(SourceRow row, string sourceId)
    {
        var candidates = new List<PlacementBibSource>();
        var directField = row.Names.FirstOrDefault(name =>
            string.Equals(name, "bibId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "bibid", StringComparison.OrdinalIgnoreCase));
        if (directField is not null && row.String(directField) is { } directBibId)
        {
            candidates.Add(new("title_request_events", sourceId, directField, directBibId));
        }

        foreach (var propertyName in new[] { "bibId", "bibid", "BibId" })
        {
            if (row.JsonPropertyString("metadata", propertyName) is { } metadataBibId)
            {
                candidates.Add(new("title_request_events", sourceId, $"metadata.{propertyName}", metadataBibId));
            }
        }

        if (candidates.Select(item => item.BibId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new MigrationOperationException(
                "placed_bib_conflict",
                $"Title request event {sourceId} has conflicting direct and metadata BIB values.");
        }
        return candidates.Distinct().ToArray();
    }

    private static void ImportHistoricalEmailDeliveryEvents(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> templateIds,
        IDictionary<string, int> importedCounts)
    {
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var status = row.RequiredString("status").ToLowerInvariant();
            var eventType = status is "sent" or "skipped" or "failed" ? status : "legacy";
            var sourceRequestId = row.String("titleRequest");
            var sourceTemplateId = row.String("emailTemplate");
            var mappedRequestId = ResolveRequiredMapping(
                sourceRequestId,
                requestIds,
                "email_delivery_request_reference_invalid",
                "email-delivery title request");
            var mappedTemplateId = ResolveRequiredMapping(
                sourceTemplateId,
                templateIds,
                "email_delivery_template_reference_invalid",
                "email-delivery template");
            var metadata = BuildDeliveryMetadata(
                row,
                sourceId,
                sourceRequestId,
                mappedRequestId,
                sourceTemplateId,
                mappedTemplateId,
                status);
            using var command = new SqlCommand(
                """
                INSERT INTO [asap].[EmailDeliveryEvent]
                    ([EmailOutboxId], [ProviderMessageId], [ProviderEventId], [EventType], [ReceivedUtc], [MetadataJson])
                OUTPUT inserted.[Id]
                VALUES (NULL, NULL, NULL, @eventType, @receivedUtc, @metadata);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@eventType", eventType);
            command.Parameters.AddWithValue("@receivedUtc", row.UtcDateTime("created") ?? throw new MigrationOperationException("email_delivery_created_missing", $"Email delivery event {sourceId} has no creation timestamp."));
            command.Parameters.AddWithValue("@metadata", metadata);
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            InsertMapping(connection, transaction, "email_delivery_event", sourceId, targetId);
        }
        importedCounts["email_delivery_events"] = rows.Count;
    }

    private static void VerifyUsableSuperAdministrator(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        using var command = new SqlCommand(
            """
            SELECT [UserPrincipalName], [NormalizedUserPrincipalName]
            FROM [asap].[StaffUser]
            WHERE [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;
            """,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        var found = false;
        while (reader.Read())
        {
            var email = reader.IsDBNull(0) ? null : RealEmail(reader.GetString(0));
            found |= email is not null && !reader.IsDBNull(1) &&
                string.Equals(email.ToUpperInvariant(), reader.GetString(1), StringComparison.Ordinal);
        }
        if (!found)
        {
            throw new MigrationOperationException("usable_super_admin_missing", "Import produced no active system super-admin with a valid authentication email.");
        }
    }

    private static long? EnsureUsableSuperAdministrator(
        SqlConnection connection,
        SqlTransaction transaction,
        string? externalConfigurationPath,
        IReadOnlySet<Guid> allowedTenantIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        importedCounts["migration_bootstrap_staff_users"] = 0;
        if (HasUsableSuperAdministrator(connection, transaction, allowedTenantIds)) return null;
        if (externalConfigurationPath is null)
        {
            throw new MigrationOperationException(
                "usable_super_admin_missing",
                "Import produced no active email-authenticated system super-admin and no target bootstrap configuration was supplied.");
        }

        var bootstrap = ReadBootstrapIdentity(externalConfigurationPath);
        long? existingId;
        using (var find = new SqlCommand(
                   "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @normalizedUpn;",
                   connection,
                   transaction))
        {
            find.Parameters.AddWithValue("@normalizedUpn", bootstrap.UserPrincipalName.ToUpperInvariant());
            existingId = find.ExecuteScalar() is { } value ? Convert.ToInt64(value) : null;
        }

        var action = existingId is null ? "inserted" : "promoted_existing";
        long targetId;
        if (existingId is not null)
        {
            using var update = new SqlCommand(
                """
                UPDATE [asap].[StaffUser]
                SET [UserPrincipalName] = @upn,
                    [NormalizedUserPrincipalName] = @normalizedUpn,
                    [Role] = N'super_admin',
                    [OrganizationId] = 1,
                    [IsActive] = 1
                WHERE [Id] = @id;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("@id", existingId.Value);
            AddBootstrapParameters(update, bootstrap);
            update.ExecuteNonQuery();
            targetId = existingId.Value;
        }
        else
        {
            using var insert = new SqlCommand(
                """
                INSERT INTO [asap].[StaffUser]
                    ([UserPrincipalName], [NormalizedUserPrincipalName], [DisplayName], [NotificationEmail],
                     [Role], [OrganizationId], [IsActive],
                     [WeeklyActionSummaryEnabled], [PurchaseReminderDefault],
                     [AdditionalCopyReminderDefault], [DefaultMineUnclaimedFilter])
                OUTPUT inserted.[Id]
                VALUES
                    (@upn, @normalizedUpn, @displayName, @notificationEmail,
                     N'super_admin', 1, 1,
                     0, 0, 0, 0);
                """,
                connection,
                transaction);
            AddBootstrapParameters(insert, bootstrap);
            targetId = Convert.ToInt64(insert.ExecuteScalar());
            importedCounts["migration_bootstrap_staff_users"] = 1;
        }

        transformations.Add(new
        {
            entity = "migration_bootstrap_super_admin",
            action,
            targetStaffUserId = targetId,
            authenticationEmail = bootstrap.UserPrincipalName,
            appliedAtUtc = exportedAtUtc
        });
        VerifyUsableSuperAdministrator(connection, transaction, allowedTenantIds);
        return targetId;
    }

    private static bool HasUsableSuperAdministrator(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        using var command = new SqlCommand(
            """
            SELECT [UserPrincipalName], [NormalizedUserPrincipalName]
            FROM [asap].[StaffUser]
            WHERE [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;
            """,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var email = reader.IsDBNull(0) ? null : RealEmail(reader.GetString(0));
            if (email is not null && !reader.IsDBNull(1) &&
                string.Equals(email.ToUpperInvariant(), reader.GetString(1), StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static BootstrapIdentity ReadBootstrapIdentity(string externalConfigurationPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(externalConfigurationPath));
            var authentication = JsonProperty(document.RootElement, "Authentication");
            var entra = JsonProperty(authentication, "Entra");
            var value = JsonProperty(entra, "InitialSuperAdmin");
            var upn = RealEmail(JsonProperty(value, "UserPrincipalName").GetString());
            var notificationEmail = RealEmail(JsonOptionalString(value, "NotificationEmail")) ?? upn;
            var displayName = Clean(JsonOptionalString(value, "DisplayName"));
            if (upn is null)
            {
                throw new MigrationOperationException(
                    "bootstrap_identity_invalid",
                    "The configured migration bootstrap identity requires a valid real email address.");
            }
            return new BootstrapIdentity(upn, displayName, notificationEmail!);
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        {
            throw new MigrationOperationException(
                "bootstrap_identity_invalid",
                "The configured migration bootstrap identity has an invalid shape.");
        }
    }

    private static JsonElement JsonProperty(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        throw new KeyNotFoundException(name);
    }

    private static string? JsonOptionalString(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }
        return null;
    }

    private static void AddBootstrapParameters(SqlCommand command, BootstrapIdentity bootstrap)
    {
        command.Parameters.AddWithValue("@upn", bootstrap.UserPrincipalName);
        command.Parameters.AddWithValue("@normalizedUpn", bootstrap.UserPrincipalName.ToUpperInvariant());
        command.Parameters.AddWithValue("@displayName", (object?)bootstrap.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@notificationEmail", bootstrap.NotificationEmail);
    }

    private static MigrationSemanticReconciliation ReconcileImportedSourceState(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> organizations,
        IReadOnlyList<SourceRow> staffUsers,
        IReadOnlyDictionary<string, string?> authenticationEmails,
        IReadOnlyList<SourceRow> titleRequests,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> tagIds,
        IReadOnlyDictionary<string, long> templateIds,
        IReadOnlyList<SourceRow> additionalCopies,
        IReadOnlyList<SourceRow> deletedAuditRows,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> claimRuleIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        IReadOnlyDictionary<string, string> requestCloseReasons,
        long? bootstrapMutatedStaffUserId,
        MigrationConfigurationReconciliation configurationReconciliation,
        IReadOnlyList<PlacementTransformation> placementTransformations)
    {
        foreach (var row in organizations)
        {
            var organizationId = row.Int32("organizationId")
                ?? throw new MigrationOperationException("reconciliation_failed", "An imported organization has no target identity.");
            using var command = new SqlCommand(
                "SELECT [DisplayName], [Abbreviation], [IsActive], [LastSyncedUtc] FROM [asap].[Organization] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", organizationId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "organization");
            EnsureSemantic(
                StringEquals(reader, 0, row.String("displayName") ?? row.String("name") ?? $"Organization {organizationId}") &&
                StringEquals(reader, 1, row.String("abbreviation")) &&
                reader.GetBoolean(2) == row.Bool("enabledForPatrons") &&
                DateEquals(reader, 3, row.UtcDateTime("lastSynced")),
                "organization");
        }

        foreach (var row in staffUsers)
        {
            var sourceId = row.RequiredString("id");
            using var command = new SqlCommand(
                """
                SELECT s.[Id], s.[EntraTenantId], s.[EntraObjectId], s.[UserPrincipalName], s.[DisplayName],
                       s.[NotificationEmail], s.[Role], s.[OrganizationId], s.[IsActive],
                       s.[WeeklyActionSummaryEnabled], s.[WeeklyActionSummaryEmail], s.[PurchaseReminderDefault],
                       s.[AdditionalCopyReminderDefault], s.[DefaultMineUnclaimedFilter], s.[LastLoginUtc]
                FROM [asap].[LegacyPocketBaseMapping] m
                JOIN [asap].[StaffUser] s ON s.[Id] = m.[NewId]
                WHERE m.[EntityType] = N'staff_user' AND m.[PocketBaseId] = @sourceId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "staff user");
            var targetId = reader.GetInt64(0);
            EnsureSemantic(
                reader.IsDBNull(1) &&
                reader.IsDBNull(2) &&
                reader.GetBoolean(9) == row.Bool("weekly_action_summary_enabled") &&
                StringEquals(reader, 10, RealEmail(row.String("weekly_action_summary_email"))) &&
                reader.GetBoolean(11) == row.Bool("purchase_reminder_default") &&
                reader.GetBoolean(12) == row.Bool("additional_copy_reminder_default") &&
                reader.GetBoolean(13) == row.Bool("default_mine_unclaimed_filter") &&
                DateEquals(reader, 14, row.UtcDateTime("lastLogin")),
                "staff user preferences");

            if (bootstrapMutatedStaffUserId != targetId)
            {
                var role = row.RequiredString("role").ToLowerInvariant();
                var organizationId = role == "super_admin" ? 1 : row.Int32("libraryOrgId")!.Value;
                var sourceEmail = authenticationEmails[sourceId];
                var weeklyEmail = RealEmail(row.String("weekly_action_summary_email"));
                var notificationEmail = sourceEmail ?? weeklyEmail;
                var userPrincipalName = sourceEmail;
                var displayName = row.String("displayName") ?? row.String("username");
                EnsureSemantic(
                    StringEquals(reader, 3, userPrincipalName) &&
                    StringEquals(reader, 4, displayName) &&
                    StringEquals(reader, 5, notificationEmail) &&
                    reader.GetString(6) == role &&
                    reader.GetInt32(7) == organizationId &&
                    reader.GetBoolean(8) == row.Bool("active"),
                    "staff user identity and authorization");
            }
        }

        foreach (var row in titleRequests)
        {
            var sourceId = row.RequiredString("id");
            var isbnStatus = NormalizeIsbnStatus(
                sourceId,
                row.String("isbnCheckStatus"),
                row.String("identifier"),
                row.String("bibid"));
            var retryCount = isbnStatus == "skipped_no_isbn" ? 0 : row.Int32("isbnCheckRetryCount") ?? 0;
            var expectedFormatId = ResolveRequestFormatId(connection, transaction, row, formatIds);
            var expectedClaim = ResolveRequestClaim(
                connection,
                transaction,
                row,
                ResolveRequestStatus(row, requestStatuses),
                expectedFormatId,
                staffIds,
                claimRuleIds,
                allowedTenantIds);
            using var command = new SqlCommand(
                """
                SELECT r.[LibraryOrganizationId], r.[PatronOrganizationId], r.[StaffLibraryOrganizationIdCreatedBy],
                       r.[Barcode], r.[Email], r.[NameFirst], r.[NameLast], r.[PatronCodeId], r.[PatronCodeDescription],
                       r.[PreferredPickupBranchId], r.[PreferredPickupBranchName], r.[LibraryNameSnapshot],
                       r.[Title], r.[Author], r.[Identifier], r.[Publication], r.[ExactPublicationDate],
                       r.[CustomFieldsJson], r.[AutoHold], r.[MaterialFormatId], r.[Status], r.[CloseReason], r.[BibId],
                       r.[LastPromoterCheckUtc], r.[IsbnCheckStatus], r.[IsbnCheckResult], r.[IsbnCheckRetryCount],
                       r.[IsbnCheckLastErrorCode], r.[LastCheckedUtc], r.[CreatedUtc], r.[UpdatedUtc],
                       r.[ClaimedByStaffUserId], r.[ClaimedByDisplayName], r.[ClaimedAtUtc], r.[ClaimType], r.[ClaimRuleId],
                       r.[LegacyId], r.[Notes]
                FROM [asap].[LegacyPocketBaseMapping] m
                JOIN [asap].[TitleRequest] r ON r.[Id] = m.[NewId]
                WHERE m.[EntityType] = N'title_request' AND m.[PocketBaseId] = @sourceId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "title request");
            EnsureSemantic(
                reader.GetInt32(0) == row.Int32("libraryOrgId") &&
                IntEquals(reader, 1, row.Int32("patronOrgId")) &&
                IntEquals(reader, 2, row.Int32("staffLibraryOrgIdCreatedBy")) &&
                StringEquals(reader, 3, row.RequiredString("barcode")) &&
                StringEquals(reader, 4, row.String("email")) &&
                StringEquals(reader, 5, row.Text("nameFirst")) &&
                StringEquals(reader, 6, row.Text("nameLast")) &&
                StringEquals(reader, 7, row.String("patronCodeId")) &&
                StringEquals(reader, 8, row.Text("patronCodeDescription")) &&
                IntEquals(reader, 9, row.Int32("preferredPickupBranchId")) &&
                StringEquals(reader, 10, row.Text("preferredPickupBranchName")) &&
                StringEquals(reader, 11, row.Text("libraryOrgName")) &&
                StringEquals(reader, 12, row.RequiredText("title")) &&
                StringEquals(reader, 13, row.Text("author")) &&
                StringEquals(reader, 14, row.String("identifier")) &&
                StringEquals(reader, 15, row.Text("publication")) &&
                DateEquals(reader, 16, ParseDate(row.String("exactPublicationDate"), "request_publication_date_invalid")) &&
                JsonEquals(reader, 17, row.JsonText("customFields")) &&
                reader.GetBoolean(18) == row.Bool("autohold") &&
                reader.GetInt64(19) == expectedFormatId &&
                StringEquals(reader, 20, ResolveRequestStatus(row, requestStatuses)) &&
                StringEquals(reader, 21, ResolveRequestCloseReason(row, requestCloseReasons)) &&
                StringEquals(reader, 22, row.String("bibid")) &&
                DateEquals(reader, 23, row.UtcDateTime("lastPromoterCheck")) &&
                StringEquals(reader, 24, isbnStatus) &&
                StringEquals(reader, 25, row.Text("isbnCheckResult")) &&
                reader.GetInt32(26) == retryCount &&
                StringEquals(reader, 27, isbnStatus == "error_max_retries" ? "legacy_retry_exhausted" : null) &&
                DateEquals(reader, 28, row.UtcDateTime("lastChecked")) &&
                DateEquals(reader, 29, row.UtcDateTime("created")) &&
                DateEquals(reader, 30, row.UtcDateTime("updated")) &&
                LongEquals(reader, 31, expectedClaim.StaffUserId) &&
                StringEquals(reader, 32, expectedClaim.DisplayName) &&
                 DateEquals(reader, 33, expectedClaim.ClaimedAtUtc) &&
                 StringEquals(reader, 34, expectedClaim.ClaimType) &&
                 LongEquals(reader, 35, expectedClaim.ClaimRuleId) &&
                 StringEquals(reader, 36, row.String("legacyId")) &&
                 StringEquals(reader, 37, row.Text("notes")),
                "title request");
        }

        foreach (var row in additionalCopies)
        {
            var sourceId = row.RequiredString("id");
            var status = NormalizeAdditionalCopyStatus(row.RequiredString("status"));
            var expectedClaim = ResolveAdditionalCopyClaim(
                connection,
                transaction,
                row,
                status,
                staffIds,
                allowedTenantIds);
            var expectedSourceId = ResolveRequiredMapping(
                row.String("sourceTitleRequest"),
                requestIds,
                "additional_copy_request_reference_invalid",
                "additional-copy source title request");
            var expectedFormatId = ResolveAdditionalCopyFormatId(connection, transaction, row);
            var createdUtc = row.UtcDateTime("created") ?? throw new MigrationOperationException(
                "reconciliation_failed",
                $"Additional-copy request {sourceId} has no creation timestamp.");
            using var command = new SqlCommand(
                """
                SELECT r.[SourceTitleRequestId], r.[LibraryOrganizationId], r.[LibraryNameSnapshot],
                       r.[BibId], r.[Title], r.[Author], r.[Identifier], r.[Publication],
                       r.[MaterialFormatId], r.[FormatSnapshot], r.[Status], r.[Notes],
                       r.[CreatedByStaffUserId], r.[CreatedByDisplayName], r.[CreatedUtc], r.[UpdatedUtc],
                       r.[ClaimedByStaffUserId], r.[ClaimedByDisplayName], r.[ClaimedAtUtc], r.[ClaimType], r.[ClaimRuleId],
                       r.[ClosedByStaffUserId], r.[ClosedByDisplayName], r.[ClosedUtc], r.[LegacyId]
                FROM [asap].[LegacyPocketBaseMapping] m
                JOIN [asap].[AdditionalCopyRequest] r ON r.[Id] = m.[NewId]
                WHERE m.[EntityType] = N'additional_copy' AND m.[PocketBaseId] = @sourceId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "additional-copy request");
            EnsureSemantic(
                LongEquals(reader, 0, expectedSourceId) &&
                reader.GetInt32(1) == row.Int32("libraryOrgId") &&
                StringEquals(reader, 2, row.Text("libraryOrgName")) &&
                StringEquals(reader, 3, row.RequiredString("bibid")) &&
                StringEquals(reader, 4, row.RequiredText("title")) &&
                StringEquals(reader, 5, row.Text("author")) &&
                StringEquals(reader, 6, row.String("identifier")) &&
                StringEquals(reader, 7, row.Text("publication")) &&
                LongEquals(reader, 8, expectedFormatId) &&
                StringEquals(reader, 9, row.String("format")) &&
                StringEquals(reader, 10, status) &&
                StringEquals(reader, 11, AdditionalCopyNotes(row, expectedClaim, package.Manifest.ExportedAtUtc.UtcDateTime)) &&
                LongEquals(reader, 12, ResolveRequiredMapping(
                    row.String("createdByStaff"),
                    staffIds,
                    "additional_copy_creator_reference_invalid",
                    "additional-copy creator staff")) &&
                StringEquals(reader, 13, row.String("createdByUsername")) &&
                DateEquals(reader, 14, createdUtc) &&
                DateEquals(reader, 15, row.UtcDateTime("updated") ?? createdUtc) &&
                LongEquals(reader, 16, expectedClaim.StaffUserId) &&
                StringEquals(reader, 17, expectedClaim.DisplayName) &&
                DateEquals(reader, 18, expectedClaim.ClaimedAtUtc) &&
                StringEquals(reader, 19, null) &&
                LongEquals(reader, 20, null) &&
                 LongEquals(reader, 21, ResolveRequiredMapping(
                    row.String("closedByStaff"),
                    staffIds,
                    "additional_copy_closer_reference_invalid",
                    "additional-copy closer staff")) &&
                 StringEquals(reader, 22, row.String("closedByUsername")) &&
                 DateEquals(reader, 23, row.UtcDateTime("closedAt")) &&
                 StringEquals(reader, 24, row.String("legacyId")),
                "additional-copy request");
        }

        foreach (var row in deletedAuditRows)
        {
            var sourceId = row.RequiredString("id");
            var barcode = row.String("barcode");
            var expectedStatus = NormalizeStatus(row.RequiredString("status"));
            var expectedCloseReason = NormalizeCloseReason(row.String("closeReason"));
            using var command = new SqlCommand(
                """
                SELECT a.[RequestType], a.[OriginalRequestKey], a.[LibraryOrganizationId], a.[Title], a.[Author],
                       a.[Identifier], a.[BibId], a.[Status], a.[CloseReason], a.[MaskedBarcode], a.[CreatedUtc],
                       a.[DeletedUtc], a.[DeletedByStaffUserId], a.[DeletedByDisplayName]
                FROM [asap].[LegacyPocketBaseMapping] m
                JOIN [asap].[DeletedRequestAudit] a ON a.[Id] = m.[NewId]
                WHERE m.[EntityType] = N'deleted_request_audit' AND m.[PocketBaseId] = @sourceId;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "deleted-request audit");
            EnsureSemantic(
                StringEquals(reader, 0, barcode is null ? "additional_copy" : "title_request") &&
                StringEquals(reader, 1, row.RequiredString("titleRequestId")) &&
                reader.GetInt32(2) == row.Int32("libraryOrgId") &&
                StringEquals(reader, 3, row.Text("title")) &&
                StringEquals(reader, 4, row.Text("author")) &&
                StringEquals(reader, 5, row.String("identifier")) &&
                StringEquals(reader, 6, row.String("bibid")) &&
                 StringEquals(reader, 7, expectedStatus) &&
                 StringEquals(reader, 8, expectedCloseReason) &&
                StringEquals(reader, 9, MaskBarcode(barcode)) &&
                DateEquals(reader, 10, ParseUtcText(row.JsonPropertyString("snapshot", "created"), "deleted_request_created_invalid")) &&
                DateEquals(reader, 11, row.UtcDateTime("deletedAt")) &&
                LongEquals(reader, 12, ResolveRequiredMapping(
                    row.String("deletedByStaff"),
                    staffIds,
                    "deleted_request_actor_reference_invalid",
                    "deleted-request actor staff")) &&
                StringEquals(reader, 13, row.String("deletedByUsername")),
                "deleted-request audit");
        }

        var brandingRows = MigrationPackageReader.ReadRows(package, "branding.json", "branding");
        foreach (var row in brandingRows)
        {
            var organizationId = row.RequiredString("scope") == "system"
                ? 1
                : ResolveOrganizationId(row, "libraryOrganization", organizationIds);
            using var command = new SqlCommand(
                "SELECT [LogoData], [LogoContentType], [LogoFileName], [LogoAltText] FROM [asap].[Branding] WHERE [OrganizationId] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", organizationId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "branding asset");
            var targetBytes = reader.GetFieldValue<byte[]>(0);
            var targetHash = Convert.ToHexStringLower(SHA256.HashData(targetBytes));
            EnsureSemantic(
                targetBytes.LongLength == row.Int32("length") &&
                string.Equals(targetHash, row.RequiredString("sha256"), StringComparison.OrdinalIgnoreCase) &&
                StringEquals(reader, 1, row.RequiredString("contentType")) &&
                StringEquals(reader, 2, row.RequiredString("fileName")) &&
                (row.Text("logoAlt") is null || StringEquals(reader, 3, row.Text("logoAlt"))),
                "branding asset");
        }

        var historyReconciliation = ReconcileImportedHistory(
            connection,
            transaction,
            package,
            titleRequests,
            requestIds,
            tagIds,
            templateIds,
            formatIds,
            organizationIds,
            claimRuleIds,
            staffIds,
            allowedTenantIds,
            requestStatuses,
            requestCloseReasons,
            placementTransformations);

        return new(
            organizations.Count,
            staffUsers.Count,
            titleRequests.Count,
            additionalCopies.Count,
            deletedAuditRows.Count,
            brandingRows.Count,
            configurationReconciliation.RowsChecked,
            configurationReconciliation.FieldsChecked,
            configurationReconciliation.RelationshipsChecked,
            historyReconciliation.WorkflowTagsChecked,
            historyReconciliation.TitleRequestTagsChecked,
            historyReconciliation.HistoryRowsChecked,
            true);
    }

    private static MigrationHistoryReconciliation ReconcileImportedHistory(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> titleRequests,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> tagIds,
        IReadOnlyDictionary<string, long> templateIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> claimRuleIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        IReadOnlyDictionary<string, string> requestCloseReasons,
        IReadOnlyList<PlacementTransformation> placementTransformations)
    {
        var workflowTagRows = MigrationPackageReader.ReadRows(package, "workflow-tags.json", "workflow_tags");
        var titleRequestTagRows = MigrationPackageReader.ReadRows(package, "title-request-tags.json", "title_request_tags");
        var autoClaimRows = MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules");
        var eventRows = MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events");
        var deliveryRows = MigrationPackageReader.ReadRows(package, "email-delivery-events.json", "email_delivery_events");
        ReconcileWorkflowTags(connection, transaction, workflowTagRows);
        ReconcileTitleRequestTags(connection, transaction, titleRequests, titleRequestTagRows, requestIds, tagIds);
        ReconcileAutoClaimRules(
            connection,
            transaction,
            autoClaimRows,
            claimRuleIds,
            formatIds,
            organizationIds,
            staffIds,
            allowedTenantIds,
            package.Manifest.ExportedAtUtc.UtcDateTime);
        ReconcileTitleRequestEvents(connection, transaction, eventRows, requestIds, requestStatuses, requestCloseReasons);
        ReconcileEmailDeliveryEvents(connection, transaction, deliveryRows, requestIds, templateIds);
        ReconcileClaimAnnotations(
            connection,
            transaction,
            titleRequests,
            requestIds,
            formatIds,
            staffIds,
            claimRuleIds,
            allowedTenantIds,
            requestStatuses,
            package.Manifest.ExportedAtUtc.UtcDateTime);
        ReconcilePlacementMarkers(connection, transaction, placementTransformations, requestIds, package.Manifest.ExportedAtUtc.UtcDateTime);
        return new(
            workflowTagRows.Count,
            titleRequestTagRows.Count,
            autoClaimRows.Count + eventRows.Count + deliveryRows.Count + placementTransformations.Count);
    }

    private static void ReconcileWorkflowTags(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows)
    {
        var ordinal = 0;
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var code = NormalizeWorkflowTagCode(row.RequiredString("code"));
            using var command = new SqlCommand(
                "SELECT t.[Code], t.[Label], t.[SortOrder] FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[WorkflowTag] t ON t.[Id] = m.[NewId] WHERE m.[EntityType] = N'workflow_tag' AND m.[PocketBaseId] = @sourceId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "workflow tag");
            EnsureSemantic(
                StringEquals(reader, 0, code) &&
                StringEquals(reader, 1, row.String("label") ?? code) &&
                reader.GetInt32(2) == (row.Int32("sortOrder") ?? 1000 + ordinal),
                "workflow tag");
            ordinal++;
        }
    }

    private static void ReconcileTitleRequestTags(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> titleRequests,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> tagIds)
    {
        var expected = new HashSet<(long RequestId, long TagId)>();
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            if (!requestIds.TryGetValue(row.RequiredString("titleRequest"), out var requestId) ||
                !tagIds.TryGetValue(row.RequiredString("tag"), out var tagId) ||
                !expected.Add((requestId, tagId)))
            {
                throw new MigrationOperationException(
                    "reconciliation_failed",
                    $"Title request tag {sourceId} has no unique target relationship.");
            }
            using var command = new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] = @requestId AND [WorkflowTagId] = @tagId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestId", requestId);
            command.Parameters.AddWithValue("@tagId", tagId);
            EnsureSemantic(Convert.ToInt32(command.ExecuteScalar()) == 1, "title request tag relationship");
        }

        foreach (var row in titleRequests)
        {
            var sourceId = row.RequiredString("id");
            var status = NormalizeIsbnStatus(sourceId, row.String("isbnCheckStatus"), row.String("identifier"), row.String("bibid"));
            if (status != "found" || !requestIds.TryGetValue(sourceId, out var requestId)) continue;
            var foundTagId = FindTagId(connection, transaction, "polaris_bib_found");
            if (foundTagId is null)
            {
                throw new MigrationOperationException("reconciliation_failed", "The canonical identifier-found workflow tag is missing.");
            }
            expected.Add((requestId, foundTagId.Value));
            using var command = new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] = @requestId AND [WorkflowTagId] = @tagId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestId", requestId);
            command.Parameters.AddWithValue("@tagId", foundTagId.Value);
            EnsureSemantic(Convert.ToInt32(command.ExecuteScalar()) == 1, "canonical identifier-found tag relationship");
        }

        using var count = new SqlCommand("SELECT COUNT(*) FROM [asap].[TitleRequestWorkflowTag];", connection, transaction);
        EnsureSemantic(Convert.ToInt32(count.ExecuteScalar()) == expected.Count, "title request tag relationship count");
    }

    private static void ReconcileAutoClaimRules(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> claimRuleIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlySet<Guid> allowedTenantIds,
        DateTime exportedAtUtc)
    {
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            if (!claimRuleIds.TryGetValue(sourceId, out var targetId))
            {
                throw new MigrationOperationException("reconciliation_failed", "An imported auto-claim rule has no target identity.");
            }
            var libraryId = row.Int32("libraryOrgId") ?? throw new MigrationOperationException("reconciliation_failed", "An auto-claim rule has no library.");
            if (row.HasValue("libraryOrganization") && ResolveOrganizationId(row, "libraryOrganization", organizationIds) != libraryId)
            {
                throw new MigrationOperationException(
                    "claim_rule_organization_conflict",
                    $"Format claim rule {sourceId} has conflicting library organization references.");
            }
            var formatCode = NormalizeFormatCode(row.RequiredString("format"));
            var formatId = FindFormatId(connection, transaction, libraryId, formatCode) ??
                FindFormatId(connection, transaction, 1, formatCode) ??
                throw new MigrationOperationException("reconciliation_failed", "An auto-claim rule has no target material format.");
            var sourceStaffId = ReadConsistentReference(
                row,
                "staffUserId",
                "staffUser",
                "claim_rule_staff_reference_conflict");
            var staffId = ResolveOptionalMapping(sourceStaffId, staffIds);
            var requestedActive = row.Bool("active");
            var eligible = staffId is not null && IsStaffEligibleForLibrary(connection, transaction, staffId.Value, libraryId, allowedTenantIds);
            var active = requestedActive && eligible;
            DateTime? deactivatedUtc = active ? null : row.UtcDateTime("updated") ?? exportedAtUtc;
            using var command = new SqlCommand(
                "SELECT [LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc], [DeactivatedUtc] FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[FormatAutoClaimRule] r ON r.[Id] = m.[NewId] WHERE m.[EntityType] = N'format_auto_claim_rule' AND m.[PocketBaseId] = @sourceId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "format auto-claim rule");
            EnsureSemantic(
                reader.GetInt32(0) == libraryId &&
                reader.GetInt64(1) == formatId &&
                LongEquals(reader, 2, staffId) &&
                reader.GetBoolean(3) == active &&
                DateEquals(reader, 4, row.UtcDateTime("created") ?? exportedAtUtc) &&
                DateEquals(reader, 5, deactivatedUtc),
                "format auto-claim rule");
        }
    }

    private static void ReconcileTitleRequestEvents(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        IReadOnlyDictionary<string, string> requestCloseReasons)
    {
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var sourceRequestId = row.RequiredString("titleRequest");
            if (!requestIds.TryGetValue(sourceRequestId, out var requestId))
            {
                throw new MigrationOperationException("reconciliation_failed", "A title request event has no target request relationship.");
            }
            var sourceEventType = row.RequiredString("eventType");
            var normalizedEventType = sourceEventType.Trim().ToLowerInvariant();
            var eventType = KnownEventTypes.Contains(normalizedEventType) ? normalizedEventType : "legacy";
            var fromStatus = ResolveEventStatus(row.String("fromStatus"), requestStatuses);
            var toStatus = ResolveEventStatus(row.String("toStatus"), requestStatuses);
            var closeReason = ResolveEventCloseReason(row.String("closeReason"), requestCloseReasons);
            var actorType = row.RequiredString("actorType").Trim().ToLowerInvariant();
            var createdUtc = row.UtcDateTime("created") ?? throw new MigrationOperationException("reconciliation_failed", "A title request event has no creation timestamp.");
            var metadata = BuildImportedEventMetadata(row, sourceId, sourceEventType);
            using var command = new SqlCommand(
                "SELECT e.[TitleRequestId], e.[EventType], e.[Status], e.[CloseReason], e.[ActorType], e.[StaffUserId], e.[ActorName], e.[Message], e.[MetadataJson], e.[CreatedUtc] FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[TitleRequestEvent] e ON e.[Id] = m.[NewId] WHERE m.[EntityType] = N'title_request_event' AND m.[PocketBaseId] = @sourceId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "title request event");
            EnsureSemantic(
                reader.GetInt64(0) == requestId &&
                StringEquals(reader, 1, eventType) &&
                StringEquals(reader, 2, toStatus) &&
                StringEquals(reader, 3, closeReason) &&
                StringEquals(reader, 4, actorType) &&
                reader.IsDBNull(5) &&
                StringEquals(reader, 6, row.Text("actorName")) &&
                StringEquals(reader, 7, row.Text("message")) &&
                JsonEquals(reader, 8, metadata) &&
                DateEquals(reader, 9, createdUtc),
                "title request event");
        }
    }

    private static void ReconcileEmailDeliveryEvents(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> templateIds)
    {
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var sourceRequestId = row.String("titleRequest");
            var sourceTemplateId = row.String("emailTemplate");
            var targetRequestId = ResolveRequiredMapping(sourceRequestId, requestIds, "reconciliation_failed", "email-delivery title request");
            var targetTemplateId = ResolveRequiredMapping(sourceTemplateId, templateIds, "reconciliation_failed", "email-delivery template");
            var status = row.RequiredString("status").Trim().ToLowerInvariant();
            var eventType = status is "sent" or "skipped" or "failed" ? status : "legacy";
            var receivedUtc = row.UtcDateTime("created") ?? throw new MigrationOperationException("reconciliation_failed", "An email delivery event has no creation timestamp.");
            var metadata = BuildDeliveryMetadata(row, sourceId, sourceRequestId, targetRequestId, sourceTemplateId, targetTemplateId, status);
            using var command = new SqlCommand(
                "SELECT e.[EmailOutboxId], e.[ProviderMessageId], e.[ProviderEventId], e.[EventType], e.[ReceivedUtc], e.[MetadataJson] FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[EmailDeliveryEvent] e ON e.[Id] = m.[NewId] WHERE m.[EntityType] = N'email_delivery_event' AND m.[PocketBaseId] = @sourceId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "email delivery event");
            EnsureSemantic(
                reader.IsDBNull(0) && reader.IsDBNull(1) && reader.IsDBNull(2) &&
                StringEquals(reader, 3, eventType) &&
                DateEquals(reader, 4, receivedUtc) &&
                JsonEquals(reader, 5, metadata),
                "email delivery event");
        }
    }

    private static void ReconcileClaimAnnotations(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> claimRuleIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        DateTime exportedAtUtc)
    {
        var expectedCount = 0;
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            if (!requestIds.TryGetValue(sourceId, out var requestId)) continue;
            var formatId = ResolveRequestFormatId(connection, transaction, row, formatIds);
            var claim = ResolveRequestClaim(
                connection,
                transaction,
                row,
                ResolveRequestStatus(row, requestStatuses),
                formatId,
                staffIds,
                claimRuleIds,
                allowedTenantIds);
            if (!claim.RequiresMigrationAnnotation) continue;
            expectedCount++;
            var metadata = BuildClaimAnnotationMetadata(row, sourceId, claim);
            using var command = new SqlCommand(
                "SELECT e.[MetadataJson], e.[CreatedUtc] FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = @requestId AND e.[EventType] = N'legacy' AND JSON_VALUE(e.[MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';",
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestId", requestId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "claim migration annotation");
            EnsureSemantic(JsonEquals(reader, 0, metadata) && DateEquals(reader, 1, exportedAtUtc), "claim migration annotation");
            EnsureSemantic(!reader.Read(), "claim migration annotation count");
        }
        using var count = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';",
            connection,
            transaction);
        EnsureSemantic(Convert.ToInt32(count.ExecuteScalar()) == expectedCount, "claim migration annotation count");
    }

    private static void ReconcilePlacementMarkers(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<PlacementTransformation> transformations,
        IReadOnlyDictionary<string, long> requestIds,
        DateTime exportedAtUtc)
    {
        foreach (var item in transformations.Where(item => item.Action == "inserted"))
        {
            if (!requestIds.TryGetValue(item.SourceId, out var requestId))
            {
                throw new MigrationOperationException("reconciliation_failed", "A placement marker has no target request.");
            }
            var metadata = BuildPlacementMarkerMetadata(item);
            using var command = new SqlCommand(
                "SELECT [EventType], [Status], [CloseReason], [ActorType], [ActorName], [Message], [MetadataJson], [CreatedUtc] FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId AND [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';",
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestId", requestId);
            using var reader = command.ExecuteReader();
            EnsureSemantic(reader.Read(), "placed BIB protection marker");
            EnsureSemantic(
                StringEquals(reader, 0, "legacy") &&
                reader.IsDBNull(1) &&
                reader.IsDBNull(2) &&
                StringEquals(reader, 3, "system") &&
                StringEquals(reader, 4, "migration") &&
                StringEquals(reader, 5, "Legacy placed-state history protection.") &&
                JsonEquals(reader, 6, metadata) &&
                DateEquals(reader, 7, exportedAtUtc),
                "placed BIB protection marker");
            EnsureSemantic(!reader.Read(), "placed BIB protection marker count");
        }
        using var count = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';",
            connection,
            transaction);
        EnsureSemantic(
            Convert.ToInt32(count.ExecuteScalar()) == transformations.Count(item => item.Action == "inserted"),
            "placed BIB protection marker count");
    }

    private static string BuildImportedEventMetadata(
        SourceRow row,
        string sourceId,
        string sourceEventType) =>
        JsonSerializer.Serialize(new
        {
            sourceCollection = "title_request_events",
            sourceRecordId = sourceId,
            sourceEventType,
            sourceFromStatus = row.String("fromStatus"),
            sourceToStatus = row.String("toStatus"),
            sourceCloseReason = row.String("closeReason"),
            sourceMetadata = ParseJsonElement(row.JsonText("metadata"))
        });

    private static string BuildDeliveryMetadata(
        SourceRow row,
        string sourceId,
        string? sourceRequestId,
        long? targetRequestId,
        string? sourceTemplateId,
        long? targetTemplateId,
        string status) =>
        JsonSerializer.Serialize(new
        {
            sourceCollection = "email_delivery_events",
            sourceRecordId = sourceId,
            sourceTitleRequestId = sourceRequestId,
            targetTitleRequestId = targetRequestId,
            sourceEmailTemplateId = sourceTemplateId,
            targetEmailTemplateId = targetTemplateId,
            templateKey = row.String("templateKey"),
            recipient = row.String("recipient"),
            subject = row.Text("subject"),
            sourceStatus = status,
            error = row.Text("error"),
            sourceMetadata = ParseJsonElement(row.JsonText("metadata"))
        });

    private static string BuildClaimAnnotationMetadata(
        SourceRow row,
        string sourceRequestId,
        ImportedClaim claim) =>
        JsonSerializer.Serialize(new
        {
            transform = "claim_attribution_normalization_v1",
            sourceCollection = "title_requests",
            sourceRecordId = sourceRequestId,
            sourceClaimantId = claim.SourceClaimantId,
            mappedStaffUserId = claim.MappedStaffUserId,
            sourceDisplayName = row.String("claimedByDisplayName"),
            sourceClaimedAtUtc = row.UtcDateTime("claimedAt"),
            sourceClaimType = row.String("claimType"),
            sourceClaimRuleId = row.String("claimRuleId"),
            reason = claim.Reason
        });

    private static string BuildPlacementMarkerMetadata(PlacementTransformation item) =>
        JsonSerializer.Serialize(new
        {
            legacyBibProtection = true,
            bibId = item.BibId,
            transform = "placed_bib_protection_v1",
            sourceTitleRequestId = item.SourceId,
            evidence = item.Evidence.Select(evidence => new
            {
                kind = evidence.Kind,
                sourceCollection = evidence.SourceCollection,
                sourceRecordId = evidence.SourceRecordId,
                sourceField = evidence.SourceField,
                value = evidence.Value
            }),
            bibSources = item.BibSources.Select(source => new
            {
                sourceCollection = source.SourceCollection,
                sourceRecordId = source.SourceRecordId,
                sourceField = source.SourceField,
                bibId = source.BibId
            })
        });

    private static void EnsureSemantic(bool condition, string entity)
    {
        if (!condition)
        {
            throw new MigrationOperationException(
                "reconciliation_failed",
                $"Imported {entity} state does not match the immutable source package.");
        }
    }

    private static bool StringEquals(SqlDataReader reader, int ordinal, string? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static bool GuidEquals(SqlDataReader reader, int ordinal, Guid? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : reader.GetGuid(ordinal) == expected;

    private static bool IntEquals(SqlDataReader reader, int ordinal, int? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : reader.GetInt32(ordinal) == expected;

    private static bool LongEquals(SqlDataReader reader, int ordinal, long? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : reader.GetInt64(ordinal) == expected;

    private static bool DateEquals(SqlDataReader reader, int ordinal, DateTime? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : expected is not null && reader.GetDateTime(ordinal).Ticks == expected.Value.Ticks;

    private static bool JsonEquals(SqlDataReader reader, int ordinal, string? expected)
    {
        if (reader.IsDBNull(ordinal)) return expected is null;
        if (expected is null) return false;
        using var actualDocument = JsonDocument.Parse(reader.GetString(ordinal));
        using var expectedDocument = JsonDocument.Parse(expected);
        return JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement);
    }

    private static SortedDictionary<string, int> ReconcileTarget(
        string connectionString,
        IReadOnlyDictionary<string, int> importedCounts,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["organizations"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[Organization];"),
            ["staff_users"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[StaffUser];"),
            ["format_auto_claim_rules"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule];"),
            ["title_requests"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest];"),
            ["additional_copy_requests"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest];"),
            ["deleted_request_audit"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[DeletedRequestAudit];"),
            ["title_request_events"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent];"),
            ["claim_migration_annotations"] = Scalar(connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';"),
            ["placed_bib_protection_markers"] = Scalar(connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';"),
            ["additional_copy_claim_migration_annotations"] = Scalar(connection,
                "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] WHERE [Notes] LIKE N'%[[]ASAP migration:additional_copy_claim_v1]%';"),
            ["legacy_mappings"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping];"),
            ["patron_sessions"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[PatronSession];"),
            ["email_outbox"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[EmailOutbox];"),
            ["queue_progress"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[QueueProgress];"),
            ["hold_placement_operations"] = 0,
            ["invalid_active_claim_rules"] = ScalarWithAllowedTenants(connection,
                """
                SELECT COUNT(*)
                FROM [asap].[FormatAutoClaimRule] r
                LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[StaffUserId]
                WHERE r.[IsActive] = 1 AND
                      (s.[Id] IS NULL OR s.[IsActive] = 0 OR
                       NULLIF(LTRIM(RTRIM(s.[UserPrincipalName])), N'') IS NULL OR
                       s.[NormalizedUserPrincipalName] <> UPPER(LTRIM(RTRIM(s.[UserPrincipalName]))) OR
                       NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR
                            (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));
                """, allowedTenantIds),
            ["invalid_open_title_request_claims"] = ScalarWithAllowedTenants(connection,
                """
                SELECT COUNT(*)
                FROM [asap].[TitleRequest] r
                LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[ClaimedByStaffUserId]
                WHERE r.[Status] IN (N'suggestion', N'outstanding_purchase', N'pending_hold', N'hold_placed')
                  AND r.[ClaimedByStaffUserId] IS NOT NULL
                  AND (s.[Id] IS NULL OR s.[IsActive] = 0 OR
                       NULLIF(LTRIM(RTRIM(s.[UserPrincipalName])), N'') IS NULL OR
                       s.[NormalizedUserPrincipalName] <> UPPER(LTRIM(RTRIM(s.[UserPrincipalName]))) OR
                       NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR
                            (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));
                """, allowedTenantIds),
            ["invalid_open_additional_copy_claims"] = ScalarWithAllowedTenants(connection,
                """
                SELECT COUNT(*)
                FROM [asap].[AdditionalCopyRequest] r
                LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[ClaimedByStaffUserId]
                WHERE r.[Status] = N'open' AND r.[ClaimedByStaffUserId] IS NOT NULL
                  AND (s.[Id] IS NULL OR s.[IsActive] = 0 OR
                       NULLIF(LTRIM(RTRIM(s.[UserPrincipalName])), N'') IS NULL OR
                       s.[NormalizedUserPrincipalName] <> UPPER(LTRIM(RTRIM(s.[UserPrincipalName]))) OR
                       NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR
                            (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));
                """, allowedTenantIds),
            ["invalid_found_requests"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] = N'found' AND NULLIF(LTRIM(RTRIM([BibId])), N'') IS NULL;")
        };
        if (counts["staff_users"] != importedCounts["staff_users"] + importedCounts.GetValueOrDefault("migration_bootstrap_staff_users") ||
            counts["format_auto_claim_rules"] != importedCounts.GetValueOrDefault("format_claim_rules") ||
            counts["title_requests"] != importedCounts.GetValueOrDefault("title_requests") ||
            counts["additional_copy_requests"] != importedCounts.GetValueOrDefault("additional_copy_requests") ||
            counts["deleted_request_audit"] != importedCounts.GetValueOrDefault("deleted_request_audit") ||
            counts["title_request_events"] != importedCounts.GetValueOrDefault("title_request_events") + importedCounts.GetValueOrDefault("placed_bib_protection_markers") + importedCounts.GetValueOrDefault("claim_migration_annotations") ||
            counts["claim_migration_annotations"] != importedCounts.GetValueOrDefault("claim_migration_annotations") ||
            counts["placed_bib_protection_markers"] != importedCounts.GetValueOrDefault("placed_bib_protection_markers") ||
            counts["additional_copy_claim_migration_annotations"] != importedCounts.GetValueOrDefault("additional_copy_claim_migration_annotations") ||
            counts["patron_sessions"] != 0 || counts["email_outbox"] != 0 || counts["queue_progress"] != 0 ||
            counts["invalid_active_claim_rules"] != 0 || counts["invalid_open_title_request_claims"] != 0 ||
            counts["invalid_open_additional_copy_claims"] != 0 ||
            counts["invalid_found_requests"] != 0)
        {
            throw new MigrationOperationException("reconciliation_failed", "Target counts or excluded runtime state do not reconcile.");
        }
        return counts;
    }

    private static void WriteReport(
        string path,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> importedCounts,
        IReadOnlyDictionary<string, int> targetCounts,
        string targetFingerprint,
        string packageIdentity,
        IReadOnlyCollection<object> transformations,
        IReadOnlyCollection<ClaimTransformation> claimTransformations,
        IReadOnlyCollection<ClaimTransformation> additionalCopyClaimTransformations,
        IReadOnlyCollection<PlacementTransformation> placementTransformations,
        MigrationSemanticReconciliation semanticReconciliation)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            reportVersion = 4,
            sourceGitSha = package.Manifest.PocketBaseSourceGitSha,
            sourceSchemaVersion = package.Manifest.PocketBaseSourceSchemaVersion,
            exportedAtUtc = package.Manifest.ExportedAtUtc,
            packageIdentitySha256 = packageIdentity,
            importedCounts,
            targetCounts,
            targetFingerprintSha256 = targetFingerprint,
            transformations,
            claimReconciliation = new
            {
                titleRequests = claimTransformations
                    .GroupBy(item => new
                    {
                        item.LibraryOrganizationId,
                        item.Status,
                        Outcome = item.Status == "closed"
                            ? "closed_history"
                            : item.EffectiveStaffUserId is null ? "cleared" : "preserved",
                        item.Reason
                    })
                    .OrderBy(group => group.Key.LibraryOrganizationId)
                    .ThenBy(group => group.Key.Status, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Outcome, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        libraryOrganizationId = group.Key.LibraryOrganizationId,
                        status = group.Key.Status,
                        outcome = group.Key.Outcome,
                        reason = group.Key.Reason,
                        count = group.Count()
                    }),
                additionalCopies = additionalCopyClaimTransformations
                    .GroupBy(item => new
                    {
                        item.LibraryOrganizationId,
                        item.Status,
                        Outcome = item.Status == "closed"
                            ? "closed_history"
                            : item.EffectiveStaffUserId is null ? "cleared" : "preserved",
                        item.Reason
                    })
                    .OrderBy(group => group.Key.LibraryOrganizationId)
                    .ThenBy(group => group.Key.Status, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Outcome, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        libraryOrganizationId = group.Key.LibraryOrganizationId,
                        status = group.Key.Status,
                        outcome = group.Key.Outcome,
                        reason = group.Key.Reason,
                        count = group.Count()
                    })
            },
            placementReconciliation = new
            {
                sourceRequestsEvaluated = placementTransformations.Count,
                protectedRequests = placementTransformations.Count(item => item.Action == "inserted"),
                knownBibMarkers = placementTransformations.Count(item => item.Action == "inserted" && item.BibId is not null),
                explicitNullBibMarkers = placementTransformations.Count(item => item.Action == "inserted" && item.BibId is null),
                noPlacementEvidence = placementTransformations.Count(item => item.Action == "no_placement_evidence"),
                placementHistoryAmbiguous = placementTransformations.Count(item => item.Action == "placement_history_ambiguous"),
                insertedMarkers = placementTransformations.Count(item => item.Action == "inserted"),
                reusedMarkers = 0,
                fabricatedHoldPlacementOperations = 0,
                byLibraryAndStatus = placementTransformations
                    .GroupBy(item => new { item.LibraryOrganizationId, item.Status, item.Action })
                    .OrderBy(group => group.Key.LibraryOrganizationId)
                    .ThenBy(group => group.Key.Status, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Action, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        libraryOrganizationId = group.Key.LibraryOrganizationId,
                        status = group.Key.Status,
                        outcome = group.Key.Action,
                        count = group.Count()
                    }),
                evidenceClasses = PlacementEvidenceKinds.Select(kind => new
                {
                    kind,
                    count = placementTransformations
                        .SelectMany(item => item.Evidence)
                        .Count(item => item.Kind == kind)
                }),
                terminalReasons = HoldTerminalReasons
                    .OrderBy(reason => reason, StringComparer.Ordinal)
                    .Select(reason => new
                    {
                        reason,
                        count = placementTransformations
                            .SelectMany(item => item.Evidence)
                            .Count(item =>
                                (item.Kind is "terminal_close_reason" or "event_terminal_reason") &&
                                item.Value == reason)
                    })
            },
            sourceToTargetReconciliation = semanticReconciliation,
            reconciliationPassed = true
        }, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static void InsertMapping(
        SqlConnection connection,
        SqlTransaction transaction,
        string entityType,
        string sourceId,
        long targetId)
    {
        using var command = new SqlCommand(
            "INSERT INTO [asap].[LegacyPocketBaseMapping] ([EntityType], [PocketBaseId], [NewId]) VALUES (@entityType, @sourceId, @targetId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@targetId", targetId);
        command.ExecuteNonQuery();
    }

    private static bool OrganizationExists(SqlConnection connection, SqlTransaction transaction, int organizationId)
    {
        using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static bool OrganizationIsActive(SqlConnection connection, SqlTransaction transaction, int organizationId)
    {
        using var command = new SqlCommand(
            "SELECT [IsActive] FROM [asap].[Organization] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        var value = command.ExecuteScalar();
        return value is not null and not DBNull && Convert.ToBoolean(value);
    }


    private static int ResolveOrganizationId(
        SourceRow row,
        string field,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var sourceValue = row.RequiredString(field);
        if (organizationIds.TryGetValue(sourceValue, out var mapped)) return mapped;
        if (int.TryParse(sourceValue, out var organizationId) &&
            organizationId > 0 &&
            organizationIds.Values.Contains(organizationId))
        {
            return organizationId;
        }
        throw new MigrationOperationException("organization_reference_invalid", $"Source organization reference {sourceValue} cannot be resolved.");
    }

    private static string? ReadConsistentReference(
        SourceRow row,
        string scalarField,
        string relationField,
        string errorCode)
    {
        var scalar = row.String(scalarField);
        var relation = row.String(relationField);
        if (scalar is not null && relation is not null &&
            !string.Equals(scalar, relation, StringComparison.Ordinal))
        {
            throw new MigrationOperationException(
                errorCode,
                $"Source fields {scalarField} and {relationField} contain conflicting references.");
        }
        return scalar ?? relation;
    }

    private static void ValidateOptionalOrganizationReference(
        int? organizationId,
        IReadOnlySet<int> validOrganizationIds,
        string sourceId,
        string description)
    {
        if (organizationId is null) return;
        if (!validOrganizationIds.Contains(organizationId.Value))
        {
            throw new MigrationOperationException(
                "organization_reference_invalid",
                $"Source {description} reference on {sourceId} cannot be resolved.");
        }
    }

    private static long? FindFormatId(
        SqlConnection connection,
        SqlTransaction transaction,
        int ownerOrganizationId,
        string code)
    {
        using var command = new SqlCommand(
            "SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @organizationId AND [Code] = @code;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", ownerOrganizationId);
        command.Parameters.AddWithValue("@code", code);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static long? FindTagId(
        SqlConnection connection,
        SqlTransaction transaction,
        string code)
    {
        using var command = new SqlCommand(
            "SELECT [Id] FROM [asap].[WorkflowTag] WHERE [Code] = @code;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", code);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static long EnsureTag(
        SqlConnection connection,
        SqlTransaction transaction,
        string code,
        string label,
        int sortOrder)
    {
        var existing = FindTagId(connection, transaction, code);
        if (existing is not null) return existing.Value;
        using var command = new SqlCommand(
            "INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) OUTPUT inserted.[Id] VALUES (@code, @label, @sortOrder);",
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@label", label);
        command.Parameters.AddWithValue("@sortOrder", sortOrder);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void InsertRequestTag(
        SqlConnection connection,
        SqlTransaction transaction,
        long requestId,
        long tagId)
    {
        using var command = new SqlCommand(
            """
            IF NOT EXISTS
            (
                SELECT 1 FROM [asap].[TitleRequestWorkflowTag]
                WHERE [TitleRequestId] = @requestId AND [WorkflowTagId] = @tagId
            )
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
                VALUES (@requestId, @tagId);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@requestId", requestId);
        command.Parameters.AddWithValue("@tagId", tagId);
        command.ExecuteNonQuery();
    }

    private static long ResolveRequestFormatId(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        IReadOnlyDictionary<string, long> formatIds)
    {
        var relation = row.String("formatRef");
        if (relation is not null)
        {
            if (!formatIds.TryGetValue(relation, out var mapped))
            {
                throw new MigrationOperationException(
                    "request_format_reference_invalid",
                    $"Title request {row.RequiredString("id")} has an unresolved format reference.");
            }

            var scalarCode = row.String("format");
            if (scalarCode is not null &&
                !string.Equals(
                    ReadFormatCode(connection, transaction, mapped),
                    NormalizeFormatCode(scalarCode),
                    StringComparison.Ordinal))
            {
                throw new MigrationOperationException(
                    "request_format_conflict",
                    $"Title request {row.RequiredString("id")} has conflicting format and formatRef values.");
            }
            return mapped;
        }

        var code = NormalizeFormatCode(row.String("format") ?? string.Empty);
        var libraryId = row.Int32("libraryOrgId") ?? 0;
        var custom = FindFormatId(connection, transaction, libraryId, code);
        var system = FindFormatId(connection, transaction, 1, code);
        return custom ?? system ?? throw new MigrationOperationException(
            "request_format_unresolved",
            $"Title request {row.RequiredString("id")} has no resolvable material format.");
    }

    private static long? ResolveOptionalMapping(string? sourceId, IReadOnlyDictionary<string, long> mappings)
    {
        if (sourceId is null) return null;
        return mappings.TryGetValue(sourceId, out var mapped) ? mapped : null;
    }

    private static long? ResolveRequiredMapping(
        string? sourceId,
        IReadOnlyDictionary<string, long> mappings,
        string errorCode,
        string description)
    {
        if (sourceId is null) return null;
        return mappings.TryGetValue(sourceId, out var mapped)
            ? mapped
            : throw new MigrationOperationException(
                errorCode,
                $"Source {description} reference {sourceId} cannot be resolved.");
    }

    private static string ReadFormatCode(
        SqlConnection connection,
        SqlTransaction transaction,
        long formatId)
    {
        using var command = new SqlCommand(
            "SELECT [Code] FROM [asap].[MaterialFormat] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", formatId);
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new MigrationOperationException(
                "request_format_unresolved",
                "A mapped material format no longer exists.");
    }

    private static ImportedClaim ResolveRequestClaim(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        string status,
        long formatId,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> claimRuleIds,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        var sourceClaimantId = row.String("claimedByStaffUserId");
        var mappedStaffId = ResolveOptionalMapping(sourceClaimantId, staffIds);
        var displayName = row.String("claimedByDisplayName");
        var claimedAt = row.UtcDateTime("claimedAt");
        var sourceType = NormalizeClaimType(row.String("claimType"));
        var sourceRuleId = row.String("claimRuleId");
        var mappedRuleId = ResolveOptionalMapping(sourceRuleId, claimRuleIds);
        if (sourceType == "manual" && sourceRuleId is not null)
        {
            throw new MigrationOperationException(
                "claim_rule_conflict",
                $"Title request {row.RequiredString("id")} has a manual claim with an automatic rule reference.");
        }
        var hasSourceAttribution = sourceClaimantId is not null || displayName is not null ||
            claimedAt is not null || sourceType is not null || sourceRuleId is not null;
        if (!hasSourceAttribution)
        {
            return new(false, false, null, null, null, null, null, null, null, "unclaimed");
        }
        if (sourceClaimantId is null)
        {
            if (status == "closed" && displayName is not null && claimedAt is not null && sourceType is not null)
            {
                var historicalType = NormalizeHistoricalClaimType(sourceType, mappedStaffId, mappedRuleId);
                return new(true, false, null, null, null, displayName, claimedAt, historicalType, mappedRuleId, "closed_claimant_unmapped");
            }
            return new(true, true, null, null, null, null, null, null, null, status == "closed" ? "closed_attribution_incomplete" : "claimant_unmapped");
        }

        if (status == "closed")
        {
            if (displayName is null || claimedAt is null || sourceType is null)
            {
                return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "closed_attribution_incomplete");
            }
            var historicalType = NormalizeHistoricalClaimType(sourceType, mappedStaffId, mappedRuleId);
            return new(
                true,
                false,
                sourceClaimantId,
                mappedStaffId,
                mappedStaffId,
                displayName,
                claimedAt,
                historicalType,
                mappedRuleId,
                mappedStaffId is null ? "closed_claimant_unmapped" : "closed_history_preserved");
        }

        if (mappedStaffId is null)
        {
            return new(true, true, sourceClaimantId, null, null, null, null, null, null, "claimant_unmapped");
        }
        if (displayName is null || claimedAt is null || sourceType is null)
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "claim_metadata_incomplete");
        }
        var libraryId = row.Int32("libraryOrgId") ?? 0;
        var eligibility = StaffEligibilityReason(
            connection,
            transaction,
            mappedStaffId.Value,
            libraryId,
            allowedTenantIds);
        if (eligibility != "eligible")
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, eligibility);
        }
        if (sourceType == "manual")
        {
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, displayName, claimedAt, "manual", null, "eligible");
        }
        if (sourceType != "automatic_format_rule")
        {
            throw new MigrationOperationException("claim_type_invalid", $"Unknown claim type: {sourceType}");
        }
        if (mappedRuleId is null)
        {
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, displayName, claimedAt, "legacy", null, "claim_rule_unmapped_normalized");
        }
        if (!RuleMatchesStoredClaim(
                connection,
                transaction,
                mappedRuleId.Value,
                libraryId,
                formatId,
                mappedStaffId.Value))
        {
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, displayName, claimedAt, "legacy", mappedRuleId, "claim_rule_mismatch_normalized");
        }
        return new(
            true,
            false,
            sourceClaimantId,
            mappedStaffId,
            mappedStaffId,
            displayName,
            claimedAt,
            "automatic_format_rule",
            mappedRuleId,
            "eligible");
    }

    private static string? NormalizeClaimType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "manual" => "manual",
        "automatic_format_rule" or "automaticformatrule" => "automatic_format_rule",
        var invalid => throw new MigrationOperationException(
            "claim_type_invalid",
            $"Unknown claim type: {invalid}")
    };

    private static string NormalizeHistoricalClaimType(string sourceType, long? mappedStaffId, long? mappedRuleId) => sourceType switch
    {
        "manual" => "manual",
        "automatic_format_rule" when mappedStaffId is not null && mappedRuleId is not null => "automatic_format_rule",
        "automatic_format_rule" => "legacy",
        _ => throw new MigrationOperationException("claim_type_invalid", $"Unknown claim type: {sourceType}")
    };

    private static bool IsStaffEligibleForLibrary(
        SqlConnection connection,
        SqlTransaction transaction,
        long staffUserId,
        int libraryId,
        IReadOnlySet<Guid> allowedTenantIds) =>
        StaffEligibilityReason(connection, transaction, staffUserId, libraryId, allowedTenantIds) == "eligible";

    private static string StaffEligibilityReason(
        SqlConnection connection,
        SqlTransaction transaction,
        long staffUserId,
        int libraryId,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        using var command = new SqlCommand(
            "SELECT [IsActive], [Role], [OrganizationId], [UserPrincipalName], [NormalizedUserPrincipalName] FROM [asap].[StaffUser] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", staffUserId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return "claimant_unmapped";
        if (!reader.GetBoolean(0)) return "claimant_inactive";
        var role = reader.GetString(1);
        var organizationId = reader.GetInt32(2);
        var authenticationEmail = reader.IsDBNull(3) ? null : RealEmail(reader.GetString(3));
        if (authenticationEmail is null || reader.IsDBNull(4) ||
            !string.Equals(authenticationEmail.ToUpperInvariant(), reader.GetString(4), StringComparison.Ordinal))
        {
            throw new MigrationOperationException("active_staff_email_invalid", $"Active staff user {staffUserId} has an invalid authentication email.");
        }
        return role switch
        {
            "super_admin" when organizationId == 1 => "eligible",
            "staff" or "admin" when organizationId == libraryId => "eligible",
            _ => "claimant_out_of_scope"
        };
    }

    private static bool RuleMatchesStoredClaim(
        SqlConnection connection,
        SqlTransaction transaction,
        long ruleId,
        int libraryId,
        long formatId,
        long staffId)
    {
        using var command = new SqlCommand(
            """
            SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule]
            WHERE [Id] = @id AND [LibraryOrganizationId] = @libraryId AND [MaterialFormatId] = @formatId
              AND [StaffUserId] = @staffId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", ruleId);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        command.Parameters.AddWithValue("@formatId", formatId);
        command.Parameters.AddWithValue("@staffId", staffId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static string NormalizeFormatCode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" => "book",
        "1" => "ebook",
        "2" => "audiobook_cd",
        "3" => "eaudiobook",
        "4" => "dvd",
        "5" => "music_cd",
        var code when !string.IsNullOrWhiteSpace(code) => code,
        _ => throw new MigrationOperationException("format_code_invalid", "Material format code is blank.")
    };

    private static string NormalizeWorkflowTagCode(string value) => value.Trim() switch
    {
        "Identifier found" or "dupe found in Polaris" => "polaris_bib_found",
        "Identifier number not found in system" or "ISBN not found in system" => "polaris_bib_not_found",
        "Multiple Polaris matches" => "polaris_multiple_matches",
        "Duplicate suggestion" => "duplicate_suggestion",
        var code when !string.IsNullOrWhiteSpace(code) => code,
        _ => throw new MigrationOperationException("workflow_tag_code_invalid", "Workflow tag code is blank.")
    };

    private static string NormalizeStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" or "suggestion" => "suggestion",
        "1" or "5" or "pending_hold" or "pendinghold" => "pending_hold",
        "2" or "hold_placed" or "holdplaced" => "hold_placed",
        "3" or "outstanding_purchase" or "outstandingpurchase" => "outstanding_purchase",
        "4" or "closed" => "closed",
        var invalid => throw new MigrationOperationException("request_status_invalid", $"Unknown request status: {invalid}")
    };

    private static string? NormalizeCloseReason(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "rejected" or "reject" => "rejected",
        "hold_completed" or "holdcompleted" or "hold_placed" or "checkout" or "checked_out" => "hold_completed",
        "hold_not_picked_up" => "hold_not_picked_up",
        "hold_unclaimed" or "unclaimed" => "hold_unclaimed",
        "hold_cancelled" or "cancelled" => "hold_cancelled",
        "hold_expired" or "expired" => "hold_expired",
        "duplicate_hold" => "duplicate_hold",
        "manual" => "manual",
        "purchased_no_hold" or "purchased_no_hold_purchase_outcome" => "purchased_no_hold",
        "silent" or "silently closed" => "Silently Closed",
        var invalid => throw new MigrationOperationException("request_close_reason_invalid", $"Unknown request close reason: {invalid}")
    };

    private static string? ResolveEventStatus(
        string? sourceValue,
        IReadOnlyDictionary<string, string> statuses)
    {
        if (sourceValue is null) return null;
        return statuses.TryGetValue(sourceValue, out var code) ? code : NormalizeStatus(sourceValue);
    }

    private static IReadOnlyDictionary<string, string> BuildStatusMap(IReadOnlyList<SourceRow> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            if (!result.TryAdd(sourceId, NormalizeStatus(row.RequiredString("code"))))
            {
                throw new MigrationOperationException(
                    "request_status_conflict",
                    $"Source request status taxonomy contains duplicate ID {sourceId}.");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildCloseReasonMap(IReadOnlyList<SourceRow> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            if (!result.TryAdd(sourceId, NormalizeCloseReason(row.RequiredString("code"))!))
            {
                throw new MigrationOperationException(
                    "request_close_reason_conflict",
                    $"Source request close-reason taxonomy contains duplicate ID {sourceId}.");
            }
        }
        return result;
    }

    private static string ResolveRequestStatus(
        SourceRow row,
        IReadOnlyDictionary<string, string> statuses)
    {
        var raw = row.String("status");
        var reference = row.String("statusRef");
        var rawStatus = raw is null ? null : NormalizeStatus(raw);
        var referencedStatus = ResolveRequiredReference(reference, statuses, "request_status_reference_invalid");
        if (rawStatus is not null && referencedStatus is not null && rawStatus != referencedStatus)
        {
            throw new MigrationOperationException(
                "request_status_conflict",
                $"Title request {row.RequiredString("id")} has conflicting status and statusRef values.");
        }
        return rawStatus ?? referencedStatus ?? throw new MigrationOperationException(
            "request_status_missing",
            $"Title request {row.RequiredString("id")} has no status.");
    }

    private static string? ResolveRequestCloseReason(
        SourceRow row,
        IReadOnlyDictionary<string, string> closeReasons)
    {
        var raw = row.String("closeReason");
        var reference = row.String("closeReasonRef");
        var rawReason = NormalizeCloseReason(raw);
        var referencedReason = ResolveRequiredReference(reference, closeReasons, "request_close_reason_reference_invalid");
        if (rawReason is not null && referencedReason is not null && rawReason != referencedReason)
        {
            throw new MigrationOperationException(
                "request_close_reason_conflict",
                $"Title request {row.RequiredString("id")} has conflicting closeReason and closeReasonRef values.");
        }
        return rawReason ?? referencedReason;
    }

    private static string? ResolveRequiredReference(
        string? reference,
        IReadOnlyDictionary<string, string> values,
        string errorCode)
    {
        if (reference is null) return null;
        return values.TryGetValue(reference, out var value)
            ? value
            : throw new MigrationOperationException(errorCode, $"Source reference {reference} cannot be resolved.");
    }

    private static string? ResolveEventCloseReason(
        string? sourceValue,
        IReadOnlyDictionary<string, string> closeReasons)
    {
        if (sourceValue is null) return null;
        return closeReasons.TryGetValue(sourceValue, out var code) ? code : NormalizeCloseReason(sourceValue);
    }

    private static JsonElement? ParseJsonElement(string? value)
    {
        if (value is null) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string? NormalizeIsbnStatus(
        string sourceId,
        string? sourceStatus,
        string? identifier,
        string? bibId) => sourceStatus?.Trim().ToLowerInvariant() switch
        {
            null => null,
            "pending" => "pending",
            "found" when bibId is not null => "found",
            "found" => throw new MigrationOperationException("identifier_found_without_bib", $"Title request {sourceId} is found without a supporting BIB."),
            "not_found" => "not_found",
            "skipped_no_isbn" => "skipped_no_isbn",
            "error_max_retries" => "error_max_retries",
            "error" when identifier is null => "skipped_no_isbn",
            "error" => throw new MigrationOperationException("identifier_error_ambiguous", $"Title request {sourceId} has an ambiguous identifier error."),
            "found_in_polaris" when bibId is not null => "found",
            "found_in_polaris" => throw new MigrationOperationException("identifier_found_without_bib", $"Title request {sourceId} is found_in_polaris without a supporting BIB."),
            var invalid => throw new MigrationOperationException("identifier_status_invalid", $"Title request {sourceId} has unknown identifier status {invalid}.")
        };

    private static string IsbnTransformationReason(string? source, string? target) => (source, target) switch
    {
        (null, null) => "preserved_absent",
        ("error", "skipped_no_isbn") => "missing_identifier_error_normalized",
        ("found_in_polaris", "found") => "historical_alias_normalized",
        ("skipped_no_isbn", "skipped_no_isbn") => "retry_count_normalized",
        _ => "preserved"
    };

    private static string? NormalizeOptionalEnum(string? value, IReadOnlyCollection<string> allowed, string errorCode)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        var canonical = allowed.FirstOrDefault(
            item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        if (canonical is not null) return canonical;
        throw new MigrationOperationException(errorCode, $"Unknown source value: {value}");
    }

    private static DateTime? ParseDate(string? value, string errorCode)
    {
        if (value is null) return null;
        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
        {
            return date.Date;
        }
        throw new MigrationOperationException(errorCode, $"Invalid source date: {value}");
    }

    private static object DbString(string? value) => (object?)value ?? DBNull.Value;

    private static object DbValue<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private sealed record ImportedClaim(
        bool HasSourceAttribution,
        bool RequiresMigrationAnnotation,
        string? SourceClaimantId,
        long? MappedStaffUserId,
        long? StaffUserId,
        string? DisplayName,
        DateTime? ClaimedAtUtc,
        string? ClaimType,
        long? ClaimRuleId,
        string Reason);

    private sealed record ClaimTransformation(
        string SourceId,
        int LibraryOrganizationId,
        string Status,
        string? SourceClaimantId,
        long? MappedStaffUserId,
        long? EffectiveStaffUserId,
        string? SourceDisplayName,
        DateTime? SourceClaimedAtUtc,
        string? SourceClaimType,
        string? SourceClaimRuleId,
        string Reason,
        bool MigrationAnnotationInserted);

    private sealed record SourceEvent(
        string Id,
        string EventType,
        string? FromStatus,
        string? ToStatus,
        string? CloseReason,
        IReadOnlyList<PlacementBibSource> BibSources);

    private sealed record SourceTemplate(SourceRow Row, bool IsRejection);

    private sealed record PlacementEvidence(
        string Kind,
        string SourceCollection,
        string SourceRecordId,
        string SourceField,
        string Value);

    private sealed record PlacementBibSource(
        string SourceCollection,
        string SourceRecordId,
        string SourceField,
        string BibId);

    private sealed record PlacementTransformation(
        string SourceId,
        int LibraryOrganizationId,
        string Status,
        string? BibId,
        IReadOnlyList<PlacementEvidence> Evidence,
        IReadOnlyList<PlacementBibSource> BibSources,
        string Action,
        IReadOnlyList<PlacementEvidence> Hints);

    private sealed record ConfigurationSourceFields(
        string File,
        string Collection,
        IReadOnlySet<string> Known,
        IReadOnlySet<string> IntentionalDrops);

    private sealed record MigrationSemanticReconciliation(
        int organizations,
        int staffUsers,
        int titleRequests,
        int additionalCopies,
        int deletedRequestAudits,
        int brandingAssets,
        int configurationRowsChecked,
        int configurationFieldsChecked,
        int configurationRelationshipsChecked,
        int workflowTagsChecked,
        int titleRequestTagsChecked,
        int historyRowsChecked,
        bool passed);

    private sealed record MigrationHistoryReconciliation(
        int WorkflowTagsChecked,
        int TitleRequestTagsChecked,
        int HistoryRowsChecked);

    private static int Scalar(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int ScalarWithAllowedTenants(
        SqlConnection connection,
        string sql,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        using var command = connection.CreateCommand();
        var parameterNames = allowedTenantIds
            .Order()
            .Select((tenantId, index) =>
            {
                var name = $"@tenant{index}";
                command.Parameters.AddWithValue(name, tenantId);
                return name;
            })
            .ToArray();
        command.CommandText = sql.Replace("{allowedTenants}", string.Join(", ", parameterNames), StringComparison.Ordinal);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string? RealEmail(string? value)
    {
        var clean = Clean(value);
        if (clean is null || clean.EndsWith("@staff.asap.local", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var address = new MailAddress(clean);
            return string.Equals(address.Address, clean, StringComparison.OrdinalIgnoreCase) ? clean : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record BootstrapIdentity(
        string UserPrincipalName,
        string? DisplayName,
        string NotificationEmail);
}
