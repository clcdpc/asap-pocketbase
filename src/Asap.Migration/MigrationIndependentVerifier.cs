using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

internal static class MigrationIndependentVerifier
{
    public static void Verify(
        string connectionString,
        ValidatedMigrationPackage package,
        bool postmarkTokenProvisioned,
        long? bootstrapTargetStaffUserId = null,
        bool bootstrapInserted = false,
        string? externalConfigurationPath = null)
    {
        var polarisRows = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        var hasRawCredential = polarisRows.Any(row =>
            row.Text("apiKey") is { Length: > 0 } || row.Text("adminPassword") is { Length: > 0 });
        var credentialProtector = hasRawCredential
            ? MigrationCredentialProtector.LoadForVerification(externalConfigurationPath)
            : null;
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        Verify(connection, transaction: null, package, postmarkTokenProvisioned, bootstrapTargetStaffUserId, bootstrapInserted, credentialProtector, externalConfigurationPath);
    }

    public static void Verify(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        bool postmarkTokenProvisioned,
        long? bootstrapTargetStaffUserId = null,
        bool bootstrapInserted = false,
        MigrationCredentialProtector? credentialProtector = null,
        string? externalConfigurationPath = null)
    {
        VerifyPreActivationPopulations(connection, transaction);
        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
        var organizationsById = organizations.ToDictionary(
            row => row.RequiredString("id"),
            StringComparer.Ordinal);
        VerifyOrganizations(connection, transaction, organizations);

        var staffUsers = MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users");
        VerifyMappingCount(connection, transaction, "staff_user", staffUsers.Count);
        VerifyMappingCount(connection, transaction, "email_template",
            MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates").Count +
            MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates").Count);
        VerifyMappingCount(connection, transaction, "workflow_tag",
            MigrationPackageReader.ReadRows(package, "workflow-tags.json", "workflow_tags").Count);
        VerifyMappingCount(connection, transaction, "title_request_tag",
            MigrationPackageReader.ReadRows(package, "title-request-tags.json", "title_request_tags").Count);

        var formats = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
        var formatsById = formats.ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        VerifyFormats(connection, transaction, package, formats, organizationsById, organizations);
        VerifyMaterialFormatSourceProjection(connection, transaction, package, formats, organizationsById, organizations);
        VerifyEffectiveFormatRuleSnapshots(connection, transaction, package);

        VerifyMappedTargetCount(
            connection,
            transaction,
            "FormatAutoClaimRule",
            "format_auto_claim_rule",
            MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules").Count);

        var requests = MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests");
        VerifyTitleRequests(connection, transaction, requests, formatsById, formats, organizations);
        VerifyBibAuthorityInputs(connection, transaction, requests);

        var copies = MigrationPackageReader.ReadRows(package, "additional-copy-requests.json", "additional_copy_requests");
        VerifyAdditionalCopies(connection, transaction, copies, requests, organizations);

        VerifyMappedTargetCount(
            connection,
            transaction,
            "DeletedRequestAudit",
            "deleted_request_audit",
            MigrationPackageReader.ReadRows(package, "deleted-request-audit.json", "deleted_request_audit").Count);
        VerifyMappingCount(
            connection,
            transaction,
            "title_request_event",
            MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events").Count);
        VerifyMappedTargetsExist(
            connection,
            transaction,
            "TitleRequestEvent",
            "title_request_event",
            MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events").Count);
        VerifyMappedTargetCount(
            connection,
            transaction,
            "EmailDeliveryEvent",
            "email_delivery_event",
            MigrationPackageReader.ReadRows(package, "email-delivery-events.json", "email_delivery_events").Count);
        VerifyTotalMappingPopulation(connection, transaction, package);
        MigrationIndependentConfigurationVerifier.Verify(
            connection,
            transaction,
            package,
            postmarkTokenProvisioned,
            credentialProtector);
        MigrationIndependentEntityVerifier.Verify(
            connection,
            transaction,
            package,
            bootstrapTargetStaffUserId,
            bootstrapInserted,
            externalConfigurationPath);
    }

    private static void VerifyPreActivationPopulations(SqlConnection connection, SqlTransaction? transaction)
    {
        // These six populations are never imported. Reconcile/recover-report verify the
        // stopped, pre-activation import snapshot, not a live post-cutover database.
        foreach (var table in new[]
        {
            "PatronSession", "EmailOutbox", "QueueProgress", "HoldPlacementOperation",
            "PickupPreferenceOperation", "AdministrativeAudit"
        })
        {
            using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM [asap].[{table}];", connection, transaction);
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new MigrationOperationException("reconciliation_requires_pre_activation_target",
                    $"Migration snapshot verification requires the stopped pre-activation target; excluded {table} rows are present. Reconcile and report recovery are not post-activation integrity checks.");
            }
        }
    }

    private static void VerifyOrganizations(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> rows)
    {
        var expectedTargetIds = new HashSet<int> { 1 };
        var expectedMappings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var id = row.Int32("organizationId") ?? Fail<int>("A source organization has no native identity.");
            expectedTargetIds.Add(id);
            if (!expectedMappings.TryAdd(sourceId, id))
            {
                Fail("The immutable source package contains a duplicate organization mapping identity.");
            }

            using var command = new SqlCommand(
                "SELECT [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive], [LastSyncedUtc] FROM [asap].[Organization] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read() ||
                NullableString(reader, 0) != (row.String("displayName") ?? row.String("name") ?? $"Organization {id}") ||
                NullableString(reader, 1) != row.String("abbreviation") ||
                NullableInt(reader, 2) != ReadOptionalOrganizationIdentity(row, "organizationCodeId", "organization_code_id") ||
                NullableInt(reader, 3) != ReadOptionalOrganizationIdentity(row, "parentOrganizationId", "parent_organization_id") ||
                NullableDateTime(reader, 5) != row.UtcDateTime("lastSynced"))
            {
                Fail("Target organization name, abbreviation, native type, parent, or synchronization timestamp differs from the source snapshot.");
            }

            var code = ReadOptionalOrganizationIdentity(row, "organizationCodeId", "organization_code_id");
            var expectedActive = id == 1 || id > 1 && code == 2 && row.Bool("enabledForPatrons");
            if (reader.GetBoolean(4) != expectedActive)
            {
                Fail("Target organization activity differs from the pinned source transformation.");
            }
        }

        var actualMappings = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var mappings = new SqlCommand(
                   "SELECT [EntityType], [PocketBaseId], [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = @entityType;",
                   connection,
                   transaction))
        {
            mappings.Parameters.AddWithValue("@entityType", "organization");
            using var mappingReader = mappings.ExecuteReader();
            while (mappingReader.Read())
            {
                if (!string.Equals(mappingReader.GetString(0), "organization", StringComparison.Ordinal) ||
                    !actualMappings.TryAdd(mappingReader.GetString(1), Convert.ToInt32(mappingReader.GetValue(2), CultureInfo.InvariantCulture)))
                {
                    Fail("An organization source mapping changes its exact entity or source identity.");
                }
            }
        }
        if (actualMappings.Count != expectedMappings.Count ||
            expectedMappings.Any(pair => !actualMappings.TryGetValue(pair.Key, out var targetId) || targetId != pair.Value))
        {
            Fail("Organization source mapping keys or native identities differ from the immutable source package.");
        }

        using var system = new SqlCommand(
            "SELECT [IsActive] FROM [asap].[Organization] WHERE [Id] = 1;",
            connection,
            transaction);
        if (system.ExecuteScalar() is not true)
        {
            Fail("The system organization must remain active.");
        }

        using var target = new SqlCommand("SELECT [Id] FROM [asap].[Organization];", connection, transaction);
        var actualTargetIds = new HashSet<int>();
        using (var targetReader = target.ExecuteReader())
        {
            while (targetReader.Read())
            {
                actualTargetIds.Add(targetReader.GetInt32(0));
            }
        }
        if (!actualTargetIds.SetEquals(expectedTargetIds))
        {
            Fail("Target organization population includes rows outside the system seed and source native identities.");
        }
        VerifyMappingCount(connection, transaction, "organization", rows.Count);
    }

    private static void VerifyFormats(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, SourceRow> organizationsById,
        IReadOnlyList<SourceRow> organizations)
    {
        var exactSystemSourceCodes = rows
            .Where(row => row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.Text("code") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var sourceId = row.RequiredString("id");
            var scope = row.RequiredString("scope");
            var expectedOwner = scope.ToLowerInvariant() switch
            {
                "system" => 1,
                "library" => ResolveLibraryId(row.RequiredString("libraryOrganization"), organizationsById, organizations),
                _ => Fail<int>("A source material format has an unsupported scope.")
            };
            var mapping = ReadFormatMapping(connection, transaction, sourceId);
            var sourceCode = row.Text("code") ?? string.Empty;
            var expectedMappingEntity = expectedOwner == 1 || !exactSystemSourceCodes.Contains(sourceCode)
                ? "material_format"
                : "material_format_override";

            if (!string.Equals(mapping.EntityType, expectedMappingEntity, StringComparison.Ordinal))
            {
                Fail("A source material format maps through the wrong target ownership record.");
            }

            sourceCode = NormalizeCode(sourceCode);

            if (mapping.OverrideLibraryId is { } overrideLibraryId)
            {
                if (expectedOwner == 1 || overrideLibraryId != expectedOwner || mapping.OwnerOrganizationId != 1)
                {
                    Fail("A library format override is attached to the wrong library or base format.");
                }
            }
            else if (mapping.OwnerOrganizationId != expectedOwner)
            {
                Fail("A material format is owned by a different library than the source snapshot.");
            }

            if (!string.Equals(mapping.Code, sourceCode, StringComparison.Ordinal))
            {
                Fail("A mapped material format code differs from its source identity.");
            }

            VerifyMaterialFormatMetadata(connection, transaction, row, mapping);
        }

        VerifyFormatPopulation(connection, transaction, package, rows, organizationsById, organizations);
        VerifyFormatTimestamps(connection, transaction, package, rows);
    }

    private static void VerifyFormatTimestamps(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows)
    {
        var exportedAtUtc = package.Manifest.ExportedAtUtc.UtcDateTime;
        var expectedByFormatId = new Dictionary<long, (DateTime CreatedUtc, DateTime UpdatedUtc)>();
        foreach (var row in rows)
        {
            var mapping = ReadFormatMapping(connection, transaction, row.RequiredString("id"));
            if (string.Equals(mapping.EntityType, "material_format", StringComparison.Ordinal))
            {
                expectedByFormatId.Add(
                    mapping.FormatId,
                    (row.UtcDateTime("created") ?? exportedAtUtc, row.UtcDateTime("updated") ?? exportedAtUtc));
            }
        }

        using var command = new SqlCommand(
            "SELECT [Id], [OwnerOrganizationId], [CreatedUtc], [UpdatedUtc] FROM [asap].[MaterialFormat];",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var ownerId = reader.GetInt32(1);
            if (!expectedByFormatId.TryGetValue(id, out var expected))
            {
                if (ownerId != 1)
                {
                    Fail("A library-owned base material format has no exact source timestamp expectation.");
                }
                expected = (exportedAtUtc, exportedAtUtc);
            }
            if (reader.GetDateTime(2).Ticks != expected.CreatedUtc.Ticks ||
                reader.GetDateTime(3).Ticks != expected.UpdatedUtc.Ticks)
            {
                Fail("A material format creation or update timestamp differs from its source record or pinned target seed.");
            }
        }
    }

    private static void VerifyMaterialFormatMetadata(
        SqlConnection connection,
        SqlTransaction? transaction,
        SourceRow row,
        (string EntityType, long FormatId, int OwnerOrganizationId, string Code, int? OverrideLibraryId) mapping)
    {
        var sourceLabel = row.Text("label");
        if (mapping.OverrideLibraryId is { } libraryId)
        {
            var expectedLabel = string.IsNullOrEmpty(sourceLabel) ? null : sourceLabel;
            using var command = new SqlCommand(
                "SELECT [Label], [SortOrder], [IsEnabled] FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @libraryId AND [MaterialFormatId] = @formatId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@libraryId", libraryId);
            command.Parameters.AddWithValue("@formatId", mapping.FormatId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() ||
                NullableString(reader, 0) != expectedLabel ||
                NullableInt(reader, 1) != (row.Int32("sortOrder") ?? 0) ||
                NullableBoolean(reader, 2) != row.Bool("enabled", false) ||
                reader.Read())
            {
                Fail("A library material format override's label, sort order, or enabled state differs from its source record.");
            }
            return;
        }

        var ownedExpectedLabel = string.IsNullOrEmpty(sourceLabel) ? row.Text("code") ?? string.Empty : sourceLabel;
        using var ownedCommand = new SqlCommand(
            "SELECT [Label], [SortOrder], [IsEnabled] FROM [asap].[MaterialFormat] WHERE [Id] = @formatId;",
            connection,
            transaction);
        ownedCommand.Parameters.AddWithValue("@formatId", mapping.FormatId);
        using var ownedReader = ownedCommand.ExecuteReader();
        if (!ownedReader.Read() ||
            NullableString(ownedReader, 0) != ownedExpectedLabel ||
            NullableInt(ownedReader, 1) != (row.Int32("sortOrder") ?? 0) ||
            NullableBoolean(ownedReader, 2) != row.Bool("enabled", false) ||
            ownedReader.Read())
        {
            Fail("A material format's label, sort order, or enabled state differs from its source record.");
        }
    }

    private static void VerifyMaterialFormatSourceProjection(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, SourceRow> organizationsById,
        IReadOnlyList<SourceRow> organizations)
    {
        var librariesWithRuleOverrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides")
            .Where(row => row.JsonText("patronFormatRules") is not null)
            .Select(row => row.Int32("orgId") ?? Fail<int>("A source patron format rule snapshot has no library identity."))
            .ToHashSet();

        foreach (var row in rows)
        {
            var scope = row.RequiredString("scope");
            var organizationId = scope.Equals("system", StringComparison.OrdinalIgnoreCase)
                ? 1
                : scope.Equals("library", StringComparison.OrdinalIgnoreCase)
                    ? ResolveLibraryId(row.RequiredString("libraryOrganization"), organizationsById, organizations)
                    : Fail<int>("A source material format has an unsupported scope.");
            if (organizationId != 1 && librariesWithRuleOverrides.Contains(organizationId))
            {
                continue;
            }

            var mapping = ReadFormatMapping(connection, transaction, row.RequiredString("id"));
            using var command = new SqlCommand(
                "SELECT COALESCE(o.[MessageBehavior], f.[MessageBehavior]), COALESCE(o.[Message], f.[Message]), " +
                "COALESCE(o.[TitleMode], f.[TitleMode]), COALESCE(o.[TitleLabel], f.[TitleLabel]), " +
                "COALESCE(o.[AuthorMode], f.[AuthorMode]), COALESCE(o.[AuthorLabel], f.[AuthorLabel]), " +
                "COALESCE(o.[IdentifierMode], f.[IdentifierMode]), COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]), " +
                "COALESCE(o.[PublicationMode], f.[PublicationMode]), COALESCE(o.[PublicationLabel], f.[PublicationLabel]) " +
                "FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o " +
                "ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE f.[Id] = @formatId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@formatId", mapping.FormatId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() ||
                NullableString(reader, 0) != NormalizeLegacyMaterialEnum(
                    row.String("messageBehavior"), "none", ["none", "message", "ebookMessage", "eaudiobookMessage"]) ||
                !reader.IsDBNull(1) ||
                NullableString(reader, 2) != "required" ||
                NullableString(reader, 3) != LegacyMaterialText(row.Text("titleLabel"), "Title") ||
                NullableString(reader, 4) != NormalizeLegacyMaterialEnum(row.String("authorMode"), "required", ["required", "optional", "hidden"]) ||
                NullableString(reader, 5) != LegacyMaterialText(row.Text("authorLabel"), "Author") ||
                NullableString(reader, 6) != NormalizeLegacyMaterialEnum(row.String("identifierMode"), "optional", ["required", "optional", "hidden"]) ||
                NullableString(reader, 7) != LegacyMaterialText(row.Text("identifierLabel"), "Identifier number") ||
                NullableString(reader, 8) != NormalizeLegacyMaterialEnum(row.String("publicationMode"), "required", ["required", "optional", "hidden"]) ||
                NullableString(reader, 9) != LegacyMaterialText(row.Text("publicationLabel"), "Publication Timing"))
            {
                Fail("A material format's effective source fields differ from the pinned legacy row defaults.");
            }
        }
    }

    private static void VerifyEffectiveFormatRuleSnapshots(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"))
        {
            if (row.JsonText("patronFormatRules") is not { } json)
            {
                continue;
            }
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Fail("A source patron format rule snapshot is not an object.");
            }
            var libraryId = row.Int32("orgId") ?? Fail<int>("A source patron format rule snapshot has no library identity.");
            var formatRows = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
            var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations");
            var organizationsById = organizations.ToDictionary(item => item.RequiredString("id"), StringComparer.Ordinal);
            foreach (var (formatId, code) in ReadEffectiveFormats(connection, transaction, libraryId))
            {
                var sourceCode = EffectiveSourceFormatCode(formatRows, organizationsById, organizations, libraryId, code);
                var expected = ExpectedLegacyFormatRule(document.RootElement, sourceCode);
                using var command = new SqlCommand(
                    "SELECT COALESCE(o.[MessageBehavior], f.[MessageBehavior]), COALESCE(o.[Message], f.[Message]), " +
                    "COALESCE(o.[TitleMode], f.[TitleMode]), COALESCE(o.[TitleLabel], f.[TitleLabel]), " +
                    "COALESCE(o.[AuthorMode], f.[AuthorMode]), COALESCE(o.[AuthorLabel], f.[AuthorLabel]), " +
                    "COALESCE(o.[IdentifierMode], f.[IdentifierMode]), COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]), " +
                    "COALESCE(o.[PublicationMode], f.[PublicationMode]), COALESCE(o.[PublicationLabel], f.[PublicationLabel]) " +
                    "FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o " +
                    "ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = @libraryId WHERE f.[Id] = @formatId;",
                    connection,
                    transaction);
                command.Parameters.AddWithValue("@libraryId", libraryId);
                command.Parameters.AddWithValue("@formatId", formatId);
                using var reader = command.ExecuteReader();
                if (!reader.Read() ||
                    NullableString(reader, 0) != expected.MessageBehavior ||
                    (NullableString(reader, 1) ?? string.Empty) != expected.Message ||
                    NullableString(reader, 2) != expected.TitleMode || NullableString(reader, 3) != expected.TitleLabel ||
                    NullableString(reader, 4) != expected.AuthorMode || NullableString(reader, 5) != expected.AuthorLabel ||
                    NullableString(reader, 6) != expected.IdentifierMode || NullableString(reader, 7) != expected.IdentifierLabel ||
                    NullableString(reader, 8) != expected.PublicationMode || NullableString(reader, 9) != expected.PublicationLabel)
                {
                    Fail("A target material format differs from the independently derived legacy settings override.");
                }
            }
        }
    }

