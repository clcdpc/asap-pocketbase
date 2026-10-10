using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Asap.Shared;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

internal static class MigrationIndependentReportVerifier
{
    private const string PolarisSettingsOperationContext = "owning_request_or_effective_servicing_library";
    private const string RetiredSmtpTransport = "legacy_smtp_transport_intentionally_dropped";
    private const string ConfiguredEmailSenderBoundary = "target_email_sender_selected_by_external_configuration";
    private const string SystemMisconfiguredMessage = "target_code_default";
    private const string SystemMisconfiguredMessageReason = "pinned_ui_settings_schema_has_no_persisted_field";
    private const string LegacyLibraryBrandingDisposition = "intentionally_dropped_not_effective_at_pinned_source";
    private const string PatronDuplicateLabelsUiDisposition = "ignored_library_ui_fields_not_effective_at_pinned_source";
    private const string PatronDuplicateLabelsLegacyDisposition = "applied_legacy_fallback";
    private const string PatronDuplicateLabelsModernDisposition = "ignored_modern_override_present";
    private const string PatronUiInheritanceDisposition = "blank_library_ui_fields_inherit_system_value";
    private const string EmailTemplateSenderDisposition = "moved_to_scoped_email_settings";
    private const string EmailTemplateSenderReason = "template_sender_fields_moved_to_scoped_email_settings";
    private static readonly string[] LegacyLibraryBrandingFields = ["logo", "logoAlt"];
    private static readonly string[] PatronDuplicateLabelsUiFields =
    [
        "duplicateLabelSuggestion",
        "duplicateLabelOutstandingPurchase",
        "duplicateLabelPendingHold",
        "duplicateLabelHoldPlaced",
        "duplicateLabelClosed",
        "duplicateLabelRejected",
        "duplicateLabelHoldCompleted",
        "duplicateLabelHoldNotPickedUp",
        "duplicateLabelManual",
        "duplicateLabelSilent",
        "systemNotEnabledMessage",
        "publicationOptions"
    ];
    private static readonly (string Source, string Target)[] PatronUiInheritanceFields =
    [
        ("pageTitle", "PageTitle"),
        ("barcodeLabel", "BarcodeLabel"),
        ("pinLabel", "PinLabel"),
        ("loginPrompt", "LoginPrompt"),
        ("loginNote", "LoginNote"),
        ("suggestionFormNote", "SuggestionFormNote"),
        ("noEmailMessage", "NoEmailMessage"),
        ("successTitle", "SuccessTitle"),
        ("successMessage", "SuccessMessage"),
        ("alreadySubmittedMessage", "AlreadySubmittedMessage"),
        ("ebookMessage", "EbookMessage"),
        ("eaudiobookMessage", "EaudiobookMessage")
    ];

