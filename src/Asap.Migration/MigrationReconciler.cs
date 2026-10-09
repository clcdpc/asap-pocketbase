using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

public sealed record MigrationReconcileOptions(
    string PackagePath,
    string ConnectionString,
    string ImportReportPath,
    string? ExternalConfigurationPath = null);

public static class MigrationReconciler
{
    private static readonly string[] ImportedCountFields =
    [
        "branding", "system_settings", "polaris_settings", "smtp_settings", "workflow_settings",
        "ui_settings", "patron_settings_overrides", "patron_library_settings", "library_settings",
        "email_templates", "rejection_templates", "workflow_tags", "polaris_organizations", "staff_users",
        "material_formats", "format_claim_rules", "title_requests", "additional_copy_requests",
        "deleted_request_audit", "title_request_tags", "title_request_events", "email_delivery_events",
        "migration_bootstrap_staff_users", "claim_migration_annotations", "placed_bib_protection_markers",
        "additional_copy_claim_migration_annotations", "title_request_bib_authority_automation_derived",
        "title_request_bib_authority_ambiguous_safe", "title_request_bib_authority_staff_authoritative"
    ];

    private static readonly string[] TargetCountFields =
    [
        "organizations", "staff_users", "format_auto_claim_rules", "title_requests",
        "additional_copy_requests", "deleted_request_audit", "title_request_events", "legacy_mappings",
        "patron_sessions", "email_outbox", "queue_progress", "hold_placement_operations",
        "claim_migration_annotations", "placed_bib_protection_markers",
        "additional_copy_claim_migration_annotations", "invalid_active_claim_rules",
        "invalid_open_title_request_claims", "invalid_open_additional_copy_claims", "invalid_found_requests",
        "title_request_bibs_automation_derived", "title_request_bibs_ambiguous_unverified",
        "title_request_bibs_staff_authoritative"
    ];

    private static readonly (string Name, string File, string Collection)[] SourceCountCollections =
    [
        ("branding", "branding.json", "branding"),
        ("system_settings", "system-settings.json", "system_settings"),
        ("polaris_settings", "polaris-settings.json", "polaris_settings"),
        ("smtp_settings", "email-settings.json", "smtp_settings"),
        ("workflow_settings", "workflow-settings.json", "workflow_settings"),
        ("ui_settings", "patron-settings.json", "ui_settings"),
        ("patron_settings_overrides", "patron-settings.json", "patron_settings_overrides"),
        ("patron_library_settings", "patron-settings.json", "patron_library_settings"),
        ("library_settings", "patron-settings.json", "library_settings"),
        ("email_templates", "email-templates.json", "email_templates"),
        ("rejection_templates", "email-templates.json", "rejection_templates"),
        ("workflow_tags", "workflow-tags.json", "workflow_tags"),
        ("polaris_organizations", "organizations.json", "polaris_organizations"),
        ("staff_users", "staff-users.json", "staff_users"),
        ("material_formats", "material-formats.json", "material_formats"),
        ("format_claim_rules", "format-auto-claim-rules.json", "format_claim_rules"),
        ("title_requests", "title-requests.json", "title_requests"),
        ("additional_copy_requests", "additional-copy-requests.json", "additional_copy_requests"),
        ("deleted_request_audit", "deleted-request-audit.json", "deleted_request_audit"),
        ("title_request_tags", "title-request-tags.json", "title_request_tags"),
        ("title_request_events", "title-request-events.json", "title_request_events"),
        ("email_delivery_events", "email-delivery-events.json", "email_delivery_events")
    ];

    public static void Reconcile(MigrationReconcileOptions options)
    {
        var package = MigrationPackageValidator.Validate(options.PackagePath);
        MigrationOperationalConfiguration.ValidateRequired(package, options.ExternalConfigurationPath);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new MigrationOperationException(
                "target_connection_missing",
                "The target SQL connection environment value is missing.");
        }
        if (!File.Exists(options.ImportReportPath))
        {
            throw new MigrationOperationException(
                "reconciliation_report_missing",
                "The restricted import reconciliation report does not exist.");
        }