    private static ExpectedFormatRule ExpectedLegacyFormatRule(JsonElement rules, string code)
    {
        var defaultsCode = code;
        if (!TryLegacyFormatRuleProperty(rules, code, out var rule))
        {
            if (IsLegacyBuiltinFormat(code))
            {
                return LegacyFormatDefaults(code);
            }
            if (!TryLegacyFormatRuleProperty(rules, "book", out rule))
            {
                return LegacyFormatDefaults("book");
            }
            defaultsCode = "book";
        }
        var defaults = LegacyFormatDefaults(defaultsCode);
        if (rule.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaults;
        }
        if (rule.ValueKind != JsonValueKind.Object)
        {
            return Fail<ExpectedFormatRule>("A source material format rule is neither an object nor null.");
        }
        var fields = default(JsonElement);
        if (TryExactProperty(rule, "fields", out var incomingFields))
        {
            if (incomingFields.ValueKind == JsonValueKind.Object)
            {
                fields = incomingFields;
            }
            else if (incomingFields.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return Fail<ExpectedFormatRule>("A source material format fields value is neither an object nor null.");
            }
        }
        var title = LegacyFormatField(fields, "title", defaults.TitleMode, defaults.TitleLabel, forceRequired: true);
        var author = LegacyFormatField(fields, "author", defaults.AuthorMode, defaults.AuthorLabel);
        var identifier = LegacyFormatField(fields, "identifier", defaults.IdentifierMode, defaults.IdentifierLabel);
        var publication = LegacyFormatField(fields, "publication", defaults.PublicationMode, defaults.PublicationLabel);
        var behavior = NormalizeLegacyFormatRuleEnum(
            ReadLegacyRuleString(rule, "messageBehavior", defaults.MessageBehavior),
            defaults.MessageBehavior,
            ["none", "message", "ebookMessage", "eaudiobookMessage"]);
        return new ExpectedFormatRule(
            behavior,
            ReadLegacyFormatMessage(rule, defaults.Message),
            title.Mode,
            title.Label,
            author.Mode,
            author.Label,
            identifier.Mode,
            identifier.Label,
            publication.Mode,
            publication.Label);
    }