    public static void Verify(string connectionString, ValidatedMigrationPackage package, JsonElement report)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        Verify(connection, null, package, report);
    }

    internal static void Verify(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        VerifyPolarisSettings(connection, transaction, package, report);
        VerifyBranding(package, report);
        VerifyLegacyLibraryBranding(package, report);
        VerifyPatronDuplicateLabels(package, report);
        VerifyPatronUiInheritance(connection, transaction, package, report);
        VerifyEmailSettingsTransport(report);
        VerifyEmailTemplateSenderGroups(connection, transaction, package, report);
        VerifyEmailTemplateSenderReasons(package, report);
        MigrationIndependentEntityVerifier.VerifyClaimAndPlacedBibReportTransformations(
            connection,
            transaction,
            package,
            report);
        VerifySystemRuntimeReport(package, report);
        VerifyFormatAutoClaimRules(connection, transaction, package, report);
        VerifyAdditionalCopyUpdatedTimestamps(connection, transaction, package, report);
        VerifyBootstrapSuperAdminTimestamp(package, report);
    }

    private static void VerifyBranding(ValidatedMigrationPackage package, JsonElement report)
    {
        var sourceRows = MigrationPackageReader.ReadRows(package, "branding.json", "branding");
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "branding", StringComparison.Ordinal))
            .ToArray();
        if (transformations.Length != sourceRows.Count)
        {
            FailBranding();
        }

        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var expectedBySourceId = new Dictionary<string, (int OrganizationId, string AssetSha256, long Length, string ContentType)>(StringComparer.Ordinal);
        foreach (var source in sourceRows)
        {
            var sourceId = source.Text("sourceRecordId") ?? FailBranding<string>();
            var assetPath = source.Text("assetPath") ?? FailBranding<string>();
            var declaredContentType = source.Text("contentType") ?? FailBranding<string>();
            if (string.IsNullOrWhiteSpace(sourceId) ||
                string.IsNullOrWhiteSpace(assetPath) ||
                string.IsNullOrWhiteSpace(declaredContentType) ||
                Path.IsPathRooted(assetPath))
            {
                FailBranding();
            }

            var packageRoot = Path.GetFullPath(package.RootPath);
            var assetFullPath = Path.GetFullPath(Path.Combine(
                packageRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
            var packagePrefix = packageRoot.EndsWith(Path.DirectorySeparatorChar)
                ? packageRoot
                : packageRoot + Path.DirectorySeparatorChar;
            if (!assetFullPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(assetFullPath))
            {
                FailBranding();
            }

            var assetBytes = File.ReadAllBytes(assetFullPath);
            if (!LogoImageValidator.TryDetectContentType(assetBytes, out var detectedContentType) ||
                !LogoImageValidator.TryValidate(assetBytes, declaredContentType, out _, out _) ||
                !string.Equals(declaredContentType.Trim(), detectedContentType, StringComparison.OrdinalIgnoreCase))
            {
                FailBranding();
            }

            var assetSha256 = Convert.ToHexStringLower(SHA256.HashData(assetBytes));
            var declaredLength = source.Int32("length");
            if (declaredLength != assetBytes.LongLength ||
                !string.Equals(source.Text("sha256"), assetSha256, StringComparison.OrdinalIgnoreCase) ||
                !expectedBySourceId.TryAdd(
                    sourceId,
                    (ResolveBrandingOrganization(source, organizations), assetSha256, assetBytes.LongLength, declaredContentType.Trim())))
            {
                FailBranding();
            }
        }

        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transformation in transformations)
        {
            var sourceId = ReadString(transformation, "sourceId");
            if (!expectedBySourceId.TryGetValue(sourceId, out var expected) || !seenSourceIds.Add(sourceId) ||
                transformation.GetProperty("organizationId").GetInt32() != expected.OrganizationId ||
                !string.Equals(
                    ReadString(transformation, "assetSha256"),
                    expected.AssetSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                transformation.GetProperty("length").GetInt64() != expected.Length ||
                !string.Equals(ReadString(transformation, "contentType"), expected.ContentType, StringComparison.Ordinal))
            {
                FailBranding();
            }
        }

        if (seenSourceIds.Count != expectedBySourceId.Count)
        {
            FailBranding();
        }
    }

    private static int ResolveBrandingOrganization(SourceRow source, IReadOnlyList<SourceRow> organizations)
    {
        var scope = source.Text("scope")?.Trim();
        if (string.Equals(scope, "system", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (!string.Equals(scope, "library", StringComparison.OrdinalIgnoreCase))
        {
            return FailBranding<int>();
        }

        return ResolveReportLibraryOrganization(source.Text("libraryOrganization"), organizations);
    }

    private static int ResolveReportLibraryOrganization(string? sourceReference, IReadOnlyList<SourceRow> organizations)
    {
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return FailReportLibraryOrganization<int>();
        }
        var normalizedReference = sourceReference.Trim();

        var matchingOrganizations = organizations.Where(item =>
            string.Equals(item.String("id"), normalizedReference, StringComparison.Ordinal)).ToArray();
        if (matchingOrganizations.Length > 1)
        {
            return FailReportLibraryOrganization<int>();
        }
        var organization = matchingOrganizations.SingleOrDefault();
        if (organization is null &&
            int.TryParse(normalizedReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nativeId))
        {
            matchingOrganizations = organizations.Where(item => item.Int32("organizationId") == nativeId).ToArray();
            if (matchingOrganizations.Length > 1)
            {
                return FailReportLibraryOrganization<int>();
            }
            organization = matchingOrganizations.SingleOrDefault();
        }
        if (organization is null)
        {
            return FailReportLibraryOrganization<int>();
        }

        var organizationId = organization.Int32("organizationId");
        var organizationCodeId = organization.HasValue("organizationCodeId")
            ? organization.Int32("organizationCodeId")
            : organization.Int32("organization_code_id");
        if (organizationId is null or 1 || organizationCodeId != 2)
        {
            return FailReportLibraryOrganization<int>();
        }
        return organizationId.Value;
    }

    private static void FailReportLibraryOrganization() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "A report transformation does not identify one valid source library organization.");

    private static T FailReportLibraryOrganization<T>() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "A report transformation does not identify one valid source library organization.");

    private static void VerifyLegacyLibraryBranding(ValidatedMigrationPackage package, JsonElement report)
    {
        var sourceRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "library_settings");
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "legacy_library_branding", StringComparison.Ordinal))
            .ToArray();
        if (transformations.Length != sourceRows.Count)
        {
            FailLegacyLibraryBranding();
        }

        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var expectedBySourceId = new Dictionary<string, (int OrganizationId, string[] PopulatedFields)>(StringComparer.Ordinal);
        foreach (var source in sourceRows)
        {
            var sourceId = source.String("id");
            var sourceReference = source.String("libraryOrganization");
            if (string.IsNullOrWhiteSpace(sourceId) ||
                string.IsNullOrWhiteSpace(sourceReference) ||
                !expectedBySourceId.TryAdd(
                    sourceId,
                    (
                        ResolveReportLibraryOrganization(sourceReference, organizations),
                        LegacyLibraryBrandingFields.Where(source.HasValue).ToArray())))
            {
                FailLegacyLibraryBranding();
            }
        }

        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transformation in transformations)
        {
            var sourceId = ReadString(transformation, "sourceId");
            if (!expectedBySourceId.TryGetValue(sourceId, out var expected) || !seenSourceIds.Add(sourceId) ||
                !string.Equals(ReadString(transformation, "sourceCollection"), "library_settings", StringComparison.Ordinal) ||
                transformation.GetProperty("organizationId").GetInt32() != expected.OrganizationId ||
                !string.Equals(ReadString(transformation, "disposition"), LegacyLibraryBrandingDisposition, StringComparison.Ordinal))
            {
                FailLegacyLibraryBranding();
            }

            var populatedFields = transformation.GetProperty("populatedFields").EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : FailLegacyLibraryBranding<string>())
                .ToArray();
            if (populatedFields.Length != expected.PopulatedFields.Length ||
                !populatedFields.ToHashSet(StringComparer.Ordinal).SetEquals(expected.PopulatedFields))
            {
                FailLegacyLibraryBranding();
            }
        }

        if (seenSourceIds.Count != expectedBySourceId.Count)
        {
            FailLegacyLibraryBranding();
        }
    }

    private static void FailLegacyLibraryBranding() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The dormant library branding report does not match its source collection, identity, scope, or populated fields.");

    private static T FailLegacyLibraryBranding<T>() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The dormant library branding report does not match its source collection, identity, scope, or populated fields.");

    private static void VerifyPatronDuplicateLabels(ValidatedMigrationPackage package, JsonElement report)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var expectedByIdentity = new Dictionary<(string SourceCollection, string SourceId), (int OrganizationId, string Disposition, string[]? PopulatedFields)>();
        foreach (var source in MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings"))
        {
            if (!string.Equals(source.RequiredString("scope"), "library", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var populatedFields = PatronDuplicateLabelsUiFields.Where(source.HasValue).ToArray();
            if (populatedFields.Length == 0)
            {
                continue;
            }

            var sourceId = source.String("id") ?? FailPatronDuplicateLabels<string>();
            var sourceReference = source.String("libraryOrganization") ?? FailPatronDuplicateLabels<string>();
            if (!expectedByIdentity.TryAdd(
                    ("ui_settings", sourceId),
                    (
                        ResolveReportLibraryOrganization(sourceReference, organizations),
                        PatronDuplicateLabelsUiDisposition,
                        populatedFields)))
            {
                FailPatronDuplicateLabels();
            }
        }

        var modernOverrideOrganizations = new HashSet<int>();
        foreach (var source in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"))
        {
            var organizationId = source.Int32("orgId") ?? FailPatronDuplicateLabels<int>();
            if (organizationId <= 1)
            {
                FailPatronDuplicateLabels();
            }
            modernOverrideOrganizations.Add(organizationId);
        }

        foreach (var source in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"))
        {
            var sourceId = source.String("id") ?? FailPatronDuplicateLabels<string>();
            var sourceReference = source.String("libraryOrganization") ?? FailPatronDuplicateLabels<string>();

            var organizationId = ResolveReportLibraryOrganization(sourceReference, organizations);
            var disposition = modernOverrideOrganizations.Contains(organizationId)
                ? PatronDuplicateLabelsModernDisposition
                : PatronDuplicateLabelsLegacyDisposition;
            if (!expectedByIdentity.TryAdd(
                    ("patron_library_settings", sourceId),
                    (organizationId, disposition, null)))
            {
                FailPatronDuplicateLabels();
            }
        }

        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "patron_duplicate_labels", StringComparison.Ordinal))
            .ToArray();
        if (transformations.Length != expectedByIdentity.Count)
        {
            FailPatronDuplicateLabels();
        }

        var seenIdentities = new HashSet<(string SourceCollection, string SourceId)>();
        foreach (var transformation in transformations)
        {
            var sourceCollection = ReadString(transformation, "sourceCollection");
            var sourceId = ReadString(transformation, "sourceId");
            var identity = (sourceCollection, sourceId);
            if (!expectedByIdentity.TryGetValue(identity, out var expected) ||
                !seenIdentities.Add(identity) ||
                transformation.GetProperty("organizationId").GetInt32() != expected.OrganizationId ||
                !string.Equals(ReadString(transformation, "disposition"), expected.Disposition, StringComparison.Ordinal))
            {
                FailPatronDuplicateLabels();
            }

            if (expected.PopulatedFields is { } expectedFields)
            {
                var populatedFields = transformation.GetProperty("populatedFields").EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : FailPatronDuplicateLabels<string>())
                    .ToArray();
                if (populatedFields.Length != expectedFields.Length ||
                    !populatedFields.ToHashSet(StringComparer.Ordinal).SetEquals(expectedFields))
                {
                    FailPatronDuplicateLabels();
                }
            }
        }

        if (seenIdentities.Count != expectedByIdentity.Count)
        {
            FailPatronDuplicateLabels();
        }
    }

    private static void FailPatronDuplicateLabels() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The patron duplicate-label report does not match its source collections, identities, scope, or decisions.");

    private static T FailPatronDuplicateLabels<T>() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The patron duplicate-label report does not match its source collections, identities, scope, or decisions.");

    private static void VerifyPatronUiInheritance(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var libraryOrganizationIds = organizations
            .Where(organization =>
            {
                var organizationId = organization.Int32("organizationId");
                var organizationCodeId = organization.HasValue("organizationCodeId")
                    ? organization.Int32("organizationCodeId")
                    : organization.Int32("organization_code_id");
                return organizationId is > 1 && organizationCodeId == 2;
            })
            .Select(organization => organization.Int32("organizationId") ?? FailPatronUiInheritance<int>())
            .ToHashSet();
        var ebookOverrideOrganizations = new HashSet<int>();
        var eaudiobookOverrideOrganizations = new HashSet<int>();
        foreach (var source in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"))
        {
            var organizationId = source.Int32("orgId") ?? FailPatronUiInheritance<int>();
            if (!libraryOrganizationIds.Contains(organizationId))
            {
                FailPatronUiInheritance();
            }
            if (source.Text("ebookMessage") is { Length: > 0 })
            {
                ebookOverrideOrganizations.Add(organizationId);
            }
            if (source.Text("eaudiobookMessage") is { Length: > 0 })
            {
                eaudiobookOverrideOrganizations.Add(organizationId);
            }
        }

        var expectedBySourceId = new Dictionary<string, (int OrganizationId, string[] AffectedFields)>(StringComparer.Ordinal);
        foreach (var source in MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings"))
        {
            if (!source.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceId = source.Text("id") ?? FailPatronUiInheritance<string>();
            var sourceReference = source.Text("libraryOrganization") ?? FailPatronUiInheritance<string>();
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(sourceReference))
            {
                FailPatronUiInheritance();
            }

            var organizationId = ResolveReportLibraryOrganization(sourceReference, organizations);
            if (!libraryOrganizationIds.Contains(organizationId))
            {
                FailPatronUiInheritance();
            }

            var affectedFields = PatronUiInheritanceFields
                .Where(field =>
                    string.IsNullOrWhiteSpace(source.Text(field.Source)) &&
                    !(field.Source == "ebookMessage" && ebookOverrideOrganizations.Contains(organizationId)) &&
                    !(field.Source == "eaudiobookMessage" && eaudiobookOverrideOrganizations.Contains(organizationId)))
                .Select(field => field.Source)
                .ToArray();
            if (affectedFields.Length > 0 && !expectedBySourceId.TryAdd(sourceId, (organizationId, affectedFields)))
            {
                FailPatronUiInheritance();
            }
        }

        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "patron_ui_inheritance", StringComparison.Ordinal))
            .ToArray();
        if (transformations.Length != expectedBySourceId.Count)
        {
            FailPatronUiInheritance();
        }

        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transformation in transformations)
        {
            var sourceId = ReadString(transformation, "sourceId");
            if (!expectedBySourceId.TryGetValue(sourceId, out var expected) ||
                !seenSourceIds.Add(sourceId) ||
                !string.Equals(ReadString(transformation, "sourceCollection"), "ui_settings", StringComparison.Ordinal) ||
                transformation.GetProperty("organizationId").GetInt32() != expected.OrganizationId ||
                !string.Equals(ReadString(transformation, "disposition"), PatronUiInheritanceDisposition, StringComparison.Ordinal))
            {
                FailPatronUiInheritance();
            }

            var affectedFields = transformation.GetProperty("affectedFields").EnumerateArray()
                .Select(field => field.ValueKind == JsonValueKind.String ? field.GetString()! : FailPatronUiInheritance<string>())
                .ToArray();
            if (affectedFields.Length != expected.AffectedFields.Length ||
                !affectedFields.ToHashSet(StringComparer.Ordinal).SetEquals(expected.AffectedFields))
            {
                FailPatronUiInheritance();
            }

            VerifyPatronUiInheritanceTarget(connection, transaction, expected.OrganizationId, expected.AffectedFields);
        }

        if (seenSourceIds.Count != expectedBySourceId.Count)
        {
            FailPatronUiInheritance();
        }
    }

    private static void VerifyPatronUiInheritanceTarget(
        SqlConnection connection,
        SqlTransaction? transaction,
        int organizationId,
        IReadOnlyCollection<string> affectedFields)
    {
        using (var ownership = new SqlCommand(
                   "SELECT CASE WHEN EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = @organizationId AND [Id] > 1 AND [OrganizationCodeId] = 2) THEN 1 ELSE 0 END;",
                   connection,
                   transaction))
        {
            ownership.Parameters.AddWithValue("@organizationId", organizationId);
            if (ownership.ExecuteScalar() is not { } result || Convert.ToInt32(result, CultureInfo.InvariantCulture) != 1)
            {
                FailPatronUiInheritance();
            }
        }

        var targetFields = PatronUiInheritanceFields
            .Where(field => affectedFields.Contains(field.Source, StringComparer.Ordinal))
            .ToArray();
        if (targetFields.Length != affectedFields.Count)
        {
            FailPatronUiInheritance();
        }

        var columns = string.Join(", ", targetFields.Select(field => $"[{field.Target}]"));
        using var command = new SqlCommand(
            $"SELECT {columns} FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            FailPatronUiInheritance();
        }
        for (var ordinal = 0; ordinal < targetFields.Length; ordinal++)
        {
            if (!reader.IsDBNull(ordinal))
            {
                FailPatronUiInheritance();
            }
        }
        if (reader.Read())
        {
            FailPatronUiInheritance();
        }
    }

    private static void FailPatronUiInheritance() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The patron UI inheritance report does not match source library settings or the mapped SQL values.");

    private static T FailPatronUiInheritance<T>() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The patron UI inheritance report does not match source library settings or the mapped SQL values.");

    private static void VerifyFormatAutoClaimRules(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var sourceById = new Dictionary<string, SourceRow>(StringComparer.Ordinal);
        foreach (var source in MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules"))
        {
            if (!sourceById.TryAdd(source.RequiredString("id"), source))
            {
                FailFormatAutoClaimRules();
            }
        }

        var targetBySourceId = new Dictionary<string, (long? StaffUserId, bool IsActive)>(StringComparer.Ordinal);
        using (var command = new SqlCommand(
                   """
                   SELECT mapping.[PocketBaseId], claimRule.[StaffUserId], claimRule.[IsActive]
                   FROM [asap].[LegacyPocketBaseMapping] mapping
                   JOIN [asap].[FormatAutoClaimRule] claimRule ON claimRule.[Id] = mapping.[NewId]
                   WHERE mapping.[EntityType] = N'format_auto_claim_rule';
                   """,
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!targetBySourceId.TryAdd(
                        reader.GetString(0),
                        (reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetBoolean(2))))
                {
                    FailFormatAutoClaimRules();
                }
            }
        }

        foreach (var transformation in report.GetProperty("transformations").EnumerateArray()
                     .Where(item => item.GetProperty("entity").GetString() == "format_auto_claim_rule"))
        {
            var sourceId = ReadString(transformation, "sourceId");
            if (!sourceById.TryGetValue(sourceId, out var source))
            {
                FailFormatAutoClaimRules();
                return;
            }
            if (!targetBySourceId.TryGetValue(sourceId, out var target))
            {
                FailFormatAutoClaimRules();
                return;
            }

            var sourceStaffUserId = ConsistentClaimRuleStaffReference(source);
            var sourceActive = source.Bool("active");
            var reason = !sourceActive
                ? "source_inactive"
                : target.StaffUserId is null
                    ? "assignee_unmapped"
                    : !target.IsActive
                        ? "assignee_ineligible"
                        : "eligible";
            var reportedTargetStaffUserId = transformation.GetProperty("targetStaffUserId");
            var targetStaffUserIdMatches = target.StaffUserId is null
                ? reportedTargetStaffUserId.ValueKind == JsonValueKind.Null
                : reportedTargetStaffUserId.ValueKind == JsonValueKind.Number &&
                    reportedTargetStaffUserId.TryGetInt64(out var parsedTargetStaffUserId) &&
                    parsedTargetStaffUserId == target.StaffUserId.Value;

            if (!NullableStringsEqual(ReadNullableString(transformation, "sourceStaffUserId"), sourceStaffUserId) ||
                !targetStaffUserIdMatches ||
                transformation.GetProperty("sourceActive").GetBoolean() != sourceActive ||
                transformation.GetProperty("targetActive").GetBoolean() != target.IsActive ||
                !string.Equals(ReadString(transformation, "reason"), reason, StringComparison.Ordinal))
            {
                FailFormatAutoClaimRules();
            }
        }
    }

    private static string? ConsistentClaimRuleStaffReference(SourceRow source)
    {
        var staffUserId = source.String("staffUserId");
        var staffUser = source.String("staffUser");
        if (staffUserId is not null && staffUser is not null &&
            !string.Equals(staffUserId, staffUser, StringComparison.Ordinal))
        {
            FailFormatAutoClaimRules();
        }
        return staffUserId ?? staffUser;
    }

    private static void FailFormatAutoClaimRules() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The format auto-claim report does not match source identities, references, activity, or independently verified target rows.");

    private static void VerifyAdditionalCopyUpdatedTimestamps(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var expectedCreatedUtcBySourceId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var source in MigrationPackageReader.ReadRows(package, "additional-copy-requests.json", "additional_copy_requests"))
        {
            if (source.HasValue("updated"))
            {
                continue;
            }

            var sourceId = source.RequiredString("id");
            var createdUtc = source.UtcDateTime("created");
            if (!createdUtc.HasValue || !expectedCreatedUtcBySourceId.TryAdd(sourceId, createdUtc.Value))
            {
                FailAdditionalCopyUpdatedTimestamps();
            }
        }

        var targetUpdatedUtcBySourceId = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using (var command = new SqlCommand(
                   """
                   SELECT mapping.[PocketBaseId], copy.[UpdatedUtc]
                   FROM [asap].[LegacyPocketBaseMapping] mapping
                   JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = mapping.[NewId]
                   WHERE mapping.[EntityType] = N'additional_copy';
                   """,
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!targetUpdatedUtcBySourceId.TryAdd(reader.GetString(0), reader.GetDateTime(1)))
                {
                    FailAdditionalCopyUpdatedTimestamps();
                }
            }
        }

        foreach (var transformation in report.GetProperty("transformations").EnumerateArray()
                     .Where(item => item.GetProperty("entity").GetString() == "additional_copy_updated_timestamp"))
        {
            var sourceId = ReadString(transformation, "sourceId");
            if (!expectedCreatedUtcBySourceId.TryGetValue(sourceId, out var sourceCreatedUtc))
            {
                FailAdditionalCopyUpdatedTimestamps();
                return;
            }
            if (!targetUpdatedUtcBySourceId.TryGetValue(sourceId, out var targetUpdatedUtc))
            {
                FailAdditionalCopyUpdatedTimestamps();
                return;
            }

            var reportedSourceUpdatedUtc = transformation.GetProperty("sourceUpdatedUtc");
            var reportedTargetUpdatedUtc = transformation.GetProperty("targetUpdatedUtc");
            if (reportedSourceUpdatedUtc.ValueKind != JsonValueKind.Null ||
                reportedTargetUpdatedUtc.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    reportedTargetUpdatedUtc.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedTargetUpdatedUtc) ||
                parsedTargetUpdatedUtc.UtcDateTime != sourceCreatedUtc ||
                parsedTargetUpdatedUtc.UtcDateTime != targetUpdatedUtc ||
                !string.Equals(
                    ReadString(transformation, "reason"),
                    "missing_updated_uses_created",
                    StringComparison.Ordinal))
            {
                FailAdditionalCopyUpdatedTimestamps();
            }
        }
    }

    private static void FailAdditionalCopyUpdatedTimestamps() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The additional-copy updated-time report does not match missing source values, required creation instants, or mapped SQL timestamps.");

    private static void VerifyBootstrapSuperAdminTimestamp(ValidatedMigrationPackage package, JsonElement report)
    {
        var expectedAppliedAtUtc = package.Manifest.ExportedAtUtc.UtcDateTime;
        foreach (var transformation in report.GetProperty("transformations").EnumerateArray()
                     .Where(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin"))
        {
            var reportedTimestamp = ReadString(transformation, "appliedAtUtc");
            if (!DateTimeOffset.TryParse(
                    reportedTimestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedTimestamp) ||
                parsedTimestamp.Offset != TimeSpan.Zero ||
                parsedTimestamp.UtcDateTime != expectedAppliedAtUtc)
            {
                FailBootstrapSuperAdminTimestamp();
            }
        }
    }

    private static void FailBootstrapSuperAdminTimestamp() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The bootstrap super-admin report timestamp does not match the immutable export manifest.");

    private static void FailBranding() => throw BrandingReportMismatch();

    private static T FailBranding<T>() => throw BrandingReportMismatch();

    private static MigrationOperationException BrandingReportMismatch() => new(
        "reconciliation_report_mismatch",
        "The branding transformation report does not match its source asset and scoped target identity.");

    internal static void VerifySerializedPendingReport(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        string pendingReportPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pendingReportPath));
            var report = document.RootElement;
            MigrationReconciler.ValidateReportPackage(report, package, allowPending: true);
            if (!string.Equals(
                    report.GetProperty("reportState").GetString(),
                    "commit_pending",
                    StringComparison.Ordinal) ||
                report.GetProperty("reconciliationPassed").GetBoolean())
            {
                throw new MigrationOperationException(
                    "reconciliation_report_mismatch",
                    "The serialized pending import report is not in the expected precommit state.");
            }
            Verify(connection, transaction, package, report);
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException or
                FormatException or InvalidCastException or OverflowException)
        {
            throw new MigrationOperationException(
                "reconciliation_report_invalid",
                "The serialized pending import report is malformed or has an unsupported shape.");
        }
    }

    private static void VerifyPolarisSettings(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var sourceRows = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "polaris_settings", StringComparison.Ordinal))
            .ToArray();

        if (sourceRows.Count == 0)
        {
            if (transformations.Length != 0)
            {
                Fail();
            }
            return;
        }

        if (sourceRows.Count != 1 || transformations.Length != 1)
        {
            Fail();
        }

        var source = sourceRows[0];
        var transformation = transformations[0];
        var sourceId = source.Text("id");
        if (string.IsNullOrWhiteSpace(sourceId) ||
            !string.Equals(ReadString(transformation, "sourceId"), sourceId, StringComparison.Ordinal))
        {
            Fail();
        }

        var sourceApiKeyProtected = source.Text("apiKey") is { Length: > 0 };
        var sourceAdminPasswordProtected = source.Text("adminPassword") is { Length: > 0 };
        var reportedApiKeyProtected = ReadBoolean(transformation, "apiKeyProtected");
        var reportedAdminPasswordProtected = ReadBoolean(transformation, "adminPasswordProtected");
        if (reportedApiKeyProtected != sourceApiKeyProtected ||
            reportedAdminPasswordProtected != sourceAdminPasswordProtected ||
            !NullableStringsEqual(
                ReadNullableString(transformation, "retiredRequestingOrganizationSource"),
                source.Text("requestingOrgId")) ||
            !NullableStringsEqual(
                ReadNullableString(transformation, "retiredPickupOrganizationSource"),
                source.Text("pickupOrgId")) ||
            !string.Equals(
                ReadString(transformation, "operationContextSource"),
                PolarisSettingsOperationContext,
                StringComparison.Ordinal))
        {
            Fail();
        }

        var targetProtection = ReadPolarisProtectedPresence(connection, transaction);
        if (reportedApiKeyProtected != targetProtection.ApiKeyProtected ||
            reportedAdminPasswordProtected != targetProtection.AdminPasswordProtected)
        {
            Fail();
        }
    }

    private static void VerifyEmailSettingsTransport(JsonElement report)
    {
        foreach (var transformation in report.GetProperty("transformations").EnumerateArray()
                     .Where(item => item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("entity", out var entity) &&
                         entity.ValueKind == JsonValueKind.String &&
                         string.Equals(entity.GetString(), "email_settings", StringComparison.Ordinal)))
        {
            if (!transformation.TryGetProperty("transport", out var sourceTransport) ||
                sourceTransport.ValueKind != JsonValueKind.String ||
                !string.Equals(sourceTransport.GetString(), RetiredSmtpTransport, StringComparison.Ordinal) ||
                !transformation.TryGetProperty("targetTransport", out var targetTransport) ||
                targetTransport.ValueKind != JsonValueKind.String ||
                !string.Equals(targetTransport.GetString(), ConfiguredEmailSenderBoundary, StringComparison.Ordinal))
            {
                throw new MigrationOperationException(
                    "reconciliation_report_mismatch",
                    "The email settings transformation report does not match the stable transport migration boundary.");
            }
        }
    }

    private static void VerifyEmailTemplateSenderGroups(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var templatesByOrganization = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Where(row => string.Equals(row.Text("scope")?.Trim(), "library", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => ResolveReportLibraryOrganization(row.Text("libraryOrganization"), organizations))
            .Where(group => group.Any(HasMeaningfulSenderValue))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("entity").GetString(), "email_template_sender", StringComparison.Ordinal))
            .ToArray();
        if (transformations.Length != templatesByOrganization.Count)
        {
            FailEmailTemplateSender();
        }

        var seenOrganizations = new HashSet<int>();
        foreach (var transformation in transformations)
        {
            var organizationId = transformation.GetProperty("organizationId").GetInt32();
            if (!templatesByOrganization.ContainsKey(organizationId) ||
                !seenOrganizations.Add(organizationId))
            {
                FailEmailTemplateSender();
            }

            var expectedSourceIds = templatesByOrganization[organizationId]
                .Select(row => row.RequiredString("id"))
                .ToHashSet(StringComparer.Ordinal);
            var reportedSourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceId in transformation.GetProperty("sourceIds").EnumerateArray())
            {
                if (sourceId.ValueKind != JsonValueKind.String ||
                    !reportedSourceIds.Add(sourceId.GetString()!))
                {
                    FailEmailTemplateSender();
                }
            }

            var targetSender = ReadLibraryEmailSender(connection, transaction, organizationId);
            if (!reportedSourceIds.SetEquals(expectedSourceIds) ||
                !NullableStringsEqual(ReadNullableString(transformation, "fromAddress"), targetSender.FromAddress) ||
                !NullableStringsEqual(ReadNullableString(transformation, "fromName"), targetSender.FromName) ||
                !string.Equals(ReadString(transformation, "disposition"), EmailTemplateSenderDisposition, StringComparison.Ordinal))
            {
                FailEmailTemplateSender();
            }
        }

        if (seenOrganizations.Count != templatesByOrganization.Count)
        {
            FailEmailTemplateSender();
        }
    }

    private static bool HasMeaningfulSenderValue(SourceRow row) =>
        !string.IsNullOrWhiteSpace(row.Text("fromAddress")) ||
        !string.IsNullOrWhiteSpace(row.Text("fromName"));

    private static (string? FromAddress, string? FromName) ReadLibraryEmailSender(
        SqlConnection connection,
        SqlTransaction? transaction,
        int organizationId)
    {
        using var command = new SqlCommand(
            "SELECT [FromAddress], [FromName] FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            FailEmailTemplateSender();
        }

        var sender = (
            FromAddress: reader.IsDBNull(0) ? null : reader.GetString(0),
            FromName: reader.IsDBNull(1) ? null : reader.GetString(1));
        if (reader.Read())
        {
            FailEmailTemplateSender();
        }
        return sender;
    }

    private static void VerifyEmailTemplateSenderReasons(ValidatedMigrationPackage package, JsonElement report)
    {
        var expectedBySourceId = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Where(HasMeaningfulSenderValue)
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => string.Equals(item.GetProperty("entity").GetString(), "email_template", StringComparison.Ordinal))
            .ToArray();
        if (transformations.Length != expectedBySourceId.Count)
        {
            FailEmailTemplateSenderReason();
        }

        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transformation in transformations)
        {
            var sourceId = ReadString(transformation, "sourceId");
            if (!expectedBySourceId.ContainsKey(sourceId) ||
                !seenSourceIds.Add(sourceId) ||
                !string.Equals(ReadString(transformation, "transformation"), EmailTemplateSenderReason, StringComparison.Ordinal))
            {
                FailEmailTemplateSenderReason();
            }
        }

        if (seenSourceIds.Count != expectedBySourceId.Count)
        {
            FailEmailTemplateSenderReason();
        }
    }

    private static void FailEmailTemplateSender() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The library email-template sender report does not match source groups, verified SQL settings, or the fixed migration disposition.");

    private static void FailEmailTemplateSenderReason() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The email-template sender transformation reasons do not match meaningful immutable source fields.");

    private static void VerifySystemRuntimeReport(ValidatedMigrationPackage package, JsonElement report)
    {
        var sourceRows = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings");
        var transformations = report.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "system_settings_effective_runtime", StringComparison.Ordinal))
            .ToArray();
        if (sourceRows.Count > 1 || transformations.Length != 1)
        {
            FailSystemRuntime();
        }

        var runtime = MigrationPackageReader.ReadMetadata(package, "effective-legacy-runtime-config.json");
        var settings = runtime.GetProperty("settings");
        var expectedStaffApplicationUrlSource = settings.GetProperty("StaffApplicationUrl").GetProperty("source").GetString();
        var expectedMaterialTypeIconUrlPatternSource = settings.GetProperty("MaterialTypeIconUrlPattern").GetProperty("source").GetString();
        var expectedSourceId = sourceRows.SingleOrDefault()?.Text("id");
        var transformation = transformations[0];
        if (!NullableStringsEqual(ReadNullableString(transformation, "sourceId"), expectedSourceId) ||
            !string.Equals(
                ReadString(transformation, "staffApplicationUrlSource"),
                expectedStaffApplicationUrlSource,
                StringComparison.Ordinal) ||
            !string.Equals(
                ReadString(transformation, "materialTypeIconUrlPatternSource"),
                expectedMaterialTypeIconUrlPatternSource,
                StringComparison.Ordinal) ||
            !string.Equals(
                ReadString(transformation, "misconfiguredMessage"),
                SystemMisconfiguredMessage,
                StringComparison.Ordinal) ||
            !string.Equals(
                ReadString(transformation, "misconfiguredMessageReason"),
                SystemMisconfiguredMessageReason,
                StringComparison.Ordinal))
        {
            FailSystemRuntime();
        }
    }

    private static (bool ApiKeyProtected, bool AdminPasswordProtected) ReadPolarisProtectedPresence(
        SqlConnection connection,
        SqlTransaction? transaction)
    {
        using var command = new SqlCommand(
            "SELECT [ProtectedApiKey], [ProtectedAdminPassword] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            Fail();
        }

        var presence = (
            ApiKeyProtected: !reader.IsDBNull(0),
            AdminPasswordProtected: !reader.IsDBNull(1));
        if (reader.Read())
        {
            Fail();
        }

        return presence;
    }

    private static string ReadString(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString()!;
        }

        return Fail<string>();
    }

    private static string? ReadNullableString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return Fail<string?>();
        }

        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            _ => Fail<string?>()
        };
    }

    private static bool ReadBoolean(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return property.GetBoolean();
        }

        return Fail<bool>();
    }

    private static bool NullableStringsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static void Fail() => throw ReportMismatch();

    private static void FailSystemRuntime() => throw new MigrationOperationException(
        "reconciliation_report_mismatch",
        "The effective system runtime transformation report does not match its source metadata and fixed migration policy.");

    private static T Fail<T>() => throw ReportMismatch();

    private static MigrationOperationException ReportMismatch() => new(
        "reconciliation_report_mismatch",
        "The Polaris settings transformation report does not match the immutable source and target state.");
}
