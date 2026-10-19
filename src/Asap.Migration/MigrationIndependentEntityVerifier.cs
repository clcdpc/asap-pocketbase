using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

/// <summary>
/// Independently projects source-owned request and history fields onto SQL.
/// This deliberately does not invoke importer reconciliation or import resolvers.
/// </summary>
internal static class MigrationIndependentEntityVerifier
{
    private static readonly HashSet<string> TerminalCloseReasons = new(StringComparer.Ordinal)
    {
        "hold_completed", "hold_not_picked_up", "hold_unclaimed", "hold_cancelled", "hold_expired"
    };

    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.Ordinal)
    {
        "created", "status_changed", "system_note", "promoted", "hold_placed", "hold_skipped",
        "fulfilled", "timeout_closed", "pickup_preference_changed", "claim_manual_assigned",
        "claim_manual_transferred", "claim_manual_cleared", "claim_auto_assigned",
        "claim_auto_reassigned", "claim_auto_cleared", "claim_auto_skipped"
    };

    public static void Verify(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        long? bootstrapTargetStaffUserId = null,
        bool bootstrapInserted = false,
        string? externalConfigurationPath = null)
    {
        var staff = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        var copies = MigrationPackageReader.ReadRows(package, "additional-copy-requests.json", "additional_copy_requests");
        var formats = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var organizationIds = organizations.ToDictionary(
            row => row.RequiredString("id"),
            row => row.Int32("organizationId") ?? Fail<int>("A source organization has no native identity."),
            StringComparer.Ordinal);
        var tags = MigrationPackageReader.ReadRows(package, "workflow-tags.json", "workflow_tags");
        var autoClaims = MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules");
        var titleTagRows = MigrationPackageReader.ReadRows(package, "title-request-tags.json", "title_request_tags");
        var eventRows = MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events");
        var deliveryRows = MigrationPackageReader.ReadRows(package, "email-delivery-events.json", "email_delivery_events");
        var deletedRows = MigrationPackageReader.ReadRows(package, "deleted-request-audit.json", "deleted_request_audit");

        var staffIds = ReadMappings(connection, transaction, "staff_user", staff);
        var requestIds = ReadMappings(connection, transaction, "title_request", requests);
        var copyIds = ReadMappings(connection, transaction, "additional_copy", copies);
        var tagIds = ReadMappings(connection, transaction, "workflow_tag", tags);
        var titleTagIds = ReadMappings(connection, transaction, "title_request_tag", titleTagRows);
        var claimRuleIds = ReadMappings(connection, transaction, "format_auto_claim_rule", autoClaims);
        var eventIds = ReadMappings(connection, transaction, "title_request_event", eventRows);
        var deliveryIds = ReadMappings(connection, transaction, "email_delivery_event", deliveryRows);
        var deletedIds = ReadMappings(connection, transaction, "deleted_request_audit", deletedRows);
        var templateRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Concat(MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates"))
            .ToArray();
        var templateIds = ReadMappings(connection, transaction, "email_template", templateRows);
        var formatIds = ReadFormatMappings(connection, transaction, formats);

        var bootstrap = MigrationIndependentBootstrapVerifier.Derive(connection, transaction, package, externalConfigurationPath);
        Ensure(bootstrap?.TargetStaffUserId == bootstrapTargetStaffUserId && (bootstrap?.Inserted ?? false) == bootstrapInserted,
            "bootstrap authority derived from source necessity and configured identity");
        VerifyStaff(connection, transaction, staff, staffIds, organizationIds, package, bootstrap);
        VerifyWorkflowTags(connection, transaction, tags, tagIds);
        VerifyAutoClaimRules(connection, transaction, autoClaims, claimRuleIds, staffIds, organizationIds, package);
        VerifyRequests(connection, transaction, package, requests, requestIds, staffIds, claimRuleIds, formatIds, organizations, autoClaims);
        VerifyAdditionalCopies(connection, transaction, package, copies, copyIds, requestIds, staffIds, organizationIds);
        VerifyDeletedAudit(connection, transaction, deletedRows, deletedIds, staffIds, organizationIds, package);
        VerifyBranding(connection, transaction, package);
        VerifyTitleRequestTags(connection, transaction, requests, titleTagRows, titleTagIds, requestIds, tagIds);
        VerifyTitleRequestEvents(connection, transaction, package, eventRows, eventIds, requestIds, requests);
        VerifyEmailDeliveryEvents(connection, transaction, deliveryRows, deliveryIds, requestIds, templateIds);
    }

    internal static void VerifyClaimAndPlacedBibReportTransformations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        VerifyTitleRequestClaimReportTransformations(connection, transaction, package, report);
        VerifyAdditionalCopyClaimReportTransformations(connection, transaction, package, report);
        VerifyPlacedBibProtectionReportTransformations(package, report);
    }

    private static void VerifyTitleRequestClaimReportTransformations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var rows = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        var staffRows = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var ruleRows = MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules");
        var formatRows = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var staffIds = ReadMappings(connection, transaction, "staff_user", staffRows);
        var ruleIds = ReadMappings(connection, transaction, "format_auto_claim_rule", ruleRows);
        var formatIds = ReadFormatMappings(connection, transaction, formatRows);
        var rulesById = ruleRows.ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var formatsById = formatRows.ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var statuses = ReadStatusMap(package);
        var transformations = ReadReportTransformationsBySourceId(report, "title_request_claim");

        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var formatId = ResolveRequestFormat(connection, transaction, row, formatIds, formatsById, organizations);
            var claim = ProjectRequestClaim(connection, transaction, row, ResolveRequestStatus(row, statuses),
                formatId, staffIds, ruleIds, rulesById);
            if (!claim.HasSourceAttribution)
            {
                continue;
            }

            if (!transformations.Remove(sourceId, out var transformation) ||
                !ReportNullableStringMatches(transformation, "sourceClaimantId", claim.SourceClaimantId) ||
                !ReportNullableLongMatches(transformation, "mappedStaffUserId", claim.MappedStaffUserId) ||
                !ReportNullableLongMatches(transformation, "effectiveStaffUserId", claim.StaffUserId) ||
                !ReportNullableStringMatches(transformation, "sourceDisplayName", row.Text("claimedByDisplayName")) ||
                !ReportNullableDateTimeMatches(transformation, "sourceClaimedAtUtc", row.UtcDateTime("claimedAt")) ||
                !ReportNullableStringMatches(transformation, "sourceClaimType", row.Text("claimType")) ||
                !ReportNullableStringMatches(transformation, "sourceClaimRuleId", row.String("claimRuleId")) ||
                !ReportStringMatches(transformation, "reason", claim.Reason) ||
                !ReportBooleanMatches(transformation, "migrationAnnotationInserted", claim.RequiresMigrationAnnotation))
            {
                FailReportProjection("The title-request claim report does not match immutable source fields and the independently verified claim projection.");
            }
        }

        if (transformations.Count != 0)
        {
            FailReportProjection("The title-request claim report contains an extra or duplicate source identity.");
        }
    }

    private static void VerifyAdditionalCopyClaimReportTransformations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var rows = MigrationPackageReader.ReadRows(package, "additional-copy-requests.json", "additional_copy_requests");
        var staffRows = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        var staffIds = ReadMappings(connection, transaction, "staff_user", staffRows);
        var transformations = ReadReportTransformationsBySourceId(report, "additional_copy_claim");

        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var claim = ProjectCopyClaim(connection, transaction, row,
                NormalizeAdditionalCopyStatus(row.RequiredString("status")), staffIds);
            if (!claim.HasSourceAttribution)
            {
                continue;
            }

            if (!transformations.Remove(sourceId, out var transformation) ||
                !ReportNullableStringMatches(transformation, "sourceClaimantId", claim.SourceClaimantId) ||
                !ReportNullableLongMatches(transformation, "mappedStaffUserId", claim.MappedStaffUserId) ||
                !ReportNullableLongMatches(transformation, "effectiveStaffUserId", claim.StaffUserId) ||
                !ReportNullableStringMatches(transformation, "sourceDisplayName", row.Text("claimedByDisplayName")) ||
                !ReportNullableDateTimeMatches(transformation, "sourceClaimedAtUtc", row.UtcDateTime("claimedAt")) ||
                !ReportStringMatches(transformation, "reason", claim.Reason) ||
                !ReportBooleanMatches(transformation, "migrationAnnotationInserted", claim.RequiresMigrationAnnotation))
            {
                FailReportProjection("The additional-copy claim report does not match immutable source fields and the independently verified claim projection.");
            }
        }

        if (transformations.Count != 0)
        {
            FailReportProjection("The additional-copy claim report contains an extra or duplicate source identity.");
        }
    }

    private static void VerifyPlacedBibProtectionReportTransformations(
        ValidatedMigrationPackage package,
        JsonElement report)
    {
        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var organizationIds = organizations.ToDictionary(
            row => row.RequiredString("id"),
            row => row.Int32("organizationId") ?? Fail<int>("A source organization has no native identity."),
            StringComparer.Ordinal);
        var statuses = ReadStatusMap(package);
        var closeReasons = ReadCloseReasonMap(package);
        var eventRows = MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events");
        var transformations = ReadReportTransformationsBySourceId(report, "placed_bib_protection");

        foreach (var request in requests)
        {
            var sourceId = request.RequiredString("id");
            var status = ResolveRequestStatus(request, statuses);
            var closeReason = ResolveRequestCloseReason(request, closeReasons);
            var evidence = ReadPlacementEvidence(request, sourceId, status, closeReason, eventRows, statuses, closeReasons);
            var sourceHints = ReadPlacementHints(request, sourceId, status, closeReason, eventRows, evidence);
            if (evidence.Count == 0 && sourceHints.Count > 0)
            {
                FailReportProjection("Hint-only BIB history cannot have a successful placed-protection report.");
            }

            var libraryId = ResolveSourceLibrary(
                (request.Int32("libraryOrgId") ?? Fail<int>("A title request has no source library identity.")).ToString(CultureInfo.InvariantCulture),
                organizationIds,
                package);
            int? bibId = null;
            IReadOnlyList<PlacementBibSource> bibSources = [];
            if (evidence.Count > 0)
            {
                using var metadata = JsonDocument.Parse(BuildPlacementMetadata(request, sourceId, eventRows, evidence));
                var metadataRoot = metadata.RootElement;
                var metadataBibId = metadataRoot.GetProperty("bibId");
                bibId = metadataBibId.ValueKind == JsonValueKind.Null ? null : metadataBibId.GetInt32();
                bibSources = metadataRoot.GetProperty("bibSources").EnumerateArray()
                    .Select(item => new PlacementBibSource(
                        item.GetProperty("sourceCollection").GetString()!,
                        item.GetProperty("sourceRecordId").GetString()!,
                        item.GetProperty("sourceField").GetString()!,
                        item.GetProperty("bibId").GetInt32(),
                        item.GetProperty("sourceValue").GetString()!))
                    .ToArray();
            }

            if (!transformations.Remove(sourceId, out var transformation) ||
                !ReportInt32Matches(transformation, "libraryOrganizationId", libraryId) ||
                !ReportStringMatches(transformation, "status", status) ||
                !ReportNullableInt32Matches(transformation, "bibId", bibId) ||
                !ReportPlacementEvidenceMatches(transformation.GetProperty("evidence"), evidence) ||
                !ReportPlacementEvidenceMatches(transformation.GetProperty("hints"), Array.Empty<PlacementEvidence>()) ||
                !ReportPlacementBibSourcesMatch(transformation.GetProperty("bibSources"), bibSources) ||
                !ReportStringMatches(transformation, "action", evidence.Count > 0 ? "inserted" : "no_placement_evidence"))
            {
                FailReportProjection("The placed-BIB protection report does not match immutable request/event evidence and the independently verified marker rows.");
            }
        }

        if (transformations.Count != 0)
        {
            FailReportProjection("The placed-BIB protection report contains an extra or duplicate source identity.");
        }
    }

    private static Dictionary<string, JsonElement> ReadReportTransformationsBySourceId(JsonElement report, string entity)
    {
        var transformations = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var transformation in report.GetProperty("transformations").EnumerateArray()
                     .Where(item => string.Equals(item.GetProperty("entity").GetString(), entity, StringComparison.Ordinal)))
        {
            var sourceId = transformation.GetProperty("sourceId").GetString();
            if (string.IsNullOrEmpty(sourceId) || !transformations.TryAdd(sourceId, transformation))
            {
                FailReportProjection($"The {entity} report has a missing or duplicate source identity.");
            }
        }
        return transformations;
    }

    private static bool ReportNullableStringMatches(JsonElement item, string propertyName, string? expected)
    {
        var property = item.GetProperty(propertyName);
        return expected is null
            ? property.ValueKind == JsonValueKind.Null
            : property.ValueKind == JsonValueKind.String && string.Equals(property.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool ReportNullableLongMatches(JsonElement item, string propertyName, long? expected)
    {
        var property = item.GetProperty(propertyName);
        return expected is null
            ? property.ValueKind == JsonValueKind.Null
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var actual) && actual == expected.Value;
    }

    private static bool ReportNullableInt32Matches(JsonElement item, string propertyName, int? expected)
    {
        var property = item.GetProperty(propertyName);
        return expected is null
            ? property.ValueKind == JsonValueKind.Null
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var actual) && actual == expected.Value;
    }

    private static bool ReportInt32Matches(JsonElement item, string propertyName, int expected) =>
        item.GetProperty(propertyName).ValueKind == JsonValueKind.Number &&
        item.GetProperty(propertyName).TryGetInt32(out var actual) &&
        actual == expected;

    private static bool ReportStringMatches(JsonElement item, string propertyName, string expected) =>
        item.GetProperty(propertyName).ValueKind == JsonValueKind.String &&
        string.Equals(item.GetProperty(propertyName).GetString(), expected, StringComparison.Ordinal);

    private static bool ReportDateTimeMatches(JsonElement item, string propertyName, DateTime expected)
    {
        var property = item.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var actual) &&
            actual.UtcDateTime == expected;
    }

    private static bool ReportNullableDateTimeMatches(JsonElement item, string propertyName, DateTime? expected)
    {
        var property = item.GetProperty(propertyName);
        return expected is null
            ? property.ValueKind == JsonValueKind.Null
            : ReportDateTimeMatches(item, propertyName, expected.Value);
    }

    private static bool ReportBooleanMatches(JsonElement item, string propertyName, bool expected) =>
        (item.GetProperty(propertyName).ValueKind is JsonValueKind.True or JsonValueKind.False) &&
        item.GetProperty(propertyName).GetBoolean() == expected;

    private static bool ReportPlacementEvidenceMatches(
        JsonElement actual,
        IReadOnlyCollection<PlacementEvidence> expected)
    {
        if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.Count)
        {
            return false;
        }

        var actualSet = new HashSet<PlacementEvidence>();
        foreach (var item in actual.EnumerateArray())
        {
            var value = new PlacementEvidence(
                item.GetProperty("kind").GetString()!,
                item.GetProperty("sourceCollection").GetString()!,
                item.GetProperty("sourceRecordId").GetString()!,
                item.GetProperty("sourceField").GetString()!,
                item.GetProperty("value").GetString()!);
            if (!actualSet.Add(value))
            {
                return false;
            }
        }
        return actualSet.SetEquals(expected);
    }

    private static bool ReportPlacementBibSourcesMatch(
        JsonElement actual,
        IReadOnlyCollection<PlacementBibSource> expected)
    {
        if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.Count)
        {
            return false;
        }

        var actualSet = new HashSet<PlacementBibSource>();
        foreach (var item in actual.EnumerateArray())
        {
            var value = new PlacementBibSource(
                item.GetProperty("sourceCollection").GetString()!,
                item.GetProperty("sourceRecordId").GetString()!,
                item.GetProperty("sourceField").GetString()!,
                item.GetProperty("bibId").GetInt32(),
                item.GetProperty("sourceValue").GetString()!);
            if (!actualSet.Add(value))
            {
                return false;
            }
        }
        return actualSet.SetEquals(expected);
    }

    private static void FailReportProjection(string message) =>
        throw new MigrationOperationException("reconciliation_report_mismatch", message);

    private static void VerifyStaff(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> mappings,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package,
        ExpectedMigrationBootstrap? bootstrap)
    {
        var expectedIds = new HashSet<long>();
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var targetId = mappings[sourceId];
            Ensure(expectedIds.Add(targetId), "unique mapped staff population");
            var role = row.RequiredString("role").ToLowerInvariant();
            var organizationId = role == "super_admin"
                ? 1
                : ResolveSourceLibrary(
                    (row.Int32("libraryOrgId") ?? Fail<int>("A source staff row has no organization identity.")).ToString(CultureInfo.InvariantCulture),
                    organizationIds,
                    package);
            var authenticationEmail = RealEmail(row.String("email"));
            var weeklyEmail = RealEmail(row.String("weekly_action_summary_email"));
            var notificationEmail = authenticationEmail ?? weeklyEmail;
            using var command = new SqlCommand(
                "SELECT [EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName], [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive], [WeeklyActionSummaryEnabled], [WeeklyActionSummaryEmail], [PurchaseReminderDefault], [AdditionalCopyReminderDefault], [DefaultMineUnclaimedFilter], [LastLoginUtc] FROM [asap].[StaffUser] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            var bootstrapMutated = bootstrap?.TargetStaffUserId == targetId;
            Ensure(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1) &&
                Same(reader, 2, bootstrapMutated ? bootstrap!.AuthenticationEmail : authenticationEmail) &&
                Same(reader, 3, (bootstrapMutated ? bootstrap!.AuthenticationEmail : authenticationEmail)?.ToUpperInvariant()) &&
                Same(reader, 6, bootstrapMutated ? "super_admin" : role) &&
                reader.GetInt32(7) == (bootstrapMutated ? 1 : organizationId) &&
                reader.GetBoolean(8) == (bootstrapMutated || row.Bool("active")) &&
                Same(reader, 4, row.String("displayName") ?? row.String("username")) &&
                Same(reader, 5, notificationEmail) &&
                reader.GetBoolean(9) == row.Bool("weekly_action_summary_enabled") &&
                Same(reader, 10, weeklyEmail) &&
                reader.GetBoolean(11) == row.Bool("purchase_reminder_default") &&
                reader.GetBoolean(12) == row.Bool("additional_copy_reminder_default") &&
                reader.GetBoolean(13) == row.Bool("default_mine_unclaimed_filter") &&
                Same(reader, 14, row.UtcDateTime("lastLogin")),
                "staff source fields and pinned authentication-email transform");
        }

        if (bootstrap is { Inserted: true })
        {
            var bootstrapStaffUserId = bootstrap.TargetStaffUserId;
            Ensure(expectedIds.Add(bootstrapStaffUserId), "single operator-created bootstrap staff row");
            using var bootstrapCommand = new SqlCommand(
                "SELECT [EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName], [Role], [OrganizationId], [IsActive], [WeeklyActionSummaryEnabled], [PurchaseReminderDefault], [AdditionalCopyReminderDefault], [DefaultMineUnclaimedFilter], [LastLoginUtc], [DisplayName], [NotificationEmail], [WeeklyActionSummaryEmail] FROM [asap].[StaffUser] WHERE [Id] = @id;",
                connection,
                transaction);
            bootstrapCommand.Parameters.AddWithValue("@id", bootstrapStaffUserId);
            using var reader = bootstrapCommand.ExecuteReader();
            Ensure(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1) &&
                Same(reader, 2, bootstrap.AuthenticationEmail) &&
                Same(reader, 3, bootstrap.AuthenticationEmail.ToUpperInvariant()) &&
                Same(reader, 4, "super_admin") && reader.GetInt32(5) == 1 && reader.GetBoolean(6) &&
                !reader.GetBoolean(7) && !reader.GetBoolean(8) && !reader.GetBoolean(9) && !reader.GetBoolean(10) && reader.IsDBNull(11) &&
                Same(reader, 12, bootstrap.DisplayName) && Same(reader, 13, bootstrap.NotificationEmail) && reader.IsDBNull(14),
                "operator-created bootstrap staff scope and default preferences");
        }
        else
        {
            Ensure(bootstrap is null || expectedIds.Contains(bootstrap.TargetStaffUserId),
                "bootstrap promotion maps to source staff identity");
        }

        using var population = new SqlCommand("SELECT [Id] FROM [asap].[StaffUser];", connection, transaction);
        using var populationReader = population.ExecuteReader();
        var actualIds = new HashSet<long>();
        while (populationReader.Read())
        {
            actualIds.Add(populationReader.GetInt64(0));
        }
        Ensure(actualIds.SetEquals(expectedIds), "exact staff population including only the authorized bootstrap row");
    }

    private static void VerifyWorkflowTags(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> mappings)
    {
        var expected = new Dictionary<string, (string Label, int SortOrder)>(StringComparer.Ordinal)
        {
            ["duplicate_suggestion"] = ("Duplicate suggestion", 10),
            ["polaris_bib_found"] = ("Polaris BIB found", 20),
            ["polaris_bib_not_found"] = ("Polaris BIB not found", 30),
            ["polaris_multiple_matches"] = ("Multiple Polaris matches", 40)
        };
        var ordinal = 0;
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var code = NormalizeWorkflowTagCode(row);
            var label = row.String("label") ?? code;
            var sortOrder = row.Int32("sortOrder") ?? 1000 + ordinal;
            expected[code] = (label, sortOrder);
            using var command = new SqlCommand(
                "SELECT [Code], [Label], [SortOrder] FROM [asap].[WorkflowTag] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", mappings[row.RequiredString("id")]);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && Same(reader, 0, code) && Same(reader, 1, label) && reader.GetInt32(2) == sortOrder,
                "workflow tag identity, label and order");
            ordinal++;
        }

        using var all = new SqlCommand("SELECT [Code], [Label], [SortOrder] FROM [asap].[WorkflowTag];", connection, transaction);
        using var allReader = all.ExecuteReader();
        var actual = new Dictionary<string, (string Label, int SortOrder)>(StringComparer.Ordinal);
        while (allReader.Read())
        {
            actual.Add(allReader.GetString(0), (allReader.GetString(1), allReader.GetInt32(2)));
        }
        Ensure(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "workflow tag population including pinned static seeds");
    }

    private static void VerifyAutoClaimRules(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> mappings,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package)
    {
        var expectedIds = new HashSet<long>();
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var targetId = mappings[sourceId];
            expectedIds.Add(targetId);
            var libraryId = ResolveSourceLibrary(
                (row.Int32("libraryOrgId") ?? Fail<int>("A source auto-claim rule has no library identity.")).ToString(CultureInfo.InvariantCulture),
                organizationIds,
                package);
            var formatCode = NormalizeFormatCode(row.Text("format") ?? string.Empty);
            var formatId = FindFormat(connection, transaction, libraryId, formatCode) ??
                FindFormat(connection, transaction, 1, formatCode) ??
                Fail<long>("A source auto-claim rule format is absent from the target format set.");
            var sourceStaff = ConsistentReference(row, "staffUserId", "staffUser");
            var staffId = sourceStaff is not null && staffIds.TryGetValue(sourceStaff, out var mappedStaff) ? mappedStaff : (long?)null;
            var active = row.Bool("active") && staffId.HasValue && IsEligibleStaff(connection, transaction, staffId.Value, libraryId);
            var created = row.UtcDateTime("created") ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            DateTime? deactivated = active ? null : row.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime;
            using var command = new SqlCommand(
                "SELECT [LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc], [DeactivatedUtc] FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && reader.GetInt32(0) == libraryId && reader.GetInt64(1) == formatId &&
                NullableLong(reader, 2) == staffId && reader.GetBoolean(3) == active &&
                Same(reader, 4, created) && Same(reader, 5, deactivated),
                "format auto-claim rule fields and eligibility transform");
        }
        Ensure(TargetIds(connection, transaction, "SELECT [Id] FROM [asap].[FormatAutoClaimRule];").SetEquals(expectedIds),
            "exact auto-claim rule population");
    }

    private static void VerifyRequests(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> ruleIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyList<SourceRow> organizations,
        IReadOnlyList<SourceRow> rules)
    {
        var statuses = ReadStatusMap(package);
        var closeReasons = ReadCloseReasonMap(package);
        var ruleRows = rules.ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var sourceFormatRows = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats")
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var organizationIds = organizations.ToDictionary(
            organization => organization.RequiredString("id"),
            organization => organization.Int32("organizationId") ?? Fail<int>("A source organization has no native identity."),
            StringComparer.Ordinal);
        var exactTargetIds = new HashSet<long>();
        var eventRows = MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events");
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var targetId = requestIds[sourceId];
            exactTargetIds.Add(targetId);
            var libraryId = ResolveSourceLibrary(
                (row.Int32("libraryOrgId") ?? Fail<int>("A title request has no source library.")).ToString(CultureInfo.InvariantCulture),
                organizationIds,
                package);
            var formatId = ResolveRequestFormat(connection, transaction, row, formatIds, sourceFormatRows, organizations);
            var status = ResolveRequestStatus(row, statuses);
            var closeReason = ResolveRequestCloseReason(row, closeReasons);
            var sourceClaim = ProjectRequestClaim(connection, transaction, row, status, formatId, staffIds, ruleIds, ruleRows);
            var isbnStatus = NormalizeIsbnStatus(row);
            var retryCount = isbnStatus == "skipped_no_isbn" ? 0 : row.Int32("isbnCheckRetryCount") ?? 0;
            var customFields = MigrationCustomFieldsSnapshot.Read(row);
            var evidence = ReadPlacementEvidence(row, sourceId, status, closeReason, eventRows, statuses, closeReasons);
            var legacyHoldProtected = evidence.Count > 0;

            using var command = new SqlCommand(
                "SELECT [LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [StaffLibraryOrganizationIdCreatedBy], [Barcode], [Email], [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription], [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot], [Title], [Author], [Identifier], [Publication], [ExactPublicationDate], [CustomFieldsJson], [AutoHold], [MaterialFormatId], [Status], [CloseReason], [BibId], [BibIdStaffVerified], [LegacyHoldProtected], [Notes], [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClaimRuleId], [LastPromoterCheckUtc], [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount], [IsbnCheckLastErrorCode], [LastCheckedUtc], [CreatedUtc], [UpdatedUtc], [PatronIdSnapshot] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() &&
                Same(reader, 0, row.String("legacyId")) && reader.GetInt32(1) == libraryId &&
                NullableInt(reader, 2) == row.Int32("patronOrgId") && NullableInt(reader, 3) == row.Int32("staffLibraryOrgIdCreatedBy") &&
                Same(reader, 4, row.RequiredString("barcode")) && Same(reader, 5, row.Text("email")) &&
                Same(reader, 6, row.Text("nameFirst")) && Same(reader, 7, row.Text("nameLast")) &&
                NullableInt(reader, 8) == row.PositiveInt32("patronCodeId") && Same(reader, 9, row.Text("patronCodeDescription")) &&
                NullableInt(reader, 10) == row.Int32("preferredPickupBranchId") && Same(reader, 11, row.Text("preferredPickupBranchName")) &&
                Same(reader, 12, row.Text("libraryOrgName")) && Same(reader, 13, row.RequiredText("title")) &&
                Same(reader, 14, row.Text("author")) && Same(reader, 15, row.Text("identifier")) &&
                Same(reader, 16, row.Text("publication")) && Same(reader, 17, ParseSourceDate(row.String("exactPublicationDate")) ) &&
                JsonSame(reader, 18, customFields) && reader.GetBoolean(19) == row.Bool("autohold") &&
                reader.GetInt64(20) == formatId && Same(reader, 21, status) && Same(reader, 22, closeReason) &&
                NullableInt(reader, 23) == row.PositiveInt32("bibid", "source_bib_invalid") && !reader.GetBoolean(24) &&
                reader.GetBoolean(25) == legacyHoldProtected && Same(reader, 26, row.Text("notes")) &&
                NullableLong(reader, 27) == sourceClaim.StaffUserId && Same(reader, 28, sourceClaim.DisplayName) &&
                Same(reader, 29, sourceClaim.ClaimedAtUtc) && Same(reader, 30, sourceClaim.ClaimType) &&
                NullableLong(reader, 31) == sourceClaim.ClaimRuleId && Same(reader, 32, row.UtcDateTime("lastPromoterCheck")) &&
                Same(reader, 33, isbnStatus) && Same(reader, 34, row.Text("isbnCheckResult")) && reader.GetInt32(35) == retryCount &&
                Same(reader, 36, isbnStatus == "error_max_retries" ? "legacy_retry_exhausted" : null) &&
                Same(reader, 37, row.UtcDateTime("lastChecked")) && Same(reader, 38, row.UtcDateTime("created")) &&
                Same(reader, 39, row.UtcDateTime("updated")) && reader.IsDBNull(40),
                "complete title request field projection and pinned transforms");
        }
        Ensure(TargetIds(connection, transaction, "SELECT [Id] FROM [asap].[TitleRequest];").SetEquals(exactTargetIds),
            "exact title-request population");
        VerifyClaimAnnotations(connection, transaction, package, rows, requestIds, staffIds, ruleIds, formatIds, ruleRows, statuses);
    }

    private static void VerifyAdditionalCopies(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> copyIds,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests")
            .ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        var exportedAtUtc = package.Manifest.ExportedAtUtc.UtcDateTime;
        var expectedIds = new HashSet<long>();
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var targetId = copyIds[sourceId];
            expectedIds.Add(targetId);
            var libraryId = ResolveSourceLibrary(
                (row.Int32("libraryOrgId") ?? Fail<int>("An additional-copy request has no library.")).ToString(CultureInfo.InvariantCulture),
                organizationIds,
                package);
            var sourceRequestId = row.String("sourceTitleRequest");
            long? requestId = null;
            if (sourceRequestId is not null)
            {
                Ensure(requestIds.TryGetValue(sourceRequestId, out var mapped), "additional-copy source request mapping");
                requestId = mapped;
                Ensure(requests.TryGetValue(sourceRequestId, out var sourceRequest) && sourceRequest.Int32("libraryOrgId") == libraryId,
                    "additional-copy source request scope");
            }
            long? formatId = null;
            if (row.Text("format") is { } sourceFormat && TrimJavascriptFormatCode(sourceFormat).Length > 0)
            {
                formatId = FindFormat(connection, transaction, libraryId, NormalizeFormatCode(sourceFormat)) ??
                    FindFormat(connection, transaction, 1, NormalizeFormatCode(sourceFormat)) ??
                    Fail<long?>("An additional-copy format is unresolved.");
            }
            var status = NormalizeAdditionalCopyStatus(row.RequiredString("status"));
            var claim = ProjectCopyClaim(connection, transaction, row, status, staffIds);
            var notes = AdditionalCopyNotes(row, claim, exportedAtUtc);
            var created = row.UtcDateTime("created") ?? Fail<DateTime>("An additional-copy creation time is missing.");
            var updated = row.UtcDateTime("updated") ?? created;
            var sourceCreatedByDisplayName = row.Text("createdByUsername");
            var createdByDisplayName = string.IsNullOrEmpty(sourceCreatedByDisplayName)
                ? null
                : sourceCreatedByDisplayName;
            var sourceClosedByDisplayName = row.Text("closedByUsername");
            var closedByDisplayName = string.IsNullOrEmpty(sourceClosedByDisplayName)
                ? null
                : sourceClosedByDisplayName;
            using var command = new SqlCommand(
                "SELECT [LegacyId], [SourceTitleRequestId], [LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [Author], [Identifier], [Publication], [MaterialFormatId], [FormatSnapshot], [Status], [Notes], [CreatedByStaffUserId], [CreatedByDisplayName], [CreatedUtc], [UpdatedUtc], [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClaimRuleId], [ClosedByStaffUserId], [ClosedByDisplayName], [ClosedUtc] FROM [asap].[AdditionalCopyRequest] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && Same(reader, 0, row.String("legacyId")) && NullableLong(reader, 1) == requestId &&
                reader.GetInt32(2) == libraryId && Same(reader, 3, row.Text("libraryOrgName")) &&
                reader.GetInt32(4) == row.PositiveInt32("bibid", "source_bib_invalid") && Same(reader, 5, row.RequiredText("title")) &&
                Same(reader, 6, row.Text("author")) && Same(reader, 7, row.Text("identifier")) &&
                Same(reader, 8, row.Text("publication")) && NullableLong(reader, 9) == formatId &&
                Same(reader, 10, row.Text("format")) && Same(reader, 11, status) && Same(reader, 12, notes) &&
                NullableLong(reader, 13) == MapOptional(row.String("createdByStaff"), staffIds) &&
                Same(reader, 14, createdByDisplayName) && Same(reader, 15, created) && Same(reader, 16, updated) &&
                NullableLong(reader, 17) == claim.StaffUserId && Same(reader, 18, claim.DisplayName) &&
                Same(reader, 19, claim.ClaimedAtUtc) && reader.IsDBNull(20) && reader.IsDBNull(21) &&
                NullableLong(reader, 22) == MapOptional(row.String("closedByStaff"), staffIds) &&
                Same(reader, 23, closedByDisplayName) && Same(reader, 24, row.UtcDateTime("closedAt")),
                "complete additional-copy history and claim transform");
        }
        Ensure(TargetIds(connection, transaction, "SELECT [Id] FROM [asap].[AdditionalCopyRequest];").SetEquals(expectedIds),
            "exact additional-copy population");
    }

    private static void VerifyDeletedAudit(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> mappings,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package)
    {
        var expectedIds = new HashSet<long>();
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var targetId = mappings[sourceId];
            expectedIds.Add(targetId);
            var barcode = row.String("barcode");
            var libraryId = ResolveSourceLibrary(
                (row.Int32("libraryOrgId") ?? Fail<int>("A deleted-request audit row has no library identity.")).ToString(CultureInfo.InvariantCulture),
                organizationIds,
                package);
            using var command = new SqlCommand(
                "SELECT [RequestType], [OriginalRequestKey], [LibraryOrganizationId], [Title], [Author], [Identifier], [BibId], [Status], [CloseReason], [MaskedBarcode], [CreatedUtc], [DeletedUtc], [DeletedByStaffUserId], [DeletedByDisplayName] FROM [asap].[DeletedRequestAudit] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && Same(reader, 0, barcode is null ? "additional_copy" : "title_request") &&
                Same(reader, 1, row.RequiredString("titleRequestId")) && reader.GetInt32(2) == libraryId &&
                Same(reader, 3, row.Text("title")) && Same(reader, 4, row.Text("author")) &&
                Same(reader, 5, row.Text("identifier")) && NullableInt(reader, 6) == row.PositiveInt32("bibid", "source_bib_invalid") &&
                Same(reader, 7, NormalizeStatus(row.RequiredString("status"))) &&
                Same(reader, 8, NormalizeCloseReason(row.String("closeReason"))) &&
                Same(reader, 9, MaskBarcode(barcode)) &&
                Same(reader, 10, ParseUtc(row.JsonPropertyString("snapshot", "created"))) &&
                Same(reader, 11, row.UtcDateTime("deletedAt")) &&
                NullableLong(reader, 12) == MapOptional(row.String("deletedByStaff"), staffIds) &&
                Same(reader, 13, row.Text("deletedByUsername")),
                "complete reduced deleted-request audit projection");
        }
        Ensure(TargetIds(connection, transaction, "SELECT [Id] FROM [asap].[DeletedRequestAudit];").SetEquals(expectedIds),
            "exact deleted-request audit population");
    }

    private static void VerifyBranding(SqlConnection connection, SqlTransaction? transaction, ValidatedMigrationPackage package)
    {
        var rows = MigrationPackageReader.ReadRows(package, "branding.json", "branding");
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations")
            .ToDictionary(row => row.RequiredString("id"), row => row.Int32("organizationId") ?? Fail<int>("A source organization has no identity."), StringComparer.Ordinal);
        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        var sourceBranding = new Dictionary<int, SourceRow>();
        var altRows = new Dictionary<int, SourceRow>();
        var expectedOrganizations = new HashSet<int>();
        foreach (var row in rows)
        {
            var organizationId = row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase)
                ? 1
                : ResolveSourceLibrary(row.RequiredString("libraryOrganization"), organizations, package);
            Ensure(expectedOrganizations.Add(organizationId), "unique source branding scope");
            sourceBranding.Add(organizationId, row);
        }
        foreach (var row in uiRows.Where(item => item.Text("logoAlt") is not null))
        {
            var organizationId = row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase)
                ? 1
                : ResolveSourceLibrary(row.RequiredString("libraryOrganization"), organizations, package);
            Ensure(altRows.TryAdd(organizationId, row), "unique source branding alt-text scope");
            expectedOrganizations.Add(organizationId);
        }

        foreach (var organizationId in expectedOrganizations)
        {
            sourceBranding.TryGetValue(organizationId, out var source);
            altRows.TryGetValue(organizationId, out var altSource);
            byte[]? bytes = null;
            string? digest = null;
            if (source is not null)
            {
                var assetPath = Path.Combine(package.RootPath, source.RequiredString("assetPath").Replace('/', Path.DirectorySeparatorChar));
                bytes = File.ReadAllBytes(assetPath);
                digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
            var expectedAlt = altSource?.Text("logoAlt") ?? source?.Text("logoAlt");
            var expectedUpdated = altSource is not null
                ? altSource.UtcDateTime("updated") ?? package.Manifest.ExportedAtUtc.UtcDateTime
                : package.Manifest.ExportedAtUtc.UtcDateTime;
            using var command = new SqlCommand(
                "SELECT [LogoData], [LogoContentType], [LogoFileName], [LogoAltText], [UpdatedUtc] FROM [asap].[Branding] WHERE [OrganizationId] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", organizationId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && (bytes is null ? reader.IsDBNull(0) : !reader.IsDBNull(0)), "source branding asset or alt-text row");
            var targetBytes = reader.IsDBNull(0) ? null : reader.GetFieldValue<byte[]>(0);
            Ensure((bytes is null || targetBytes is not null && targetBytes.LongLength == source!.Int32("length") &&
                    string.Equals(Convert.ToHexStringLower(SHA256.HashData(targetBytes)), source.RequiredString("sha256"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(digest, source.RequiredString("sha256"), StringComparison.OrdinalIgnoreCase) &&
                    targetBytes.AsSpan().SequenceEqual(bytes)) &&
                Same(reader, 1, source?.Text("contentType")) &&
                Same(reader, 2, source?.Text("fileName")) &&
                Same(reader, 3, expectedAlt) &&
                Same(reader, 4, expectedUpdated),
                "branding bytes, metadata and package timestamp");
        }
        Ensure(TargetIntIds(connection, transaction, "SELECT [OrganizationId] FROM [asap].[Branding];").SetEquals(expectedOrganizations),
            "exact imported branding population");
    }

    private static void VerifyTitleRequestTags(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> requestRows,
        IReadOnlyList<SourceRow> tagRows,
        IReadOnlyDictionary<string, long> tagRelationshipMappings,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> tagIds)
    {
        var expected = new HashSet<(long RequestId, long TagId)>();
        foreach (var row in tagRows)
        {
            var requestReference = row.RequiredString("titleRequest");
            var tagReference = row.RequiredString("tag");
            Ensure(requestIds.TryGetValue(requestReference, out var requestId), "title request tag request mapping");
            Ensure(tagIds.TryGetValue(tagReference, out var tagId), "title request tag tag mapping");
            Ensure(tagRelationshipMappings[row.RequiredString("id")] == requestId && expected.Add((requestId, tagId)),
                "unique source title-request tag relationship");
        }

        var foundTagId = ScalarLong(connection, transaction,
            "SELECT [Id] FROM [asap].[WorkflowTag] WHERE [Code] = N'polaris_bib_found';");
        foreach (var row in requestRows)
        {
            var status = NormalizeIsbnStatus(row);
            if (status == "found")
            {
                var requestId = requestIds[row.RequiredString("id")];
                expected.Add((requestId, foundTagId));
            }
        }

        var actual = new HashSet<(long RequestId, long TagId)>();
        using (var command = new SqlCommand(
                   "SELECT [TitleRequestId], [WorkflowTagId] FROM [asap].[TitleRequestWorkflowTag];",
                   connection,
                   transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                actual.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }
        Ensure(actual.SetEquals(expected), "exact explicit and canonical BIB-found title-request tag relationships");
    }

    private static void VerifyTitleRequestEvents(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> eventRows,
        IReadOnlyDictionary<string, long> eventIds,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyList<SourceRow> requestRows)
    {
        var statusMap = ReadStatusMap(package);
        var closeMap = ReadCloseReasonMap(package);
        foreach (var row in eventRows)
        {
            var sourceId = row.RequiredString("id");
            var sourceRequestId = row.RequiredString("titleRequest");
            var requestId = requestIds[sourceRequestId];
            var sourceType = row.RequiredString("eventType");
            var normalizedType = sourceType.Trim().ToLowerInvariant();
            var eventType = KnownEventTypes.Contains(normalizedType) ? normalizedType : "legacy";
            var toStatus = ResolveEventStatus(row.String("toStatus"), statusMap);
            var closeReason = ResolveEventCloseReason(row.String("closeReason"), closeMap);
            var created = row.UtcDateTime("created") ?? Fail<DateTime>("A source title-request event has no timestamp.");
            var metadata = JsonSerializer.Serialize(new
            {
                sourceCollection = "title_request_events",
                sourceRecordId = sourceId,
                sourceEventType = row.Text("eventType"),
                sourceFromStatus = row.String("fromStatus"),
                sourceToStatus = row.String("toStatus"),
                sourceCloseReason = row.String("closeReason"),
                sourceMetadata = ParseJson(row.JsonText("metadata"))
            });
            using var command = new SqlCommand(
                "SELECT [TitleRequestId], [EventType], [Status], [CloseReason], [ActorType], [StaffUserId], [ActorName], [Message], [MetadataJson], [CreatedUtc] FROM [asap].[TitleRequestEvent] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", eventIds[sourceId]);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && reader.GetInt64(0) == requestId && Same(reader, 1, eventType) &&
                Same(reader, 2, toStatus) && Same(reader, 3, closeReason) &&
                Same(reader, 4, row.RequiredString("actorType").Trim().ToLowerInvariant()) && reader.IsDBNull(5) &&
                Same(reader, 6, row.Text("actorName")) && Same(reader, 7, row.Text("message")) &&
                JsonSame(reader, 8, metadata) && Same(reader, 9, created),
                "complete source title-request event projection");
        }

        var markers = 0;
        var exportedAtUtc = package.Manifest.ExportedAtUtc.UtcDateTime;
        foreach (var request in requestRows)
        {
            var sourceId = request.RequiredString("id");
            var requestId = requestIds[sourceId];
            var status = ResolveRequestStatus(request, statusMap);
            var closeReason = ResolveRequestCloseReason(request, closeMap);
            var evidence = ReadPlacementEvidence(request, sourceId, status, closeReason, eventRows, statusMap, closeMap);
            var hintedBibValues = ReadPlacementHints(request, sourceId, status, closeReason, eventRows, evidence);
            Ensure(evidence.Count > 0 || hintedBibValues.Count == 0,
                "ambiguous unplaced BIB hints require source correction");
            var inserted = evidence.Count > 0;
            using (var protection = new SqlCommand(
                       "SELECT [LegacyHoldProtected] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                       connection,
                       transaction))
            {
                protection.Parameters.AddWithValue("@id", requestId);
                Ensure(Convert.ToBoolean(protection.ExecuteScalar(), CultureInfo.InvariantCulture) == inserted,
                    "structured placed-history protection flag");
            }
            var markerMetadata = inserted ? BuildPlacementMetadata(request, sourceId, eventRows, evidence) : null;
            using var marker = new SqlCommand(
                "SELECT [EventType], [Status], [CloseReason], [ActorType], [StaffUserId], [ActorName], [Message], [MetadataJson], [CreatedUtc] FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id AND [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';",
                connection,
                transaction);
            marker.Parameters.AddWithValue("@id", requestId);
            using var reader = marker.ExecuteReader();
            if (inserted)
            {
                markers++;
                Ensure(reader.Read() && Same(reader, 0, "legacy") && reader.IsDBNull(1) && reader.IsDBNull(2) &&
                    Same(reader, 3, "system") && reader.IsDBNull(4) && Same(reader, 5, "migration") &&
                    Same(reader, 6, "Legacy placed-state history protection.") &&
                    JsonSame(reader, 7, markerMetadata) && Same(reader, 8, exportedAtUtc) && !reader.Read(),
                    "placed-history marker provenance and exact row count");
            }
            else
            {
                Ensure(!reader.Read(), "no unsubstantiated placed-history marker");
            }
        }

        using var eventCount = new SqlCommand("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];", connection, transaction);
        var claimAnnotationCount = Count(connection, transaction,
            "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';");
        Ensure(Convert.ToInt32(eventCount.ExecuteScalar(), CultureInfo.InvariantCulture) == eventRows.Count + claimAnnotationCount + markers,
            "exact complete title-request event population");
    }

    private static void VerifyEmailDeliveryEvents(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> mappings,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> templateIds)
    {
        var expectedIds = new HashSet<long>();
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var sourceRequest = row.String("titleRequest");
            var sourceTemplate = row.String("emailTemplate");
            Ensure(sourceRequest is not null && sourceTemplate is not null, "email delivery event references");
            Ensure(requestIds.TryGetValue(sourceRequest!, out var requestId), "email delivery title request mapping");
            Ensure(templateIds.TryGetValue(sourceTemplate!, out var templateId), "email delivery template mapping");
            var status = row.RequiredString("status").Trim().ToLowerInvariant();
            var eventType = status is "sent" or "skipped" or "failed" ? status : "legacy";
            var created = row.UtcDateTime("created") ?? Fail<DateTime>("An email delivery event has no timestamp.");
            var metadata = JsonSerializer.Serialize(new
            {
                sourceCollection = "email_delivery_events",
                sourceRecordId = sourceId,
                sourceTitleRequestId = sourceRequest,
                targetTitleRequestId = requestId,
                sourceEmailTemplateId = sourceTemplate,
                targetEmailTemplateId = templateId,
                templateKey = row.Text("templateKey"),
                recipient = row.Text("recipient"),
                subject = row.Text("subject"),
                sourceStatus = row.Text("status"),
                error = row.Text("error"),
                sourceMetadata = ParseJson(row.JsonText("metadata"))
            });
            var targetId = mappings[sourceId];
            expectedIds.Add(targetId);
            using var command = new SqlCommand(
                "SELECT [EmailOutboxId], [ProviderMessageId], [ProviderEventId], [EventType], [ReceivedUtc], [MetadataJson] FROM [asap].[EmailDeliveryEvent] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", targetId);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1) && reader.IsDBNull(2) &&
                Same(reader, 3, eventType) && Same(reader, 4, created) && JsonSame(reader, 5, metadata),
                "complete historical email-delivery projection and null provider-owned identifiers");
        }
        Ensure(TargetIds(connection, transaction, "SELECT [Id] FROM [asap].[EmailDeliveryEvent];").SetEquals(expectedIds),
            "exact historical email-delivery population");
    }

    private static IReadOnlyDictionary<string, long> ReadMappings(
        SqlConnection connection,
        SqlTransaction? transaction,
        string entityType,
        IReadOnlyList<SourceRow> rows)
    {
        var expected = rows.Select(row => row.RequiredString("id")).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = new SqlCommand(
            "SELECT [EntityType], [PocketBaseId], [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = @entityType;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@entityType", entityType);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Ensure(string.Equals(reader.GetString(0), entityType, StringComparison.Ordinal) &&
                result.TryAdd(reader.GetString(1), Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)),
                $"unique {entityType} source mapping");
        }
        Ensure(result.Count == expected.Count && result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            $"exact {entityType} source mapping population");
        return result;
    }

    private static IReadOnlyDictionary<string, long> ReadFormatMappings(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows)
    {
        var expected = rows.Select(row => row.RequiredString("id")).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = new SqlCommand(
            "SELECT m.[PocketBaseId], CASE WHEN m.[EntityType] = N'material_format_override' THEN o.[MaterialFormatId] ELSE f.[Id] END " +
            "FROM [asap].[LegacyPocketBaseMapping] m " +
            "LEFT JOIN [asap].[MaterialFormatOverride] o ON m.[EntityType] = N'material_format_override' AND o.[Id] = m.[NewId] " +
            "LEFT JOIN [asap].[MaterialFormat] f ON (m.[EntityType] = N'material_format' AND f.[Id] = m.[NewId]) OR " +
            "(m.[EntityType] = N'material_format_override' AND f.[Id] = o.[MaterialFormatId]) " +
            "WHERE m.[EntityType] IN (N'material_format', N'material_format_override');",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            Ensure(!reader.IsDBNull(1) && result.TryAdd(reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)),
                "unique material-format mapping");
        }
        Ensure(result.Count == expected.Count && result.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            "exact material-format mapping population");
        return result;
    }

    private static HashSet<long> TargetIds(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        var result = new HashSet<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }
        return result;
    }

    private static HashSet<int> TargetIntIds(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        var result = new HashSet<int>();
        while (reader.Read())
        {
            result.Add(reader.GetInt32(0));
        }
        return result;
    }

    private static long ScalarLong(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? Fail<long>("A required static migration row is missing.") : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static int Count(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long? MapOptional(string? sourceId, IReadOnlyDictionary<string, long> mappings)
    {
        if (sourceId is null)
        {
            return null;
        }
        return mappings.TryGetValue(sourceId, out var targetId)
            ? targetId
            : Fail<long?>("A source relationship references a missing mapped identity.");
    }

    private static int ResolveSourceLibrary(
        string sourceReference,
        IReadOnlyDictionary<string, int> organizationIds,
        ValidatedMigrationPackage package)
    {
        var id = organizationIds.TryGetValue(sourceReference, out var mapped)
            ? mapped
            : int.TryParse(sourceReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && organizationIds.Values.Contains(parsed)
                ? parsed
                : Fail<int>("A source library reference cannot be resolved.");
        var row = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations")
            .SingleOrDefault(item => item.Int32("organizationId") == id);
        Ensure(id != 1 && row is not null && (row.HasValue("organizationCodeId") ? row.Int32("organizationCodeId") : row.Int32("organization_code_id")) == 2,
            "source organization library type");
        return id;
    }

    private static int ResolveSourceOrganization(string sourceReference, IReadOnlyList<SourceRow> organizations)
    {
        var row = organizations.SingleOrDefault(item => string.Equals(item.RequiredString("id"), sourceReference, StringComparison.Ordinal)) ??
            organizations.SingleOrDefault(item => item.Int32("organizationId") is { } id &&
                string.Equals(id.ToString(CultureInfo.InvariantCulture), sourceReference, StringComparison.Ordinal));
        if (row is null && int.TryParse(sourceReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nativeId))
        {
            row = organizations.SingleOrDefault(item => item.Int32("organizationId") == nativeId);
        }
        Ensure(row is not null, "source organization relationship");
        return row!.Int32("organizationId") ?? Fail<int>("A source organization has no native identity.");
    }

    private static string? RealEmail(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (cleaned is null || cleaned.EndsWith("@staff.asap.local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            var address = new MailAddress(cleaned);
            return string.Equals(address.Address, cleaned, StringComparison.OrdinalIgnoreCase) ? cleaned : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static long? FindFormat(SqlConnection connection, SqlTransaction? transaction, int ownerId, string code)
    {
        using var command = new SqlCommand(
            "SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @ownerId AND [Code] = @code;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@ownerId", ownerId);
        command.Parameters.AddWithValue("@code", code);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long ResolveRequestFormat(
        SqlConnection connection,
        SqlTransaction? transaction,
        SourceRow row,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, SourceRow> formats,
        IReadOnlyList<SourceRow> organizations)
    {
        var libraryId = row.Int32("libraryOrgId") ?? Fail<int>("A title request has no library identity.");
        if (row.String("formatRef") is { } reference)
        {
            Ensure(formatIds.TryGetValue(reference, out var formatId), "title-request target format mapping");
            Ensure(formats.TryGetValue(reference, out var sourceFormat), "title-request source format reference");
            var targetCode = ReadFormatCode(connection, transaction, formatId);
            var targetOwner = ReadFormatOwner(connection, transaction, formatId);
            var sourceScope = sourceFormat!.RequiredString("scope");
            var sourceOwner = sourceScope.Equals("system", StringComparison.OrdinalIgnoreCase)
                ? 1
                : ResolveSourceOrganization(sourceFormat.RequiredString("libraryOrganization"), organizations);
            var scalarFormat = row.Text("format");
            Ensure((sourceOwner == 1 || sourceOwner == libraryId) &&
                (targetOwner == 1 || targetOwner == libraryId) &&
                (scalarFormat is null || TrimJavascriptFormatCode(scalarFormat).Length == 0 ||
                    NormalizeFormatCode(scalarFormat) == targetCode),
                "title-request format reference ownership and scalar consistency");
            return formatId;
        }
        var code = NormalizeFormatCode(row.Text("format") ?? string.Empty);
        return FindFormat(connection, transaction, libraryId, code) ??
            FindFormat(connection, transaction, 1, code) ??
            Fail<long>("A title-request format code is unresolved.");
    }

    private static string ReadFormatCode(SqlConnection connection, SqlTransaction? transaction, long id)
    {
        using var command = new SqlCommand("SELECT [Code] FROM [asap].[MaterialFormat] WHERE [Id] = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar() as string ?? Fail<string>("A mapped material format is missing.");
    }

    private static int ReadFormatOwner(SqlConnection connection, SqlTransaction? transaction, long id)
    {
        using var command = new SqlCommand("SELECT [OwnerOrganizationId] FROM [asap].[MaterialFormat] WHERE [Id] = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar() is { } value and not DBNull
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : Fail<int>("A mapped material format is missing.");
    }

    private static IReadOnlyDictionary<string, string> ReadStatusMap(ValidatedMigrationPackage package) =>
        MigrationPackageReader.ReadRowsOrEmpty(package, "title-request-events.json", "request_statuses")
            .ToDictionary(row => row.RequiredString("id"), row => NormalizeStatus(row.RequiredString("code")), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> ReadCloseReasonMap(ValidatedMigrationPackage package) =>
        MigrationPackageReader.ReadRowsOrEmpty(package, "title-request-events.json", "request_close_reasons")
            .ToDictionary(row => row.RequiredString("id"), row => NormalizeCloseReason(row.RequiredString("code"))!, StringComparer.Ordinal);

    private static string ResolveRequestStatus(SourceRow row, IReadOnlyDictionary<string, string> statuses)
    {
        var sourceStatus = row.String("status");
        var reference = row.String("statusRef");
        var raw = sourceStatus is null ? null : NormalizeStatus(sourceStatus);
        var referenced = reference is null ? null : statuses.TryGetValue(reference, out var mapped)
            ? mapped
            : Fail<string?>("A title-request status reference is missing.");
        Ensure(raw is null || referenced is null || raw == referenced, "consistent title-request status aliases");
        return raw ?? referenced ?? Fail<string>("A title request has no effective status.");
    }

    private static string? ResolveRequestCloseReason(SourceRow row, IReadOnlyDictionary<string, string> closeReasons)
    {
        var sourceReason = row.String("closeReason");
        var reference = row.String("closeReasonRef");
        var raw = NormalizeCloseReason(sourceReason);
        var referenced = reference is null ? null : closeReasons.TryGetValue(reference, out var mapped)
            ? mapped
            : Fail<string?>("A title-request close-reason reference is missing.");
        Ensure(raw is null || referenced is null || raw == referenced, "consistent title-request close-reason aliases");
        return raw ?? referenced;
    }

    private static string? ResolveEventStatus(string? sourceValue, IReadOnlyDictionary<string, string> statuses) =>
        sourceValue is null ? null : statuses.TryGetValue(sourceValue, out var mapped) ? mapped : NormalizeStatus(sourceValue);

    private static string? ResolveEventCloseReason(string? sourceValue, IReadOnlyDictionary<string, string> closeReasons) =>
        sourceValue is null ? null : closeReasons.TryGetValue(sourceValue, out var mapped) ? mapped : NormalizeCloseReason(sourceValue);

    private static string NormalizeStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0" or "suggestion" => "suggestion",
        "1" or "5" or "pending_hold" or "pendinghold" => "pending_hold",
        "2" or "hold_placed" or "holdplaced" => "hold_placed",
        "3" or "outstanding_purchase" or "outstandingpurchase" => "outstanding_purchase",
        "4" or "closed" => "closed",
        _ => Fail<string>("A source request status is not supported.")
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
        _ => Fail<string?>("A source close reason is not supported.")
    };

    private static string NormalizeWorkflowTagCode(SourceRow row)
    {
        var value = row.Text("code");
        if (string.IsNullOrEmpty(value))
        {
            value = row.Text("label");
        }

        var code = TrimJavascriptFormatCode(value ?? string.Empty);
        return code switch
        {
            "Identifier found" or "Dupe found in Polaris" or "dupe found in Polaris" => "polaris_bib_found",
            "Identifier number not found in system" or "ISBN not found in system" => "polaris_bib_not_found",
            "Multiple Polaris matches" => "polaris_multiple_matches",
            "Duplicate suggestion" => "duplicate_suggestion",
            var nonblankCode when nonblankCode.Length > 0 => nonblankCode,
            _ => Fail<string>("A source workflow tag code is blank.")
        };
    }

    private static string NormalizeFormatCode(string value) => TrimJavascriptFormatCode(value).ToLowerInvariant() switch
    {
        "0" => "book",
        "1" => "ebook",
        "2" => "audiobook_cd",
        "3" => "eaudiobook",
        "4" => "dvd",
        "5" => "music_cd",
        var code when code.Length > 0 => code,
        _ => Fail<string>("A source format code is blank.")
    };

    private static string TrimJavascriptFormatCode(string value)
    {
        var start = 0;
        while (start < value.Length && IsJavascriptFormatWhitespace(value[start]))
        {
            start++;
        }
        var end = value.Length;
        while (end > start && IsJavascriptFormatWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsJavascriptFormatWhitespace(char value) => value is
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or
        '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or
        '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028' or '\u2029' or
        '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static string? NormalizeIsbnStatus(SourceRow row)
    {
        var status = row.String("isbnCheckStatus")?.Trim().ToLowerInvariant();
        var bibId = row.PositiveInt32("bibid", "source_bib_invalid");
        var identifier = row.String("identifier");
        return status switch
        {
            null => null,
            "pending" => "pending",
            "found" when bibId is not null => "found",
            "found_in_polaris" when bibId is not null => "found",
            "not_found" => "not_found",
            "skipped_no_isbn" => "skipped_no_isbn",
            "error_max_retries" => "error_max_retries",
            "error" when identifier is null => "skipped_no_isbn",
            "found" or "found_in_polaris" => Fail<string?>("A source found status has no BIB identity."),
            "error" => Fail<string?>("An ambiguous source identifier error cannot be reconciled."),
            _ => Fail<string?>("A source identifier-check status is unsupported.")
        };
    }

    private static string NormalizeAdditionalCopyStatus(string value) => value.Trim().ToLowerInvariant() switch
    {
        "open" => "open",
        "closed" => "closed",
        _ => Fail<string>("A source additional-copy status is unsupported.")
    };

    private static string? NormalizeClaimType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "manual" => "manual",
        "automatic_format_rule" or "automaticformatrule" => "automatic_format_rule",
        _ => Fail<string?>("A source claim type is unsupported.")
    };

    private static string NormalizeHistoricalClaimType(string sourceType, long? staffId, long? ruleId) => sourceType switch
    {
        "manual" => "manual",
        "automatic_format_rule" when staffId.HasValue && ruleId.HasValue => "automatic_format_rule",
        "automatic_format_rule" => "legacy",
        _ => Fail<string>("A source historical claim type is unsupported.")
    };

    private static string? ConsistentReference(SourceRow row, string first, string second)
    {
        var left = row.String(first);
        var right = row.String(second);
        Ensure(left is null || right is null || left == right, "consistent source relationship aliases");
        return left ?? right;
    }

    private static ClaimProjection ProjectRequestClaim(
        SqlConnection connection,
        SqlTransaction? transaction,
        SourceRow row,
        string status,
        long formatId,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> ruleIds,
        IReadOnlyDictionary<string, SourceRow> sourceRules)
    {
        var sourceClaimantId = row.String("claimedByStaffUserId");
        var mappedStaffId = sourceClaimantId is not null && staffIds.TryGetValue(sourceClaimantId, out var mappedStaff) ? mappedStaff : (long?)null;
        var displayName = row.String("claimedByDisplayName");
        var sourceDisplayName = row.Text("claimedByDisplayName");
        var claimedAt = row.UtcDateTime("claimedAt");
        var sourceType = NormalizeClaimType(row.String("claimType"));
        var sourceRuleId = row.String("claimRuleId");
        var mappedRuleId = sourceRuleId is not null && ruleIds.TryGetValue(sourceRuleId, out var mappedRule) ? mappedRule : (long?)null;
        Ensure(sourceType != "manual" || sourceRuleId is null, "manual claim has no automatic rule reference");
        var hasAttribution = sourceClaimantId is not null || displayName is not null || claimedAt.HasValue || sourceType is not null || sourceRuleId is not null;
        if (!hasAttribution)
        {
            return new(false, false, null, null, null, null, null, null, null, "unclaimed");
        }

        if (sourceClaimantId is null)
        {
            if (status == "closed" && displayName is not null && claimedAt.HasValue && sourceType is not null)
            {
                return new(true, false, null, null, null, sourceDisplayName, claimedAt,
                    NormalizeHistoricalClaimType(sourceType, mappedStaffId, mappedRuleId), mappedRuleId, "closed_claimant_unmapped");
            }
            return new(true, true, null, null, null, null, null, null, null,
                status == "closed" ? "closed_attribution_incomplete" : "claimant_unmapped");
        }

        if (status == "closed")
        {
            if (displayName is null || !claimedAt.HasValue || sourceType is null)
            {
                return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "closed_attribution_incomplete");
            }
            var historicalType = NormalizeHistoricalClaimType(sourceType, mappedStaffId, mappedRuleId);
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, historicalType, mappedRuleId,
                mappedStaffId is null ? "closed_claimant_unmapped" : "closed_history_preserved");
        }

        if (!mappedStaffId.HasValue)
        {
            return new(true, true, sourceClaimantId, null, null, null, null, null, null, "claimant_unmapped");
        }
        if (displayName is null || !claimedAt.HasValue || sourceType is null)
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "claim_metadata_incomplete");
        }
        var libraryId = row.Int32("libraryOrgId") ?? 0;
        if (!IsEligibleStaff(connection, transaction, mappedStaffId.Value, libraryId))
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, StaffIneligibilityReason(connection, transaction, mappedStaffId.Value, libraryId));
        }
        if (sourceType == "manual")
        {
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, "manual", null, "eligible");
        }
        if (sourceType != "automatic_format_rule")
        {
            return Fail<ClaimProjection>("An open source claim has an unsupported claim type.");
        }
        if (!mappedRuleId.HasValue)
        {
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, "legacy", null, "claim_rule_unmapped_normalized");
        }
        Ensure(sourceRules.ContainsKey(sourceRuleId!), "source claim rule mapping relationship");
        if (!StoredRuleMatches(connection, transaction, mappedRuleId.Value, libraryId, formatId, mappedStaffId.Value))
        {
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, "legacy", mappedRuleId, "claim_rule_mismatch_normalized");
        }
        return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, "automatic_format_rule", mappedRuleId, "eligible");
    }

    private static ClaimProjection ProjectCopyClaim(
        SqlConnection connection,
        SqlTransaction? transaction,
        SourceRow row,
        string status,
        IReadOnlyDictionary<string, long> staffIds)
    {
        var sourceClaimantId = row.String("claimedByStaffUserId");
        var mappedStaffId = sourceClaimantId is not null && staffIds.TryGetValue(sourceClaimantId, out var mapped) ? mapped : (long?)null;
        var sourceDisplayName = row.Text("claimedByDisplayName");
        var hasDisplayName = !string.IsNullOrEmpty(sourceDisplayName);
        var claimedAt = row.UtcDateTime("claimedAt");
        var hasAttribution = sourceClaimantId is not null || hasDisplayName || claimedAt.HasValue;
        if (!hasAttribution)
        {
            return new(false, false, null, null, null, null, null, null, null, "unclaimed");
        }
        if (status == "closed")
        {
            if (!hasDisplayName || !claimedAt.HasValue)
            {
                return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "closed_attribution_incomplete");
            }
            return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, null, null,
                mappedStaffId.HasValue ? "closed_history_preserved" : "closed_claimant_unmapped");
        }
        if (sourceClaimantId is null || !mappedStaffId.HasValue)
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "claimant_unmapped");
        }
        if (!hasDisplayName || !claimedAt.HasValue)
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, "claim_metadata_incomplete");
        }
        var libraryId = row.Int32("libraryOrgId") ?? 0;
        if (!IsEligibleStaff(connection, transaction, mappedStaffId.Value, libraryId))
        {
            return new(true, true, sourceClaimantId, mappedStaffId, null, null, null, null, null, StaffIneligibilityReason(connection, transaction, mappedStaffId.Value, libraryId));
        }
        return new(true, false, sourceClaimantId, mappedStaffId, mappedStaffId, sourceDisplayName, claimedAt, null, null, "eligible");
    }

    private static string StaffIneligibilityReason(SqlConnection connection, SqlTransaction? transaction, long staffId, int libraryId)
    {
        using var command = new SqlCommand("SELECT [IsActive], [Role], [OrganizationId] FROM [asap].[StaffUser] WHERE [Id] = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", staffId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return "claimant_unmapped";
        }
        if (!reader.GetBoolean(0))
        {
            return "claimant_inactive";
        }
        return reader.GetString(1) switch
        {
            "super_admin" when reader.GetInt32(2) == 1 => "eligible",
            "staff" or "admin" when reader.GetInt32(2) == libraryId => "eligible",
            _ => "claimant_out_of_scope"
        };
    }

    private static bool StoredRuleMatches(
        SqlConnection connection,
        SqlTransaction? transaction,
        long ruleId,
        int libraryId,
        long formatId,
        long staffId)
    {
        using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @id AND [LibraryOrganizationId] = @libraryId AND [MaterialFormatId] = @formatId AND [StaffUserId] = @staffId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", ruleId);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        command.Parameters.AddWithValue("@formatId", formatId);
        command.Parameters.AddWithValue("@staffId", staffId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string? AdditionalCopyNotes(SourceRow row, ClaimProjection claim, DateTime exportedAtUtc)
    {
        var notes = row.Text("notes");
        if (!claim.RequiresMigrationAnnotation)
        {
            return notes;
        }
        var claimantId = claim.SourceClaimantId ?? "unmapped";
        var displayName = row.Text("claimedByDisplayName") ?? "unknown";
        var claimedAt = row.UtcDateTime("claimedAt")?.ToString("O") ?? "unknown";
        var status = NormalizeAdditionalCopyStatus(row.RequiredString("status"));
        var action = status == "closed"
            ? "Retained incomplete closed attribution as historical notes"
            : $"Cleared open claim ({claim.Reason})";
        var annotation = $"[{exportedAtUtc:O}] [ASAP migration:additional_copy_claim_v1] {action}. Previous claimant ID: {claimantId}; display: {displayName.Replace('\r', ' ').Replace('\n', ' ')}; claimed at: {claimedAt}.";
        return string.IsNullOrWhiteSpace(notes) ? annotation : $"{notes.TrimEnd()}\n{annotation}";
    }

    private static void VerifyClaimAnnotations(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, long> requestIds,
        IReadOnlyDictionary<string, long> staffIds,
        IReadOnlyDictionary<string, long> ruleIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, SourceRow> sourceRules,
        IReadOnlyDictionary<string, string> statuses)
    {
        var expectedCount = 0;
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var formatId = ResolveRequestFormat(connection, transaction, row, formatIds,
                MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats")
                    .ToDictionary(item => item.RequiredString("id"), StringComparer.Ordinal),
                MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations"));
            var claim = ProjectRequestClaim(connection, transaction, row, ResolveRequestStatus(row, statuses), formatId,
                staffIds, ruleIds, sourceRules);
            if (!claim.RequiresMigrationAnnotation)
            {
                continue;
            }
            expectedCount++;
            var metadata = JsonSerializer.Serialize(new
            {
                transform = "claim_attribution_normalization_v1",
                sourceCollection = "title_requests",
                sourceRecordId = sourceId,
                sourceClaimantId = claim.SourceClaimantId,
                mappedStaffUserId = claim.MappedStaffUserId,
                sourceDisplayName = row.Text("claimedByDisplayName"),
                sourceClaimedAtUtc = row.UtcDateTime("claimedAt"),
                sourceClaimType = row.Text("claimType"),
                sourceClaimRuleId = row.String("claimRuleId"),
                reason = claim.Reason
            });
            using var command = new SqlCommand(
                "SELECT [MetadataJson], [CreatedUtc] FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId AND [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';",
                connection,
                transaction);
            command.Parameters.AddWithValue("@requestId", requestIds[sourceId]);
            using var reader = command.ExecuteReader();
            Ensure(reader.Read() && JsonSame(reader, 0, metadata) && Same(reader, 1, package.Manifest.ExportedAtUtc.UtcDateTime) && !reader.Read(),
                "claim normalization provenance metadata");
        }
        Ensure(Count(connection, transaction,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';") == expectedCount,
            "exact title claim annotation population");
    }

    private static List<PlacementEvidence> ReadPlacementEvidence(
        SourceRow request,
        string sourceRequestId,
        string currentStatus,
        string? currentCloseReason,
        IReadOnlyList<SourceRow> events,
        IReadOnlyDictionary<string, string> statuses,
        IReadOnlyDictionary<string, string> closeReasons)
    {
        var evidence = new List<PlacementEvidence>();
        if (currentStatus == "hold_placed")
        {
            evidence.Add(new("current_status", "title_requests", sourceRequestId,
                request.String("statusRef") is null ? "status" : "statusRef", currentStatus));
        }
        if (currentCloseReason is not null && TerminalCloseReasons.Contains(currentCloseReason))
        {
            evidence.Add(new("terminal_close_reason", "title_requests", sourceRequestId,
                request.String("closeReasonRef") is null ? "closeReason" : "closeReasonRef", currentCloseReason));
        }
        foreach (var sourceEvent in events.Where(row => string.Equals(row.String("titleRequest"), sourceRequestId, StringComparison.Ordinal)))
        {
            var sourceEventId = sourceEvent.RequiredString("id");
            var eventType = sourceEvent.RequiredString("eventType").Trim().ToLowerInvariant();
            var fromStatus = ResolveEventStatus(sourceEvent.String("fromStatus"), statuses);
            var toStatus = ResolveEventStatus(sourceEvent.String("toStatus"), statuses);
            var eventCloseReason = ResolveEventCloseReason(sourceEvent.String("closeReason"), closeReasons);
            if (eventType == "hold_placed")
            {
                evidence.Add(new("dedicated_event", "title_request_events", sourceEventId, "eventType", "hold_placed"));
            }
            if (toStatus == "hold_placed")
            {
                evidence.Add(new("transition_to_placed", "title_request_events", sourceEventId, "toStatus", "hold_placed"));
            }
            if (fromStatus == "hold_placed")
            {
                evidence.Add(new("transition_from_placed", "title_request_events", sourceEventId, "fromStatus", "hold_placed"));
            }
            if (eventCloseReason is not null && TerminalCloseReasons.Contains(eventCloseReason))
            {
                evidence.Add(new("event_terminal_reason", "title_request_events", sourceEventId, "closeReason", eventCloseReason));
            }
        }
        return evidence.Distinct()
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceCollection, StringComparer.Ordinal)
            .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceField, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToList();
    }

    private static List<PlacementEvidence> ReadPlacementHints(
        SourceRow request,
        string sourceRequestId,
        string currentStatus,
        string? currentCloseReason,
        IReadOnlyList<SourceRow> events,
        IReadOnlyList<PlacementEvidence> evidence)
    {
        var hints = new List<PlacementEvidence>();
        if (evidence.Count == 0 && currentStatus == "closed" && currentCloseReason is not "rejected" &&
            request.PositiveInt32("bibid", "source_bib_invalid") is not null)
        {
            hints.Add(new("recorded_bib_hint", "title_requests", sourceRequestId, "bibid", request.String("bibid")!));
        }
        foreach (var sourceEvent in events.Where(row => string.Equals(row.String("titleRequest"), sourceRequestId, StringComparison.Ordinal)))
        {
            var sourceEventId = sourceEvent.RequiredString("id");
            if (evidence.Any(item => string.Equals(item.SourceRecordId, sourceEventId, StringComparison.Ordinal)))
            {
                continue;
            }
            hints.AddRange(ReadEventBibSources(sourceEvent, sourceEventId).Select(source => new PlacementEvidence(
                "event_bib_hint", source.SourceCollection, source.SourceRecordId, source.SourceField, source.SourceValue)));
        }
        return hints.Distinct()
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceCollection, StringComparer.Ordinal)
            .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceField, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<PlacementBibSource> ReadEventBibSources(SourceRow row, string sourceEventId)
    {
        var sources = new List<PlacementBibSource>();
        var direct = row.Names.FirstOrDefault(name =>
            string.Equals(name, "bibId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "bibid", StringComparison.OrdinalIgnoreCase));
        if (direct is not null && row.PositiveInt32(direct, "source_bib_invalid") is { } directBibId)
        {
            sources.Add(new("title_request_events", sourceEventId, direct, directBibId, row.String(direct)!));
        }
        foreach (var property in row.JsonPropertyNames("metadata")
                     .Where(name => string.Equals(name, "bibId", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal))
        {
            if (row.JsonPropertyPositiveInt32("metadata", property, "source_bib_invalid") is { } metadataBibId)
            {
                sources.Add(new("title_request_events", sourceEventId, $"metadata.{property}", metadataBibId,
                    row.JsonPropertyString("metadata", property)!));
            }
        }
        Ensure(sources.Select(item => item.BibId).Distinct().Count() <= 1,
            "one consistent BIB identity in title event source hints");
        return sources.Distinct().ToArray();
    }

    private static string BuildPlacementMetadata(
        SourceRow request,
        string sourceRequestId,
        IReadOnlyList<SourceRow> events,
        IReadOnlyList<PlacementEvidence> evidence)
    {
        var bibSources = new List<PlacementBibSource>();
        if (request.PositiveInt32("bibid", "source_bib_invalid") is { } requestBibId)
        {
            bibSources.Add(new("title_requests", sourceRequestId, "bibid", requestBibId, request.String("bibid")!));
        }
        foreach (var sourceEvent in events.Where(row => string.Equals(row.String("titleRequest"), sourceRequestId, StringComparison.Ordinal)))
        {
            var eventId = sourceEvent.RequiredString("id");
            if (evidence.Any(item => string.Equals(item.SourceRecordId, eventId, StringComparison.Ordinal)))
            {
                bibSources.AddRange(ReadEventBibSources(sourceEvent, eventId));
            }
        }
        var bibIds = bibSources.Select(item => item.BibId).Distinct().ToArray();
        Ensure(bibIds.Length <= 1, "one source BIB identity across placed history");
        int? bibId = bibIds.Length == 0 ? null : bibIds[0];
        var orderedEvidence = evidence.Distinct()
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceCollection, StringComparer.Ordinal)
            .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceField, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToArray();
        var orderedBibSources = bibSources.Distinct()
            .OrderBy(item => item.SourceCollection, StringComparer.Ordinal)
            .ThenBy(item => item.SourceRecordId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceField, StringComparer.Ordinal)
            .ThenBy(item => item.BibId)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            legacyBibProtection = true,
            bibId,
            transform = "placed_bib_protection_v1",
            sourceTitleRequestId = sourceRequestId,
            evidence = orderedEvidence.Select(item => new
            {
                kind = item.Kind,
                sourceCollection = item.SourceCollection,
                sourceRecordId = item.SourceRecordId,
                sourceField = item.SourceField,
                value = item.Value
            }),
            bibSources = orderedBibSources.Select(item => new
            {
                sourceCollection = item.SourceCollection,
                sourceRecordId = item.SourceRecordId,
                sourceField = item.SourceField,
                bibId = item.BibId,
                sourceValue = item.SourceValue
            })
        });
    }

    private sealed record PlacementEvidence(string Kind, string SourceCollection, string SourceRecordId, string SourceField, string Value);
    private sealed record PlacementBibSource(string SourceCollection, string SourceRecordId, string SourceField, int BibId, string SourceValue);

    private sealed record ClaimProjection(
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


    private static bool IsEligibleStaff(SqlConnection connection, SqlTransaction? transaction, long staffId, int libraryId)
    {
        using var command = new SqlCommand(
            "SELECT [IsActive], [Role], [OrganizationId], [UserPrincipalName], [NormalizedUserPrincipalName] FROM [asap].[StaffUser] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", staffId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !reader.GetBoolean(0))
        {
            return false;
        }
        var authenticationEmail = reader.IsDBNull(3) ? null : RealEmail(reader.GetString(3));
        Ensure(authenticationEmail is not null && !reader.IsDBNull(4) &&
            string.Equals(authenticationEmail.ToUpperInvariant(), reader.GetString(4), StringComparison.Ordinal),
            "eligible staff authentication identity");
        return reader.GetString(1) switch
        {
            "super_admin" => reader.GetInt32(2) == 1,
            "staff" or "admin" => reader.GetInt32(2) == libraryId,
            _ => false
        };
    }

    private static bool Same(SqlDataReader reader, int ordinal, string? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : expected is not null && string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static bool Same(SqlDataReader reader, int ordinal, DateTime? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : expected.HasValue && reader.GetDateTime(ordinal) == expected.Value;

    private static int? NullableInt(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? NullableLong(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool JsonSame(SqlDataReader reader, int ordinal, string? expected)
    {
        if (reader.IsDBNull(ordinal) || expected is null)
        {
            return reader.IsDBNull(ordinal) && expected is null;
        }
        using var actualDocument = JsonDocument.Parse(reader.GetString(ordinal));
        using var expectedDocument = JsonDocument.Parse(expected);
        return JsonElement.DeepEquals(actualDocument.RootElement, expectedDocument.RootElement);
    }

    private static JsonElement? ParseJson(string? value)
    {
        if (value is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static DateTime? ParseSourceDate(string? value)
    {
        if (value is null)
        {
            return null;
        }
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.Date
            : Fail<DateTime?>("A source title-request date is invalid.");
    }

    private static DateTime? ParseUtc(string? value)
    {
        if (value is null)
        {
            return null;
        }
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.UtcDateTime
            : Fail<DateTime?>("A source deleted-request timestamp is invalid.");
    }

    private static string? MaskBarcode(string? value) => value is null
        ? null
        : value.Length <= 4 ? new string('*', value.Length) : $"***{value[^4..]}";

    private static void Ensure(bool condition, string entity)
    {
        if (!condition)
        {
            throw new MigrationOperationException(
                "reconciliation_failed",
                $"Imported {entity} differs from the immutable source package.");
        }
    }

    private static T Fail<T>(string message) =>
        throw new MigrationOperationException("reconciliation_failed", message);
}