    private static (string Mode, string Label) LegacyFormatField(
        JsonElement fields,
        string key,
        string fallbackMode,
        string fallbackLabel,
        bool forceRequired = false)
    {
        var field = default(JsonElement);
        if (TryExactProperty(fields, key, out var incoming))
        {
            if (incoming.ValueKind == JsonValueKind.Object)
            {
                field = incoming;
            }
            else if (incoming.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return Fail<(string, string)>($"A source material format field {key} is neither an object nor null.");
            }
        }
        string[] allowedModes = ["required", "optional", "hidden"];
        var mode = NormalizeLegacyFormatRuleEnum(ReadLegacyRuleString(field, "mode", fallbackMode), fallbackMode, allowedModes);
        var label = ReadLegacyRuleString(field, "label", fallbackLabel);
        return (forceRequired ? "required" : mode, label);
    }

    private static string ReadLegacyRuleString(JsonElement value, string property, string fallback)
    {
        if (!TryExactProperty(value, property, out var item) || item.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }
        if (item.ValueKind != JsonValueKind.String)
        {
            return Fail<string>($"Source format rule {property} is not a string.");
        }
        var result = TrimLegacyFormatRuleText(item.GetString()!);
        return result.Length == 0 ? fallback : result;
    }

    private static string ReadLegacyFormatMessage(JsonElement value, string fallback)
    {
        if (!TryExactProperty(value, "message", out var item) || item.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }
        if (item.ValueKind != JsonValueKind.String)
        {
            return Fail<string>("Source format rule message is not a string.");
        }

        var message = TrimLegacyFormatRuleText(item.GetString()!);
        return message.Length == 0 ? fallback : message;
    }