        try
        {
            using var report = ReadReportDocument(options.ImportReportPath);
            var root = report.RootElement;
            ValidateReportPackage(root, package, allowPending: false);
            VerifyReportTarget(root, package, options.ConnectionString);
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new MigrationOperationException(
                "reconciliation_report_invalid",
                "The restricted import reconciliation report is invalid.");
        }
    }

    public static void RecoverReport(MigrationReconcileOptions options)
    {
        var package = MigrationPackageValidator.Validate(options.PackagePath);
        MigrationOperationalConfiguration.ValidateRequired(package, options.ExternalConfigurationPath);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new MigrationOperationException(
                "target_connection_missing",
                "The target SQL connection environment value is missing.");
        }

        var reportPath = Path.GetFullPath(options.ImportReportPath);
        var pendingPath = reportPath + ".pending";
        if (File.Exists(reportPath))
        {
            using var completed = ReadReportDocument(reportPath);
            ValidateReportPackage(completed.RootElement, package, allowPending: false);
            VerifyReportTarget(completed.RootElement, package, options.ConnectionString);
            return;
        }
        if (!File.Exists(pendingPath))
        {
            throw new MigrationOperationException(
                "reconciliation_report_pending_missing",
                "No prepared import report exists to recover; import was not confirmed as committed.");
        }

        using (var pending = ReadReportDocument(pendingPath))
        {
            ValidateReportPackage(pending.RootElement, package, allowPending: true);
            VerifyReportTarget(pending.RootElement, package, options.ConnectionString);
        }

        SetReportState(pendingPath, "recovered", reconciliationPassed: true);
        File.Move(pendingPath, reportPath, overwrite: true);
    }

    internal static void FinalizeCommittedReport(
        string connectionString,
        ValidatedMigrationPackage package,
        string pendingPath,
        string reportPath)
    {
        using var pending = ReadReportDocument(pendingPath);
        ValidateReportPackage(pending.RootElement, package, allowPending: true);
        VerifyReportTarget(pending.RootElement, package, connectionString);
        SetReportState(pendingPath, "committed", reconciliationPassed: true);
        File.Move(pendingPath, reportPath, overwrite: true);
    }

    private static void VerifyReportTarget(JsonElement report, ValidatedMigrationPackage package, string connectionString)
    {
        var expectedTargetIdentity = report.GetProperty("targetIdentitySha256").GetString();
        var actualTargetIdentity = ComputeTargetIdentitySha256(connectionString);
        if (string.IsNullOrWhiteSpace(expectedTargetIdentity) ||
            !string.Equals(expectedTargetIdentity, actualTargetIdentity, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException(
                "reconciliation_report_target_mismatch",
                "The reconciliation report belongs to a different SQL Server database target.");
        }

        var tokenTransformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "email_provider_token")
            .ToArray();
        if (tokenTransformations.Length != 1 ||
            !HasPositiveInt32(tokenTransformations[0], "organizationId") ||
            tokenTransformations[0].GetProperty("organizationId").GetInt32() != 1 ||
            !HasBoolean(tokenTransformations[0], "postmarkTokenProvisioned"))
        {
            throw new MigrationOperationException(
                "reconciliation_report_mismatch",
                "The report does not identify the bounded system Postmark-token presence state.");
        }
        var postmarkTokenProvisioned = tokenTransformations[0].GetProperty("postmarkTokenProvisioned").GetBoolean();

        var bootstrapTransformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin")
            .ToArray();
        long? bootstrapTargetStaffUserId = null;
        var bootstrapInserted = false;
        if (bootstrapTransformations.Length == 1)
        {
            var bootstrap = bootstrapTransformations[0];
            bootstrapTargetStaffUserId = bootstrap.GetProperty("targetStaffUserId").GetInt64();
            bootstrapInserted = bootstrap.GetProperty("action").GetString() == "inserted";
        }

        MigrationIndependentVerifier.Verify(
            connectionString,
            package,
            postmarkTokenProvisioned,
            bootstrapTargetStaffUserId,
            bootstrapInserted);
        var expectedQueueProgress = report.GetProperty("targetCounts").GetProperty("queue_progress").GetInt32();
        var actualQueueProgress = ReadQueueProgressCount(connectionString);
        if (actualQueueProgress != expectedQueueProgress)
        {
            throw new MigrationOperationException(
                "reconciliation_failed",
                $"QueueProgress runtime count changed: report={expectedQueueProgress}, target={actualQueueProgress}.");
        }

        var expectedFingerprint = report.GetProperty("targetFingerprintSha256").GetString();
        var actualFingerprint = ComputeTargetFingerprint(connectionString);
        if (string.IsNullOrWhiteSpace(expectedFingerprint) ||
            !string.Equals(expectedFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException(
                "reconciliation_failed",
                "Target SQL state changed after the successful import reconciliation.");
        }

        VerifyReportCounts(report, package, connectionString);
    }

    private static void ValidateReportPackage(JsonElement root, ValidatedMigrationPackage package, bool allowPending)
    {
        try
        {
            ValidateReportShape(root);
            ValidateReportAudit(root, package);
            var state = root.GetProperty("reportState").GetString();
            var passed = root.GetProperty("reconciliationPassed").GetBoolean();
            var exportedAtUtc = DateTimeOffset.Parse(
                root.GetProperty("exportedAtUtc").GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None);
            var stateValid = (state is "committed" or "recovered") && passed ||
                allowPending && state == "commit_pending" && !passed;
            if (root.GetProperty("reportVersion").GetInt32() != 6 ||
                !stateValid ||
                !string.Equals(root.GetProperty("sourceGitSha").GetString(), package.Manifest.PocketBaseSourceGitSha, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("sourceSchemaVersion").GetString(), package.Manifest.PocketBaseSourceSchemaVersion, StringComparison.Ordinal) ||
                exportedAtUtc != package.Manifest.ExportedAtUtc ||
                !string.Equals(
                    root.GetProperty("packageIdentitySha256").GetString(),
                    MigrationPackageValidator.ComputePackageIdentitySha256(package),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException(
                    "reconciliation_report_mismatch",
                    "The reconciliation report does not identify this immutable export package and expected commit state.");
            }
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new MigrationOperationException(
                "reconciliation_report_invalid",
                "The restricted import reconciliation report is malformed.");
        }
    }

    private static void ValidateReportShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidReport("The restricted import report must be a JSON object.");
        }
        MigrationPackageValidator.EnsureNoDuplicateProperties(root, "reconciliation_report_invalid");
        if (
            !HasString(root, "reportState") ||
            !HasBoolean(root, "reconciliationPassed") ||
            !HasInt32(root, "reportVersion") ||
            !HasString(root, "sourceGitSha") ||
            !HasString(root, "sourceSchemaVersion") ||
            !HasDateTimeOffset(root, "exportedAtUtc") ||
            !HasSha256(root, "packageIdentitySha256") ||
            !HasSha256(root, "targetFingerprintSha256") ||
            !HasSha256(root, "targetIdentitySha256") ||
            !HasCountObject(root, "importedCounts", ImportedCountFields) ||
            !HasCountObject(root, "targetCounts", TargetCountFields) ||
            !root.TryGetProperty("transformations", out var transformations) ||
            transformations.ValueKind != JsonValueKind.Array ||
            !HasArrayProperties(root, "claimReconciliation", "titleRequests", "additionalCopies") ||
            !HasPlacementReconciliationShape(root) ||
            !HasBibAuthorityReconciliationShape(root) ||
            !HasSourceToTargetReconciliationShape(root))
        {
            throw InvalidReport("The restricted import report is malformed or has an unsupported shape.");
        }

        foreach (var transformation in transformations.EnumerateArray())
        {
            if (transformation.ValueKind != JsonValueKind.Object || !HasTransformationEntryShape(transformation))
            {
                throw InvalidReport("The restricted import report contains an incomplete or unsupported transformation entry.");
            }
        }
    }

    private static bool HasTransformationEntryShape(JsonElement item)
    {
        if (!HasString(item, "entity"))
        {
            return false;
        }

        return item.GetProperty("entity").GetString() switch
        {
            "source_field_accounting" => HasExactProperties(item, "entity", "collection", "sourceId", "intentionallyDroppedFields") &&
                HasString(item, "collection") && HasNullableString(item, "sourceId") && HasStringArray(item, "intentionallyDroppedFields"),
            "operational_configuration" => HasExactProperties(item, "entity", "status", "matchedSchedules", "matchedQueues", "retiredObsoleteOverrides") &&
                HasString(item, "status") && HasNonNegativeInt32(item, "matchedSchedules") &&
                HasNonNegativeInt32(item, "matchedQueues") && HasNonNegativeInt32(item, "retiredObsoleteOverrides"),
            "branding" => HasExactProperties(item, "entity", "sourceId", "organizationId", "assetSha256", "length", "contentType") &&
                HasString(item, "sourceId") && HasPositiveInt32(item, "organizationId") && HasSha256(item, "assetSha256") &&
                HasNonNegativeInt64(item, "length") && HasString(item, "contentType"),
            "system_settings_effective_runtime" => HasExactProperties(item, "entity", "sourceId", "staffApplicationUrlSource", "materialTypeIconUrlPatternSource", "misconfiguredMessage", "misconfiguredMessageReason") &&
                HasNullableString(item, "sourceId") && HasString(item, "staffApplicationUrlSource") &&
                HasString(item, "materialTypeIconUrlPatternSource") && HasString(item, "misconfiguredMessage") && HasString(item, "misconfiguredMessageReason"),
            "polaris_settings" => HasExactProperties(item, "entity", "sourceId", "apiKeyProtected", "adminPasswordProtected", "retiredRequestingOrganizationSource", "retiredPickupOrganizationSource", "operationContextSource") &&
                HasString(item, "sourceId") && HasBoolean(item, "apiKeyProtected") && HasBoolean(item, "adminPasswordProtected") &&
                HasNullableString(item, "retiredRequestingOrganizationSource") && HasNullableString(item, "retiredPickupOrganizationSource") &&
                HasString(item, "operationContextSource"),
            "staff_user" => HasExactProperties(item, "entity", "sourceId", "notificationEmailSource", "sourceAssignmentRecipient", "sourcePurchaseReminderRecipient", "sourceAdditionalCopyReminderRecipient", "sourceWeeklyRecipient", "targetAssignmentRecipient", "targetPurchaseReminderRecipient", "targetAdditionalCopyReminderRecipient", "targetWeeklyRecipient", "assignmentRecipientChanged", "purchaseReminderRecipientChanged", "additionalCopyReminderRecipientChanged", "weeklyRecipientChanged", "sourceWeeklyEligible", "targetWeeklyEligible", "newlyWeeklyEligible", "targetHasNotificationEmail", "targetHasWeeklyRecipient") &&
                HasString(item, "sourceId") && HasString(item, "notificationEmailSource") &&
                HasNullableString(item, "sourceAssignmentRecipient") && HasNullableString(item, "sourcePurchaseReminderRecipient") &&
                HasNullableString(item, "sourceAdditionalCopyReminderRecipient") && HasNullableString(item, "sourceWeeklyRecipient") &&
                HasNullableString(item, "targetAssignmentRecipient") && HasNullableString(item, "targetPurchaseReminderRecipient") &&
                HasNullableString(item, "targetAdditionalCopyReminderRecipient") && HasNullableString(item, "targetWeeklyRecipient") &&
                HasBoolean(item, "assignmentRecipientChanged") && HasBoolean(item, "purchaseReminderRecipientChanged") &&
                HasBoolean(item, "additionalCopyReminderRecipientChanged") && HasBoolean(item, "weeklyRecipientChanged") &&
                HasBoolean(item, "sourceWeeklyEligible") && HasBoolean(item, "targetWeeklyEligible") &&
                HasBoolean(item, "newlyWeeklyEligible") && HasBoolean(item, "targetHasNotificationEmail") && HasBoolean(item, "targetHasWeeklyRecipient"),
            "email_settings" => HasExactProperties(item, "entity", "sourceId", "transport", "targetTransport", "postmarkTokenProvisioned") &&
                HasString(item, "sourceId") && HasString(item, "transport") && HasString(item, "targetTransport") && HasBoolean(item, "postmarkTokenProvisioned"),
            "email_provider_token" => HasExactProperties(item, "entity", "organizationId", "postmarkTokenProvisioned") &&
                HasPositiveInt32(item, "organizationId") && HasBoolean(item, "postmarkTokenProvisioned"),
            "email_template_sender" => HasExactProperties(item, "entity", "sourceIds", "organizationId", "fromAddress", "fromName", "disposition") &&
                HasStringArray(item, "sourceIds") && HasPositiveInt32(item, "organizationId") && HasNullableString(item, "fromAddress") &&
                HasNullableString(item, "fromName") && HasString(item, "disposition"),
            "email_template" => HasExactProperties(item, "entity", "sourceId", "transformation") &&
                HasString(item, "sourceId") && HasString(item, "transformation"),
            "patron_duplicate_labels" => HasPatronDuplicateLabelsShape(item),
            "system_material_format_availability" => HasExactProperties(item, "entity", "unavailableSeedCodes") &&
                HasStringArray(item, "unavailableSeedCodes"),
            "legacy_library_branding" => HasExactProperties(item, "entity", "sourceCollection", "sourceId", "organizationId", "disposition", "populatedFields") &&
                HasString(item, "sourceCollection") && HasNullableString(item, "sourceId") && HasPositiveInt32(item, "organizationId") &&
                HasString(item, "disposition") && HasStringArray(item, "populatedFields"),
            "patron_format_rules" => HasExactProperties(item, "entity", "organizationId", "customFields", "formats") &&
                HasPositiveInt32(item, "organizationId") && HasNonNegativeInt32(item, "customFields") && HasStringArray(item, "formats"),
            "patron_custom_fields" => HasExactProperties(item, "entity", "organizationId", "fields", "formatRules", "absentOrDisabledRule", "requiredSelectWithoutEnabledOptions", "downgradedRequiredSelectRules") &&
                HasPositiveInt32(item, "organizationId") && HasNonNegativeInt32(item, "fields") && HasNonNegativeInt32(item, "formatRules") &&
                HasString(item, "absentOrDisabledRule") && HasString(item, "requiredSelectWithoutEnabledOptions") &&
                HasNonNegativeInt32(item, "downgradedRequiredSelectRules"),
            "format_auto_claim_rule" => HasExactProperties(item, "entity", "sourceId", "sourceStaffUserId", "targetStaffUserId", "sourceActive", "targetActive", "reason") &&
                HasString(item, "sourceId") && HasNullableString(item, "sourceStaffUserId") && HasNullablePositiveInt64(item, "targetStaffUserId") &&
                HasBoolean(item, "sourceActive") && HasBoolean(item, "targetActive") && HasString(item, "reason"),
            "title_request_isbn_status" => HasExactProperties(item, "entity", "sourceId", "sourceStatus", "targetStatus", "reason") &&
                HasString(item, "sourceId") && HasNullableString(item, "sourceStatus") && HasNullableString(item, "targetStatus") && HasString(item, "reason"),
            "title_request_claim" => HasExactProperties(item, "entity", "sourceId", "sourceClaimantId", "mappedStaffUserId", "effectiveStaffUserId", "sourceDisplayName", "sourceClaimedAtUtc", "sourceClaimType", "sourceClaimRuleId", "reason", "migrationAnnotationInserted") &&
                HasString(item, "sourceId") && HasNullableString(item, "sourceClaimantId") && HasNullablePositiveInt64(item, "mappedStaffUserId") &&
                HasNullablePositiveInt64(item, "effectiveStaffUserId") && HasNullableString(item, "sourceDisplayName") &&
                HasNullableDateTimeOffset(item, "sourceClaimedAtUtc") && HasNullableString(item, "sourceClaimType") &&
                HasNullableString(item, "sourceClaimRuleId") && HasString(item, "reason") && HasBoolean(item, "migrationAnnotationInserted"),
            "additional_copy_claim" => HasExactProperties(item, "entity", "sourceId", "sourceClaimantId", "mappedStaffUserId", "effectiveStaffUserId", "sourceDisplayName", "sourceClaimedAtUtc", "reason", "migrationAnnotationInserted") &&
                HasString(item, "sourceId") && HasNullableString(item, "sourceClaimantId") && HasNullablePositiveInt64(item, "mappedStaffUserId") &&
                HasNullablePositiveInt64(item, "effectiveStaffUserId") && HasNullableString(item, "sourceDisplayName") &&
                HasNullableDateTimeOffset(item, "sourceClaimedAtUtc") && HasString(item, "reason") && HasBoolean(item, "migrationAnnotationInserted"),
            "additional_copy_updated_timestamp" => HasExactProperties(item, "entity", "sourceId", "sourceUpdatedUtc", "targetUpdatedUtc", "reason") &&
                HasString(item, "sourceId") && HasNullableDateTimeOffset(item, "sourceUpdatedUtc") &&
                HasDateTimeOffset(item, "targetUpdatedUtc") && HasString(item, "reason"),
            "deleted_request_audit" => HasExactProperties(item, "entity", "sourceRows", "targetRows", "reason") &&
                HasNonNegativeInt32(item, "sourceRows") && HasNonNegativeInt32(item, "targetRows") && HasString(item, "reason"),
            "placed_bib_protection" => HasExactProperties(item, "entity", "sourceId", "libraryOrganizationId", "status", "bibId", "evidence", "hints", "bibSources", "action") &&
                HasString(item, "sourceId") && HasPositiveInt32(item, "libraryOrganizationId") && HasString(item, "status") &&
                HasNullablePositiveInt32(item, "bibId") && HasStringObjectArray(item, "evidence", "kind", "sourceCollection", "sourceRecordId", "sourceField", "value") &&
                HasStringObjectArray(item, "hints", "kind", "sourceCollection", "sourceRecordId", "sourceField", "value") &&
                HasBibSourceArray(item, "bibSources") && HasString(item, "action"),
            "title_request_bib_authority" => HasExactProperties(item, "entity", "sourceId", "bibId", "requestStatus", "sourceIsbnCheckStatus", "classification", "bibIdStaffVerified", "placedHistoryProtected", "outcome") &&
                HasString(item, "sourceId") && HasPositiveInt32(item, "bibId") && HasString(item, "requestStatus") &&
                HasNullableString(item, "sourceIsbnCheckStatus") && HasString(item, "classification") &&
                HasBoolean(item, "bibIdStaffVerified") && HasBoolean(item, "placedHistoryProtected") && HasString(item, "outcome") &&
                item.GetProperty("outcome").GetString() == "imported_without_staff_authority",
            "migration_bootstrap_super_admin" => HasExactProperties(item, "entity", "action", "targetStaffUserId", "authenticationEmail", "appliedAtUtc") &&
                HasString(item, "action") && HasPositiveInt64(item, "targetStaffUserId") && HasString(item, "authenticationEmail") &&
                HasDateTimeOffset(item, "appliedAtUtc"),
            _ => false
        };
    }

    private static bool HasPatronDuplicateLabelsShape(JsonElement item)
    {
        if (!HasString(item, "sourceCollection"))
        {
            return false;
        }

        var sourceCollection = item.GetProperty("sourceCollection").GetString();
        return sourceCollection switch
        {
            "ui_settings" => HasExactProperties(item, "entity", "sourceCollection", "sourceId", "organizationId", "disposition", "populatedFields") &&
                HasNullableString(item, "sourceId") && HasPositiveInt32(item, "organizationId") && HasString(item, "disposition") && HasStringArray(item, "populatedFields"),
            "patron_library_settings" => HasExactProperties(item, "entity", "sourceCollection", "sourceId", "organizationId", "disposition") &&
                HasNullableString(item, "sourceId") && HasPositiveInt32(item, "organizationId") && HasString(item, "disposition"),
            _ => false
        };
    }

    private static bool HasExactProperties(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var actual = item.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return actual.Count == names.Length && actual.SetEquals(names);
    }

    private static bool HasNullableString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static bool NullableJsonStringEquals(JsonElement parent, string name, string? expected)
    {
        var actual = parent.GetProperty(name);
        return expected is null
            ? actual.ValueKind == JsonValueKind.Null
            : actual.ValueKind == JsonValueKind.String && string.Equals(actual.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool HasNullablePositiveInt32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) && parsed > 0);

    private static bool HasPositiveInt32(JsonElement parent, string name) =>
        HasInt32(parent, name) && parent.GetProperty(name).GetInt32() > 0;

    private static bool HasNonNegativeInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) && parsed >= 0;

    private static bool HasNullablePositiveInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) && parsed > 0);

    private static bool HasNullableDateTimeOffset(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    private static bool HasStringArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array &&
        value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);

    private static bool HasStringObjectArray(JsonElement parent, string name, params string[] stringProperties)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        return value.EnumerateArray().All(item => HasExactProperties(item, stringProperties) && stringProperties.All(property => HasString(item, property)));
    }

    private static bool HasBibSourceArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        return value.EnumerateArray().All(item =>
            HasExactProperties(item, "sourceCollection", "sourceRecordId", "sourceField", "bibId", "sourceValue") &&
            HasString(item, "sourceCollection") && HasString(item, "sourceRecordId") && HasString(item, "sourceField") &&
            HasPositiveInt32(item, "bibId") && HasString(item, "sourceValue"));
    }

    private static bool HasArrayProperties(JsonElement parent, string objectName, params string[] arrayNames)
    {
        if (!parent.TryGetProperty(objectName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return arrayNames.All(name => value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array);
    }

    private static bool HasPlacementReconciliationShape(JsonElement root)
    {
        if (!root.TryGetProperty("placementReconciliation", out var placement) || placement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return new[]
            {
                "sourceRequestsEvaluated", "protectedRequests", "knownBibMarkers", "explicitNullBibMarkers",
                "noPlacementEvidence", "placementHistoryAmbiguous", "insertedMarkers", "reusedMarkers",
                "fabricatedHoldPlacementOperations"
            }.All(name => HasNonNegativeInt32(placement, name)) &&
            HasArray(placement, "byLibraryAndStatus") &&
            HasArray(placement, "evidenceClasses") &&
            HasArray(placement, "terminalReasons");
    }

    private static bool HasBibAuthorityReconciliationShape(JsonElement root)
    {
        if (!root.TryGetProperty("bibAuthorityReconciliation", out var authority) || authority.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return new[]
            {
                "sourceRequestsWithBibId", "automationDerivedBibs", "staffAuthoritativeBibs",
                "ambiguousBibsImportedWithoutStaffAuthority", "blockedRiskBibs"
            }.All(name => HasNonNegativeInt32(authority, name)) &&
            HasArray(authority, "byClassification");
    }

    private static bool HasSourceToTargetReconciliationShape(JsonElement root)
    {
        if (!root.TryGetProperty("sourceToTargetReconciliation", out var reconciliation) || reconciliation.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return new[]
            {
                "organizations", "staffUsers", "titleRequests", "additionalCopies", "deletedRequestAudits",
                "brandingAssets", "configurationRowsChecked", "configurationFieldsChecked",
                "configurationRelationshipsChecked", "workflowTagsChecked", "titleRequestTagsChecked",
                "historyRowsChecked"
            }.All(name => HasNonNegativeInt32(reconciliation, name)) &&
            HasBoolean(reconciliation, "passed");
    }

    private static bool HasArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;

    private static bool HasNonNegativeInt32(JsonElement parent, string name) =>
        HasInt32(parent, name) && parent.GetProperty(name).GetInt32() >= 0;

    private static MigrationOperationException InvalidReport(string message) =>
        new("reconciliation_report_invalid", message);

    private static JsonDocument ReadReportDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            throw new MigrationOperationException(
                "reconciliation_report_invalid",
                "The restricted import report is not valid JSON.");
        }
    }

    private static bool HasString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static bool HasBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool HasInt32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);

    private static bool HasDateTimeOffset(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool HasSha256(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: 64 } hash && hash.All(Uri.IsHexDigit);

    private static bool HasCountObject(JsonElement parent, string name, IReadOnlyCollection<string> requiredFields)
    {
        if (!parent.TryGetProperty(name, out var counts) || counts.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var field in requiredFields)
        {
            if (!HasInt32(counts, field))
            {
                return false;
            }
        }
        var allowed = requiredFields.ToHashSet(StringComparer.Ordinal);
        return counts.EnumerateObject().All(property => allowed.Contains(property.Name) &&
            property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var count) && count >= 0);
    }

    private static Dictionary<string, int> ReadSourceCounts(ValidatedMigrationPackage package) =>
        SourceCountCollections.ToDictionary(
            item => item.Name,
            item => MigrationPackageReader.ReadRows(package, item.File, item.Collection).Count,
            StringComparer.Ordinal);

    private static void ValidateReportAudit(JsonElement report, ValidatedMigrationPackage package)
    {
        var sourceCounts = ReadSourceCounts(package);
        var imported = report.GetProperty("importedCounts");
        foreach (var sourceCount in sourceCounts)
        {
            if (imported.GetProperty(sourceCount.Key).GetInt32() != sourceCount.Value)
            {
                throw ReportMismatch($"Import report count {sourceCount.Key} does not match the immutable source package.");
            }
        }

        var transformations = report.GetProperty("transformations").EnumerateArray().ToArray();
        var operational = transformations
            .Where(item => item.GetProperty("entity").GetString() == "operational_configuration")
            .ToArray();
        if (operational.Length != 1)
        {
            throw ReportMismatch("The report must include exactly one operational-configuration reconciliation entry.");
        }
        ValidateOperationalTransformation(operational[0], package);
        var titleClaimAnnotations = ValidateClaimGroups(
            report.GetProperty("claimReconciliation").GetProperty("titleRequests"),
            sourceCounts["title_requests"],
            titleRequests: true);
        var copyClaimAnnotations = ValidateClaimGroups(
            report.GetProperty("claimReconciliation").GetProperty("additionalCopies"),
            sourceCounts["additional_copy_requests"],
            titleRequests: false);
        ValidateClaimGroupsAgainstSource(
            report.GetProperty("claimReconciliation").GetProperty("titleRequests"),
            report,
            package,
            "title-requests.json",
            "title_requests",
            titleRequests: true);
        ValidateClaimGroupsAgainstSource(
            report.GetProperty("claimReconciliation").GetProperty("additionalCopies"),
            report,
            package,
            "additional-copy-requests.json",
            "additional_copy_requests",
            titleRequests: false);
        var expectedTitleClaimCount = CountSourceClaimAttributions(
            package, "title-requests.json", "title_requests", titleRequest: true);
        var expectedCopyClaimCount = CountSourceClaimAttributions(
            package, "additional-copy-requests.json", "additional_copy_requests", titleRequest: false);
        if (ClaimGroupTotal(report.GetProperty("claimReconciliation").GetProperty("titleRequests")) != expectedTitleClaimCount ||
            ClaimGroupTotal(report.GetProperty("claimReconciliation").GetProperty("additionalCopies")) != expectedCopyClaimCount)
        {
            throw ReportMismatch("Claim-reconciliation totals do not match source claim attribution fields.");
        }

        var placement = ComputeSourcePlacementSummary(package);
        ValidatePlacementAudit(report.GetProperty("placementReconciliation"), placement);
        var bibAuthority = ComputeSourceBibAuthoritySummary(package, placement.ProtectedSourceIds);
        ValidateBibAuthorityAudit(report.GetProperty("bibAuthorityReconciliation"), bibAuthority);
        ValidateSourceToTargetAudit(report.GetProperty("sourceToTargetReconciliation"), sourceCounts, package);
        ValidateTransformationPopulation(
            transformations,
            package,
            sourceCounts,
            bibAuthority,
            expectedTitleClaimCount,
            expectedCopyClaimCount,
            imported);

        RequireImportedCount(imported, "claim_migration_annotations", titleClaimAnnotations);
        RequireImportedCount(imported, "additional_copy_claim_migration_annotations", copyClaimAnnotations);
        RequireImportedCount(imported, "placed_bib_protection_markers", placement.ProtectedRequests);
        RequireImportedCount(imported, "title_request_bib_authority_automation_derived", bibAuthority.AutomationDerived);
        RequireImportedCount(imported, "title_request_bib_authority_ambiguous_safe", bibAuthority.AmbiguousWithoutStaffAuthority);
        RequireImportedCount(imported, "title_request_bib_authority_staff_authoritative", 0);

    }

    private static int ClaimGroupTotal(JsonElement groups) => groups.EnumerateArray()
        .Sum(group => group.GetProperty("count").GetInt32());

    private static int CountSourceClaimAttributions(
        ValidatedMigrationPackage package,
        string file,
        string collection,
        bool titleRequest)
    {
        return MigrationPackageReader.ReadRows(package, file, collection).Count(row =>
            row.String("claimedByStaffUserId") is not null ||
            row.String("claimedByDisplayName") is not null ||
            row.UtcDateTime("claimedAt").HasValue ||
            titleRequest && (row.String("claimType") is not null || row.String("claimRuleId") is not null));
    }

    private static void ValidateTransformationPopulation(
        IReadOnlyList<JsonElement> transformations,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> sourceCounts,
        BibAuthoritySummary bibAuthority,
        int expectedTitleClaimCount,
        int expectedCopyClaimCount,
        JsonElement imported)
    {
        int Count(string entity) => transformations.Count(item => item.GetProperty("entity").GetString() == entity);
        var staffRows = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var emailRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var rejectionRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates");
        var claimRules = MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules");
        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        var copies = MigrationPackageReader.ReadRows(package, "additional-copy-requests.json", "additional_copy_requests");
        var emailTemplateRows = emailRows.Concat(rejectionRows).ToArray();
        var sourceFieldRows = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.Ordinal);
        foreach (var (file, collection) in new[]
                 {
                     ("system-settings.json", "system_settings"),
                     ("polaris-settings.json", "polaris_settings"),
                     ("email-settings.json", "smtp_settings"),
                     ("workflow-settings.json", "workflow_settings"),
                     ("patron-settings.json", "ui_settings"),
                     ("patron-settings.json", "patron_settings_overrides"),
                     ("patron-settings.json", "patron_library_settings"),
                     ("patron-settings.json", "library_settings"),
                     ("material-formats.json", "material_formats"),
                     ("format-auto-claim-rules.json", "format_claim_rules"),
                     ("email-templates.json", "email_templates"),
                     ("email-templates.json", "rejection_templates")
                 })
        {
            sourceFieldRows.Add(collection, MigrationPackageReader.ReadRows(package, file, collection));
        }

        var expectedEntityCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["source_field_accounting"] = sourceFieldRows.Values.Sum(rows => rows.Count),
            ["operational_configuration"] = 1,
            ["branding"] = sourceCounts["branding"],
            ["system_settings_effective_runtime"] = 1,
            ["polaris_settings"] = sourceCounts["polaris_settings"],
            ["staff_user"] = sourceCounts["staff_users"],
            ["email_settings"] = sourceCounts["smtp_settings"],
            ["email_provider_token"] = 1,
            ["email_template_sender"] = ExpectedLibrarySenderTransformCount(emailRows, package),
            ["patron_duplicate_labels"] = ExpectedPatronDuplicateLabelTransformCount(package),
            ["system_material_format_availability"] = 1,
            ["legacy_library_branding"] = sourceCounts["library_settings"],
            ["patron_format_rules"] = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides")
                .Count(row => row.JsonText("patronFormatRules") is not null && row.JsonText("additionalFieldDefinitions") is null),
            ["patron_custom_fields"] = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides")
                .Count(row => row.JsonText("additionalFieldDefinitions") is not null),
            ["email_template"] = emailTemplateRows.Count(row => row.String("fromAddress") is not null || row.String("fromName") is not null),
            ["format_auto_claim_rule"] = claimRules.Count,
            ["title_request_isbn_status"] = requests.Count,
            ["title_request_claim"] = expectedTitleClaimCount,
            ["additional_copy_claim"] = expectedCopyClaimCount,
            ["additional_copy_updated_timestamp"] = copies.Count(row => !row.HasValue("updated")),
            ["deleted_request_audit"] = 1,
            ["placed_bib_protection"] = requests.Count,
            ["title_request_bib_authority"] = bibAuthority.SourceRequestsWithBibId
        };
        var transformationsByEntity = transformations
            .GroupBy(item => item.GetProperty("entity").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        if (transformationsByEntity.Keys.Any(entity => !expectedEntityCounts.ContainsKey(entity) && entity != "migration_bootstrap_super_admin"))
        {
            throw ReportMismatch("The report contains an undocumented transformation entity.");
        }
        foreach (var expected in expectedEntityCounts)
        {
            if (Count(expected.Key) != expected.Value)
            {
                throw ReportMismatch($"Transformation entries for {expected.Key} do not match the immutable source population.");
            }
        }
        var tokenTransformation = transformationsByEntity["email_provider_token"].Single();
        if (tokenTransformation.GetProperty("organizationId").GetInt32() != 1)
        {
            throw ReportMismatch("The Postmark-token presence boundary must describe only system organization 1.");
        }
        var emailSettingsTransformations = transformationsByEntity.GetValueOrDefault("email_settings") ?? [];
        if (emailSettingsTransformations.Length > 0 &&
            emailSettingsTransformations.Single().GetProperty("postmarkTokenProvisioned").GetBoolean() !=
            tokenTransformation.GetProperty("postmarkTokenProvisioned").GetBoolean())
        {
            throw ReportMismatch("The source email-settings audit disagrees with the operator Postmark-token boundary.");
        }
        var systemSeedCodes = new[] { "book", "audiobook_cd", "dvd", "music_cd", "ebook", "eaudiobook" };
        var sourceSystemFormatCodes = sourceFieldRows["material_formats"]
            .Where(row => row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(row => NormalizeAuditFormatCode(row.RequiredString("code")))
            .ToHashSet(StringComparer.Ordinal);
        var expectedUnavailableSeedCodes = systemSeedCodes
            .Where(code => !sourceSystemFormatCodes.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        var availabilityTransformation = transformationsByEntity["system_material_format_availability"].Single();
        var actualUnavailableSeedCodes = availabilityTransformation.GetProperty("unavailableSeedCodes")
            .EnumerateArray()
            .Select(code => code.GetString()!)
            .ToArray();
        if (!actualUnavailableSeedCodes.SequenceEqual(expectedUnavailableSeedCodes, StringComparer.Ordinal))
        {
            throw ReportMismatch("System format availability decisions do not match source system format rows.");
        }
        ValidateTransformationSourceIds(transformations, "staff_user", staffRows);
        ValidateTransformationSourceIds(transformations, "polaris_settings", MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings"));
        ValidateTransformationSourceIds(transformations, "email_settings", MigrationPackageReader.ReadRows(package, "email-settings.json", "smtp_settings"));
        ValidateTransformationSourceIds(transformations, "email_template", emailTemplateRows.Where(row => row.String("fromAddress") is not null || row.String("fromName") is not null));
        ValidateTransformationSourceIds(transformations, "format_auto_claim_rule", claimRules);
        ValidateTransformationSourceIds(transformations, "title_request_isbn_status", requests);
        ValidateTransformationSourceIds(transformations, "title_request_claim", requests.Where(row => HasSourceClaimAttribution(row, titleRequest: true)));
        ValidateTransformationSourceIds(transformations, "additional_copy_claim", copies.Where(row => HasSourceClaimAttribution(row, titleRequest: false)));
        ValidateTransformationSourceIds(transformations, "additional_copy_updated_timestamp", copies.Where(row => !row.HasValue("updated")));
        ValidateTransformationSourceIds(transformations, "placed_bib_protection", requests);
        ValidateTransformationSourceIds(transformations, "title_request_bib_authority", requests.Where(row => row.PositiveInt32("bibid", "source_bib_invalid") is not null));
        ValidateIsbnStatusTransformations(transformationsByEntity.GetValueOrDefault("title_request_isbn_status") ?? [], requests);
        ValidateBibAuthorityTransformations(
            transformationsByEntity.GetValueOrDefault("title_request_bib_authority") ?? [],
            package,
            requests);

        var deletedAudit = transformationsByEntity["deleted_request_audit"].Single();
        if (!HasNonNegativeInt32(deletedAudit, "sourceRows") ||
            !HasNonNegativeInt32(deletedAudit, "targetRows") ||
            deletedAudit.GetProperty("sourceRows").GetInt32() != sourceCounts["deleted_request_audit"] ||
            deletedAudit.GetProperty("targetRows").GetInt32() != sourceCounts["deleted_request_audit"] ||
            !HasString(deletedAudit, "reason") ||
            deletedAudit.GetProperty("reason").GetString() != "reduced_audit_excludes_sensitive_and_freeform_fields")
        {
            throw ReportMismatch("Deleted-request audit transformation counts do not match the immutable source population.");
        }

        if (Count("title_request_isbn_status") != sourceCounts["title_requests"] ||
            Count("placed_bib_protection") != sourceCounts["title_requests"] ||
            Count("title_request_claim") != expectedTitleClaimCount ||
            Count("additional_copy_claim") != expectedCopyClaimCount ||
            Count("title_request_bib_authority") != bibAuthority.SourceRequestsWithBibId)
        {
            throw ReportMismatch("Transformation entries do not cover source request, claim, and BIB populations.");
        }

        var accountingRows = transformations
            .Where(item => item.GetProperty("entity").GetString() == "source_field_accounting")
            .ToArray();
        var accountingSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in accountingRows)
        {
            if (!HasString(item, "collection") || !HasString(item, "sourceId") ||
                !HasArray(item, "intentionallyDroppedFields"))
            {
                throw InvalidReport("A source-field accounting transformation is malformed.");
            }
            var collection = item.GetProperty("collection").GetString()!;
            var sourceId = item.GetProperty("sourceId").GetString()!;
            if (!sourceFieldRows.TryGetValue(collection, out var sourceRows) ||
                !accountingSeen.Add($"{collection}:{sourceId}"))
            {
                throw ReportMismatch("Source-field accounting entries do not identify unique source configuration rows.");
            }
            var sourceRow = sourceRows.SingleOrDefault(row => row.RequiredString("id") == sourceId);
            if (sourceRow is null)
            {
                throw ReportMismatch("A source-field accounting entry does not identify a source configuration row.");
            }
            var actualDroppedFields = new List<string>();
            foreach (var field in item.GetProperty("intentionallyDroppedFields").EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.String)
                {
                    throw InvalidReport("A source-field accounting dropped-field entry is malformed.");
                }
                actualDroppedFields.Add(field.GetString()!);
            }
            var expectedDroppedFields = ExpectedIntentionallyDroppedFields(collection, sourceRow);
            if (!actualDroppedFields.SequenceEqual(expectedDroppedFields, StringComparer.Ordinal))
            {
                throw ReportMismatch("Source-field accounting differs from documented source drops.");
            }
        }
        var expectedAccounting = sourceFieldRows.Values.Sum(rows => rows.Count);
        if (accountingRows.Length != expectedAccounting || accountingSeen.Count != expectedAccounting)
        {
            throw ReportMismatch("Source-field accounting does not cover every immutable configuration row exactly once.");
        }

        if (transformationsByEntity.TryGetValue("patron_custom_fields", out var customFieldTransforms))
        {
            var expectedRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides")
                .Where(row => row.JsonText("additionalFieldDefinitions") is not null)
                .ToDictionary(row => row.Int32("orgId") ?? throw ReportMismatch("A custom-field override has no library identity."));
            var seenLibraries = new HashSet<int>();
            foreach (var item in customFieldTransforms)
            {
                if (!HasNonNegativeInt32(item, "organizationId") ||
                    !HasNonNegativeInt32(item, "fields") ||
                    !HasNonNegativeInt32(item, "formatRules") ||
                    !HasNonNegativeInt32(item, "downgradedRequiredSelectRules") ||
                    !HasString(item, "absentOrDisabledRule") || item.GetProperty("absentOrDisabledRule").GetString() != "hidden" ||
                    !HasString(item, "requiredSelectWithoutEnabledOptions") || item.GetProperty("requiredSelectWithoutEnabledOptions").GetString() != "optional")
                {
                    throw InvalidReport("A custom-field transformation is incomplete.");
                }
                var libraryId = item.GetProperty("organizationId").GetInt32();
                if (!expectedRows.TryGetValue(libraryId, out var sourceRow) || !seenLibraries.Add(libraryId))
                {
                    throw ReportMismatch("A custom-field transformation does not identify a unique source library override.");
                }
                using var definitions = JsonDocument.Parse(sourceRow.JsonText("additionalFieldDefinitions")!);
                var expectedFields = definitions.RootElement.GetArrayLength();
                var expectedFormatRules = ExpectedEffectiveFormatCount(package, libraryId) * expectedFields;
                if (item.GetProperty("fields").GetInt32() != expectedFields ||
                    item.GetProperty("formatRules").GetInt32() != expectedFormatRules ||
                    item.GetProperty("downgradedRequiredSelectRules").GetInt32() !=
                        ExpectedRequiredSelectDowngradeCount(package, sourceRow, libraryId))
                {
                    throw ReportMismatch("A custom-field transformation count differs from source definitions and effective formats.");
                }
            }
            if (seenLibraries.Count != expectedRows.Count)
            {
                throw ReportMismatch("Custom-field transformation entries do not cover every source override with definitions.");
            }
        }

        var formatRuleRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides")
            .Where(row => row.JsonText("additionalFieldDefinitions") is null && row.JsonText("patronFormatRules") is not null)
            .ToDictionary(row => row.Int32("orgId") ?? throw ReportMismatch("A format-rule override has no library identity."));
        var formatRuleTransforms = transformationsByEntity.GetValueOrDefault("patron_format_rules") ?? [];
        var seenFormatRuleLibraries = new HashSet<int>();
        foreach (var item in formatRuleTransforms)
        {
            if (!HasNonNegativeInt32(item, "organizationId") || !HasNonNegativeInt32(item, "customFields") ||
                item.GetProperty("customFields").GetInt32() != 0 || !HasArray(item, "formats"))
            {
                throw InvalidReport("A patron format-rule transformation is incomplete.");
            }
            var libraryId = item.GetProperty("organizationId").GetInt32();
            if (!formatRuleRows.TryGetValue(libraryId, out var sourceRow) || !seenFormatRuleLibraries.Add(libraryId))
            {
                throw ReportMismatch("A patron format-rule transformation does not identify a unique source override.");
            }
            using var rules = JsonDocument.Parse(sourceRow.JsonText("patronFormatRules")!);
            var actualCodes = item.GetProperty("formats").EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .ToArray();
            var expectedCodes = rules.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (actualCodes.Any(value => value is null) || !actualCodes.SequenceEqual(expectedCodes, StringComparer.Ordinal))
            {
                throw ReportMismatch("A patron format-rule transformation differs from its source format codes.");
            }
        }
        if (seenFormatRuleLibraries.Count != formatRuleRows.Count)
        {
            throw ReportMismatch("Patron format-rule transformations do not cover every applicable source override.");
        }

        var bootstrapCount = imported.TryGetProperty("migration_bootstrap_staff_users", out var bootstrap)
            ? bootstrap.GetInt32()
            : 0;
        var bootstrapEntries = transformations
            .Where(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin")
            .ToArray();
        var bootstrapAction = bootstrapEntries.Length == 1 && HasString(bootstrapEntries[0], "action")
            ? bootstrapEntries[0].GetProperty("action").GetString()
            : null;
        if (bootstrapCount is < 0 or > 1 || bootstrapEntries.Length > 1 ||
            bootstrapCount == 1 && bootstrapAction != "inserted" ||
            bootstrapCount == 0 && bootstrapAction == "inserted" ||
            bootstrapEntries.Length == 1 &&
                (bootstrapAction is not ("inserted" or "promoted_existing") ||
                 !HasPositiveInt64(bootstrapEntries[0], "targetStaffUserId") ||
                 !HasString(bootstrapEntries[0], "authenticationEmail")))
        {
            throw InvalidReport("The bootstrap-super-admin transformation does not match the source count.");
        }
    }

    private static bool HasSourceClaimAttribution(SourceRow row, bool titleRequest) =>
        row.String("claimedByStaffUserId") is not null ||
        row.String("claimedByDisplayName") is not null ||
        row.UtcDateTime("claimedAt").HasValue ||
        titleRequest && (row.String("claimType") is not null || row.String("claimRuleId") is not null);

    private static void ValidateTransformationSourceIds(
        IReadOnlyList<JsonElement> transformations,
        string entity,
        IEnumerable<SourceRow> expectedRows)
    {
        var expected = expectedRows.Select(row => row.RequiredString("id")).ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in transformations.Where(item => item.GetProperty("entity").GetString() == entity))
        {
            if (!HasString(item, "sourceId") || !actual.Add(item.GetProperty("sourceId").GetString()!))
            {
                throw InvalidReport($"A {entity} transformation has a missing or duplicate source identity.");
            }
        }
        if (!actual.SetEquals(expected))
        {
            throw ReportMismatch($"Transformation entries for {entity} do not identify the immutable source rows.");
        }
    }

    private static void ValidateIsbnStatusTransformations(
        IReadOnlyList<JsonElement> transformations,
        IReadOnlyList<SourceRow> requests)
    {
        var bySourceId = transformations.ToDictionary(
            item => item.GetProperty("sourceId").GetString()!,
            StringComparer.Ordinal);
        foreach (var row in requests)
        {
            var sourceId = row.RequiredString("id");
            var sourceStatus = row.String("isbnCheckStatus");
            var targetStatus = NormalizeAuditIsbnStatus(
                sourceStatus,
                row.String("identifier"),
                row.PositiveInt32("bibid", "source_bib_invalid") ?? 0);
            var expectedReason = (sourceStatus, targetStatus) switch
            {
                (null, null) => "preserved_absent",
                ("error", "skipped_no_isbn") => "missing_identifier_error_normalized",
                ("found_in_polaris", "found") => "historical_alias_normalized",
                ("skipped_no_isbn", "skipped_no_isbn") => "retry_count_normalized",
                _ => "preserved"
            };
            var item = bySourceId[sourceId];
            if (!NullableJsonStringEquals(item, "sourceStatus", sourceStatus) ||
                !NullableJsonStringEquals(item, "targetStatus", targetStatus) ||
                !string.Equals(item.GetProperty("reason").GetString(), expectedReason, StringComparison.Ordinal))
            {
                throw ReportMismatch("Identifier-status transformation fields differ from the immutable source request.");
            }
        }
    }

    private static void ValidateBibAuthorityTransformations(
        IReadOnlyList<JsonElement> transformations,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> requests)
    {
        var bySourceId = transformations.ToDictionary(
            item => item.GetProperty("sourceId").GetString()!,
            StringComparer.Ordinal);
        var protectedSourceIds = ComputeSourcePlacementSummary(package).ProtectedSourceIds;
        var statuses = MigrationPackageReader.ReadRowsOrEmpty(package, "title-request-events.json", "request_statuses")
            .ToDictionary(
                row => row.RequiredString("id"),
                row => NormalizeAuditStatus(row.RequiredString("code")),
                StringComparer.Ordinal);
        foreach (var row in requests)
        {
            var bibId = row.PositiveInt32("bibid", "source_bib_invalid");
            if (bibId is null)
            {
                continue;
            }

            var sourceId = row.RequiredString("id");
            var sourceStatus = row.String("isbnCheckStatus");
            var isbnStatus = NormalizeAuditIsbnStatus(sourceStatus, row.String("identifier"), bibId.Value);
            var placedHistoryProtected = protectedSourceIds.Contains(sourceId);
            var lastChecked = row.UtcDateTime("lastChecked");
            var updated = row.UtcDateTime("updated");
            var automationDerived = isbnStatus == "found" &&
                !string.IsNullOrWhiteSpace(row.String("identifier")) && lastChecked.HasValue && lastChecked == updated;
            var classification = automationDerived
                ? "automation_derived"
                : placedHistoryProtected
                    ? "ambiguous_protected_by_placed_history"
                    : "ambiguous_noneligible_identifier_state";
            var item = bySourceId[sourceId];
            if (item.GetProperty("bibId").GetInt32() != bibId.Value ||
                !string.Equals(item.GetProperty("requestStatus").GetString(), ResolveAuditRequestStatus(row, statuses), StringComparison.Ordinal) ||
                !NullableJsonStringEquals(item, "sourceIsbnCheckStatus", sourceStatus) ||
                !string.Equals(item.GetProperty("classification").GetString(), classification, StringComparison.Ordinal) ||
                item.GetProperty("bibIdStaffVerified").GetBoolean() ||
                item.GetProperty("placedHistoryProtected").GetBoolean() != placedHistoryProtected ||
                !string.Equals(item.GetProperty("outcome").GetString(), "imported_without_staff_authority", StringComparison.Ordinal))
            {
                throw ReportMismatch("Per-request BIB-authority transformations differ from immutable source evidence.");
            }
        }
    }

    private static int ExpectedLibrarySenderTransformCount(
        IReadOnlyList<SourceRow> emailRows,
        ValidatedMigrationPackage package)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var organizationIds = organizations.ToDictionary(
            row => row.RequiredString("id"),
            row => row.Int32("organizationId") ?? throw ReportMismatch("A source organization has no native identity."),
            StringComparer.Ordinal);
        int ResolveLibrary(string reference) => organizationIds.TryGetValue(reference, out var id)
            ? id
            : int.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && organizationIds.Values.Contains(parsed)
                ? parsed
                : throw ReportMismatch("A template sender scope does not identify a source organization.");

        return emailRows
            .Where(row => row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => ResolveLibrary(row.RequiredString("libraryOrganization")))
            .Count(group => group.Any(row => !string.IsNullOrWhiteSpace(row.Text("fromAddress")) || !string.IsNullOrWhiteSpace(row.Text("fromName"))));
    }

    private static int ExpectedPatronDuplicateLabelTransformCount(ValidatedMigrationPackage package)
    {
        var ignoredUiFields = new[]
        {
            "duplicateLabelSuggestion", "duplicateLabelOutstandingPurchase", "duplicateLabelPendingHold",
            "duplicateLabelHoldPlaced", "duplicateLabelClosed", "duplicateLabelRejected",
            "duplicateLabelHoldCompleted", "duplicateLabelHoldNotPickedUp", "duplicateLabelManual",
            "duplicateLabelSilent", "systemNotEnabledMessage", "publicationOptions"
        };
        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        var legacyRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings");
        return legacyRows.Count + uiRows.Count(row =>
            row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase) &&
            ignoredUiFields.Any(row.HasValue));
    }

    private static int ExpectedEffectiveFormatCount(ValidatedMigrationPackage package, int libraryId)
        => ExpectedEffectiveFormatCodes(package, libraryId).Count;

    private static string AuditCustomFieldType(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object ||
            !definition.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            throw ReportMismatch("A source custom-field type is invalid.");
        }

        var normalized = TrimAuditConfigurationText(type.GetString()!);
        if (normalized is not ("text" or "textarea" or "select"))
        {
            throw ReportMismatch("A source custom-field type is unsupported.");
        }

        return normalized;
    }

    private static string AuditCustomFieldIdentity(JsonElement definition)
    {
        string? identity = null;
        if (definition.TryGetProperty("key", out var key) && key.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (key.ValueKind != JsonValueKind.String)
            {
                throw ReportMismatch("A source custom-field key is invalid.");
            }

            var sourceKey = key.GetString()!;
            if (sourceKey.Length > 0)
            {
                identity = sourceKey;
            }
        }

        if (identity is null)
        {
            if (!definition.TryGetProperty("label", out var label) ||
                label.ValueKind != JsonValueKind.String ||
                TrimAuditConfigurationText(label.GetString()!).Length == 0)
            {
                throw ReportMismatch("A source custom-field label is invalid.");
            }

            identity = label.GetString()!;
        }

        var normalized = Regex.Replace(
            TrimAuditConfigurationText(identity).Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant(),
            "[^a-z0-9]+",
            "_",
            RegexOptions.CultureInvariant).Trim('_');
        return normalized.Length > 0
            ? normalized
            : throw ReportMismatch("A source custom-field key normalizes to an empty identity.");
    }

    private static string TrimAuditConfigurationText(string value)
    {
        var start = 0;
        while (start < value.Length && IsAuditConfigurationWhitespace(value[start]))
        {
            start++;
        }
        var end = value.Length;
        while (end > start && IsAuditConfigurationWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsAuditConfigurationWhitespace(char value) => value is
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or
        '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or
        '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028' or '\u2029' or
        '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static int ExpectedRequiredSelectDowngradeCount(
        ValidatedMigrationPackage package,
        SourceRow sourceRow,
        int libraryId)
    {
        using var definitions = JsonDocument.Parse(sourceRow.JsonText("additionalFieldDefinitions")!);
        using var rules = sourceRow.JsonText("patronFormatRules") is { } rulesJson ? JsonDocument.Parse(rulesJson) : null;
        var formatCodes = ExpectedEffectiveFormatCodes(package, libraryId);
        var count = 0;
        foreach (var definition in definitions.RootElement.EnumerateArray())
        {
            if (AuditCustomFieldType(definition) != "select" ||
                definition.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            var enabledOptionCount = definition.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                ? options.EnumerateArray().Count(option => !option.TryGetProperty("enabled", out var optionEnabled) || optionEnabled.ValueKind != JsonValueKind.False)
                : 0;
            if (enabledOptionCount != 0 || rules is null || rules.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var fieldKey = AuditCustomFieldIdentity(definition);
            foreach (var formatCode in formatCodes)
            {
                var sourceFormatCode = ResolveAuditSourceFormatCode(package, libraryId, formatCode);
                if (!TryGetLegacyFormatRuleForAudit(rules.RootElement, sourceFormatCode, out var format) ||
                    format.ValueKind != JsonValueKind.Object ||
                    !format.TryGetProperty("customFields", out var customFields) ||
                    customFields.ValueKind != JsonValueKind.Object ||
                    !customFields.TryGetProperty(fieldKey, out var fieldRule) ||
                    fieldRule.ValueKind != JsonValueKind.Object ||
                    !fieldRule.TryGetProperty("mode", out var mode) ||
                    mode.ValueKind != JsonValueKind.String ||
                    !string.Equals(TrimAuditConfigurationText(mode.GetString()!), "required", StringComparison.Ordinal))
                {
                    continue;
                }
                count++;
            }
        }
        return count;
    }

    private static bool TryGetLegacyFormatRuleForAudit(JsonElement rules, string formatCode, out JsonElement format)
    {
        if (rules.TryGetProperty(formatCode, out format))
        {
            return true;
        }
        if (formatCode is "book" or "audiobook_cd" or "dvd" or "music_cd" or "ebook" or "eaudiobook")
        {
            format = default;
            return false;
        }
        return rules.TryGetProperty("book", out format);
    }

    private static string ResolveAuditSourceFormatCode(ValidatedMigrationPackage package, int libraryId, string normalizedCode)
    {
        var formats = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
        var libraryRow = formats.FirstOrDefault(row =>
            row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase) &&
            ResolveAuditLibraryId(package, row.RequiredString("libraryOrganization")) == libraryId &&
            NormalizeAuditFormatCode(row.RequiredString("code")) == NormalizeAuditFormatCode(normalizedCode));
        if (libraryRow is not null)
        {
            return libraryRow.RequiredString("code");
        }

        var systemRow = formats.FirstOrDefault(row =>
            row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase) &&
            NormalizeAuditFormatCode(row.RequiredString("code")) == NormalizeAuditFormatCode(normalizedCode));
        return systemRow?.RequiredString("code") ?? NormalizeAuditFormatCode(normalizedCode);
    }

    private static HashSet<string> ExpectedEffectiveFormatCodes(ValidatedMigrationPackage package, int libraryId)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "book", "audiobook_cd", "dvd", "music_cd", "ebook", "eaudiobook"
        };
        foreach (var row in MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats"))
        {
            var code = NormalizeAuditFormatCode(row.RequiredString("code"));
            if (row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase) ||
                row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase) &&
                ResolveAuditLibraryId(package, row.RequiredString("libraryOrganization")) == libraryId)
            {
                codes.Add(code);
            }
        }
        return codes;
    }

    private static int ResolveAuditLibraryId(ValidatedMigrationPackage package, string reference)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var match = organizations.FirstOrDefault(row => row.RequiredString("id") == reference);
        if (match?.Int32("organizationId") is { } nativeId)
        {
            return nativeId;
        }
        return int.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               organizations.Any(row => row.Int32("organizationId") == parsed)
            ? parsed
            : throw ReportMismatch("A source material format scope does not identify a source organization.");
    }

    private static string[] ExpectedIntentionallyDroppedFields(string collection, SourceRow row)
    {
        var fields = collection switch
        {
            "system_settings" => new[]
            {
                "allowedStaffUsers", "enabledLibraries", "organizationsSyncStatus", "organizationsLastSynced",
                "organizationsSyncMessage", "organizationsSyncError", "patronCodesSyncStatus", "patronCodesLastSynced",
                "patronCodesSyncMessage", "patronCodesSyncError"
            },
            "polaris_settings" => new[]
            {
                "settingsKey", "overridePassword", "langId", "appId", "orgId", "autoPromote",
                "firstSuccessfulSaveAt", "materialTypesCache", "materialTypesCacheUpdated"
            },
            "smtp_settings" => new[] { "settingsKey", "host", "port", "username", "password", "tls" },
            "library_settings" => new[] { "logo", "logoAlt" },
            "format_claim_rules" => new[] { "createdBy", "updatedBy" },
            "email_templates" or "rejection_templates" => new[] { "created", "updated" },
            _ => Array.Empty<string>()
        };
        return fields.Where(row.HasValue).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasPositiveInt64(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var parsed) && parsed > 0;

    private static void ValidateOperationalTransformation(
        JsonElement transformation,
        ValidatedMigrationPackage package)
    {
        if (transformation.ValueKind != JsonValueKind.Object ||
            !HasString(transformation, "status") ||
            transformation.GetProperty("status").GetString() != "matched" ||
            !HasNonNegativeInt32(transformation, "matchedSchedules") ||
            !HasNonNegativeInt32(transformation, "matchedQueues") ||
            !HasNonNegativeInt32(transformation, "retiredObsoleteOverrides"))
        {
            throw InvalidReport("The operational-configuration audit entry is incomplete.");
        }

        var operational = MigrationPackageReader.ReadMetadata(package, "effective-legacy-operational-config.json");
        var sourceLimits = operational.GetProperty("processingLimits");
        var obsolete = sourceLimits.GetProperty("obsoletePathOverrides").GetProperty("pending_isbn_checks");
        var expectedRetiredOverrides = obsolete.GetProperty("pageSize").ValueKind != JsonValueKind.Null ||
            obsolete.GetProperty("maxPerRun").ValueKind != JsonValueKind.Null ? 1 : 0;
        if (transformation.GetProperty("matchedSchedules").GetInt32() != operational.GetProperty("schedules").EnumerateObject().Count() ||
            transformation.GetProperty("matchedQueues").GetInt32() != sourceLimits.GetProperty("effectiveQueues").EnumerateObject().Count() ||
            transformation.GetProperty("retiredObsoleteOverrides").GetInt32() != expectedRetiredOverrides)
        {
            throw ReportMismatch("Operational-configuration audit counts differ from the frozen package configuration.");
        }
    }

    private static int ValidateClaimGroups(JsonElement groups, int sourceRows, bool titleRequests)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var annotations = 0;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !HasNonNegativeInt32(group, "libraryOrganizationId") ||
                group.GetProperty("libraryOrganizationId").GetInt32() <= 1 ||
                !HasString(group, "status") ||
                !HasString(group, "outcome") ||
                !HasString(group, "reason") ||
                !HasNonNegativeInt32(group, "count") ||
                group.GetProperty("count").GetInt32() == 0)
            {
                throw InvalidReport("A claim-reconciliation group is malformed.");
            }

            var status = group.GetProperty("status").GetString()!;
            var outcome = group.GetProperty("outcome").GetString()!;
            var reason = group.GetProperty("reason").GetString()!;
            var validStatus = titleRequests
                ? status is "suggestion" or "outstanding_purchase" or "pending_hold" or "hold_placed" or "closed"
                : status is "open" or "closed";
            var validOutcome = outcome is "cleared" or "preserved" or "closed_history";
            if (!validStatus || !validOutcome || string.IsNullOrWhiteSpace(reason))
            {
                throw InvalidReport("A claim-reconciliation group contains an unsupported status or outcome.");
            }

            var libraryId = group.GetProperty("libraryOrganizationId").GetInt32();
            var count = group.GetProperty("count").GetInt32();
            var key = $"{libraryId}:{status}:{outcome}:{reason}";
            if (!keys.Add(key))
            {
                throw InvalidReport("The claim-reconciliation report contains duplicate groups.");
            }
            total += count;
            if (outcome == "cleared" || outcome == "closed_history" && reason == "closed_attribution_incomplete")
            {
                annotations += count;
            }
        }

        if (total > sourceRows)
        {
            throw ReportMismatch("Claim-reconciliation groups exceed the source request population.");
        }
        return annotations;
    }

    private static void ValidateClaimGroupsAgainstSource(
        JsonElement groups,
        JsonElement report,
        ValidatedMigrationPackage package,
        string file,
        string collection,
        bool titleRequests)
    {
        var rows = MigrationPackageReader.ReadRows(package, file, collection);
        var staff = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users")
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var statuses = MigrationPackageReader.ReadRowsOrEmpty(package, "title-request-events.json", "request_statuses")
            .ToDictionary(row => row.RequiredString("id"), row => NormalizeAuditStatus(row.RequiredString("code")), StringComparer.Ordinal);
        var rules = MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules")
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var formats = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats")
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var promotedBootstrapEmail = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin" &&
                item.GetProperty("action").GetString() == "promoted_existing")
            .Select(item => item.GetProperty("authenticationEmail").GetString())
            .SingleOrDefault();
        var expected = new Dictionary<(int LibraryId, string Status, string Outcome, string Reason), int>();

        foreach (var row in rows)
        {
            var sourceClaimantId = row.String("claimedByStaffUserId");
            var displayName = row.String("claimedByDisplayName");
            var claimedAt = row.UtcDateTime("claimedAt");
            var sourceClaimType = titleRequests ? NormalizeAuditClaimType(row.String("claimType")) : null;
            var sourceRuleId = titleRequests ? row.String("claimRuleId") : null;
            var hasAttribution = sourceClaimantId is not null || displayName is not null || claimedAt.HasValue ||
                titleRequests && (sourceClaimType is not null || sourceRuleId is not null);
            if (!hasAttribution)
            {
                continue;
            }

            var libraryId = row.Int32("libraryOrgId") ?? throw ReportMismatch("A claimed source row has no library identity.");
            var status = titleRequests
                ? ResolveAuditRequestStatus(row, statuses)
                : NormalizeAuditAdditionalCopyStatus(row.RequiredString("status"));
            var reason = ResolveAuditClaimReason(
                row,
                status,
                sourceClaimantId,
                displayName,
                claimedAt,
                sourceClaimType,
                sourceRuleId,
                staff,
                rules,
                formats,
                promotedBootstrapEmail,
                titleRequests);
            var outcome = status == "closed"
                ? "closed_history"
                : reason is "eligible" or "claim_rule_unmapped_normalized" or "claim_rule_mismatch_normalized" or "closed_history_preserved" or "closed_claimant_unmapped"
                    ? "preserved"
                    : "cleared";
            var key = (libraryId, status, outcome, reason);
            expected.TryGetValue(key, out var count);
            expected[key] = count + 1;
        }

        var actual = new Dictionary<(int LibraryId, string Status, string Outcome, string Reason), int>();
        foreach (var group in groups.EnumerateArray())
        {
            var key = (
                group.GetProperty("libraryOrganizationId").GetInt32(),
                group.GetProperty("status").GetString()!,
                group.GetProperty("outcome").GetString()!,
                group.GetProperty("reason").GetString()!);
            actual.TryGetValue(key, out var count);
            actual[key] = count + group.GetProperty("count").GetInt32();
        }
        if (actual.Count != expected.Count || expected.Any(item => !actual.TryGetValue(item.Key, out var count) || count != item.Value))
        {
            throw ReportMismatch("Claim-reconciliation groups differ from source library, status, and attribution inputs.");
        }
    }

    private static string ResolveAuditClaimReason(
        SourceRow row,
        string status,
        string? sourceClaimantId,
        string? displayName,
        DateTime? claimedAt,
        string? sourceClaimType,
        string? sourceRuleId,
        IReadOnlyDictionary<string, SourceRow> staff,
        IReadOnlyDictionary<string, SourceRow> rules,
        IReadOnlyDictionary<string, SourceRow> formats,
        string? promotedBootstrapEmail,
        bool titleRequest)
    {
        var mappedStaff = sourceClaimantId is not null && staff.TryGetValue(sourceClaimantId, out var sourceStaff)
            ? sourceStaff
            : null;
        if (!titleRequest)
        {
            if (status == "closed")
            {
                if (displayName is null || !claimedAt.HasValue)
                {
                    return "closed_attribution_incomplete";
                }
                return mappedStaff is null ? "closed_claimant_unmapped" : "closed_history_preserved";
            }
            if (sourceClaimantId is null || mappedStaff is null)
            {
                return "claimant_unmapped";
            }
            if (displayName is null || !claimedAt.HasValue)
            {
                return "claim_metadata_incomplete";
            }
            return AuditStaffEligibilityReason(mappedStaff, row.Int32("libraryOrgId") ?? 0, promotedBootstrapEmail);
        }

        if (sourceClaimantId is null)
        {
            return status == "closed" && displayName is not null && claimedAt.HasValue && sourceClaimType is not null
                ? "closed_claimant_unmapped"
                : status == "closed" ? "closed_attribution_incomplete" : "claimant_unmapped";
        }
        if (status == "closed")
        {
            if (displayName is null || !claimedAt.HasValue || sourceClaimType is null)
            {
                return "closed_attribution_incomplete";
            }
            return mappedStaff is null ? "closed_claimant_unmapped" : "closed_history_preserved";
        }
        if (mappedStaff is null)
        {
            return "claimant_unmapped";
        }
        if (displayName is null || !claimedAt.HasValue || sourceClaimType is null)
        {
            return "claim_metadata_incomplete";
        }
        var eligibility = AuditStaffEligibilityReason(mappedStaff, row.Int32("libraryOrgId") ?? 0, promotedBootstrapEmail);
        if (eligibility != "eligible")
        {
            return eligibility;
        }
        if (sourceClaimType == "manual")
        {
            return "eligible";
        }
        if (sourceRuleId is null || !rules.TryGetValue(sourceRuleId, out var rule))
        {
            return "claim_rule_unmapped_normalized";
        }

        var ruleStaffId = rule.String("staffUserId") ?? rule.String("staffUser");
        var requestFormatCode = row.String("formatRef") is { } formatRef && formats.TryGetValue(formatRef, out var format)
            ? NormalizeAuditFormatCode(format.RequiredString("code"))
            : row.String("format") is { } formatCode ? NormalizeAuditFormatCode(formatCode) : null;
        var ruleFormatCode = NormalizeAuditFormatCode(rule.RequiredString("format"));
        var ruleLibraryId = rule.Int32("libraryOrgId") ?? 0;
        return ruleLibraryId == (row.Int32("libraryOrgId") ?? 0) &&
               string.Equals(ruleFormatCode, requestFormatCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(ruleStaffId, sourceClaimantId, StringComparison.Ordinal)
            ? "eligible"
            : "claim_rule_mismatch_normalized";
    }

    private static string AuditStaffEligibilityReason(SourceRow staff, int libraryId, string? promotedBootstrapEmail)
    {
        var sourceAuthenticationEmail = ExpectedAuthenticationEmail(staff.String("email"));
        if (promotedBootstrapEmail is not null &&
            string.Equals(sourceAuthenticationEmail, promotedBootstrapEmail, StringComparison.OrdinalIgnoreCase))
        {
            return "eligible";
        }
        if (!staff.Bool("active"))
        {
            return "claimant_inactive";
        }
        var role = staff.RequiredString("role").ToLowerInvariant();
        return role == "super_admin" || (role is "staff" or "admin") && staff.Int32("libraryOrgId") == libraryId
            ? "eligible"
            : "claimant_out_of_scope";
    }

    private static string NormalizeAuditAdditionalCopyStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "open" => "open",
        "closed" => "closed",
        _ => throw ReportMismatch("A source additional-copy status is invalid.")
    };

    private static string? NormalizeAuditClaimType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "manual" => "manual",
        "automatic_format_rule" or "automaticformatrule" => "automatic_format_rule",
        _ => throw ReportMismatch("A source claim type is invalid.")
    };

    private static void ValidateSourceToTargetAudit(
        JsonElement audit,
        IReadOnlyDictionary<string, int> sourceCounts,
        ValidatedMigrationPackage package)
    {
        if (!audit.GetProperty("passed").GetBoolean())
        {
            throw ReportMismatch("The source-to-target reconciliation did not pass.");
        }

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["organizations"] = sourceCounts["polaris_organizations"],
            ["staffUsers"] = sourceCounts["staff_users"],
            ["titleRequests"] = sourceCounts["title_requests"],
            ["additionalCopies"] = sourceCounts["additional_copy_requests"],
            ["deletedRequestAudits"] = sourceCounts["deleted_request_audit"],
            ["brandingAssets"] = sourceCounts["branding"],
            ["workflowTagsChecked"] = sourceCounts["workflow_tags"],
            ["titleRequestTagsChecked"] = sourceCounts["title_request_tags"],
            ["historyRowsChecked"] = sourceCounts["format_claim_rules"] + sourceCounts["title_request_events"] +
                sourceCounts["email_delivery_events"] + sourceCounts["title_requests"]
        };
        foreach (var item in expected)
        {
            if (audit.GetProperty(item.Key).GetInt32() != item.Value)
            {
                throw ReportMismatch($"Source-to-target reconciliation count {item.Key} differs from the immutable package.");
            }
        }

        var expectedConfigurationRows = ExpectedConfigurationRowsChecked(package);
        var expectedConfigurationAudit = ExpectedConfigurationAuditCounts(package);
        if (audit.GetProperty("configurationRowsChecked").GetInt32() != expectedConfigurationRows ||
            audit.GetProperty("configurationFieldsChecked").GetInt32() != expectedConfigurationAudit.FieldsChecked ||
            audit.GetProperty("configurationRelationshipsChecked").GetInt32() != expectedConfigurationAudit.RelationshipsChecked)
        {
            throw ReportMismatch("Source-to-target configuration check totals do not match the immutable package.");
        }
    }

    private static (int FieldsChecked, int RelationshipsChecked) ExpectedConfigurationAuditCounts(
        ValidatedMigrationPackage package)
    {
        var systemSettings = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings").SingleOrDefault();
        var polarisSettings = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        var emailRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var rejectionRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates");
        var workflowRows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var legacyPatronRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings");
        var formatRows = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");

        var fields = 4 + (systemSettings?.HasValue("systemNotEnabledMessage") == true ? 1 : 0) +
            AuditSplitValues(systemSettings?.String("patronEmbedAllowedOrigins"))
                .Select(NormalizeAuditEmbedOrigin)
                .Distinct(StringComparer.Ordinal)
                .Count();
        if (polarisSettings.Count == 1)
        {
            fields += 8;
        }

        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        fields += 3 + ExpectedLibrarySenderAuditGroupCount(emailRows, organizations) * 2;

        var relationships = 0;
        foreach (var row in workflowRows)
        {
            fields += 21 + AuditCommonCreatorLines(row.Text("commonAuthorsList")).Length +
                AuditPatronCodeIds(row.Text("allowedPatronCodeIds")).Length;
            relationships += 2;

            var isSystem = row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase);
            for (var slot = 1; slot <= 4; slot++)
            {
                if (isSystem || row.HasValue($"externalSearch{slot}Enabled") || row.HasValue($"externalSearch{slot}Label") || row.HasValue($"externalSearch{slot}UrlTemplate"))
                {
                    fields += 3;
                    relationships++;
                }
            }
        }

        var organizationIds = organizations
            .Select(row => row.Int32("organizationId") ?? throw ReportMismatch("A source organization has no native identity."))
            .ToHashSet();
        var settingsOrganizations = new HashSet<int>();
        foreach (var row in uiRows)
        {
            var organizationId = AuditSettingsOrganizationId(package, row, "scope", "libraryOrganization");
            settingsOrganizations.Add(organizationId);
            if (organizationId == 1 && row.HasValue("systemNotEnabledMessage"))
            {
                fields++;
            }
        }

        var modernOverrideOrganizations = overrides
            .Select(row => row.Int32("orgId") ?? throw ReportMismatch("A patron settings override has no organization."))
            .ToHashSet();
        foreach (var organizationId in modernOverrideOrganizations)
        {
            if (organizationId == 1 || !organizationIds.Contains(organizationId))
            {
                throw ReportMismatch("A patron settings override references an unknown or system organization.");
            }
            settingsOrganizations.Add(organizationId);
        }

        foreach (var row in legacyPatronRows)
        {
            var organizationId = AuditSettingsOrganizationId(package, row, "libraryOrganization", "libraryOrganization");
            if (!modernOverrideOrganizations.Contains(organizationId))
            {
                settingsOrganizations.Add(organizationId);
            }
        }
        fields += settingsOrganizations.Count * 22;

        var publicationRows = new Dictionary<int, string>();
        foreach (var row in uiRows)
        {
            var value = row.Text("publicationOptions");
            if (value is not null && HasAuditConfigurationText(value) && AuditSettingsOrganizationId(package, row, "scope", "libraryOrganization") == 1)
            {
                publicationRows[1] = value;
            }
        }
        foreach (var row in overrides)
        {
            var value = row.Text("publicationOptions");
            if (value is not null && HasAuditConfigurationText(value))
            {
                publicationRows[row.Int32("orgId")!.Value] = value;
            }

            var definitionsJson = row.JsonText("additionalFieldDefinitions");
            if (definitionsJson is not null)
            {
                using var definitions = JsonDocument.Parse(definitionsJson);
                if (definitions.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw ReportMismatch("A custom-field definition snapshot is not an array.");
                }
                var definitionItems = definitions.RootElement.EnumerateArray().ToArray();
                var effectiveFormatCount = ExpectedEffectiveFormatCodes(package, row.Int32("orgId")!.Value).Count;
                foreach (var definition in definitionItems)
                {
                    var optionCount = AuditCustomFieldType(definition) == "select" &&
                        definition.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                        ? options.GetArrayLength()
                        : 0;
                    fields += 5 + optionCount * 3 + effectiveFormatCount * 2;
                    relationships += optionCount + effectiveFormatCount;
                }
            }

            if (row.JsonText("patronFormatRules") is { } formatRulesJson)
            {
                using var formatRules = JsonDocument.Parse(formatRulesJson);
                if (formatRules.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw ReportMismatch("A patron format rule snapshot is not an object.");
                }
                var formatRuleCount = ExpectedEffectiveFormatCodes(package, row.Int32("orgId")!.Value).Count;
                fields += formatRuleCount * 10;
            }
        }

        fields += publicationRows.Sum(item => AuditPublicationOptionCount(item.Value, isSystem: item.Key == 1)) * 4;
        relationships += publicationRows.Count;

        var exactSystemSourceCodes = formatRows
            .Where(row => row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.RequiredString("code"))
            .ToHashSet(StringComparer.Ordinal);
        var librariesWithRuleOverrides = overrides
            .Where(row => row.JsonText("patronFormatRules") is not null)
            .Select(row => row.Int32("orgId") ?? throw ReportMismatch("A patron format rule snapshot has no library identity."))
            .ToHashSet();
        foreach (var row in formatRows)
        {
            var isLibrary = row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase);
            var libraryId = isLibrary ? ResolveAuditLibraryId(package, row.RequiredString("libraryOrganization")) : 1;
            var sparse = isLibrary && exactSystemSourceCodes.Contains(row.RequiredString("code"));
            var ruleOverridePresent = isLibrary && librariesWithRuleOverrides.Contains(libraryId);
            if (!sparse)
            {
                fields += ruleOverridePresent ? 7 : 16;
            }
            else
            {
                var metadataFields = row.Names.Count(name => name is "label" or "sortOrder" or "enabled");
                fields += 2 + metadataFields + (ruleOverridePresent ? 0 : 9);
            }
            relationships++;
        }

        foreach (var template in emailRows.Concat(rejectionRows))
        {
            fields += 9;
            relationships++;
        }

        return (fields, relationships);
    }

    private static int ExpectedLibrarySenderAuditGroupCount(
        IReadOnlyList<SourceRow> rows,
        IReadOnlyList<SourceRow> organizations)
    {
        return rows
            .Where(row => row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => ResolveAuditLibraryId(organizations, row.RequiredString("libraryOrganization")))
            .Count(group => group.Any(row => !string.IsNullOrWhiteSpace(row.Text("fromAddress")) || !string.IsNullOrWhiteSpace(row.Text("fromName"))));
    }

    private static int AuditSettingsOrganizationId(
        ValidatedMigrationPackage package,
        SourceRow row,
        string scopeField,
        string libraryField)
    {
        if (scopeField == "scope" && row.RequiredString(scopeField).Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        return ResolveAuditLibraryId(organizations, row.RequiredString(libraryField));
    }

    private static int ResolveAuditLibraryId(IReadOnlyList<SourceRow> organizations, string reference)
    {
        var bySource = organizations.FirstOrDefault(row => row.RequiredString("id") == reference)?.Int32("organizationId");
        if (bySource is { } sourceId)
        {
            return sourceId;
        }
        return int.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               organizations.Any(row => row.Int32("organizationId") == parsed)
            ? parsed
            : throw ReportMismatch("A settings or email scope does not identify a source organization.");
    }

    private static string[] AuditSplitValues(string? source) =>
        string.IsNullOrWhiteSpace(source)
            ? []
            : source.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value.Length > 0)
                .ToArray();

    private static string[] AuditCommonCreatorLines(string? source) =>
        source is null
            ? []
            : source.Split('\n')
                .Select(TrimAuditConfigurationText)
                .Where(value => value.Length > 0)
                .ToArray();

    private static bool HasAuditConfigurationText(string? value) =>
        value is not null && TrimAuditConfigurationText(value).Length > 0;

    private static int[] AuditPatronCodeIds(string? source)
    {
        if (source is null)
        {
            return [];
        }
        var ids = new HashSet<int>();
        foreach (var raw in source.Split(','))
        {
            var value = TrimAuditConfigurationText(raw);
            if (value.Length == 0)
            {
                continue;
            }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                !string.Equals(value, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                throw ReportMismatch("A source patron-code identity cannot be represented exactly by the target.");
            }
            ids.Add(id);
        }
        return ids.Order().ToArray();
    }

    private static int AuditPublicationOptionCount(string rawValue, bool isSystem)
    {
        var trimmed = TrimAuditConfigurationText(rawValue);
        string[] labels;
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw ReportMismatch("Publication options are not an array.");
            }
            labels = document.RootElement.EnumerateArray()
                .Select(item => item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString() ?? string.Empty,
                    JsonValueKind.Object => AuditPublicationOptionLabel(item),
                    _ => throw ReportMismatch("A publication option has an unsupported source type.")
                })
                .Select(TrimAuditConfigurationText)
                .ToArray();
        }
        else
        {
            labels = trimmed.Split('\n')
                .Select(TrimAuditConfigurationText)
                .Where(label => label.Length > 0)
                .ToArray();
        }

        if (labels.Length == 0 || labels.Length > 3 && labels.Count(label => label.Length > 0 && label.All(character => character is >= '0' and <= '9')) * 2 > labels.Length)
        {
            return isSystem ? 3 : 0;
        }
        return labels.Length;
    }

    private static string AuditPublicationOptionLabel(JsonElement item)
    {
        foreach (var name in new[] { "label", "name", "value" })
        {
            if (!item.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            if (property.ValueKind != JsonValueKind.String)
            {
                throw ReportMismatch("A publication-option label alias has an unsupported source type.");
            }
            var value = property.GetString() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }
            if (TrimAuditConfigurationText(value).Length == 0)
            {
                throw ReportMismatch("A publication-option selected label alias is blank after normalization.");
            }
            return TrimAuditConfigurationText(value);
        }
        return string.Empty;
    }

    private static string NormalizeAuditEmbedOrigin(string value)
    {
        var origin = value.Trim();
        if (origin.Length == 0 || origin.Any(char.IsWhiteSpace) ||
            origin.IndexOfAny(['"', '\'', '`', ';', '\\']) >= 0)
        {
            throw ReportMismatch("A source patron embed origin is invalid.");
        }

        var wildcard = System.Text.RegularExpressions.Regex.Match(
            origin,
            "^(https?)://\\*\\.(.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (wildcard.Success)
        {
            var wildcardAuthority = wildcard.Groups[2].Value;
            var authorityMatch = System.Text.RegularExpressions.Regex.Match(
                wildcardAuthority,
                "^(?<host>[a-z0-9.-]+)(?::(?<port>[0-9]+))?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            var host = authorityMatch.Groups["host"].Value;
            if (!string.Equals(wildcard.Groups[1].Value, "https", StringComparison.OrdinalIgnoreCase) ||
                !authorityMatch.Success || !ValidAuditPort(authorityMatch) || !IsAuditDnsHostname(host) ||
                !host.TrimEnd('.').Contains('.') ||
                !Uri.TryCreate($"https://{wildcardAuthority}", UriKind.Absolute, out var wildcardUri) ||
                wildcardUri.HostNameType != UriHostNameType.Dns || wildcardUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(wildcardUri.Query) || !string.IsNullOrEmpty(wildcardUri.Fragment) ||
                !string.IsNullOrEmpty(wildcardUri.UserInfo) || wildcardUri.Port is < 0 or > 65535)
            {
                throw ReportMismatch("A source patron embed wildcard origin is invalid.");
            }
            return $"https://*.{wildcardAuthority.ToLowerInvariant()}";
        }

        var plain = System.Text.RegularExpressions.Regex.Match(
            origin,
            "^(https?)://([^/?#]+)(.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!plain.Success || plain.Groups[3].Value.Length > 0 && plain.Groups[3].Value != "/")
        {
            throw ReportMismatch("A source patron embed origin is invalid.");
        }
        var protocol = plain.Groups[1].Value.ToLowerInvariant();
        var authority = plain.Groups[2].Value;
        var authorityMatchPlain = System.Text.RegularExpressions.Regex.Match(
            authority,
            "^(?<host>[a-z0-9.-]+|\\[[0-9a-f:.]+\\])(?::(?<port>[0-9]+))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var rawHost = authorityMatchPlain.Success ? authorityMatchPlain.Groups["host"].Value : string.Empty;
        var ipv6Authority = rawHost.StartsWith("[", StringComparison.Ordinal);
        var unbracketedHost = ipv6Authority ? rawHost[1..^1] : rawHost;
        if (!authorityMatchPlain.Success || !ValidAuditPort(authorityMatchPlain) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.Equals(uri.Scheme, protocol, StringComparison.Ordinal) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.Port is < 0 or > 65535 ||
            (ipv6Authority && uri.HostNameType != UriHostNameType.IPv6) ||
            (!ipv6Authority && (uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
                uri.HostNameType == UriHostNameType.Dns && !IsAuditDnsHostname(unbracketedHost))))
        {
            throw ReportMismatch("A source patron embed origin authority is invalid.");
        }
        var local = unbracketedHost.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";
        if (protocol != "https" && !(protocol == "http" && local))
        {
            throw ReportMismatch("Only HTTPS or local HTTP embed origins are allowed.");
        }
        return $"{protocol}://{authority.ToLowerInvariant()}";
    }

    private static bool ValidAuditPort(System.Text.RegularExpressions.Match authority)
    {
        if (!authority.Groups["port"].Success)
        {
            return true;
        }
        return int.TryParse(authority.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= 0 and <= 65535;
    }

    private static bool IsAuditDnsHostname(string host)
    {
        var value = host.EndsWith(".", StringComparison.Ordinal) ? host[..^1] : host;
        if (value.Length is 0 or > 253)
        {
            return false;
        }
        return value.Split('.').All(label => label.Length is > 0 and <= 63 &&
            IsAuditAsciiAlphaNumeric(label[0]) && IsAuditAsciiAlphaNumeric(label[^1]) &&
            label.All(character => IsAuditAsciiAlphaNumeric(character) || character == '-'));
    }

    private static bool IsAuditAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static int ExpectedConfigurationRowsChecked(ValidatedMigrationPackage package)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var organizationBySourceId = organizations.ToDictionary(
            row => row.RequiredString("id"),
            row => row.Int32("organizationId") ?? throw ReportMismatch("A source organization has no native identity."),
            StringComparer.Ordinal);
        int ResolveLibrary(string reference)
        {
            if (organizationBySourceId.TryGetValue(reference, out var nativeId))
            {
                return nativeId;
            }
            return int.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                   organizationBySourceId.Values.Contains(parsed)
                ? parsed
                : throw ReportMismatch("A configuration audit scope does not identify a source organization.");
        }

        var emailTemplateRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var templateRows = emailTemplateRows
            .Concat(MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates"))
            .ToArray();
        var librarySenderRows = emailTemplateRows
            .Where(row => row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => ResolveLibrary(row.RequiredString("libraryOrganization")))
            .Count(group => group.Any(row =>
                !string.IsNullOrWhiteSpace(row.Text("fromAddress")) || !string.IsNullOrWhiteSpace(row.Text("fromName"))));

        var workflows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        var externalSearchRows = 0;
        foreach (var row in workflows)
        {
            var systemScope = row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase);
            for (var slot = 1; slot <= 4; slot++)
            {
                if (systemScope || row.HasValue($"externalSearch{slot}Enabled") ||
                    row.HasValue($"externalSearch{slot}Label") || row.HasValue($"externalSearch{slot}UrlTemplate"))
                {
                    externalSearchRows++;
                }
            }
        }

        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var customFieldRows = 0;
        var formatRuleRows = 0;
        foreach (var row in overrides)
        {
            if (row.JsonText("additionalFieldDefinitions") is { } definitionsJson)
            {
                using var definitions = JsonDocument.Parse(definitionsJson);
                if (definitions.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw ReportMismatch("A source custom-field definition is not an array.");
                }
                customFieldRows += definitions.RootElement.GetArrayLength();
            }
            if (row.JsonText("patronFormatRules") is { } rulesJson)
            {
                using var rules = JsonDocument.Parse(rulesJson);
                if (rules.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw ReportMismatch("A source material format-rule snapshot is not an object.");
                }
                formatRuleRows += ExpectedEffectiveFormatCodes(package, row.Int32("orgId")!.Value).Count;
            }
        }

        return 1 +
            MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings").Count +
            1 + librarySenderRows +
            workflows.Count + externalSearchRows +
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings").Count +
            overrides.Count +
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings").Count +
            customFieldRows + formatRuleRows +
            templateRows.Length +
            MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats").Count;
    }

    private static void RequireImportedCount(JsonElement imported, string name, int expected)
    {
        var actual = imported.TryGetProperty(name, out var count) ? count.GetInt32() : 0;
        if (actual != expected)
        {
            throw ReportMismatch($"Import report count {name} does not match its independently derived meaning.");
        }
    }

    private static MigrationOperationException ReportMismatch(string message) =>
        new("reconciliation_report_mismatch", message);

    private static SourcePlacementSummary ComputeSourcePlacementSummary(ValidatedMigrationPackage package)
    {
        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        var statusRows = MigrationPackageReader.ReadRowsOrEmpty(package, "title-request-events.json", "request_statuses");
        var closeReasonRows = MigrationPackageReader.ReadRowsOrEmpty(package, "title-request-events.json", "request_close_reasons");
        var statuses = statusRows.ToDictionary(
            row => row.RequiredString("id"),
            row => NormalizeAuditStatus(row.RequiredString("code")),
            StringComparer.Ordinal);
        var closeReasons = closeReasonRows.ToDictionary(
            row => row.RequiredString("id"),
            row => NormalizeAuditCloseReason(row.RequiredString("code")) ??
                   throw ReportMismatch("A source close-reason taxonomy entry is blank."),
            StringComparer.Ordinal);
        var eventsByRequest = new Dictionary<string, List<SourcePlacementEvent>>(StringComparer.Ordinal);
        foreach (var row in MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events"))
        {
            var requestId = row.RequiredString("titleRequest");
            if (!eventsByRequest.TryGetValue(requestId, out var requestEvents))
            {
                requestEvents = [];
                eventsByRequest.Add(requestId, requestEvents);
            }
            requestEvents.Add(new SourcePlacementEvent(
                row.RequiredString("id"),
                row.RequiredString("eventType").Trim().ToLowerInvariant(),
                ResolveAuditEventStatus(row.String("fromStatus"), statuses),
                ResolveAuditEventStatus(row.String("toStatus"), statuses),
                ResolveAuditEventCloseReason(row.String("closeReason"), closeReasons),
                ReadAuditEventBibIds(row)));
        }

        var terminalReasons = new[]
        {
            "hold_completed", "hold_not_picked_up", "hold_unclaimed", "hold_cancelled", "hold_expired"
        };
        var terminalSet = new HashSet<string>(terminalReasons, StringComparer.Ordinal);
        var evidenceKinds = new[]
        {
            "current_status", "terminal_close_reason", "dedicated_event", "transition_to_placed",
            "transition_from_placed", "event_terminal_reason"
        };
        var evidenceCounts = evidenceKinds.ToDictionary(kind => kind, _ => 0, StringComparer.Ordinal);
        var terminalCounts = terminalReasons.ToDictionary(reason => reason, _ => 0, StringComparer.Ordinal);
        var byLibraryAndStatus = new Dictionary<(int LibraryId, string Status, string Outcome), int>();
        var protectedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        var knownBibMarkers = 0;
        var explicitNullBibMarkers = 0;
        var noPlacementEvidence = 0;

        foreach (var request in requests)
        {
            var sourceId = request.RequiredString("id");
            var status = ResolveAuditRequestStatus(request, statuses);
            var closeReason = ResolveAuditRequestCloseReason(request, closeReasons);
            var libraryId = request.Int32("libraryOrgId") ??
                throw ReportMismatch("A source title request has no library identity.");
            var requestBib = request.PositiveInt32("bibid", "source_bib_invalid");
            var evidence = new HashSet<SourcePlacementEvidence>();
            if (status == "hold_placed")
            {
                evidence.Add(new("current_status", "title_requests", sourceId,
                    request.String("statusRef") is null ? "status" : "statusRef", status));
            }
            if (closeReason is not null && terminalSet.Contains(closeReason))
            {
                evidence.Add(new("terminal_close_reason", "title_requests", sourceId,
                    request.String("closeReasonRef") is null ? "closeReason" : "closeReasonRef", closeReason));
            }

            eventsByRequest.TryGetValue(sourceId, out var requestEvents);
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
                if (sourceEvent.CloseReason is not null && terminalSet.Contains(sourceEvent.CloseReason))
                {
                    evidence.Add(new("event_terminal_reason", "title_request_events", sourceEvent.Id, "closeReason", sourceEvent.CloseReason));
                }
            }

            var placed = evidence.Count > 0;
            var action = placed ? "inserted" : "no_placement_evidence";
            if (placed)
            {
                protectedSourceIds.Add(sourceId);
                foreach (var item in evidence)
                {
                    evidenceCounts[item.Kind]++;
                    if ((item.Kind is "terminal_close_reason" or "event_terminal_reason") && terminalCounts.ContainsKey(item.Value))
                    {
                        terminalCounts[item.Value]++;
                    }
                }
                var bibIds = new HashSet<int>();
                if (requestBib is { } requestBibId)
                {
                    bibIds.Add(requestBibId);
                }
                foreach (var sourceEvent in requestEvents ?? [])
                {
                    if (evidence.Any(item => item.SourceRecordId == sourceEvent.Id))
                    {
                        bibIds.UnionWith(sourceEvent.BibIds);
                    }
                }
                if (bibIds.Count > 1)
                {
                    throw ReportMismatch("A committed source package has conflicting placed-history BIB evidence.");
                }
                if (bibIds.Count == 0)
                {
                    explicitNullBibMarkers++;
                }
                else
                {
                    knownBibMarkers++;
                }
            }
            else
            {
                noPlacementEvidence++;
                var ambiguousHints = status == "closed" && closeReason is not "rejected" && requestBib is not null ||
                    (requestEvents ?? []).Any(item => item.BibIds.Count > 0);
                if (ambiguousHints)
                {
                    throw ReportMismatch("A committed source package contains unresolved placed-history hints.");
                }
            }

            var group = (libraryId, status, action);
            byLibraryAndStatus[group] = byLibraryAndStatus.GetValueOrDefault(group) + 1;
        }

        return new(
            requests.Count,
            protectedSourceIds.Count,
            knownBibMarkers,
            explicitNullBibMarkers,
            noPlacementEvidence,
            protectedSourceIds,
            byLibraryAndStatus,
            evidenceCounts,
            terminalCounts);
    }

    private static void ValidatePlacementAudit(JsonElement placement, SourcePlacementSummary expected)
    {
        var expectedCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sourceRequestsEvaluated"] = expected.SourceRequestsEvaluated,
            ["protectedRequests"] = expected.ProtectedRequests,
            ["knownBibMarkers"] = expected.KnownBibMarkers,
            ["explicitNullBibMarkers"] = expected.ExplicitNullBibMarkers,
            ["noPlacementEvidence"] = expected.NoPlacementEvidence,
            ["placementHistoryAmbiguous"] = 0,
            ["insertedMarkers"] = expected.ProtectedRequests,
            ["reusedMarkers"] = 0,
            ["fabricatedHoldPlacementOperations"] = 0
        };
        foreach (var item in expectedCounts)
        {
            if (placement.GetProperty(item.Key).GetInt32() != item.Value)
            {
                throw ReportMismatch($"Placement reconciliation count {item.Key} differs from source evidence.");
            }
        }

        var groupCounts = new Dictionary<(int LibraryId, string Status, string Outcome), int>();
        foreach (var item in placement.GetProperty("byLibraryAndStatus").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !HasNonNegativeInt32(item, "libraryOrganizationId") ||
                !HasString(item, "status") || !HasString(item, "outcome") ||
                !HasNonNegativeInt32(item, "count") || item.GetProperty("count").GetInt32() == 0)
            {
                throw InvalidReport("A placement by-library audit entry is malformed.");
            }
            var key = (
                item.GetProperty("libraryOrganizationId").GetInt32(),
                item.GetProperty("status").GetString()!,
                item.GetProperty("outcome").GetString()!);
            if (!groupCounts.TryAdd(key, item.GetProperty("count").GetInt32()))
            {
                throw InvalidReport("The placement audit contains duplicate library and status groups.");
            }
        }
        if (groupCounts.Count != expected.ByLibraryAndStatus.Count ||
            expected.ByLibraryAndStatus.Any(item => groupCounts.GetValueOrDefault(item.Key) != item.Value))
        {
            throw ReportMismatch("Placement by-library audit groups do not match source requests.");
        }

        ValidateStringCountArray(placement.GetProperty("evidenceClasses"), "kind", expected.EvidenceClasses, allowZero: true);
        ValidateStringCountArray(placement.GetProperty("terminalReasons"), "reason", expected.TerminalReasons, allowZero: true);
    }

    private static void ValidateStringCountArray(
        JsonElement items,
        string keyName,
        IReadOnlyDictionary<string, int> expected,
        bool allowZero)
    {
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !HasString(item, keyName) ||
                !HasNonNegativeInt32(item, "count") || (!allowZero && item.GetProperty("count").GetInt32() == 0))
            {
                throw InvalidReport("A reconciliation count group is malformed.");
            }
            if (!actual.TryAdd(item.GetProperty(keyName).GetString()!, item.GetProperty("count").GetInt32()))
            {
                throw InvalidReport("A reconciliation count group is duplicated.");
            }
        }
        if (actual.Count != expected.Count || expected.Any(item => actual.GetValueOrDefault(item.Key, int.MinValue) != item.Value))
        {
            throw ReportMismatch("A reconciliation count group differs from the immutable source package.");
        }
    }

    private static BibAuthoritySummary ComputeSourceBibAuthoritySummary(
        ValidatedMigrationPackage package,
        IReadOnlySet<string> protectedSourceIds)
    {
        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        var classifications = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceRequestsWithBibId = 0;
        var automationDerived = 0;
        foreach (var row in requests)
        {
            var bibId = row.PositiveInt32("bibid", "source_bib_invalid");
            if (bibId is null)
            {
                continue;
            }
            sourceRequestsWithBibId++;
            var status = NormalizeAuditIsbnStatus(row.String("isbnCheckStatus"), row.String("identifier"), bibId.Value);
            var lastChecked = row.UtcDateTime("lastChecked");
            var updated = row.UtcDateTime("updated");
            var isAutomationDerived = status == "found" &&
                !string.IsNullOrWhiteSpace(row.String("identifier")) && lastChecked.HasValue && lastChecked == updated;
            var classification = isAutomationDerived
                ? "automation_derived"
                : protectedSourceIds.Contains(row.RequiredString("id"))
                    ? "ambiguous_protected_by_placed_history"
                    : "ambiguous_noneligible_identifier_state";
            classifications[classification] = classifications.GetValueOrDefault(classification) + 1;
            if (isAutomationDerived)
            {
                automationDerived++;
            }
        }
        return new(sourceRequestsWithBibId, automationDerived, sourceRequestsWithBibId - automationDerived, classifications);
    }

    private static void ValidateBibAuthorityAudit(JsonElement authority, BibAuthoritySummary expected)
    {
        if (authority.GetProperty("sourceRequestsWithBibId").GetInt32() != expected.SourceRequestsWithBibId ||
            authority.GetProperty("automationDerivedBibs").GetInt32() != expected.AutomationDerived ||
            authority.GetProperty("staffAuthoritativeBibs").GetInt32() != 0 ||
            authority.GetProperty("ambiguousBibsImportedWithoutStaffAuthority").GetInt32() != expected.AmbiguousWithoutStaffAuthority ||
            authority.GetProperty("blockedRiskBibs").GetInt32() != 0)
        {
            throw ReportMismatch("BIB-authority audit totals differ from the immutable source package.");
        }
        ValidateStringCountArray(
            authority.GetProperty("byClassification"),
            "classification",
            expected.Classifications,
            allowZero: false);
    }

    private static string ResolveAuditRequestStatus(SourceRow row, IReadOnlyDictionary<string, string> statuses)
    {
        var raw = row.String("status");
        var reference = row.String("statusRef");
        var rawStatus = raw is null ? null : NormalizeAuditStatus(raw);
        var referencedStatus = reference is null
            ? null
            : statuses.TryGetValue(reference, out var value)
                ? value
                : throw ReportMismatch("A source title request status reference is unresolved.");
        if (rawStatus is not null && referencedStatus is not null && rawStatus != referencedStatus)
        {
            throw ReportMismatch("A source title request has conflicting status values.");
        }
        return rawStatus ?? referencedStatus ?? throw ReportMismatch("A source title request has no status.");
    }

    private static string? ResolveAuditRequestCloseReason(SourceRow row, IReadOnlyDictionary<string, string> reasons)
    {
        var raw = NormalizeAuditCloseReason(row.String("closeReason"));
        var reference = row.String("closeReasonRef");
        var referenced = reference is null
            ? null
            : reasons.TryGetValue(reference, out var value)
                ? value
                : throw ReportMismatch("A source title request close-reason reference is unresolved.");
        if (raw is not null && referenced is not null && raw != referenced)
        {
            throw ReportMismatch("A source title request has conflicting close-reason values.");
        }
        return raw ?? referenced;
    }

    private static string? ResolveAuditEventStatus(string? value, IReadOnlyDictionary<string, string> statuses)
    {
        if (value is null)
        {
            return null;
        }
        return statuses.TryGetValue(value, out var status) ? status : NormalizeAuditStatus(value);
    }

    private static string? ResolveAuditEventCloseReason(string? value, IReadOnlyDictionary<string, string> reasons)
    {
        if (value is null)
        {
            return null;
        }
        return reasons.TryGetValue(value, out var reason) ? reason : NormalizeAuditCloseReason(value);
    }

    private static string NormalizeAuditStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" or "suggestion" => "suggestion",
        "1" or "5" or "pending_hold" or "pendinghold" => "pending_hold",
        "2" or "hold_placed" or "holdplaced" => "hold_placed",
        "3" or "outstanding_purchase" or "outstandingpurchase" => "outstanding_purchase",
        "4" or "closed" => "closed",
        _ => throw ReportMismatch("A source request status cannot be reconciled from its immutable package.")
    };

    private static string? NormalizeAuditCloseReason(string? value) => value?.Trim().ToLowerInvariant() switch
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
        _ => throw ReportMismatch("A source request close reason cannot be reconciled from its immutable package.")
    };

    private static string? NormalizeAuditIsbnStatus(string? value, string? identifier, int bibId) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "pending" => "pending",
        "found" => "found",
        "not_found" => "not_found",
        "skipped_no_isbn" => "skipped_no_isbn",
        "error_max_retries" => "error_max_retries",
        "error" when identifier is null => "skipped_no_isbn",
        "found_in_polaris" => "found",
        _ => throw ReportMismatch("A source BIB authority status cannot be reconciled from its immutable package.")
    };

    private static HashSet<int> ReadAuditEventBibIds(SourceRow row)
    {
        var result = new HashSet<int>();
        var directField = row.Names.FirstOrDefault(name =>
            string.Equals(name, "bibId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "bibid", StringComparison.OrdinalIgnoreCase));
        if (directField is not null && row.PositiveInt32(directField, "source_bib_invalid") is { } directBibId)
        {
            result.Add(directBibId);
        }

        if (row.JsonText("metadata") is { } metadata)
        {
            using var document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject().Where(item =>
                             string.Equals(item.Name, "bibId", StringComparison.OrdinalIgnoreCase)))
                {
                    var sourceValue = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.GetRawText();
                    if (SourceRow.ParsePositiveInt32(sourceValue, $"{row.String("id")}/metadata.{property.Name}", "source_bib_invalid") is { } bibId)
                    {
                        result.Add(bibId);
                    }
                }
            }
        }
        return result;
    }

    private sealed record SourcePlacementEvidence(
        string Kind,
        string SourceCollection,
        string SourceRecordId,
        string SourceField,
        string Value);

    private sealed record SourcePlacementEvent(
        string Id,
        string EventType,
        string? FromStatus,
        string? ToStatus,
        string? CloseReason,
        IReadOnlySet<int> BibIds);

    private sealed record SourcePlacementSummary(
        int SourceRequestsEvaluated,
        int ProtectedRequests,
        int KnownBibMarkers,
        int ExplicitNullBibMarkers,
        int NoPlacementEvidence,
        IReadOnlySet<string> ProtectedSourceIds,
        IReadOnlyDictionary<(int LibraryId, string Status, string Outcome), int> ByLibraryAndStatus,
        IReadOnlyDictionary<string, int> EvidenceClasses,
        IReadOnlyDictionary<string, int> TerminalReasons);

    private sealed record BibAuthoritySummary(
        int SourceRequestsWithBibId,
        int AutomationDerived,
        int AmbiguousWithoutStaffAuthority,
        IReadOnlyDictionary<string, int> Classifications);

    private static void VerifyReportCounts(JsonElement report, ValidatedMigrationPackage package, string connectionString)
    {
        var imported = report.GetProperty("importedCounts");
        var sourceCounts = ReadSourceCounts(package);
        var titleClaims = ValidateClaimGroups(
            report.GetProperty("claimReconciliation").GetProperty("titleRequests"),
            sourceCounts["title_requests"],
            titleRequests: true);
        var copyClaims = ValidateClaimGroups(
            report.GetProperty("claimReconciliation").GetProperty("additionalCopies"),
            sourceCounts["additional_copy_requests"],
            titleRequests: false);
        var placement = ComputeSourcePlacementSummary(package);
        var bibAuthority = ComputeSourceBibAuthoritySummary(package, placement.ProtectedSourceIds);
        var bootstrapStaffUsers = imported.TryGetProperty("migration_bootstrap_staff_users", out var bootstrapCount)
            ? bootstrapCount.GetInt32()
            : 0;
        if (bootstrapStaffUsers is < 0 or > 1)
        {
            throw ReportMismatch("The bootstrap staff count is outside the supported source transformation.");
        }

        var expectedTargetCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["organizations"] = ExpectedOrganizationPopulation(package),
            ["staff_users"] = sourceCounts["staff_users"] + bootstrapStaffUsers,
            ["format_auto_claim_rules"] = sourceCounts["format_claim_rules"],
            ["title_requests"] = sourceCounts["title_requests"],
            ["additional_copy_requests"] = sourceCounts["additional_copy_requests"],
            ["deleted_request_audit"] = sourceCounts["deleted_request_audit"],
            ["title_request_events"] = sourceCounts["title_request_events"] + placement.ProtectedRequests + titleClaims,
            ["legacy_mappings"] = ExpectedLegacyMappingPopulation(sourceCounts, package),
            ["patron_sessions"] = 0,
            ["email_outbox"] = 0,
            ["queue_progress"] = 0,
            ["hold_placement_operations"] = 0,
            ["claim_migration_annotations"] = titleClaims,
            ["placed_bib_protection_markers"] = placement.ProtectedRequests,
            ["additional_copy_claim_migration_annotations"] = copyClaims,
            ["invalid_active_claim_rules"] = 0,
            ["invalid_open_title_request_claims"] = 0,
            ["invalid_open_additional_copy_claims"] = 0,
            ["invalid_found_requests"] = 0,
            ["title_request_bibs_automation_derived"] = bibAuthority.AutomationDerived,
            ["title_request_bibs_ambiguous_unverified"] = bibAuthority.AmbiguousWithoutStaffAuthority,
            ["title_request_bibs_staff_authoritative"] = 0
        };
        var targets = report.GetProperty("targetCounts");
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        VerifyStaffTargetPopulation(report, package, connection, bootstrapStaffUsers);
        var sqlCounts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["organizations"] = "SELECT COUNT(*) FROM [asap].[Organization];",
            ["staff_users"] = "SELECT COUNT(*) FROM [asap].[StaffUser];",
            ["format_auto_claim_rules"] = "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule];",
            ["title_requests"] = "SELECT COUNT(*) FROM [asap].[TitleRequest];",
            ["additional_copy_requests"] = "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest];",
            ["deleted_request_audit"] = "SELECT COUNT(*) FROM [asap].[DeletedRequestAudit];",
            ["title_request_events"] = "SELECT COUNT(*) FROM [asap].[TitleRequestEvent];",
            ["legacy_mappings"] = "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping];",
            ["patron_sessions"] = "SELECT COUNT(*) FROM [asap].[PatronSession];",
            ["email_outbox"] = "SELECT COUNT(*) FROM [asap].[EmailOutbox];",
            ["queue_progress"] = "SELECT COUNT(*) FROM [asap].[QueueProgress];",
            ["hold_placement_operations"] = "SELECT COUNT(*) FROM [asap].[HoldPlacementOperation];",
            ["claim_migration_annotations"] = "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';",
            ["placed_bib_protection_markers"] = "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';",
            ["additional_copy_claim_migration_annotations"] = "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] WHERE [Notes] LIKE N'%[[]ASAP migration:additional_copy_claim_v1]%';",
            ["invalid_active_claim_rules"] = "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] r LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[StaffUserId] WHERE r.[IsActive] = 1 AND (s.[Id] IS NULL OR s.[IsActive] = 0 OR NULLIF(LTRIM(RTRIM(s.[UserPrincipalName])), N'') IS NULL OR s.[NormalizedUserPrincipalName] IS NULL OR s.[NormalizedUserPrincipalName] <> UPPER(LTRIM(RTRIM(s.[UserPrincipalName]))) OR NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));",
            ["invalid_open_title_request_claims"] = "SELECT COUNT(*) FROM [asap].[TitleRequest] r LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[ClaimedByStaffUserId] WHERE r.[Status] IN (N'suggestion', N'outstanding_purchase', N'pending_hold', N'hold_placed') AND r.[ClaimedByStaffUserId] IS NOT NULL AND (s.[Id] IS NULL OR s.[IsActive] = 0 OR NULLIF(LTRIM(RTRIM(s.[UserPrincipalName])), N'') IS NULL OR s.[NormalizedUserPrincipalName] IS NULL OR s.[NormalizedUserPrincipalName] <> UPPER(LTRIM(RTRIM(s.[UserPrincipalName]))) OR NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));",
            ["invalid_open_additional_copy_claims"] = "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] r LEFT JOIN [asap].[StaffUser] s ON s.[Id] = r.[ClaimedByStaffUserId] WHERE r.[Status] = N'open' AND r.[ClaimedByStaffUserId] IS NOT NULL AND (s.[Id] IS NULL OR s.[IsActive] = 0 OR NULLIF(LTRIM(RTRIM(s.[UserPrincipalName])), N'') IS NULL OR s.[NormalizedUserPrincipalName] IS NULL OR s.[NormalizedUserPrincipalName] <> UPPER(LTRIM(RTRIM(s.[UserPrincipalName]))) OR NOT ((s.[Role] IN (N'staff', N'admin') AND s.[OrganizationId] = r.[LibraryOrganizationId]) OR (s.[Role] = N'super_admin' AND s.[OrganizationId] = 1)));",
            ["invalid_found_requests"] = "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] = N'found' AND [BibId] IS NULL;",
            ["title_request_bibs_automation_derived"] = "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [BibId] IS NOT NULL AND [IsbnCheckStatus] = N'found' AND NULLIF(LTRIM(RTRIM([Identifier])), N'') IS NOT NULL AND [LastCheckedUtc] IS NOT NULL AND [LastCheckedUtc] = [UpdatedUtc];",
            ["title_request_bibs_ambiguous_unverified"] = "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [BibId] IS NOT NULL AND ([IsbnCheckStatus] IS NULL OR [IsbnCheckStatus] <> N'found' OR NULLIF(LTRIM(RTRIM([Identifier])), N'') IS NULL OR [LastCheckedUtc] IS NULL OR [LastCheckedUtc] <> [UpdatedUtc]);",
            ["title_request_bibs_staff_authoritative"] = "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [BibIdStaffVerified] = 1;"
        };
        foreach (var expected in expectedTargetCounts)
        {
            if (targets.GetProperty(expected.Key).GetInt32() != expected.Value)
            {
                throw ReportMismatch($"Import report target count {expected.Key} differs from source-derived expectations.");
            }
        }
        foreach (var expected in sqlCounts)
        {
            var actual = ReadCount(connection, expected.Value);
            if (expectedTargetCounts[expected.Key] != actual)
            {
                throw new MigrationOperationException(
                    "reconciliation_report_mismatch",
                    $"Target count {expected.Key} does not match the immutable source package and SQL state.");
            }
        }
    }

    private static void VerifyStaffTargetPopulation(
        JsonElement report,
        ValidatedMigrationPackage package,
        SqlConnection connection,
        int bootstrapStaffUsers)
    {
        var sourceRows = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var staffTransforms = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "staff_user")
            .ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
        var expectedTargetIds = new HashSet<long>();
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin")
            .ToArray();
        long? promotedBootstrapTargetId = null;
        if (transformations.Length == 1)
        {
            var bootstrap = transformations[0];
            var targetId = bootstrap.GetProperty("targetStaffUserId").GetInt64();
            var email = bootstrap.GetProperty("authenticationEmail").GetString();
            var action = bootstrap.GetProperty("action").GetString();
            if (string.IsNullOrWhiteSpace(email) ||
                action == "inserted" && (bootstrapStaffUsers != 1 || !expectedTargetIds.Add(targetId)) ||
                action == "promoted_existing" && bootstrapStaffUsers != 0 ||
                action is not ("inserted" or "promoted_existing"))
            {
                throw ReportMismatch("The bootstrap transformation does not identify the permitted target staff population.");
            }
            if (action == "promoted_existing")
            {
                promotedBootstrapTargetId = targetId;
            }

            using var bootstrapUser = connection.CreateCommand();
            bootstrapUser.CommandText = "SELECT [UserPrincipalName], [NormalizedUserPrincipalName], [Role], [OrganizationId], [IsActive] FROM [asap].[StaffUser] WHERE [Id] = @id;";
            bootstrapUser.Parameters.AddWithValue("@id", targetId);
            using var reader = bootstrapUser.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) ||
                !string.Equals(reader.GetString(0), email, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), email.ToUpperInvariant(), StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), "super_admin", StringComparison.Ordinal) ||
                reader.GetInt32(3) != 1 || !reader.GetBoolean(4))
            {
                throw ReportMismatch("The bootstrap transformation does not match its active system super-admin row.");
            }
        }
        else if (bootstrapStaffUsers != 0)
        {
            throw ReportMismatch("The bootstrap staff count has no matching target transformation.");
        }

        foreach (var row in sourceRows)
        {
            using var mapping = connection.CreateCommand();
            mapping.CommandText = "SELECT s.[Id], s.[Role], s.[OrganizationId], s.[IsActive], s.[UserPrincipalName], s.[NormalizedUserPrincipalName], s.[NotificationEmail], s.[WeeklyActionSummaryEmail], s.[WeeklyActionSummaryEnabled] FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[StaffUser] s ON s.[Id] = m.[NewId] WHERE m.[EntityType] = N'staff_user' AND m.[PocketBaseId] = @sourceId;";
            mapping.Parameters.AddWithValue("@sourceId", row.RequiredString("id"));
            using var reader = mapping.ExecuteReader();
            if (!reader.Read())
            {
                throw new MigrationOperationException("reconciliation_failed", "A source staff row has no unique target mapping.");
            }
            var targetId = reader.GetInt64(0);
            var targetRole = reader.GetString(1);
            var targetOrganizationId = reader.GetInt32(2);
            var targetActive = reader.GetBoolean(3);
            var targetAuthenticationEmail = reader.IsDBNull(4) ? null : reader.GetString(4);
            var targetNormalizedAuthenticationEmail = reader.IsDBNull(5) ? null : reader.GetString(5);
            var targetNotificationEmail = reader.IsDBNull(6) ? null : reader.GetString(6);
            var targetWeeklyEmail = reader.IsDBNull(7) ? null : reader.GetString(7);
            var targetWeeklyEnabled = reader.GetBoolean(8);
            reader.Close();
            var role = row.RequiredString("role").ToLowerInvariant();
            var organizationId = role == "super_admin"
                ? 1
                : row.Int32("libraryOrgId") ?? throw ReportMismatch("A source staff row has no library identity.");
            var authenticationEmail = ExpectedAuthenticationEmail(row.String("email"));
            if (!expectedTargetIds.Add(targetId) ||
                targetId != promotedBootstrapTargetId &&
                (!string.Equals(targetRole, role, StringComparison.Ordinal) ||
                 targetOrganizationId != organizationId ||
                 targetActive != row.Bool("active") ||
                 !string.Equals(targetAuthenticationEmail, authenticationEmail, StringComparison.Ordinal) ||
                 !string.Equals(targetNormalizedAuthenticationEmail, authenticationEmail?.ToUpperInvariant(), StringComparison.Ordinal)))
            {
                throw new MigrationOperationException("reconciliation_failed", "Mapped StaffUser identity or authorization differs from the immutable source package.");
            }

            if (!staffTransforms.TryGetValue(row.RequiredString("id"), out var transformation))
            {
                throw ReportMismatch("A staff-user transformation is missing its source row.");
            }
            ValidateStaffUserTransformation(
                transformation,
                row,
                targetNotificationEmail,
                targetWeeklyEmail,
                targetWeeklyEnabled,
                targetRole,
                targetOrganizationId,
                targetActive,
                targetAuthenticationEmail,
                targetNormalizedAuthenticationEmail,
                TargetOrganizationIsActive(connection, targetOrganizationId));
        }

        if (promotedBootstrapTargetId is { } promotedId && !expectedTargetIds.Contains(promotedId))
        {
            throw ReportMismatch("The promoted bootstrap staff row does not map to a source staff identity.");
        }

        using (var usableSystemSuperAdmin = connection.CreateCommand())
        {
            usableSystemSuperAdmin.CommandText = "SELECT [UserPrincipalName], [NormalizedUserPrincipalName] FROM [asap].[StaffUser] WHERE [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;";
            using var reader = usableSystemSuperAdmin.ExecuteReader();
            var found = false;
            while (reader.Read())
            {
                var email = ExpectedAuthenticationEmail(reader.IsDBNull(0) ? null : reader.GetString(0));
                found |= email is not null && !reader.IsDBNull(1) &&
                    string.Equals(email.ToUpperInvariant(), reader.GetString(1), StringComparison.Ordinal);
            }
            if (!found)
            {
                throw ReportMismatch("Target has no active system super-admin with a valid authentication email.");
            }
        }

        using var targetUsers = connection.CreateCommand();
        targetUsers.CommandText = "SELECT [Id] FROM [asap].[StaffUser];";
        using var targetReader = targetUsers.ExecuteReader();
        var actualTargetIds = new HashSet<long>();
        while (targetReader.Read())
        {
            actualTargetIds.Add(targetReader.GetInt64(0));
        }
        if (!actualTargetIds.SetEquals(expectedTargetIds))
        {
            throw new MigrationOperationException("reconciliation_failed", "Target StaffUser population contains rows outside the source mapping and bootstrap transformation.");
        }
    }

    private static void ValidateStaffUserTransformation(
        JsonElement transformation,
        SourceRow source,
        string? targetNotificationEmail,
        string? targetWeeklyEmail,
        bool targetWeeklyEnabled,
        string targetRole,
        int targetOrganizationId,
        bool targetActive,
        string? targetAuthenticationEmail,
        string? targetNormalizedAuthenticationEmail,
        bool targetOrganizationActive)
    {
        var sourceEmailValue = source.Text("email");
        var sourceWeeklyValue = source.Text("weekly_action_summary_email");
        var sourceAssignmentRecipient = NormalizeAuditRecipient(
            sourceWeeklyValue is { Length: > 0 } ? sourceWeeklyValue : sourceEmailValue);
        var sourceWeeklyRecipient = NormalizeAuditRecipient(sourceWeeklyValue);
        var sourceEmail = ExpectedAuthenticationEmail(source.String("email"));
        var weeklyEmail = ExpectedAuthenticationEmail(source.String("weekly_action_summary_email"));
        var notificationEmailSource = sourceEmail is not null
            ? "staff_email"
            : weeklyEmail is not null
                ? "weekly_action_summary_email"
                : "none";
        var sourceWeeklyEligible = source.Bool("weekly_action_summary_enabled") && source.Bool("verified") && sourceWeeklyRecipient is not null;

        var targetAssignmentRecipient = targetNotificationEmail;
        var targetWeeklyRecipient = targetWeeklyEmail ?? targetNotificationEmail;
        var targetScopeValid = targetRole == "super_admin"
            ? targetOrganizationId == 1
            : targetRole is "staff" or "admin" && targetOrganizationId != 1;
        var validTargetAuthenticationEmail = ExpectedAuthenticationEmail(targetAuthenticationEmail);
        var targetWeeklyEligible = targetWeeklyEnabled && targetWeeklyRecipient is not null && targetActive &&
            validTargetAuthenticationEmail is not null &&
            string.Equals(validTargetAuthenticationEmail.ToUpperInvariant(), targetNormalizedAuthenticationEmail, StringComparison.Ordinal) &&
            targetScopeValid && targetOrganizationActive;

        if (!string.Equals(transformation.GetProperty("notificationEmailSource").GetString(), notificationEmailSource, StringComparison.Ordinal) ||
            !NullableJsonStringEquals(transformation, "sourceAssignmentRecipient", sourceAssignmentRecipient) ||
            !NullableJsonStringEquals(transformation, "sourcePurchaseReminderRecipient", sourceWeeklyRecipient) ||
            !NullableJsonStringEquals(transformation, "sourceAdditionalCopyReminderRecipient", sourceWeeklyRecipient) ||
            !NullableJsonStringEquals(transformation, "sourceWeeklyRecipient", sourceWeeklyRecipient) ||
            !NullableJsonStringEquals(transformation, "targetAssignmentRecipient", targetAssignmentRecipient) ||
            !NullableJsonStringEquals(transformation, "targetPurchaseReminderRecipient", targetNotificationEmail) ||
            !NullableJsonStringEquals(transformation, "targetAdditionalCopyReminderRecipient", targetNotificationEmail) ||
            !NullableJsonStringEquals(transformation, "targetWeeklyRecipient", targetWeeklyRecipient) ||
            transformation.GetProperty("assignmentRecipientChanged").GetBoolean() != !AuditRecipientEquals(sourceAssignmentRecipient, targetAssignmentRecipient) ||
            transformation.GetProperty("purchaseReminderRecipientChanged").GetBoolean() != !AuditRecipientEquals(sourceWeeklyRecipient, targetNotificationEmail) ||
            transformation.GetProperty("additionalCopyReminderRecipientChanged").GetBoolean() != !AuditRecipientEquals(sourceWeeklyRecipient, targetNotificationEmail) ||
            transformation.GetProperty("weeklyRecipientChanged").GetBoolean() != !AuditRecipientEquals(sourceWeeklyRecipient, targetWeeklyRecipient) ||
            transformation.GetProperty("sourceWeeklyEligible").GetBoolean() != sourceWeeklyEligible ||
            transformation.GetProperty("targetWeeklyEligible").GetBoolean() != targetWeeklyEligible ||
            transformation.GetProperty("newlyWeeklyEligible").GetBoolean() != (!sourceWeeklyEligible && targetWeeklyEligible) ||
            transformation.GetProperty("targetHasNotificationEmail").GetBoolean() != (targetNotificationEmail is not null) ||
            transformation.GetProperty("targetHasWeeklyRecipient").GetBoolean() != (targetWeeklyRecipient is not null))
        {
            throw ReportMismatch("Staff-user transformation fields differ from immutable source and SQL target state.");
        }
    }

    private static string? NormalizeAuditRecipient(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool AuditRecipientEquals(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static bool TargetOrganizationIsActive(SqlConnection connection, int organizationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT [IsActive] FROM [asap].[Organization] WHERE [Id] = @organizationId;";
        command.Parameters.AddWithValue("@organizationId", organizationId);
        return command.ExecuteScalar() is true;
    }

    private static string? ExpectedAuthenticationEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = value.Trim();
        if (clean.EndsWith("@staff.asap.local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

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

    private static bool NullableStringEquals(SqlDataReader reader, int ordinal, string? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static int ExpectedOrganizationPopulation(ValidatedMigrationPackage package)
    {
        var ids = new HashSet<int> { 1 };
        foreach (var row in MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations"))
        {
            ids.Add(row.Int32("organizationId") ?? throw ReportMismatch("A source organization has no native ID."));
        }
        return ids.Count;
    }

    private static int ExpectedLegacyMappingPopulation(IReadOnlyDictionary<string, int> sourceCounts, ValidatedMigrationPackage package)
    {
        var formats = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
        var exactSystemSourceCodes = formats
            .Where(row => row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.RequiredString("code"))
            .ToHashSet(StringComparer.Ordinal);
        var baseFormats = formats.Count(row =>
            row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase) ||
            !exactSystemSourceCodes.Contains(row.RequiredString("code")));
        var formatOverrides = formats.Count - baseFormats;
        return sourceCounts["polaris_organizations"] + sourceCounts["staff_users"] +
            sourceCounts["email_templates"] + sourceCounts["rejection_templates"] + baseFormats + formatOverrides +
            sourceCounts["workflow_tags"] + sourceCounts["format_claim_rules"] + sourceCounts["title_requests"] +
            sourceCounts["additional_copy_requests"] + sourceCounts["deleted_request_audit"] +
            sourceCounts["title_request_tags"] + sourceCounts["title_request_events"] + sourceCounts["email_delivery_events"];
    }

    private static string NormalizeAuditFormatCode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" => "book",
        "1" => "ebook",
        "2" => "audiobook_cd",
        "3" => "eaudiobook",
        "4" => "dvd",
        "5" => "music_cd",
        var code => code
    };

    private static int ReadCount(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetReportState(string path, string state, bool reconciliationPassed)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ??
            throw new MigrationOperationException("reconciliation_report_invalid", "The restricted import report is invalid.");
        node["reportState"] = state;
        node["reconciliationPassed"] = reconciliationPassed;
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    internal static string ComputeTargetFingerprint(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        return ComputeTargetFingerprint(connection, transaction: null);
    }

    internal static string ComputeTargetIdentitySha256(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        return ComputeTargetIdentitySha256(connection, transaction: null);
    }

    internal static string ComputeTargetIdentitySha256(SqlConnection connection, SqlTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY(N'ServerName')), DB_NAME();";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            throw new MigrationOperationException(
                "target_identity_unavailable",
                "SQL Server did not return the current server and database identity.");
        }

        var serverName = reader.GetString(0).Trim();
        var databaseName = reader.GetString(1).Trim();
        if (serverName.Length == 0 || databaseName.Length == 0)
        {
            throw new MigrationOperationException(
                "target_identity_unavailable",
                "SQL Server returned an empty server or database identity.");
        }

        var identity = $"{serverName.Length}:{serverName}{databaseName.Length}:{databaseName}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    internal static string ComputeTargetFingerprint(SqlConnection connection, SqlTransaction? transaction)
    {
        var tables = ReadTables(connection, transaction);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            Append(hash, $"table:{table.Name}");
            foreach (var column in table.Columns)
            {
                Append(hash, $"column:{column}");
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var selected = string.Join(", ", table.Columns.Select(QuoteIdentifier));
            var ordered = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
            command.CommandText =
                $"SELECT {selected} FROM [asap].{QuoteIdentifier(table.Name)} ORDER BY {ordered};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Append(hash, "row");
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    Append(hash, CanonicalValue(table.Columns[index], reader.GetValue(index)));
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static int ReadQueueProgressCount(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT COUNT(*) FROM [asap].[QueueProgress];", connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IReadOnlyList<TargetTable> ReadTables(SqlConnection connection, SqlTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                t.[name], c.[name], c.[column_id],
                CASE WHEN ic.[column_id] IS NULL THEN 0 ELSE ic.[key_ordinal] END AS [key_ordinal]
            FROM sys.tables t
            JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
            JOIN sys.columns c ON c.[object_id] = t.[object_id]
            JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
            LEFT JOIN sys.indexes i ON i.[object_id] = t.[object_id] AND i.[is_primary_key] = 1
            LEFT JOIN sys.index_columns ic
              ON ic.[object_id] = i.[object_id]
             AND ic.[index_id] = i.[index_id]
             AND ic.[column_id] = c.[column_id]
            WHERE s.[name] = N'asap'
              AND c.[is_computed] = 0
              AND ty.[name] NOT IN (N'timestamp', N'rowversion')
            ORDER BY t.[name], c.[column_id];
            """;
        var builders = new SortedDictionary<string, TableBuilder>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (!builders.TryGetValue(table, out var builder))
            {
                builder = new TableBuilder();
                builders.Add(table, builder);
            }
            var column = reader.GetString(1);
            builder.Columns.Add(column);
            var keyOrdinal = reader.GetInt32(3);
            if (keyOrdinal > 0)
            {
                builder.Keys.Add((keyOrdinal, column));
            }
        }
        return builders.Select(item => new TargetTable(
                item.Key,
                item.Value.Columns,
                item.Value.Keys.Count == 0
                    ? item.Value.Columns
                    : item.Value.Keys.OrderBy(key => key.Ordinal).Select(key => key.Name).ToArray()))
            .ToArray();
    }

    private static string CanonicalValue(string column, object value)
    {
        return value switch
        {
            DBNull => "null",
            byte[] bytes => $"bytes:{Convert.ToHexString(bytes)}",
            DateTime timestamp => $"datetime:{timestamp:O}",
            DateTimeOffset timestamp => $"datetimeoffset:{timestamp:O}",
            Guid identifier => $"guid:{identifier:D}",
            bool boolean => boolean ? "bool:1" : "bool:0",
            string text => $"string:{text}",
            IFormattable formattable => $"value:{formattable.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $"value:{value}"
        };
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string QuoteIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed class TableBuilder
    {
        public List<string> Columns { get; } = [];
        public List<(int Ordinal, string Name)> Keys { get; } = [];
    }

    private sealed record TargetTable(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> KeyColumns);
}
