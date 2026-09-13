using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

public sealed record MigrationImportOptions(
    string PackagePath,
    string ConnectionString,
    string StaffIdentityMapPath,
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
        ValidateSliceDomainCoverage(package);
        ValidateOptions(options, package);
        var operationalConfiguration = options.ExternalConfigurationPath is null
            ? null
            : MigrationOperationalConfiguration.Validate(package, options.ExternalConfigurationPath);
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var staffUsers = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var identityMap = ReadIdentityMap(options.StaffIdentityMapPath, staffUsers, options.AllowedTenantIds);
        var credentialProtector = MigrationCredentialProtector.Load(options.ExternalConfigurationPath);
        var postmarkToken = ReadOptionalSecretEnvironment(
            options.PostmarkTokenEnvironmentName,
            "postmark_token_missing");
        var importedCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var transformations = new List<object>();
        var claimTransformations = new List<ClaimTransformation>();
        var placementTransformations = new List<PlacementTransformation>();
        MigrationSemanticReconciliation semanticReconciliation;
        transformations.AddRange(ValidateConfigurationSourceFields(package));
        transformations.Add(new
        {
            entity = "operational_configuration",
            status = operationalConfiguration is null ? "external_config_not_supplied" : "matched",
            matchedSchedules = operationalConfiguration?.MatchedSchedules ?? 0,
            matchedQueues = operationalConfiguration?.MatchedQueues ?? 0,
            retiredObsoleteOverrides = operationalConfiguration?.RetiredObsoleteOverrides ?? 0
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
                identityMap,
                organizationIds,
                options.AllowedTenantIds,
                importedCounts,
                transformations);
            var bootstrapMutatedStaffUserId = EnsureUsableSuperAdministrator(
                connection,
                transaction,
                options.ExternalConfigurationPath,
                options.AllowedTenantIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
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
                options.AllowedTenantIds,
                package.Manifest.ExportedAtUtc.UtcDateTime,
                importedCounts,
                transformations);
            var titleRequestRows = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
            var requestIds = ImportTitleRequests(
                connection,
                transaction,
                titleRequestRows,
                formatIds,
                staffIds,
                claimRuleIds,
                tagIds,
                options.AllowedTenantIds,
                requestStatuses,
                requestCloseReasons,
                importedCounts,
                transformations,
                claimTransformations);
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
                identityMap,
                titleRequestRows,
                organizationIds,
                formatIds,
                requestStatuses,
                requestCloseReasons,
                bootstrapMutatedStaffUserId);
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
            placementTransformations,
            semanticReconciliation);
        return new MigrationImportResult(importedCounts, true);
    }

    private static void ValidateSliceDomainCoverage(ValidatedMigrationPackage package)
    {
        var deferredCollections = new[]
        {
            (File: "additional-copy-requests.json", Collection: "additional_copy_requests"),
            (File: "deleted-request-audit.json", Collection: "deleted_request_audit")
        };
        foreach (var item in deferredCollections)
        {
            var count = MigrationPackageReader.ReadRowsOrEmpty(package, item.File, item.Collection).Count;
            if (count != 0)
            {
                throw new MigrationOperationException(
                    "source_domain_not_supported",
                    $"Source collection {item.Collection} contains {count} row(s) owned by a later migration slice.");
            }
        }
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
                    "firstSuccessfulSaveAt", "materialTypesCache", "materialTypesCacheUpdated")),
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
                Fields("logo", "logoAlt"))
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
        if (!File.Exists(options.StaffIdentityMapPath))
        {
            throw new MigrationOperationException("staff_identity_map_missing", "The staff Entra identity map is missing.");
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

    private static Dictionary<string, StaffIdentity> ReadIdentityMap(
        string path,
        IReadOnlyList<SourceRow> staffUsers,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        StaffIdentityMap document;
        try
        {
            document = JsonSerializer.Deserialize<StaffIdentityMap>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Identity map was null.");
        }
        catch (JsonException exception)
        {
            throw new MigrationOperationException("staff_identity_map_invalid", exception.Message);
        }

        var sourceIds = staffUsers.Select(row => row.RequiredString("id")).ToHashSet(StringComparer.Ordinal);
        var bySource = new Dictionary<string, StaffIdentity>(StringComparer.Ordinal);
        var durableIdentities = new HashSet<(Guid TenantId, Guid ObjectId)>();
        foreach (var identity in document.Users)
        {
            if (string.IsNullOrWhiteSpace(identity.PocketBaseStaffUserId) ||
                identity.TenantId == Guid.Empty ||
                identity.ObjectId == Guid.Empty ||
                !allowedTenantIds.Contains(identity.TenantId) ||
                string.IsNullOrWhiteSpace(identity.UserPrincipalName) ||
                !sourceIds.Contains(identity.PocketBaseStaffUserId) ||
                !bySource.TryAdd(identity.PocketBaseStaffUserId, identity) ||
                !durableIdentities.Add((identity.TenantId, identity.ObjectId)))
            {
                throw new MigrationOperationException(
                    "staff_identity_map_invalid",
                    "Staff identity mappings must be unique, allowed-tenant, and reference one exported staff user.");
            }
        }

        foreach (var staff in staffUsers.Where(row => row.Bool("active")))
        {
            if (!bySource.ContainsKey(staff.RequiredString("id")))
            {
                throw new MigrationOperationException(
                    "active_staff_identity_missing",
                    $"Active staff user {staff.RequiredString("id")} has no Entra identity mapping.");
            }
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
                (SELECT COUNT(*) FROM [asap].[PatronSession]),
                (SELECT COUNT(*) FROM [asap].[EmailOutbox]),
                (SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent]),
                (SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping]);
            """,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != MigrationContract.ExpectedSchemaVersion)
        {
            throw new MigrationOperationException("target_schema_version_mismatch", "Target application schema version is incompatible.");
        }
        if (Enumerable.Range(1, 7).Any(index => reader.GetInt32(index) != 0))
        {
            throw new MigrationOperationException("target_not_fresh", "Target contains runtime, business, or prior migration rows.");
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
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var organizationId = row.Int32("organizationId")
                ?? throw new MigrationOperationException("organization_id_missing", $"Organization {sourceId} has no Polaris ID.");
            if (organizationId <= 0 || !mapped.TryAdd(sourceId, organizationId))
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
        IReadOnlyDictionary<string, StaffIdentity> identities,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
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
            identities.TryGetValue(sourceId, out var identity);
            if (active && identity is null)
            {
                throw new MigrationOperationException("active_staff_identity_missing", $"Active staff user {sourceId} has no Entra identity mapping.");
            }
            if (identity is not null && !allowedTenantIds.Contains(identity.TenantId))
            {
                throw new MigrationOperationException("staff_tenant_not_allowed", $"Staff user {sourceId} maps to a disallowed tenant.");
            }

            var sourceEmail = RealEmail(row.String("email"));
            var weeklyEmail = RealEmail(row.String("weekly_action_summary_email"));
            var mappedNotification = RealEmail(identity?.NotificationEmail);
            var notificationEmail = mappedNotification ?? sourceEmail ?? weeklyEmail;
            var notificationSource = mappedNotification is not null
                ? "identity_map"
                : sourceEmail is not null
                    ? "staff_email"
                    : weeklyEmail is not null
                        ? "weekly_action_summary_email"
                        : "none";
            var sourceWeeklyRecipient = weeklyEmail ?? sourceEmail;
            var targetWeeklyRecipient = weeklyEmail ?? notificationEmail;
            var userPrincipalName = identity?.UserPrincipalName.Trim() ?? sourceEmail ?? row.String("username");
            var displayName = Clean(identity?.DisplayName) ?? row.String("displayName") ?? row.String("username");

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
            command.Parameters.AddWithValue("@tenantId", (object?)identity?.TenantId ?? DBNull.Value);
            command.Parameters.AddWithValue("@objectId", (object?)identity?.ObjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("@upn", (object?)userPrincipalName ?? DBNull.Value);
            command.Parameters.AddWithValue("@normalizedUpn", (object?)userPrincipalName?.ToUpperInvariant() ?? DBNull.Value);
            command.Parameters.AddWithValue("@displayName", (object?)displayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@notificationEmail", (object?)notificationEmail ?? DBNull.Value);
            command.Parameters.AddWithValue("@role", role);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@isActive", active);
            command.Parameters.AddWithValue("@weeklyEnabled", row.Bool("weekly_action_summary_enabled"));
            command.Parameters.AddWithValue("@weeklyEmail", (object?)weeklyEmail ?? DBNull.Value);
            command.Parameters.AddWithValue("@purchaseDefault", row.Bool("purchase_reminder_default"));
            command.Parameters.AddWithValue("@additionalCopyDefault", row.Bool("additional_copy_reminder_default"));
            command.Parameters.AddWithValue("@mineDefault", row.Bool("default_mine_unclaimed_filter"));
            command.Parameters.AddWithValue("@lastLoginUtc", (object?)row.UtcDateTime("lastLogin") ?? DBNull.Value);
            var targetId = Convert.ToInt64(command.ExecuteScalar());
            mapped.Add(sourceId, targetId);
            InsertMapping(connection, transaction, "staff_user", sourceId, targetId);
            transformations.Add(new
            {
                entity = "staff_user",
                sourceId,
                notificationEmailSource = notificationSource,
                sourceOrdinaryRecipient = sourceEmail,
                sourceWeeklyRecipient,
                targetOrdinaryRecipient = notificationEmail,
                targetWeeklyRecipient,
                ordinaryRecipientChanged = !string.Equals(sourceEmail, notificationEmail, StringComparison.OrdinalIgnoreCase),
                weeklyRecipientChanged = !string.Equals(sourceWeeklyRecipient, targetWeeklyRecipient, StringComparison.OrdinalIgnoreCase),
                targetHasNotificationEmail = notificationEmail is not null,
                targetHasWeeklyRecipient = targetWeeklyRecipient is not null
            });
        }
        importedCounts["staff_users"] = rows.Count;
        return mapped;
    }

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
            var subject = row.Text("subject");
            var body = row.Text("body");
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
                AddFormatParameters(overrideCommand, row, ownerId, targetId, includeOwnerAndDates: false, exportedAtUtc);
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
                    AddFormatParameters(update, row, ownerId, targetId, includeOwnerAndDates: true, exportedAtUtc);
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
                    AddFormatParameters(insert, row, ownerId, null, includeOwnerAndDates: true, exportedAtUtc);
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
        DateTime exportedAtUtc)
    {
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@formatId", (object?)formatId ?? DBNull.Value);
        command.Parameters.AddWithValue("@code", NormalizeFormatCode(row.RequiredString("code")));
        command.Parameters.AddWithValue("@label", row.RequiredText("label"));
        command.Parameters.AddWithValue("@sortOrder", row.Int32("sortOrder") ?? 0);
        command.Parameters.AddWithValue("@enabled", row.Bool("enabled", true));
        command.Parameters.AddWithValue("@messageBehavior", DbString(NormalizeOptionalEnum(
            row.String("messageBehavior"),
            ["none", "message", "ebookMessage", "eaudiobookMessage"],
            "format_message_behavior_invalid")));
        command.Parameters.AddWithValue("@message", DbString(row.Text("message")));
        command.Parameters.AddWithValue("@titleMode", DbString("required"));
        command.Parameters.AddWithValue("@titleLabel", DbString(row.Text("titleLabel")));
        command.Parameters.AddWithValue("@authorMode", DbString(NormalizeOptionalEnum(
            row.String("authorMode"), ["required", "optional", "hidden"], "format_author_mode_invalid")));
        command.Parameters.AddWithValue("@authorLabel", DbString(row.Text("authorLabel")));
        command.Parameters.AddWithValue("@identifierMode", DbString(NormalizeOptionalEnum(
            row.String("identifierMode"), ["required", "optional", "hidden"], "format_identifier_mode_invalid")));
        command.Parameters.AddWithValue("@identifierLabel", DbString(row.Text("identifierLabel")));
        command.Parameters.AddWithValue("@publicationMode", DbString(NormalizeOptionalEnum(
            row.String("publicationMode"), ["required", "optional", "hidden"], "format_publication_mode_invalid")));
        command.Parameters.AddWithValue("@publicationLabel", DbString(row.Text("publicationLabel")));
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
        var ordinal = 0;
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var code = NormalizeWorkflowTagCode(row.RequiredString("code"));
            var id = FindTagId(connection, transaction, code);
            if (id is null)
            {
                using var insert = new SqlCommand(
                    "INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder]) OUTPUT inserted.[Id] VALUES (@code, @label, @sortOrder);",
                    connection,
                    transaction);
                insert.Parameters.AddWithValue("@code", code);
                insert.Parameters.AddWithValue("@label", row.String("label") ?? code);
                insert.Parameters.AddWithValue("@sortOrder", 1000 + ordinal);
                id = Convert.ToInt64(insert.ExecuteScalar());
            }
            mapped.Add(sourceId, id.Value);
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
        IReadOnlySet<Guid> allowedTenantIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var libraryId = row.Int32("libraryOrgId")
                ?? throw new MigrationOperationException("claim_rule_library_missing", $"Format claim rule {sourceId} has no library.");
            if (libraryId == 1 || !OrganizationExists(connection, transaction, libraryId))
            {
                throw new MigrationOperationException("claim_rule_library_invalid", $"Format claim rule {sourceId} has an invalid library.");
            }
            var formatCode = NormalizeFormatCode(row.RequiredString("format"));
            var formatId = FindFormatId(connection, transaction, libraryId, formatCode) ??
                FindFormatId(connection, transaction, 1, formatCode) ??
                throw new MigrationOperationException("claim_rule_format_unresolved", $"Format claim rule {sourceId} has no resolvable format.");
            var sourceStaffId = row.String("staffUserId");
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
        IDictionary<string, long> tagIds,
        IReadOnlySet<Guid> allowedTenantIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        IReadOnlyDictionary<string, string> requestCloseReasons,
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
            command.Parameters.AddWithValue("@libraryId", row.Int32("libraryOrgId") ?? throw new MigrationOperationException("request_library_missing", $"Title request {sourceId} has no library."));
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
            if (claim.SourceClaimantId is not null)
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
                    claim.Reason);
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
                    reason = claim.Reason
                });
            }
        }
        importedCounts["title_requests"] = rows.Count;
        return mapped;
    }

    private static void ImportTitleRequestTags(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> tagIds,
        IDictionary<string, int> importedCounts)
    {
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            if (!requestIds.TryGetValue(row.RequiredString("titleRequest"), out var requestId) ||
                !tagIds.TryGetValue(row.RequiredString("tag"), out var tagId))
            {
                throw new MigrationOperationException("request_tag_reference_invalid", $"Title request tag {sourceId} has an unresolved reference.");
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
            var eventType = KnownEventTypes.Contains(sourceEventType) ? sourceEventType : "legacy";
            var fromStatus = ResolveEventStatus(row.String("fromStatus"), statuses);
            var toStatus = ResolveEventStatus(row.String("toStatus"), statuses);
            var closeReason = ResolveEventCloseReason(row.String("closeReason"), closeReasons);
            var sourceMetadata = row.JsonText("metadata");
            var metadata = JsonSerializer.Serialize(new
            {
                sourceCollection = "title_request_events",
                sourceRecordId = sourceId,
                sourceEventType,
                sourceFromStatus = row.String("fromStatus"),
                sourceToStatus = row.String("toStatus"),
                sourceCloseReason = row.String("closeReason"),
                sourceMetadata = ParseJsonElement(sourceMetadata)
            });

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
            requestEvents.Add(new SourceEvent(
                sourceId,
                sourceEventType,
                fromStatus,
                toStatus,
                closeReason,
                row.JsonPropertyString("metadata", "bibId") ??
                    row.JsonPropertyString("metadata", "bibid") ??
                    row.JsonPropertyString("metadata", "BibId")));
        }

        var markerCount = 0;
        foreach (var request in requestRows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceRequestId = request.RequiredString("id");
            var evidence = new List<PlacementEvidence>();
            var currentStatus = ResolveRequestStatus(request, statuses);
            var currentCloseReason = ResolveRequestCloseReason(request, closeReasons);
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
            foreach (var sourceEvent in requestEvents ?? [])
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
            if (evidence.Count == 0)
            {
                placementTransformations.Add(new(
                    sourceRequestId,
                    libraryOrganizationId,
                    currentStatus,
                    null,
                    [],
                    "no_placement_evidence"));
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

            var bibSources = new List<object>();
            var bibIds = new HashSet<string>(StringComparer.Ordinal);
            var requestBib = request.String("bibid");
            if (requestBib is not null)
            {
                bibIds.Add(requestBib);
                bibSources.Add(new { sourceCollection = "title_requests", sourceRecordId = sourceRequestId, sourceField = "bibid", bibId = requestBib });
            }
            foreach (var sourceEvent in requestEvents ?? [])
            {
                if (sourceEvent.BibId is null || !evidence.Any(item => item.SourceRecordId == sourceEvent.Id)) continue;
                bibIds.Add(sourceEvent.BibId);
                bibSources.Add(new { sourceCollection = "title_request_events", sourceRecordId = sourceEvent.Id, sourceField = "metadata.bibId", bibId = sourceEvent.BibId });
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
                bibSources
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
                "inserted"));
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
                bibSources,
                action = "inserted"
            });
        }
        importedCounts["title_request_events"] = eventRows.Count;
        importedCounts["placed_bib_protection_markers"] = markerCount;
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
            var mappedRequestId = ResolveOptionalMapping(sourceRequestId, requestIds);
            var mappedTemplateId = ResolveOptionalMapping(sourceTemplateId, templateIds);
            var metadata = JsonSerializer.Serialize(new
            {
                sourceCollection = "email_delivery_events",
                sourceRecordId = sourceId,
                sourceTitleRequestId = sourceRequestId,
                targetTitleRequestId = mappedRequestId,
                sourceEmailTemplateId = sourceTemplateId,
                targetEmailTemplateId = mappedTemplateId,
                templateKey = row.String("templateKey"),
                recipient = row.String("recipient"),
                subject = row.Text("subject"),
                sourceStatus = status,
                error = row.Text("error"),
                sourceMetadata = ParseJsonElement(row.JsonText("metadata"))
            });
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
            SELECT [EntraTenantId], [EntraObjectId]
            FROM [asap].[StaffUser]
            WHERE [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;
            """,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        var found = false;
        while (reader.Read())
        {
            found |= !reader.IsDBNull(0) && !reader.IsDBNull(1) &&
                allowedTenantIds.Contains(reader.GetGuid(0)) && reader.GetGuid(1) != Guid.Empty;
        }
        if (!found)
        {
            throw new MigrationOperationException("usable_super_admin_missing", "Import produced no active, allowed-tenant, bound system super-admin.");
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
                "Import produced no active, allowed-tenant, bound system super-admin and no target bootstrap configuration was supplied.");
        }

        var bootstrap = ReadBootstrapIdentity(externalConfigurationPath, allowedTenantIds);
        long? existingId;
        using (var find = new SqlCommand(
                   "SELECT [Id] FROM [asap].[StaffUser] WHERE [EntraTenantId] = @tenantId AND [EntraObjectId] = @objectId;",
                   connection,
                   transaction))
        {
            find.Parameters.AddWithValue("@tenantId", bootstrap.TenantId);
            find.Parameters.AddWithValue("@objectId", bootstrap.ObjectId);
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
                    [DisplayName] = @displayName,
                    [NotificationEmail] = @notificationEmail,
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
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive],
                     [WeeklyActionSummaryEnabled], [PurchaseReminderDefault],
                     [AdditionalCopyReminderDefault], [DefaultMineUnclaimedFilter])
                OUTPUT inserted.[Id]
                VALUES
                    (@tenantId, @objectId, @upn, @normalizedUpn,
                     @displayName, @notificationEmail, N'super_admin', 1, 1,
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
            tenantAllowed = true,
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
            SELECT [EntraTenantId], [EntraObjectId]
            FROM [asap].[StaffUser]
            WHERE [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;
            """,
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1) &&
                allowedTenantIds.Contains(reader.GetGuid(0)) && reader.GetGuid(1) != Guid.Empty)
            {
                return true;
            }
        }
        return false;
    }

    private static BootstrapIdentity ReadBootstrapIdentity(
        string externalConfigurationPath,
        IReadOnlySet<Guid> allowedTenantIds)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(externalConfigurationPath));
            var authentication = JsonProperty(document.RootElement, "Authentication");
            var entra = JsonProperty(authentication, "Entra");
            var value = JsonProperty(entra, "InitialSuperAdmin");
            var tenantId = Guid.Parse(JsonProperty(value, "TenantId").GetString() ?? string.Empty);
            var objectId = Guid.Parse(JsonProperty(value, "ObjectId").GetString() ?? string.Empty);
            var upn = RealEmail(JsonProperty(value, "UserPrincipalName").GetString());
            var notificationEmail = RealEmail(JsonProperty(value, "NotificationEmail").GetString());
            var displayName = Clean(JsonProperty(value, "DisplayName").GetString());
            if (tenantId == Guid.Empty || objectId == Guid.Empty || !allowedTenantIds.Contains(tenantId) ||
                upn is null || notificationEmail is null || displayName is null)
            {
                throw new MigrationOperationException(
                    "bootstrap_identity_invalid",
                    "The configured migration bootstrap identity is incomplete or outside the allowed tenant set.");
            }
            return new BootstrapIdentity(tenantId, objectId, upn, displayName, notificationEmail);
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

    private static void AddBootstrapParameters(SqlCommand command, BootstrapIdentity bootstrap)
    {
        command.Parameters.AddWithValue("@tenantId", bootstrap.TenantId);
        command.Parameters.AddWithValue("@objectId", bootstrap.ObjectId);
        command.Parameters.AddWithValue("@upn", bootstrap.UserPrincipalName);
        command.Parameters.AddWithValue("@normalizedUpn", bootstrap.UserPrincipalName.ToUpperInvariant());
        command.Parameters.AddWithValue("@displayName", bootstrap.DisplayName);
        command.Parameters.AddWithValue("@notificationEmail", bootstrap.NotificationEmail);
    }

    private static MigrationSemanticReconciliation ReconcileImportedSourceState(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> organizations,
        IReadOnlyList<SourceRow> staffUsers,
        IReadOnlyDictionary<string, StaffIdentity> identities,
        IReadOnlyList<SourceRow> titleRequests,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, string> requestStatuses,
        IReadOnlyDictionary<string, string> requestCloseReasons,
        long? bootstrapMutatedStaffUserId)
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
            identities.TryGetValue(sourceId, out var identity);
            EnsureSemantic(
                GuidEquals(reader, 1, identity?.TenantId) &&
                GuidEquals(reader, 2, identity?.ObjectId) &&
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
                var sourceEmail = RealEmail(row.String("email"));
                var weeklyEmail = RealEmail(row.String("weekly_action_summary_email"));
                var mappedNotification = RealEmail(identity?.NotificationEmail);
                var notificationEmail = mappedNotification ?? sourceEmail ?? weeklyEmail;
                var userPrincipalName = identity?.UserPrincipalName.Trim() ?? sourceEmail ?? row.String("username");
                var displayName = Clean(identity?.DisplayName) ?? row.String("displayName") ?? row.String("username");
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
            using var command = new SqlCommand(
                """
                SELECT r.[LibraryOrganizationId], r.[PatronOrganizationId], r.[StaffLibraryOrganizationIdCreatedBy],
                       r.[Barcode], r.[Email], r.[NameFirst], r.[NameLast], r.[PatronCodeId], r.[PatronCodeDescription],
                       r.[PreferredPickupBranchId], r.[PreferredPickupBranchName], r.[LibraryNameSnapshot],
                       r.[Title], r.[Author], r.[Identifier], r.[Publication], r.[ExactPublicationDate],
                       r.[CustomFieldsJson], r.[AutoHold], r.[MaterialFormatId], r.[Status], r.[CloseReason], r.[BibId],
                       r.[LastPromoterCheckUtc], r.[IsbnCheckStatus], r.[IsbnCheckResult], r.[IsbnCheckRetryCount],
                       r.[IsbnCheckLastErrorCode], r.[LastCheckedUtc], r.[CreatedUtc], r.[UpdatedUtc]
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
                DateEquals(reader, 30, row.UtcDateTime("updated")),
                "title request");
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

        return new(
            organizations.Count,
            staffUsers.Count,
            titleRequests.Count,
            brandingRows.Count,
            true);
    }

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
            ["title_request_events"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent];"),
            ["placed_bib_protection_markers"] = Scalar(connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';"),
            ["legacy_mappings"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping];"),
            ["patron_sessions"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[PatronSession];"),
            ["email_outbox"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[EmailOutbox];"),
            ["hold_placement_operations"] = 0,
            ["invalid_active_claim_rules"] = ScalarWithAllowedTenants(connection,
                """
                SELECT COUNT(*)
                FROM [asap].[FormatAutoClaimRule] r
                LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[StaffUserId]
                WHERE r.[IsActive] = 1 AND
                      (s.[Id] IS NULL OR s.[IsActive] = 0 OR s.[EntraTenantId] IS NULL OR s.[EntraObjectId] IS NULL OR
                       s.[EntraTenantId] NOT IN ({allowedTenants}) OR
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
                  AND (s.[Id] IS NULL OR s.[IsActive] = 0 OR s.[EntraTenantId] IS NULL OR s.[EntraObjectId] IS NULL OR
                       s.[EntraTenantId] NOT IN ({allowedTenants}) OR
                       NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR
                            (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));
                """, allowedTenantIds),
            ["invalid_found_requests"] = Scalar(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] = N'found' AND NULLIF(LTRIM(RTRIM([BibId])), N'') IS NULL;")
        };
        if (counts["staff_users"] != importedCounts["staff_users"] + importedCounts.GetValueOrDefault("migration_bootstrap_staff_users") ||
            counts["format_auto_claim_rules"] != importedCounts.GetValueOrDefault("format_claim_rules") ||
            counts["title_requests"] != importedCounts.GetValueOrDefault("title_requests") ||
            counts["title_request_events"] != importedCounts.GetValueOrDefault("title_request_events") + importedCounts.GetValueOrDefault("placed_bib_protection_markers") ||
            counts["placed_bib_protection_markers"] != importedCounts.GetValueOrDefault("placed_bib_protection_markers") ||
            counts["patron_sessions"] != 0 || counts["email_outbox"] != 0 ||
            counts["invalid_active_claim_rules"] != 0 || counts["invalid_open_title_request_claims"] != 0 ||
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
        IReadOnlyCollection<PlacementTransformation> placementTransformations,
        MigrationSemanticReconciliation semanticReconciliation)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new
        {
            reportVersion = 2,
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
                    })
            },
            placementReconciliation = new
            {
                sourceRequestsEvaluated = placementTransformations.Count,
                protectedRequests = placementTransformations.Count(item => item.Action == "inserted"),
                knownBibMarkers = placementTransformations.Count(item => item.Action == "inserted" && item.BibId is not null),
                explicitNullBibMarkers = placementTransformations.Count(item => item.Action == "inserted" && item.BibId is null),
                noPlacementEvidence = placementTransformations.Count(item => item.Action == "no_placement_evidence"),
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
                evidenceClasses = placementTransformations
                    .SelectMany(item => item.Evidence)
                    .GroupBy(item => item.Kind, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { kind = group.Key, count = group.Count() }),
                terminalReasons = placementTransformations
                    .SelectMany(item => item.Evidence)
                    .Where(item => item.Kind is "terminal_close_reason" or "event_terminal_reason")
                    .GroupBy(item => item.Value, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new { reason = group.Key, count = group.Count() })
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

    private static int ResolveOrganizationId(
        SourceRow row,
        string field,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var sourceValue = row.RequiredString(field);
        if (organizationIds.TryGetValue(sourceValue, out var mapped)) return mapped;
        if (int.TryParse(sourceValue, out var organizationId) && organizationId > 0) return organizationId;
        throw new MigrationOperationException("organization_reference_invalid", $"Source organization reference {sourceValue} cannot be resolved.");
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
        if (relation is not null && formatIds.TryGetValue(relation, out var mapped)) return mapped;

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
        var sourceType = row.String("claimType");
        var sourceRuleId = row.String("claimRuleId");
        var mappedRuleId = ResolveOptionalMapping(sourceRuleId, claimRuleIds);
        if (sourceClaimantId is null)
        {
            return new(null, null, null, null, null, null, null, "unclaimed");
        }

        if (status == "closed")
        {
            if (displayName is null || claimedAt is null || sourceType is null)
            {
                return new(sourceClaimantId, mappedStaffId, null, null, null, null, null, "closed_attribution_incomplete");
            }
            var historicalType = sourceType switch
            {
                "manual" => "manual",
                "automatic_format_rule" when mappedStaffId is not null && mappedRuleId is not null => "automatic_format_rule",
                "automatic_format_rule" => "legacy",
                _ => throw new MigrationOperationException("claim_type_invalid", $"Unknown claim type: {sourceType}")
            };
            return new(
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
            return new(sourceClaimantId, null, null, null, null, null, null, "claimant_unmapped");
        }
        if (displayName is null || claimedAt is null || sourceType is null)
        {
            return new(sourceClaimantId, mappedStaffId, null, null, null, null, null, "claim_metadata_incomplete");
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
            return new(sourceClaimantId, mappedStaffId, null, null, null, null, null, eligibility);
        }
        if (sourceType == "manual")
        {
            return new(sourceClaimantId, mappedStaffId, mappedStaffId, displayName, claimedAt, "manual", null, "eligible");
        }
        if (sourceType != "automatic_format_rule")
        {
            throw new MigrationOperationException("claim_type_invalid", $"Unknown claim type: {sourceType}");
        }
        if (mappedRuleId is null || !ActiveRuleMatches(
                connection,
                transaction,
                mappedRuleId.Value,
                libraryId,
                formatId,
                mappedStaffId.Value))
        {
            return new(sourceClaimantId, mappedStaffId, null, null, null, null, null, "claim_rule_inactive_or_mismatched");
        }
        return new(
            sourceClaimantId,
            mappedStaffId,
            mappedStaffId,
            displayName,
            claimedAt,
            "automatic_format_rule",
            mappedRuleId,
            "eligible");
    }

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
            "SELECT [IsActive], [Role], [OrganizationId], [EntraTenantId], [EntraObjectId] FROM [asap].[StaffUser] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", staffUserId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return "claimant_unmapped";
        if (!reader.GetBoolean(0)) return "claimant_inactive";
        var role = reader.GetString(1);
        var organizationId = reader.GetInt32(2);
        if (reader.IsDBNull(3) || reader.IsDBNull(4) ||
            !allowedTenantIds.Contains(reader.GetGuid(3)) || reader.GetGuid(4) == Guid.Empty)
        {
            throw new MigrationOperationException("active_staff_identity_invalid", $"Active staff user {staffUserId} has an invalid target identity.");
        }
        return role switch
        {
            "super_admin" when organizationId == 1 => "eligible",
            "staff" or "admin" when organizationId == libraryId => "eligible",
            _ => "claimant_out_of_scope"
        };
    }

    private static bool ActiveRuleMatches(
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
              AND [StaffUserId] = @staffId AND [IsActive] = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", ruleId);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        command.Parameters.AddWithValue("@formatId", formatId);
        command.Parameters.AddWithValue("@staffId", staffId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static string NormalizeFormatCode(string value) => value.Trim() switch
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

    private static string NormalizeStatus(string value) => value.Trim() switch
    {
        "0" or "suggestion" => "suggestion",
        "1" or "5" or "pending_hold" or "pendingHold" => "pending_hold",
        "2" or "hold_placed" or "holdPlaced" => "hold_placed",
        "3" or "outstanding_purchase" or "outstandingPurchase" => "outstanding_purchase",
        "4" or "closed" => "closed",
        var invalid => throw new MigrationOperationException("request_status_invalid", $"Unknown request status: {invalid}")
    };

    private static string? NormalizeCloseReason(string? value) => value switch
    {
        null => null,
        "rejected" or "reject" => "rejected",
        "hold_completed" or "holdCompleted" or "hold_placed" or "checkout" or "checked_out" => "hold_completed",
        "hold_not_picked_up" => "hold_not_picked_up",
        "hold_unclaimed" or "unclaimed" => "hold_unclaimed",
        "hold_cancelled" or "cancelled" => "hold_cancelled",
        "hold_expired" or "expired" => "hold_expired",
        "duplicate_hold" => "duplicate_hold",
        "manual" => "manual",
        "purchased_no_hold" or "purchased_no_hold_purchase_outcome" => "purchased_no_hold",
        "silent" or "Silently Closed" => "Silently Closed",
        var invalid => throw new MigrationOperationException("request_close_reason_invalid", $"Unknown request close reason: {invalid}")
    };

    private static string? ResolveEventStatus(
        string? sourceValue,
        IReadOnlyDictionary<string, string> statuses)
    {
        if (sourceValue is null) return null;
        return statuses.TryGetValue(sourceValue, out var code) ? code : NormalizeStatus(sourceValue);
    }

    private static IReadOnlyDictionary<string, string> BuildStatusMap(IReadOnlyList<SourceRow> rows) =>
        rows.ToDictionary(
            row => row.RequiredString("id"),
            row => NormalizeStatus(row.RequiredString("code")),
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> BuildCloseReasonMap(IReadOnlyList<SourceRow> rows) =>
        rows.ToDictionary(
            row => row.RequiredString("id"),
            row => NormalizeCloseReason(row.RequiredString("code"))!,
            StringComparer.Ordinal);

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
        string? bibId) => sourceStatus switch
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
        if (allowed.Contains(value, StringComparer.Ordinal)) return value;
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
        string SourceClaimantId,
        long? MappedStaffUserId,
        long? EffectiveStaffUserId,
        string? SourceDisplayName,
        DateTime? SourceClaimedAtUtc,
        string? SourceClaimType,
        string? SourceClaimRuleId,
        string Reason);

    private sealed record SourceEvent(
        string Id,
        string EventType,
        string? FromStatus,
        string? ToStatus,
        string? CloseReason,
        string? BibId);

    private sealed record SourceTemplate(SourceRow Row, bool IsRejection);

    private sealed record PlacementEvidence(
        string Kind,
        string SourceCollection,
        string SourceRecordId,
        string SourceField,
        string Value);

    private sealed record PlacementTransformation(
        string SourceId,
        int LibraryOrganizationId,
        string Status,
        string? BibId,
        IReadOnlyList<PlacementEvidence> Evidence,
        string Action);

    private sealed record ConfigurationSourceFields(
        string File,
        string Collection,
        IReadOnlySet<string> Known,
        IReadOnlySet<string> IntentionalDrops);

    private sealed record MigrationSemanticReconciliation(
        int organizations,
        int staffUsers,
        int titleRequests,
        int brandingAssets,
        bool passed);

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

    private sealed class StaffIdentityMap
    {
        public List<StaffIdentity> Users { get; init; } = [];
    }

    private sealed class StaffIdentity
    {
        public string PocketBaseStaffUserId { get; init; } = string.Empty;
        public Guid TenantId { get; init; }
        public Guid ObjectId { get; init; }
        public string UserPrincipalName { get; init; } = string.Empty;
        public string? DisplayName { get; init; }
        public string? NotificationEmail { get; init; }
    }

    private sealed record BootstrapIdentity(
        Guid TenantId,
        Guid ObjectId,
        string UserPrincipalName,
        string DisplayName,
        string NotificationEmail);
}