    private static string NormalizeLegacyFormatRuleEnum(string? value, string fallback, string[] allowed)
    {
        if (value is null)
        {
            return fallback;
        }
        return allowed.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.Ordinal))
            ?? fallback;
    }

    private static string TrimLegacyFormatRuleText(string value)
    {
        var start = 0;
        while (start < value.Length && IsLegacyFormatRuleWhitespace(value[start]))
        {
            start++;
        }
        var end = value.Length;
        while (end > start && IsLegacyFormatRuleWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsLegacyFormatRuleWhitespace(char value) => value is
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or
        '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or
        '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028' or '\u2029' or
        '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static bool TryExactProperty(JsonElement value, string propertyName, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
            {
                if (string.Equals(item.Name, propertyName, StringComparison.Ordinal))
                {
                    result = item.Value;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    private static bool TryLegacyFormatRuleProperty(JsonElement rules, string code, out JsonElement result)
    {
        if (rules.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rules.EnumerateObject())
            {
                if (string.Equals(property.Name, code, StringComparison.Ordinal))
                {
                    result = property.Value;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }

    private static string EffectiveSourceFormatCode(
        IReadOnlyList<SourceRow> formatRows,
        IReadOnlyDictionary<string, SourceRow> organizationsById,
        IReadOnlyList<SourceRow> organizations,
        int libraryId,
        string normalizedCode)
    {
        var libraryRow = formatRows.FirstOrDefault(row =>
            row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase) &&
            ResolveLibraryId(row.RequiredString("libraryOrganization"), organizationsById, organizations) == libraryId &&
            NormalizeCode(row.Text("code") ?? string.Empty) == NormalizeCode(normalizedCode));
        if (libraryRow is not null)
        {
            return libraryRow.Text("code") ?? string.Empty;
        }

        var systemRow = formatRows.FirstOrDefault(row =>
            row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase) &&
            NormalizeCode(row.Text("code") ?? string.Empty) == NormalizeCode(normalizedCode));
        return systemRow?.Text("code") ?? NormalizeCode(normalizedCode);
    }

    private static bool IsLegacyBuiltinFormat(string code) =>
        code is "book" or "audiobook_cd" or "dvd" or "music_cd" or "ebook" or "eaudiobook";

    private static ExpectedFormatRule LegacyFormatDefaults(string code) => code switch
    {
        "book" or "audiobook_cd" => new("none", string.Empty, "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        "dvd" => new("none", string.Empty, "required", "Title", "required", "Director/Actors/Producer", "hidden", "UPC", "required", "Publication Timing"),
        "music_cd" => new("none", string.Empty, "required", "Title", "required", "Artist", "hidden", "UPC", "required", "Publication Timing"),
        "ebook" => new("message", "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>", "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        "eaudiobook" => new("message", "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>", "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        _ => new("none", string.Empty, "required", "Title", "optional", "Author", "optional", "Identifier", "optional", "Publication")
    };

    private static string NormalizeLegacyMaterialEnum(string? value, string fallback, string[] allowed)
    {
        if (value is null)
        {
            return fallback;
        }
        return allowed.FirstOrDefault(candidate => string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? Fail<string>("A source material format enum value is invalid.");
    }

    private static string LegacyMaterialText(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static string LegacyMaterialTitleMode(string? value)
    {
        _ = NormalizeLegacyMaterialEnum(value, "required", ["required", "optional", "hidden"]);
        return "required";
    }

    private static void VerifyTitleRequests(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> requests,
        IReadOnlyDictionary<string, SourceRow> formatsById,
        IReadOnlyList<SourceRow> formats,
        IReadOnlyList<SourceRow> organizations)
    {
        foreach (var row in requests)
        {
            var sourceId = row.RequiredString("id");
            var libraryId = row.Int32("libraryOrgId") ?? Fail<int>("A source title request has no library identity.");
            if (!IsSourceLibrary(libraryId, organizations))
            {
                Fail("A source title request is scoped to a non-library organization.");
            }

            var requestTargetId = ReadMapping(connection, transaction, "title_request", sourceId);
            var formatReference = row.String("formatRef");
            long? expectedFormatId = null;
            var expectedFormatOwner = 1;
            string? expectedFormatCode = null;
            if (formatReference is not null)
            {
                if (!formatsById.TryGetValue(formatReference, out var sourceFormat))
                {
                    Fail("A title request references a format absent from the source package.");
                }

                var mappedFormat = ReadFormatMapping(connection, transaction, formatReference);
                expectedFormatId = mappedFormat.FormatId;
                expectedFormatOwner = sourceFormat!.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase)
                    ? ResolveLibraryId(sourceFormat.RequiredString("libraryOrganization"),
                        organizations.ToDictionary(item => item.RequiredString("id"), StringComparer.Ordinal), organizations)
                    : 1;
                expectedFormatCode = NormalizeCode(sourceFormat.Text("code") ?? string.Empty);
                if (row.Text("format") is { } scalarCode && TrimLegacyFormatRuleText(scalarCode).Length > 0 &&
                    !string.Equals(NormalizeCode(scalarCode), expectedFormatCode, StringComparison.Ordinal))
                {
                    Fail("A title request's scalar format and source format reference disagree.");
                }
            }
            else if (row.Text("format") is { } scalarFormat && TrimLegacyFormatRuleText(scalarFormat).Length > 0)
            {
                expectedFormatCode = NormalizeCode(scalarFormat);
                var effectiveFormat = ReadFormatByCode(connection, transaction, libraryId, expectedFormatCode);
                expectedFormatId = effectiveFormat.FormatId;
                expectedFormatOwner = effectiveFormat.OwnerOrganizationId;
            }

            using var command = new SqlCommand(
                "SELECT r.[LibraryOrganizationId], r.[MaterialFormatId], r.[CustomFieldsJson], f.[OwnerOrganizationId], r.[PatronIdSnapshot] " +
                "FROM [asap].[TitleRequest] r JOIN [asap].[MaterialFormat] f ON f.[Id] = r.[MaterialFormatId] WHERE r.[Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", requestTargetId);
            int targetLibraryId;
            long targetFormatId;
            string? targetSnapshot;
            int targetFormatOwner;
            bool hasPatronIdSnapshot;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    Fail("A target title request is missing from SQL.");
                }
                targetLibraryId = reader.GetInt32(0);
                targetFormatId = reader.GetInt64(1);
                targetSnapshot = reader.IsDBNull(2) ? null : reader.GetString(2);
                targetFormatOwner = reader.GetInt32(3);
                hasPatronIdSnapshot = !reader.IsDBNull(4);
            }

            if (hasPatronIdSnapshot)
            {
                Fail("A legacy title request has a fabricated native patron ID snapshot.");
            }

            if (targetLibraryId != libraryId || expectedFormatId is { } expectedId && targetFormatId != expectedId)
            {
                Fail("A target title request lost its source library or format relationship.");
            }

            if (expectedFormatOwner == 1)
            {
                if (targetFormatOwner != 1)
                {
                    Fail("A system format reference resolves to a non-system material format.");
                }
            }
            else if (expectedFormatOwner != libraryId ||
                targetFormatOwner != libraryId &&
                !(targetFormatOwner == 1 && HasFormatOverride(connection, transaction, libraryId, targetFormatId)))
            {
                Fail("A title request references a format owned by another library.");
            }

            var sourceSnapshot = MigrationCustomFieldsSnapshot.Read(row);
            _ = MigrationCustomFieldsSnapshot.Validate(targetSnapshot);
            if (!JsonEqual(sourceSnapshot, targetSnapshot))
            {
                Fail("A target title request custom-fields snapshot differs from the source snapshot.");
            }
        }

        VerifyMappedTargetCount(connection, transaction, "TitleRequest", "title_request", requests.Count);
    }

    private static void VerifyAdditionalCopies(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> copies,
        IReadOnlyList<SourceRow> requests,
        IReadOnlyList<SourceRow> organizations)
    {
        var requestsById = requests.ToDictionary(row => row.RequiredString("id"), StringComparer.Ordinal);
        foreach (var row in copies)
        {
            var sourceId = row.RequiredString("id");
            var sourceRequestId = row.String("sourceTitleRequest");
            SourceRow? sourceRequest = null;
            long? expectedSourceRequestId = null;
            if (sourceRequestId is not null)
            {
                if (!requestsById.TryGetValue(sourceRequestId, out sourceRequest))
                {
                    Fail("An additional-copy request references a title request absent from the source package.");
                }
                expectedSourceRequestId = ReadMapping(connection, transaction, "title_request", sourceRequestId);
            }

            var copyLibraryId = row.Int32("libraryOrgId") ?? Fail<int>("An additional-copy request has no source library.");
            if (!IsSourceLibrary(copyLibraryId, organizations))
            {
                Fail("An additional-copy request is scoped to a non-library organization.");
            }
            var sourceFormatCode = row.Text("format") is { } sourceFormat && TrimLegacyFormatRuleText(sourceFormat).Length > 0
                ? NormalizeCode(sourceFormat)
                : null;
            long? expectedFormatId = null;
            int? expectedFormatOwner = null;
            if (sourceFormatCode is not null)
            {
                var effectiveFormat = ReadFormatByCode(connection, transaction, copyLibraryId, sourceFormatCode);
                expectedFormatId = effectiveFormat.FormatId;
                expectedFormatOwner = effectiveFormat.OwnerOrganizationId;
            }
            var sourceLibraryId = sourceRequest?.Int32("libraryOrgId");
            if (sourceRequest is not null && sourceLibraryId != copyLibraryId)
            {
                Fail("An additional-copy request and its source title request belong to different libraries.");
            }
            var copyTargetId = ReadMapping(connection, transaction, "additional_copy", sourceId);
            using var command = new SqlCommand(
                "SELECT c.[LibraryOrganizationId], c.[SourceTitleRequestId], r.[LibraryOrganizationId], " +
                "c.[MaterialFormatId], f.[OwnerOrganizationId], f.[Code], c.[FormatSnapshot] " +
                "FROM [asap].[AdditionalCopyRequest] c LEFT JOIN [asap].[TitleRequest] r ON r.[Id] = c.[SourceTitleRequestId] " +
                "LEFT JOIN [asap].[MaterialFormat] f ON f.[Id] = c.[MaterialFormatId] " +
                "WHERE c.[Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", copyTargetId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != copyLibraryId ||
                expectedSourceRequestId is null && !reader.IsDBNull(1) ||
                expectedSourceRequestId is { } expectedId &&
                    (reader.IsDBNull(1) || reader.GetInt64(1) != expectedId ||
                     reader.IsDBNull(2) || reader.GetInt32(2) != copyLibraryId) ||
                (expectedFormatId is null
                    ? !reader.IsDBNull(3) || !reader.IsDBNull(4) || !reader.IsDBNull(5) || !reader.IsDBNull(6)
                    : reader.IsDBNull(3) || reader.GetInt64(3) != expectedFormatId.Value ||
                      reader.IsDBNull(4) || reader.GetInt32(4) != expectedFormatOwner ||
                      reader.IsDBNull(5) || !string.Equals(NormalizeCode(reader.GetString(5)), sourceFormatCode, StringComparison.Ordinal) ||
                      !string.Equals(reader.IsDBNull(6) ? null : reader.GetString(6), row.Text("format"), StringComparison.Ordinal)))
            {
                Fail("A target additional-copy request does not preserve its source relation, format identity, and library ownership.");
            }
        }

        VerifyMappedTargetCount(connection, transaction, "AdditionalCopyRequest", "additional_copy", copies.Count);
    }

    private static void VerifyBibAuthorityInputs(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SourceRow> requests)
    {
        foreach (var row in requests)
        {
            var sourceId = row.RequiredString("id");
            var bibId = row.PositiveInt32("bibid", "source_bib_invalid");
            var identifier = row.String("identifier");
            var sourceIdentifierText = row.Text("identifier");
            var sourceStatus = row.String("isbnCheckStatus")?.Trim().ToLowerInvariant();
            var expectedStatus = sourceStatus switch
            {
                null => null,
                "pending" => "pending",
                "found" when bibId is not null => "found",
                "found" => Fail<string?>("A source request is marked found without a BIB ID."),
                "not_found" => "not_found",
                "skipped_no_isbn" => "skipped_no_isbn",
                "error_max_retries" => "error_max_retries",
                "error" when identifier is null => "skipped_no_isbn",
                "error" => Fail<string?>("A source identifier error cannot be reconciled."),
                "found_in_polaris" when bibId is not null => "found",
                "found_in_polaris" => Fail<string?>("A source Polaris-found request has no BIB ID."),
                _ => Fail<string?>("A source identifier status is unsupported.")
            };

            using var command = new SqlCommand(
                "SELECT [BibId], [BibIdStaffVerified], [IsbnCheckStatus], [Identifier], [LastCheckedUtc], [UpdatedUtc] " +
                "FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", ReadMapping(connection, transaction, "title_request", sourceId));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || NullableInt(reader, 0) != bibId || reader.GetBoolean(1) ||
                NullableString(reader, 2) != expectedStatus || NullableString(reader, 3) != sourceIdentifierText ||
                NullableDateTime(reader, 4) != row.UtcDateTime("lastChecked") ||
                NullableDateTime(reader, 5) != row.UtcDateTime("updated"))
            {
                Fail("A target request's BIB authority fields differ from the immutable source classification inputs.");
            }
        }
    }

    private static void VerifyFormatPopulation(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> rows,
        IReadOnlyDictionary<string, SourceRow> organizationsById,
        IReadOnlyList<SourceRow> organizations)
    {
        // These codes describe target seed population only; source ownership is derived from exact system rows below.
        var systemSeedCodes = new HashSet<string>(
            ["book", "audiobook_cd", "dvd", "music_cd", "ebook", "eaudiobook"],
            StringComparer.Ordinal);
        var exactSystemSourceCodes = rows
            .Where(item => item.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Text("code") ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var targetSystemCodes = new HashSet<string>(systemSeedCodes, StringComparer.Ordinal);
        foreach (var sourceCode in exactSystemSourceCodes)
        {
            targetSystemCodes.Add(NormalizeCode(sourceCode));
        }

        var expectedFormats = new HashSet<(int OwnerId, string Code)>(systemSeedCodes.Select(code => (1, code)));
        var expectedOverrides = new HashSet<(int LibraryId, string Code)>();
        foreach (var row in rows)
        {
            var code = NormalizeCode(row.Text("code") ?? string.Empty);
            var scope = row.RequiredString("scope");
            if (scope.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                expectedFormats.Add((1, code));
            }
            else if (scope.Equals("library", StringComparison.OrdinalIgnoreCase))
            {
                var libraryId = ResolveLibraryId(row.RequiredString("libraryOrganization"), organizationsById, organizations);
                if (exactSystemSourceCodes.Contains(row.Text("code") ?? string.Empty))
                {
                    expectedOverrides.Add((libraryId, code));
                }
                else
                {
                    expectedFormats.Add((libraryId, code));
                }
            }
            else
            {
                Fail("A source material format has an unsupported scope.");
            }
        }

        foreach (var overrideRow in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"))
        {
            if (overrideRow.JsonText("patronFormatRules") is not { } rulesJson)
            {
                continue;
            }
            var libraryId = overrideRow.Int32("orgId") ?? Fail<int>("A source patron format rule snapshot has no library identity.");
            using var rulesDocument = JsonDocument.Parse(rulesJson);
            var libraryOwnedCodes = rows
                .Where(row => row.RequiredString("scope").Equals("library", StringComparison.OrdinalIgnoreCase) &&
                    ResolveLibraryId(row.RequiredString("libraryOrganization"), organizationsById, organizations) == libraryId)
                .Where(row => !exactSystemSourceCodes.Contains(row.Text("code") ?? string.Empty))
                .Select(row => NormalizeCode(row.Text("code") ?? string.Empty))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var code in targetSystemCodes.Except(libraryOwnedCodes, StringComparer.Ordinal))
            {
                var sourceCode = EffectiveSourceFormatCode(rows, organizationsById, organizations, libraryId, code);
                var expected = ExpectedLegacyFormatRule(rulesDocument.RootElement, sourceCode);
                var sourceSystemFormat = rows.FirstOrDefault(row =>
                    row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase) &&
                    NormalizeCode(row.Text("code") ?? string.Empty) == code);
                var baseline = sourceSystemFormat is null
                    ? TargetSeedMaterialFormatDefaults(code)
                    : ExpectedLegacyMaterialFormatRule(sourceSystemFormat);
                if (FormatRulesDiffer(expected, baseline))
                {
                    expectedOverrides.Add((libraryId, code));
                }
            }
        }

        var systemSourceEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var sourceRow in rows.Where(row => row.RequiredString("scope").Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            var code = NormalizeCode(sourceRow.Text("code") ?? string.Empty);
            if (!systemSourceEnabled.TryAdd(code, sourceRow.Bool("enabled", false)))
            {
                Fail("Source system material formats collide after target code normalization.");
            }
        }

        using var formatsCommand = new SqlCommand(
            "SELECT [OwnerOrganizationId], [Code], [IsEnabled], [Message] FROM [asap].[MaterialFormat];",
            connection,
            transaction);
        var actualFormats = new HashSet<(int OwnerId, string Code)>();
        var actualSystemEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        var actualSystemMessages = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var formatsReader = formatsCommand.ExecuteReader())
        {
            while (formatsReader.Read())
            {
                var ownerId = formatsReader.GetInt32(0);
                var code = NormalizeCode(formatsReader.GetString(1));
                actualFormats.Add((ownerId, code));
                if (ownerId == 1)
                {
                    actualSystemEnabled.Add(code, formatsReader.GetBoolean(2));
                    actualSystemMessages.Add(code, NullableString(formatsReader, 3));
                }
            }
        }
        if (!actualFormats.SetEquals(expectedFormats))
        {
            Fail("Target material format population includes rows outside the six system seeds and source-owned formats.");
        }
        foreach (var code in targetSystemCodes)
        {
            var expectedEnabled = systemSourceEnabled.TryGetValue(code, out var sourceEnabled) && sourceEnabled;
            if (!actualSystemEnabled.TryGetValue(code, out var actualEnabled) || actualEnabled != expectedEnabled ||
                !actualSystemMessages.TryGetValue(code, out var actualMessage) || actualMessage is not null)
            {
                Fail("Target system material format availability or empty message differs from the immutable source package and pinned target seed.");
            }
            if (!systemSourceEnabled.ContainsKey(code))
            {
                VerifyTargetSeedMaterialFormat(connection, transaction, code);
            }
        }

        using var overridesCommand = new SqlCommand(
            "SELECT o.[LibraryOrganizationId], f.[Code] FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId];",
            connection,
            transaction);
        var actualOverrides = new HashSet<(int LibraryId, string Code)>();
        using (var overridesReader = overridesCommand.ExecuteReader())
        {
            while (overridesReader.Read())
            {
                actualOverrides.Add((overridesReader.GetInt32(0), NormalizeCode(overridesReader.GetString(1))));
            }
        }
        if (!actualOverrides.SetEquals(expectedOverrides))
        {
            Fail("Target material format overrides differ from source library formats that extend a system format.");
        }

        VerifyMappingCount(connection, transaction, "material_format", rows.Count, "material_format_override");
    }

    private static void VerifyTargetSeedMaterialFormat(SqlConnection connection, SqlTransaction? transaction, string code)
    {
        var expected = TargetSeedMaterialFormatIdentity(code);
        var rules = TargetSeedMaterialFormatDefaults(code);
        using var command = new SqlCommand(
            "SELECT [Code], [Label], [SortOrder], [IsEnabled], [MessageBehavior], [Message], [TitleMode], [TitleLabel], " +
            "[AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel], [PublicationMode], [PublicationLabel] " +
            "FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = @code;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", code);
        using var reader = command.ExecuteReader();
        if (!reader.Read() ||
            !string.Equals(reader.GetString(0), code, StringComparison.Ordinal) ||
            NullableString(reader, 1) != expected.Label || reader.GetInt32(2) != expected.SortOrder || reader.GetBoolean(3) ||
            NullableString(reader, 4) != rules.MessageBehavior || !reader.IsDBNull(5) ||
            NullableString(reader, 6) != rules.TitleMode || NullableString(reader, 7) != rules.TitleLabel ||
            NullableString(reader, 8) != rules.AuthorMode || NullableString(reader, 9) != rules.AuthorLabel ||
            NullableString(reader, 10) != rules.IdentifierMode || NullableString(reader, 11) != rules.IdentifierLabel ||
            NullableString(reader, 12) != rules.PublicationMode || NullableString(reader, 13) != rules.PublicationLabel || reader.Read())
        {
            Fail("A source-absent system material format differs from its pinned target seed values.");
        }
    }

    private static (string Label, int SortOrder) TargetSeedMaterialFormatIdentity(string code) => NormalizeCode(code) switch
    {
        "book" => ("Book", 10),
        "audiobook_cd" => ("Audiobook (Physical CD)", 20),
        "dvd" => ("DVD", 30),
        "music_cd" => ("Music CD", 40),
        "ebook" => ("eBook", 50),
        "eaudiobook" => ("eAudiobook", 60),
        _ => Fail<(string, int)>("A source-independent target seed expectation references a non-seed material format.")
    };

    private static ExpectedFormatRule ExpectedLegacyMaterialFormatRule(SourceRow row) => new(
        NormalizeLegacyMaterialEnum(row.String("messageBehavior"), "none", ["none", "message", "ebookMessage", "eaudiobookMessage"]),
        string.Empty,
        LegacyMaterialTitleMode(row.String("titleMode")),
        LegacyMaterialText(row.Text("titleLabel"), "Title"),
        NormalizeLegacyMaterialEnum(row.String("authorMode"), "required", ["required", "optional", "hidden"]),
        LegacyMaterialText(row.Text("authorLabel"), "Author"),
        NormalizeLegacyMaterialEnum(row.String("identifierMode"), "optional", ["required", "optional", "hidden"]),
        LegacyMaterialText(row.Text("identifierLabel"), "Identifier number"),
        NormalizeLegacyMaterialEnum(row.String("publicationMode"), "required", ["required", "optional", "hidden"]),
        LegacyMaterialText(row.Text("publicationLabel"), "Publication Timing"));

    private static ExpectedFormatRule TargetSeedMaterialFormatDefaults(string code) => NormalizeCode(code) switch
    {
        "book" => new("none", string.Empty, "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        "audiobook_cd" => new("none", string.Empty, "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        "dvd" => new("none", string.Empty, "required", "Title", "required", "Director/Actors/Producer", "hidden", "UPC", "required", "Publication Timing"),
        "music_cd" => new("none", string.Empty, "required", "Title", "required", "Artist", "hidden", "UPC", "required", "Publication Timing"),
        "ebook" => new("ebookMessage", string.Empty, "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        "eaudiobook" => new("eaudiobookMessage", string.Empty, "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
        _ => Fail<ExpectedFormatRule>("A source-independent target seed expectation references a non-seed material format.")
    };

    private static bool FormatRulesDiffer(ExpectedFormatRule expected, ExpectedFormatRule baseline) =>
        !SameFormatValue(expected.MessageBehavior, baseline.MessageBehavior) ||
        !SameFormatValue(expected.Message, baseline.Message) ||
        !SameFormatValue(expected.TitleMode, baseline.TitleMode) ||
        !SameFormatValue(expected.TitleLabel, baseline.TitleLabel) ||
        !SameFormatValue(expected.AuthorMode, baseline.AuthorMode) ||
        !SameFormatValue(expected.AuthorLabel, baseline.AuthorLabel) ||
        !SameFormatValue(expected.IdentifierMode, baseline.IdentifierMode) ||
        !SameFormatValue(expected.IdentifierLabel, baseline.IdentifierLabel) ||
        !SameFormatValue(expected.PublicationMode, baseline.PublicationMode) ||
        !SameFormatValue(expected.PublicationLabel, baseline.PublicationLabel);

    private static bool SameFormatValue(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal);

    private static void VerifyMappedTargetCount(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableName,
        string entityType,
        int expectedCount)
    {
        using var command = new SqlCommand($"SELECT COUNT(*) FROM [asap].[{tableName}];", connection, transaction);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expectedCount)
        {
            Fail($"Target {tableName} population contains rows outside the immutable source package.");
        }
        VerifyMappingCount(connection, transaction, entityType, expectedCount);
        VerifyMappedTargetsExist(connection, transaction, tableName, entityType, expectedCount);
    }

    private static void VerifyMappedTargetsExist(
        SqlConnection connection,
        SqlTransaction? transaction,
        string tableName,
        string entityType,
        int expectedCount)
    {
        using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[{tableName}] t ON t.[Id] = m.[NewId] WHERE m.[EntityType] = @entityType;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@entityType", entityType);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expectedCount)
        {
            Fail($"Target {tableName} mappings do not identify existing rows in the immutable source population.");
        }
    }

    private static void VerifyMappingCount(
        SqlConnection connection,
        SqlTransaction? transaction,
        string entityType,
        int expectedCount,
        string? alternateEntityType = null)
    {
        using var command = new SqlCommand(
            alternateEntityType is null
                ? "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = @entityType;"
                : "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] IN (@entityType, @alternateEntityType);",
            connection,
            transaction);
        command.Parameters.AddWithValue("@entityType", entityType);
        if (alternateEntityType is not null)
        {
            command.Parameters.AddWithValue("@alternateEntityType", alternateEntityType);
        }
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expectedCount)
        {
            Fail($"Target {entityType} mappings do not match the immutable source population.");
        }
    }

    private static void VerifyTotalMappingPopulation(
        SqlConnection connection,
        SqlTransaction? transaction,
        ValidatedMigrationPackage package)
    {
        var expected = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations").Count +
            MigrationPackageReader.ReadRows(package, "staff-users.json", "staff_users").Count +
            MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates").Count +
            MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates").Count +
            MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats").Count +
            MigrationPackageReader.ReadRows(package, "workflow-tags.json", "workflow_tags").Count +
            MigrationPackageReader.ReadRows(package, "format-auto-claim-rules.json", "format_claim_rules").Count +
            MigrationPackageReader.ReadRows(package, "title-requests.json", "title_requests").Count +
            MigrationPackageReader.ReadRows(package, "additional-copy-requests.json", "additional_copy_requests").Count +
            MigrationPackageReader.ReadRows(package, "deleted-request-audit.json", "deleted_request_audit").Count +
            MigrationPackageReader.ReadRows(package, "title-request-tags.json", "title_request_tags").Count +
            MigrationPackageReader.ReadRows(package, "title-request-events.json", "title_request_events").Count +
            MigrationPackageReader.ReadRows(package, "email-delivery-events.json", "email_delivery_events").Count;
        using var command = new SqlCommand("SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping];", connection, transaction);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expected)
        {
            Fail("Legacy migration mapping population contains rows outside the immutable source package.");
        }
    }

    private static IReadOnlyList<(long Id, string Code)> ReadEffectiveFormats(
        SqlConnection connection,
        SqlTransaction? transaction,
        int libraryId)
    {
        using var command = new SqlCommand(
            """
            WITH scoped AS
            (
                SELECT [Id], [Code], ROW_NUMBER() OVER
                    (PARTITION BY LOWER([Code]) ORDER BY CASE WHEN [OwnerOrganizationId] = @libraryId THEN 0 ELSE 1 END, [Id]) AS [ordinal]
                FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] IN (1, @libraryId)
            )
            SELECT [Id], [Code] FROM scoped WHERE [ordinal] = 1 ORDER BY [Id];
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        using var reader = command.ExecuteReader();
        var formats = new List<(long Id, string Code)>();
        while (reader.Read())
        {
            formats.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return formats;
    }

    private static (string EntityType, long FormatId, int OwnerOrganizationId, string Code, int? OverrideLibraryId) ReadFormatMapping(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sourceId)
    {
        using var command = new SqlCommand(
            "SELECT m.[EntityType], m.[NewId], CASE WHEN m.[EntityType] = N'material_format_override' THEN o.[MaterialFormatId] ELSE f.[Id] END, f.[OwnerOrganizationId], f.[Code], o.[LibraryOrganizationId] " +
            "FROM [asap].[LegacyPocketBaseMapping] m " +
            "LEFT JOIN [asap].[MaterialFormatOverride] o ON m.[EntityType] = N'material_format_override' AND o.[Id] = m.[NewId] " +
            "LEFT JOIN [asap].[MaterialFormat] f ON (m.[EntityType] = N'material_format' AND f.[Id] = m.[NewId]) OR (m.[EntityType] = N'material_format_override' AND f.[Id] = o.[MaterialFormatId]) " +
            "WHERE m.[PocketBaseId] = @sourceId AND m.[EntityType] IN (N'material_format', N'material_format_override');",
            connection,
            transaction);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4))
        {
            return Fail<(string, long, int, string, int?)>("A source material format has no valid target relationship.");
        }
        var result = (reader.GetString(0), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4), reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5));
        if (reader.Read())
        {
            return Fail<(string, long, int, string, int?)>("A source material format has ambiguous target mappings.");
        }
        return result;
    }

    private static (long FormatId, int OwnerOrganizationId) ReadFormatByCode(
        SqlConnection connection,
        SqlTransaction? transaction,
        int libraryId,
        string code)
    {
        using var command = new SqlCommand(
            "SELECT TOP (1) [Id], [OwnerOrganizationId] FROM [asap].[MaterialFormat] " +
            "WHERE [Code] = @code AND [OwnerOrganizationId] IN (1, @libraryId) " +
            "ORDER BY CASE WHEN [OwnerOrganizationId] = @libraryId THEN 0 ELSE 1 END;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetInt32(1))
            : Fail<(long, int)>("A title request's source format code has no target material format.");
    }

    private static bool HasFormatOverride(SqlConnection connection, SqlTransaction? transaction, int libraryId, long formatId)
    {
        using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @libraryId AND [MaterialFormatId] = @formatId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@libraryId", libraryId);
        command.Parameters.AddWithValue("@formatId", formatId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static long ReadMapping(SqlConnection connection, SqlTransaction? transaction, string entityType, string sourceId)
    {
        using var command = new SqlCommand(
            "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = @entityType AND [PocketBaseId] = @sourceId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Fail<long>($"No target mapping exists for source {entityType} {sourceId}.");
        }
        var result = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (reader.Read())
        {
            return Fail<long>($"Source {entityType} {sourceId} has multiple target mappings.");
        }
        return result;
    }

    private static int ResolveLibraryId(
        string sourceReference,
        IReadOnlyDictionary<string, SourceRow> organizationsById,
        IReadOnlyList<SourceRow> organizations)
    {
        var organization = organizationsById.TryGetValue(sourceReference, out var referenced)
            ? referenced
            : organizations.SingleOrDefault(row => row.Int32("organizationId")?.ToString(CultureInfo.InvariantCulture) == sourceReference);
        var id = organization?.Int32("organizationId") ??
            (int.TryParse(sourceReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0);
        return IsSourceLibrary(id, organizations) ? id : Fail<int>("A source relation does not identify an authoritative library.");
    }

    private static bool IsSourceLibrary(int id, IReadOnlyList<SourceRow> organizations) =>
        id > 1 && organizations.Any(row => row.Int32("organizationId") == id &&
            ReadOptionalOrganizationIdentity(row, "organizationCodeId", "organization_code_id") == 2);

    private static int? ReadOptionalOrganizationIdentity(SourceRow row, string primaryName, string aliasName)
    {
        var values = new[] { primaryName, aliasName }
            .Where(row.HasValue)
            .Select(row.Int32)
            .Distinct()
            .ToArray();
        if (values.Length > 1)
        {
            return Fail<int?>("A source organization has conflicting type or parent aliases.");
        }
        return values.SingleOrDefault();
    }

    private static int? NullableInt(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? NullableBoolean(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static string? NullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? NullableDateTime(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);

    private static string NormalizeCode(string value) => TrimLegacyFormatRuleText(value).ToLowerInvariant() switch
    {
        "0" => "book",
        "1" => "ebook",
        "2" => "audiobook_cd",
        "3" => "eaudiobook",
        "4" => "dvd",
        "5" => "music_cd",
        var code => code
    };

    private static bool JsonEqual(string? first, string? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }
        using var firstDocument = System.Text.Json.JsonDocument.Parse(first);
        using var secondDocument = System.Text.Json.JsonDocument.Parse(second);
        return System.Text.Json.JsonElement.DeepEquals(firstDocument.RootElement, secondDocument.RootElement);
    }

    private sealed record ExpectedFormatRule(
        string MessageBehavior,
        string Message,
        string TitleMode,
        string TitleLabel,
        string AuthorMode,
        string AuthorLabel,
        string IdentifierMode,
        string IdentifierLabel,
        string PublicationMode,
        string PublicationLabel);

    private static T Fail<T>(string message) =>
        throw new MigrationOperationException("reconciliation_failed", message);

    private static void Fail(string message) =>
        throw new MigrationOperationException("reconciliation_failed", message);
}
