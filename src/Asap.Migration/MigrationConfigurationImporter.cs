using System.Data;
using System.Globalization;
using Asap.Shared;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

internal sealed record MigrationConfigurationReconciliation(
    int RowsChecked,
    int FieldsChecked,
    int RelationshipsChecked);

internal static class MigrationConfigurationImporter
{
    private sealed record PublicationImportOption(
        string Key,
        string Label,
        bool Enabled,
        int SortOrder);

    private sealed record NormalizedCustomFieldOption(
        string Key,
        string Label,
        bool Enabled,
        int SortOrder);

    private sealed record NormalizedCustomFieldDefinition(
        string Key,
        string Type,
        string Label,
        string? HelpText,
        bool Enabled,
        int SortOrder,
        IReadOnlyList<NormalizedCustomFieldOption> Options);

    private sealed record SenderCandidate(string SourceId, string? Value);

    private sealed record NormalizedFormatRule(
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

    private sealed record StoredFormatColumns(
        string? MessageBehavior,
        string? Message,
        string? TitleMode,
        string? TitleLabel,
        string? AuthorMode,
        string? AuthorLabel,
        string? IdentifierMode,
        string? IdentifierLabel,
        string? PublicationMode,
        string? PublicationLabel);

    private sealed record ScopedFormat(long Id, string Code, int OwnerOrganizationId);

    private static readonly string[] LegacyUiDuplicateLabelFields =
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
        "duplicateLabelSilent"
    ];

    private static readonly string[] ScopedWorkflowTextFields =
    [
        "suggestionLimitMessage",
        "commonAuthorsLabel",
        "commonAuthorsHelp",
        "commonAuthorsMessage",
        "patronCodeEligibilityMessage"
    ];

    private static readonly string[] SeededEmailTemplateKeys = ["suggestion_submitted"];

    public static void Import(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, long> templateIds,
        MigrationCredentialProtector? credentialProtector,
        string? postmarkToken,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        ValidateConfigurationRows(package);
        ImportSystemSettings(connection, transaction, package, exportedAtUtc, importedCounts, transformations);
        ImportPolarisSettings(
            connection,
            transaction,
            package,
            credentialProtector,
            exportedAtUtc,
            importedCounts,
            transformations);
        ImportEmailSettings(
            connection,
            transaction,
            package,
            organizationIds,
            credentialProtector,
            postmarkToken,
            exportedAtUtc,
            importedCounts,
            transformations);
        ImportWorkflowSettings(
            connection,
            transaction,
            package,
            organizationIds,
            templateIds,
            exportedAtUtc,
            importedCounts);
        ImportBranding(
            connection,
            transaction,
            package,
            organizationIds,
            exportedAtUtc,
            importedCounts,
            transformations);
        ImportPatronConfiguration(
            connection,
            transaction,
            package,
            organizationIds,
            formatIds,
            exportedAtUtc,
            importedCounts,
            transformations);
    }

    private static void ImportBranding(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var rows = MigrationPackageReader.ReadRows(package, "branding.json", "branding");
        foreach (var row in rows.OrderBy(item => item.RequiredString("sourceRecordId"), StringComparer.Ordinal))
        {
            var organizationId = ResolveScopedOrganization(connection, transaction, row, organizationIds);
            var assetPath = row.RequiredString("assetPath");
            var fullPath = ResolvePackagePath(package.RootPath, assetPath);
            var data = File.ReadAllBytes(fullPath);
            var expectedLength = row.Int32("length")
                ?? throw new MigrationOperationException("branding_asset_invalid", "A branding asset has no length.");
            var expectedHash = row.RequiredString("sha256");
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(data));
            var contentType = row.RequiredString("contentType");
            if (data.LongLength != expectedLength ||
                !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase) ||
                !LogoImageValidator.TryValidate(data, contentType, out _, out _))
            {
                throw new MigrationOperationException(
                    "branding_asset_invalid",
                    "A branding asset failed its length, hash, or image-content validation.");
            }

            using var command = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM [asap].[Branding] WHERE [OrganizationId] = @organizationId)
                    UPDATE [asap].[Branding]
                    SET [LogoData] = @data,
                        [LogoContentType] = @contentType,
                        [LogoFileName] = @fileName,
                        [LogoAltText] = COALESCE(@logoAlt, [LogoAltText]),
                        [UpdatedUtc] = @updatedUtc
                    WHERE [OrganizationId] = @organizationId;
                ELSE
                    INSERT INTO [asap].[Branding]
                        ([OrganizationId], [LogoData], [LogoContentType], [LogoFileName], [LogoAltText], [UpdatedUtc])
                    VALUES
                        (@organizationId, @data, @contentType, @fileName, @logoAlt, @updatedUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.Add("@data", System.Data.SqlDbType.VarBinary, -1).Value = data;
            command.Parameters.AddWithValue("@contentType", contentType);
            command.Parameters.AddWithValue("@fileName", row.RequiredString("fileName"));
            command.Parameters.AddWithValue("@logoAlt", Db(row.Text("logoAlt")));
            AddDateTime2Parameter(command, "@updatedUtc", exportedAtUtc);
            command.ExecuteNonQuery();
            transformations.Add(new
            {
                entity = "branding",
                sourceId = row.RequiredString("sourceRecordId"),
                organizationId,
                assetSha256 = actualHash,
                length = data.LongLength,
                contentType
            });
        }
        importedCounts["branding"] = rows.Count;
    }

    public static void ValidateEmbedOrigins(ValidatedMigrationPackage package)
    {
        var rows = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings");
        if (rows.Count > 1)
        {
            throw new MigrationOperationException(
                "system_settings_ambiguous",
                "More than one source system_settings row was exported.");
        }
        _ = SplitLegacyEmbedOrigins(rows.SingleOrDefault()?.Text("patronEmbedAllowedOrigins"))
            .Select(NormalizeEmbedOrigin)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static void ValidateSourcePackage(ValidatedMigrationPackage package)
    {
        ValidateConfigurationRows(package);
        ValidateEmbedOrigins(package);
    }

    private static void ValidateConfigurationRows(ValidatedMigrationPackage package)
    {
        ValidatePolarisSourceSettings(package);
        ValidateLeapPatronUrlRepresentability(package);

        var workflowRows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        ValidateUniqueScope(
            workflowRows,
            "workflow settings",
            row => ScopedKey(row, "libraryOrganization"));
        foreach (var row in workflowRows)
        {
            if (UnrepresentableWorkflowTextField(row) is { } field)
            {
                throw new MigrationOperationException(
                    "workflow_text_unrepresentable",
                    $"The pinned workflow text field {field} is selected by the source but contains only whitespace that target patron configuration treats as absent.");
            }
            _ = ParsePatronCodeIds(row.Text("allowedPatronCodeIds"));
        }
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings"),
            "patron UI settings",
            row => ScopedKey(row, "libraryOrganization"));
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"),
            "patron settings overrides",
            row => row.RequiredString("orgId"));
        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"))
        {
            if (row.JsonText("additionalFieldDefinitions") is not { } definitionsJson)
            {
                continue;
            }
            using var document = JsonDocument.Parse(definitionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new MigrationOperationException(
                    "custom_fields_invalid",
                    "Additional field definitions must be a JSON array.");
            }
            _ = NormalizeCustomFieldDefinitions(document.RootElement);
        }
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"),
            "legacy patron settings",
            row => row.RequiredString("libraryOrganization"));
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "library_settings"),
            "legacy library settings",
            row => row.RequiredString("libraryOrganization"));

        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings"))
        {
            var rawOptions = row.Text("publicationOptions");
            if (string.Equals(row.RequiredString("scope").Trim(), "system", StringComparison.OrdinalIgnoreCase) &&
                HasLegacyConfigurationText(rawOptions))
            {
                _ = ParsePublicationOptions(rawOptions!, isSystem: true);
            }
        }
        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"))
        {
            if (UnrepresentablePatronMessageOverrideField(row) is { } messageField)
            {
                throw new MigrationOperationException(
                    "patron_message_unrepresentable",
                    $"The modern {messageField} override contains a non-empty whitespace-only message. The pinned source returns that raw value, but target patron configuration treats it as absent.");
            }
            _ = ParseDuplicateLabelObject(row.JsonText("duplicateStatusLabels"), modernOverride: true);
            var rawOptions = row.Text("publicationOptions");
            if (HasLegacyConfigurationText(rawOptions))
            {
                _ = ParsePublicationOptions(rawOptions!, isSystem: false);
            }
        }
        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"))
        {
            _ = ParseDuplicateLabelObject(row.JsonText("duplicateRequestStatusLabels"), modernOverride: false);
        }

        var emailRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates");
        var rejectionRows = MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates");
        ValidateUniqueScope(
            emailRows,
            "email templates",
            row => $"{ScopedKey(row, "libraryOrganization")}|{row.RequiredString("templateKey").Trim().ToLowerInvariant()}");
        ValidateUniqueScope(
            rejectionRows,
            "rejection templates",
            row => $"{ScopedKey(row, "libraryOrganization")}|{row.String("sourceTemplateId") ?? row.RequiredString("id")}");
        var templateSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in emailRows.Concat(rejectionRows))
        {
            if (!templateSourceIds.Add(row.RequiredString("id")))
            {
                throw new MigrationOperationException(
                    "configuration_source_id_conflict",
                    "Email and rejection templates contain the same source identifier.");
            }
        }

        ValidateEmailTemplateSqlIdentity(package, emailRows, rejectionRows);

        ValidateTemplateLineageAndWorkflowSelections(package, emailRows, rejectionRows);

        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats"),
            "material formats",
            row => $"{ScopedKey(row, "libraryOrganization")}|{NormalizeFormatCode(row.Text("code") ?? string.Empty)}");
    }

    private static void ValidatePolarisSourceSettings(ValidatedMigrationPackage package)
    {
        var rows = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        if (rows.Count > 1)
        {
            throw new MigrationOperationException(
                "polaris_settings_ambiguous",
                "More than one source polaris_settings row was exported.");
        }

        var row = rows.SingleOrDefault();
        if (row is null)
        {
            return;
        }

        _ = LegacyPolarisHost(row.Text("host"));
        _ = LegacyPolarisAccessId(row.Text("accessId"));
        EnsurePolarisTextRepresentable(row.Text("staffDomain"), 256);
        EnsurePolarisTextRepresentable(row.Text("adminUser"), 256);
        ValidateLegacyPolarisEndpointValue(row.Text("langId"), "1033");
        ValidateLegacyPolarisEndpointValue(row.Text("appId"), "100");
        _ = LegacyPolarisIdentity(row, "workstationId");
        _ = LegacyPolarisIdentity(row, "userId");
    }

    private static string? LegacyPolarisHost(string? sourceHost)
    {
        if (string.IsNullOrEmpty(sourceHost))
        {
            return null;
        }

        var trimmedHost = sourceHost.TrimEnd('/');
        if (HasUnsupportedHostScheme(trimmedHost))
        {
            throw UnrepresentablePolarisSettings();
        }

        var effectiveHost = HasHttpScheme(trimmedHost)
            ? trimmedHost
            : $"https://{trimmedHost}";
        if (effectiveHost.Length > 2048 ||
            !string.Equals(effectiveHost, TrimLegacyConfigurationText(effectiveHost), StringComparison.Ordinal) ||
            !string.Equals(effectiveHost, effectiveHost.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(effectiveHost, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw UnrepresentablePolarisSettings();
        }

        return effectiveHost;
    }

    private static string LegacyPolarisAccessId(string? sourceAccessId)
    {
        var effectiveAccessId = string.IsNullOrEmpty(sourceAccessId) ? "SuggestAPI" : sourceAccessId;
        EnsurePolarisTextRepresentable(effectiveAccessId, 256);
        if (string.IsNullOrWhiteSpace(effectiveAccessId))
        {
            throw UnrepresentablePolarisSettings();
        }

        return effectiveAccessId;
    }

    private static void EnsurePolarisTextRepresentable(string? value, int maximumLength)
    {
        if (value is not null &&
            (value.Length > maximumLength || value.Length > 0 && string.IsNullOrWhiteSpace(value)))
        {
            throw UnrepresentablePolarisSettings();
        }
    }

    private static bool HasHttpScheme(string value) =>
        value.StartsWith("https://", StringComparison.Ordinal) ||
        value.StartsWith("http://", StringComparison.Ordinal);

    private static bool HasUnsupportedHostScheme(string value) =>
        Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9+.-]*://", RegexOptions.CultureInvariant) &&
        !HasHttpScheme(value);

    private static int LegacyPolarisIdentity(SourceRow row, string field)
    {
        var raw = row.Text(field);
        if (string.IsNullOrEmpty(raw))
        {
            return 1;
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new MigrationOperationException(
                "source_polaris_identity_invalid",
                $"Source field {field} is not a positive Int32 Polaris identity.");
        }

        return row.PositiveInt32(field, "source_polaris_identity_invalid") ??
            throw new MigrationOperationException(
                "source_polaris_identity_invalid",
                $"Source field {field} is not a positive Int32 Polaris identity.");
    }

    private static void ValidateLegacyPolarisEndpointValue(string? sourceValue, string fallback)
    {
        if (!string.IsNullOrEmpty(sourceValue) &&
            !string.Equals(sourceValue, fallback, StringComparison.Ordinal))
        {
            throw UnrepresentablePolarisSettings();
        }
    }

    private static string? LegacyEffectivePatronPattern(string? sourcePattern)
    {
        if (sourcePattern is null)
        {
            return null;
        }

        var effective = TrimLegacyConfigurationText(sourcePattern);
        return effective.Length == 0 ? null : effective;
    }

    private static MigrationOperationException UnrepresentablePolarisSettings() =>
        new(
            "polaris_settings_unrepresentable",
            "A source Polaris setting cannot be represented by the target provider configuration.");

    private static void ValidateLeapPatronUrlRepresentability(ValidatedMigrationPackage package)
    {
        var rows = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings");
        if (rows.Count > 1)
        {
            throw new MigrationOperationException(
                "system_settings_ambiguous",
                "More than one source system_settings row was exported.");
        }

        var row = rows.SingleOrDefault();
        var bibPattern = row?.Text("leapBibUrlPattern");
        var pattern = row?.Text("leapPatronUrlPattern");
        if (bibPattern is { Length: > 2048 } || pattern is { Length: > 2048 })
        {
            throw new MigrationOperationException(
                "system_settings_unrepresentable",
                "A source research URL pattern exceeds the target system settings capacity.");
        }
        if (pattern is null)
        {
            return;
        }

        var legacyPattern = TrimLegacyConfigurationText(pattern);
        var targetPattern = pattern.Trim();
        if (HasUsableTargetPatronResearchUrl(targetPattern) &&
            !string.Equals(legacyPattern, targetPattern, StringComparison.Ordinal))
        {
            throw new MigrationOperationException(
                "leap_patron_url_pattern_unrepresentable",
                "A source patron research URL pattern cannot be represented by the target lookup behavior.");
        }
    }

    private static bool HasUsableTargetPatronResearchUrl(string? pattern)
    {
        var value = pattern?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !value.Contains("{{patron-id}}", StringComparison.Ordinal) &&
            !value.Contains("{{patronId}}", StringComparison.Ordinal))
        {
            return false;
        }

        var hasUnsupportedToken = false;
        var candidate = Regex.Replace(value, "\\{\\{([^{}]+)\\}\\}", match =>
        {
            if (match.Groups[1].Value is not ("title" or "identifier" or "bibid" or "patron-id" or "patronId"))
            {
                hasUnsupportedToken = true;
                return string.Empty;
            }
            return "1";
        });
        if (hasUnsupportedToken || candidate.Contains("{{", StringComparison.Ordinal) ||
            candidate.Contains("}}", StringComparison.Ordinal) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var url))
        {
            return false;
        }

        return (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) &&
               string.IsNullOrEmpty(url.UserInfo);
    }

    private static void ValidateEmailTemplateSqlIdentity(
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> emailRows,
        IReadOnlyList<SourceRow> rejectionRows)
    {
        foreach (var row in emailRows)
        {
            var key = row.RequiredString("templateKey");
            if (key.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException(
                    "email_template_target_kind_invalid",
                    $"Ordinary source email template {row.RequiredString("id")} uses the target-reserved rejection-template key prefix.");
            }
            if (SeededEmailTemplateKeys.Any(seed =>
                    string.Equals(key, seed, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, seed, StringComparison.Ordinal)))
            {
                throw new MigrationOperationException(
                    "email_template_seed_identity_collision",
                    $"Email template {row.RequiredString("id")} changes the exact identity of a seeded system template.");
            }
        }

        var systemKeys = emailRows
            .Where(row => TemplateScope(row) == "system")
            .Select(row => row.RequiredString("templateKey"))
            .ToArray();
        foreach (var row in emailRows.Where(row => TemplateScope(row) == "library" && row.String("sourceTemplateId") is null))
        {
            var key = row.RequiredString("templateKey");
            if (systemKeys.Any(systemKey =>
                    string.Equals(key, systemKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, systemKey, StringComparison.Ordinal)))
            {
                throw new MigrationOperationException(
                    "email_template_sql_identity_collision",
                    $"Library email template {row.RequiredString("id")} collides with a system template only under target SQL case-insensitive comparison.");
            }
        }

        var organizations = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations")
            .ToDictionary(
                row => row.RequiredString("id"),
                row => row.Int32("organizationId") ?? throw new MigrationOperationException(
                    "settings_organization_unresolved",
                    "A source organization has no native identity."),
                StringComparer.Ordinal);
        var targetIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, isRejection) in emailRows.Select(row => (row, false)).Concat(rejectionRows.Select(row => (row, true))))
        {
            var scope = TemplateScope(row);
            var organizationId = scope == "system"
                ? 1
                : ResolvePackageLibraryOrganization(row.RequiredString("libraryOrganization"), package, organizations);
            var templateKey = isRejection
                ? "rejection:" + (scope == "library" ? row.String("sourceTemplateId") ?? row.RequiredString("id") : row.RequiredString("id"))
                : row.RequiredString("templateKey");
            if (!targetIdentities.Add($"{organizationId.ToString(CultureInfo.InvariantCulture)}|{templateKey}"))
            {
                throw new MigrationOperationException(
                    "email_template_sql_identity_collision",
                    $"Email template {row.RequiredString("id")} collides with another source template in the target's case-insensitive organization/key identity.");
            }
        }
    }

    private static void ValidateTemplateLineageAndWorkflowSelections(
        ValidatedMigrationPackage package,
        IReadOnlyList<SourceRow> emailRows,
        IReadOnlyList<SourceRow> rejectionRows)
    {
        var templates = emailRows.Select(row => new SourceTemplateInfo(row, false))
            .Concat(rejectionRows.Select(row => new SourceTemplateInfo(row, true)))
            .ToDictionary(item => item.Row.RequiredString("id"), StringComparer.Ordinal);
        foreach (var template in templates.Values)
        {
            var scope = TemplateScope(template.Row);
            var sourceTemplateId = template.Row.String("sourceTemplateId");
            if (scope == "system")
            {
                if (sourceTemplateId is not null)
                {
                    throw new MigrationOperationException(
                        "email_template_source_scope_invalid",
                        "A system email template cannot declare library override lineage.");
                }
                continue;
            }
            if (scope != "library" || sourceTemplateId is null)
            {
                continue;
            }
            if (!templates.TryGetValue(sourceTemplateId, out var source) || TemplateScope(source.Row) != "system")
            {
                throw new MigrationOperationException(
                    "email_template_source_scope_invalid",
                    "A library email template may inherit only from a system template in the immutable source package.");
            }
            if (template.IsRejection != source.IsRejection)
            {
                throw new MigrationOperationException(
                    "email_template_source_kind_invalid",
                    "A rejection template may inherit only from a system rejection template, and an ordinary template only from an ordinary system template.");
            }
            if (!template.IsRejection && !string.Equals(
                    template.Row.RequiredString("templateKey"),
                    source.Row.RequiredString("templateKey"),
                    StringComparison.Ordinal))
            {
                throw new MigrationOperationException(
                    "email_template_source_key_invalid",
                    "An ordinary library template's explicit system parent must have the same template key.");
            }
        }

        var organizationIds = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations")
            .ToDictionary(
                row => row.RequiredString("id"),
                row => row.Int32("organizationId") ?? throw new MigrationOperationException(
                    "settings_organization_unresolved",
                    "A source organization has no native identity."),
                StringComparer.Ordinal);
        foreach (var workflow in MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings"))
        {
            if (workflow.String("outstandingTimeoutRejectionTemplate") is not { } selectedSourceId)
            {
                continue;
            }
            if (!templates.TryGetValue(selectedSourceId, out var selected))
            {
                throw new MigrationOperationException(
                    "workflow_template_unresolved",
                    "A workflow rejection template reference cannot be resolved.");
            }
            if (!selected.IsRejection)
            {
                throw new MigrationOperationException(
                    "workflow_template_kind_invalid",
                    "A workflow timeout must reference the rejection_templates collection.");
            }

            var workflowScope = TemplateScope(workflow);
            var workflowOrganizationId = workflowScope switch
            {
                "system" => 1,
                "library" => ResolvePackageLibraryOrganization(workflow.RequiredString("libraryOrganization"), package, organizationIds),
                _ => throw new MigrationOperationException("settings_scope_invalid", "A workflow setting has an unsupported scope.")
            };
            var selectedScope = TemplateScope(selected.Row);
            var selectedOrganizationId = selectedScope switch
            {
                "system" => 1,
                "library" => ResolvePackageLibraryOrganization(selected.Row.RequiredString("libraryOrganization"), package, organizationIds),
                _ => throw new MigrationOperationException("email_template_scope_invalid", "An email template has an unsupported scope.")
            };
            if (selectedOrganizationId != 1 && selectedOrganizationId != workflowOrganizationId)
            {
                throw new MigrationOperationException(
                    "workflow_template_scope_invalid",
                    "A workflow can select only a system rejection template or one owned by its own library.");
            }

            var effective = ResolveEffectiveWorkflowTemplate(selected, workflowOrganizationId, templates.Values, package, organizationIds);
            if (!GetTemplateKey(effective.Base).StartsWith("rejection:", StringComparison.OrdinalIgnoreCase) ||
                effective.Hidden || string.IsNullOrWhiteSpace(effective.Subject) || string.IsNullOrWhiteSpace(effective.Body))
            {
                throw new MigrationOperationException(
                    "workflow_template_invalid",
                    "A workflow must select an enabled rejection template with complete effective content in its own scope.");
            }
        }
    }

    private static (SourceTemplateInfo Base, bool Hidden, string? Subject, string? Body) ResolveEffectiveWorkflowTemplate(
        SourceTemplateInfo selected,
        int workflowOrganizationId,
        IEnumerable<SourceTemplateInfo> allTemplates,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var templates = allTemplates.ToArray();
        SourceTemplateInfo baseTemplate;
        SourceTemplateInfo? overrideTemplate = null;
        if (TemplateScope(selected.Row) == "system")
        {
            baseTemplate = selected;
            if (workflowOrganizationId != 1)
            {
                overrideTemplate = templates.SingleOrDefault(item =>
                    TemplateScope(item.Row) == "library" &&
                    ResolvePackageLibraryOrganization(item.Row.RequiredString("libraryOrganization"), package, organizationIds) == workflowOrganizationId &&
                    (string.Equals(item.Row.String("sourceTemplateId"), selected.Row.RequiredString("id"), StringComparison.Ordinal) ||
                     !item.IsRejection && string.Equals(GetTemplateKey(item), GetTemplateKey(selected), StringComparison.Ordinal)));
            }
        }
        else
        {
            overrideTemplate = selected;
            var sourceTemplateId = selected.Row.String("sourceTemplateId");
            if (sourceTemplateId is not null)
            {
                baseTemplate = templates.Single(item => string.Equals(item.Row.RequiredString("id"), sourceTemplateId, StringComparison.Ordinal));
            }
            else if (!selected.IsRejection)
            {
                baseTemplate = templates.SingleOrDefault(item =>
                    TemplateScope(item.Row) == "system" &&
                    string.Equals(GetTemplateKey(item), GetTemplateKey(selected), StringComparison.Ordinal)) ?? selected;
            }
            else
            {
                baseTemplate = selected;
            }
            if (ReferenceEquals(baseTemplate, selected))
            {
                overrideTemplate = null;
            }
        }

        var hidden = !baseTemplate.Row.Bool("enabled", true) ||
            overrideTemplate is not null && !overrideTemplate.Row.Bool("enabled", true);
        var subject = MeaningfulTemplateText(overrideTemplate?.Row.Text("subject")) ?? MeaningfulTemplateText(baseTemplate.Row.Text("subject"));
        var body = MeaningfulTemplateText(overrideTemplate?.Row.Text("body")) ?? MeaningfulTemplateText(baseTemplate.Row.Text("body"));
        return (baseTemplate, hidden, subject, body);
    }

    private static int ResolvePackageLibraryOrganization(
        string sourceReference,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var organizationId = organizationIds.TryGetValue(sourceReference, out var mapped)
            ? mapped
            : int.TryParse(sourceReference, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nativeId) && organizationIds.Values.Contains(nativeId)
                ? nativeId
                : throw new MigrationOperationException("settings_organization_unresolved", "A source library reference cannot be resolved.");
        var row = MigrationPackageReader.ReadRows(package, "organizations.json", "polaris_organizations")
            .SingleOrDefault(item => item.Int32("organizationId") == organizationId);
        var organizationCodeId = row is null
            ? null
            : row.HasValue("organizationCodeId") ? row.Int32("organizationCodeId") : row.Int32("organization_code_id");
        if (organizationId <= 1 || organizationCodeId != 2)
        {
            throw new MigrationOperationException("settings_organization_unresolved", "A source organization reference is not a library.");
        }
        return organizationId;
    }

    private static string TemplateScope(SourceRow row) => row.RequiredString("scope").Trim().ToLowerInvariant();

    private static string GetTemplateKey(SourceTemplateInfo template) => template.IsRejection
        ? "rejection:" + (TemplateScope(template.Row) == "library"
            ? template.Row.String("sourceTemplateId") ?? template.Row.RequiredString("id")
            : template.Row.RequiredString("id"))
        : template.Row.RequiredString("templateKey");

    private static string? MeaningfulTemplateText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record SourceTemplateInfo(SourceRow Row, bool IsRejection);

    private static void ValidateUniqueScope(
        IReadOnlyList<SourceRow> rows,
        string entity,
        Func<SourceRow, string> keySelector)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!keys.Add(keySelector(row)))
            {
                throw new MigrationOperationException(
                    "configuration_scope_conflict",
                    $"Source {entity} contains duplicate scoped configuration.");
            }
        }
    }

    private static string ScopedKey(SourceRow row, string organizationField)
    {
        var scope = row.RequiredString("scope").Trim().ToLowerInvariant();
        return scope == "system"
            ? "system"
            : $"library:{row.RequiredString(organizationField)}";
    }

    private static string ResolvePackagePath(string packageRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new MigrationOperationException("branding_asset_path_invalid", "Branding asset paths must be relative.");
        }
        var root = Path.GetFullPath(packageRoot);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw new MigrationOperationException(
                "branding_asset_path_invalid",
                "A branding asset path is missing or escapes the migration package.");
        }
        return fullPath;
    }

    private static void ImportSystemSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var rows = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings");
        if (rows.Count > 1)
        {
            throw new MigrationOperationException("system_settings_ambiguous", "More than one source system_settings row was exported.");
        }
        var row = rows.SingleOrDefault();
        var runtime = MigrationPackageReader.ReadMetadata(
            package,
            "effective-legacy-runtime-config.json");
        var settings = runtime.GetProperty("settings");
        var staffUrlSetting = settings.GetProperty("StaffApplicationUrl");
        var iconPatternSetting = settings.GetProperty("MaterialTypeIconUrlPattern");
        var staffUrl = staffUrlSetting.GetProperty("value").GetString();
        var iconPattern = iconPatternSetting.GetProperty("value").GetString();
        if (string.IsNullOrWhiteSpace(staffUrl) || string.IsNullOrWhiteSpace(iconPattern))
        {
            throw new MigrationOperationException(
                "effective_runtime_config_invalid",
                "The frozen effective runtime configuration is incomplete.");
        }

        var origins = SplitLegacyEmbedOrigins(row?.Text("patronEmbedAllowedOrigins"))
            .Select(NormalizeEmbedOrigin)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using (var command = new SqlCommand(
            """
            UPDATE [asap].[SystemSettings]
            SET [StaffApplicationUrl] = @staffUrl,
                [LeapBibUrlPattern] = @bibPattern,
                [LeapPatronUrlPattern] = @patronPattern,
                [MaterialTypeIconUrlPattern] = @iconPattern,
                [UpdatedUtc] = @updatedUtc
            WHERE [OrganizationId] = 1;

            DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("@staffUrl", staffUrl);
            command.Parameters.AddWithValue("@bibPattern", Db(row?.Text("leapBibUrlPattern")));
            command.Parameters.AddWithValue("@patronPattern", Db(LegacyEffectivePatronPattern(row?.Text("leapPatronUrlPattern"))));
            command.Parameters.AddWithValue("@iconPattern", iconPattern);
            AddDateTime2Parameter(command, "@updatedUtc", row?.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
        }

        foreach (var origin in origins)
        {
            using var insert = new SqlCommand(
                "INSERT INTO [asap].[PatronEmbedAllowedOrigin] ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc]) VALUES (1, @origin, @normalized, @createdUtc);",
                connection,
                transaction);
            insert.Parameters.AddWithValue("@origin", origin);
            insert.Parameters.AddWithValue("@normalized", origin);
            AddDateTime2Parameter(insert, "@createdUtc", exportedAtUtc);
            insert.ExecuteNonQuery();
        }
        importedCounts["system_settings"] = rows.Count;
        transformations.Add(new
        {
            entity = "system_settings_effective_runtime",
            sourceId = row?.String("id"),
            staffApplicationUrlSource = staffUrlSetting.GetProperty("source").GetString(),
            materialTypeIconUrlPatternSource = iconPatternSetting.GetProperty("source").GetString(),
            misconfiguredMessage = "target_code_default",
            misconfiguredMessageReason = "pinned_ui_settings_schema_has_no_persisted_field"
        });
    }

    private static string NormalizeEmbedOrigin(string value)
    {
        var origin = TrimLegacyConfigurationText(value);
        if (origin.Any(char.IsWhiteSpace) || origin.IndexOfAny(['"', '\'', '`', ';', '\\']) >= 0)
        {
            throw InvalidEmbedOrigin();
        }

        var wildcard = Regex.Match(
            origin,
            "^(https?)://\\*\\.(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (wildcard.Success)
        {
            var scheme = wildcard.Groups[1].Value.ToLowerInvariant();
            var authority = wildcard.Groups[2].Value;
            var hostMatch = Regex.Match(
                authority,
                "^(?<host>[a-z0-9.-]+)(?::(?<port>[0-9]+))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var host = authority.ToLowerInvariant();
            if (scheme != "https" ||
                !hostMatch.Success ||
                !IsValidEmbedPort(hostMatch) ||
                !IsDnsHostname(hostMatch.Groups["host"].Value) ||
                !Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var wildcardUri) ||
                wildcardUri.HostNameType != UriHostNameType.Dns ||
                !hostMatch.Groups["host"].Value.TrimEnd('.').Contains('.') ||
                wildcardUri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(wildcardUri.Query) ||
                !string.IsNullOrEmpty(wildcardUri.Fragment) ||
                !string.IsNullOrEmpty(wildcardUri.UserInfo) ||
                wildcardUri.Port is < 0 or > 65535)
            {
                throw InvalidEmbedOrigin();
            }

            return $"https://*.{host}";
        }

        var plain = Regex.Match(
            origin,
            "^(https?)://([^/?#]+)(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!plain.Success || (plain.Groups[3].Value.Length > 0 && plain.Groups[3].Value != "/"))
        {
            throw InvalidEmbedOrigin();
        }

        var protocol = plain.Groups[1].Value.ToLowerInvariant();
        var plainHost = plain.Groups[2].Value.ToLowerInvariant();
        var authorityMatch = Regex.Match(
            plainHost,
            "^(?<host>[a-z0-9.-]+|\\[[0-9a-f:.]+\\])(?::(?<port>[0-9]+))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var rawAuthorityHost = authorityMatch.Success ? authorityMatch.Groups["host"].Value : string.Empty;
        var hostForValidation = rawAuthorityHost.Trim('[', ']');
        if (plainHost.Length == 0 ||
            plainHost.Contains('@') ||
            !authorityMatch.Success ||
            !IsValidEmbedPort(authorityMatch) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin) ||
            !string.IsNullOrEmpty(parsedOrigin.UserInfo) ||
            parsedOrigin.Scheme != protocol ||
            parsedOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsedOrigin.Query) ||
            !string.IsNullOrEmpty(parsedOrigin.Fragment) ||
            parsedOrigin.Port is < 0 or > 65535 ||
            parsedOrigin.HostNameType == UriHostNameType.Unknown ||
            (parsedOrigin.HostNameType == UriHostNameType.Dns && !IsDnsHostname(hostForValidation)) ||
            (rawAuthorityHost.StartsWith("[", StringComparison.Ordinal) && parsedOrigin.HostNameType != UriHostNameType.IPv6))
        {
            throw InvalidEmbedOrigin();
        }

        var hostname = Regex.Replace(plainHost, ":[0-9]+$", string.Empty, RegexOptions.CultureInvariant);
        var isLocal = hostname is "localhost" or "127.0.0.1" or "[::1]";
        if (protocol != "https" && !(protocol == "http" && isLocal))
        {
            throw InvalidEmbedOrigin();
        }

        return $"{protocol}://{plainHost}";
    }

    private static bool IsValidEmbedPort(Match authority)
    {
        if (!authority.Groups["port"].Success)
        {
            return true;
        }
        return int.TryParse(authority.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is >= 0 and <= 65535;
    }

    private static bool IsDnsHostname(string host)
    {
        var value = host.EndsWith(".", StringComparison.Ordinal) ? host[..^1] : host;
        if (value.Length is 0 or > 253)
        {
            return false;
        }
        return value.Split('.').All(label => label.Length is > 0 and <= 63 &&
            IsAsciiAlphaNumeric(label[0]) && IsAsciiAlphaNumeric(label[^1]) &&
            label.All(character => IsAsciiAlphaNumeric(character) || character == '-'));
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static MigrationOperationException InvalidEmbedOrigin() =>
        new(
            "patron_embed_origin_invalid",
            "A source patron embed origin is not an allowed origin.");

    private static void ImportPolarisSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        MigrationCredentialProtector? credentialProtector,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var rows = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        if (rows.Count > 1)
        {
            throw new MigrationOperationException("polaris_settings_ambiguous", "More than one source polaris_settings row was exported.");
        }
        foreach (var row in rows)
        {
            var apiKey = row.Text("apiKey") is { Length: > 0 } rawApiKey ? rawApiKey : null;
            var adminPassword = row.Text("adminPassword") is { Length: > 0 } rawAdminPassword ? rawAdminPassword : null;
            if ((apiKey is not null || adminPassword is not null) && credentialProtector is null)
            {
                throw new MigrationOperationException(
                    "credential_protection_not_configured",
                    "Source Polaris credentials require the target Data Protection import inputs.");
            }
            using var command = new SqlCommand(
                """
                UPDATE [asap].[PolarisSettings]
                SET [Host] = @host, [AccessId] = @accessId, [ProtectedApiKey] = @protectedApiKey,
                    [StaffDomain] = @staffDomain, [AdminUser] = @adminUser,
                    [ProtectedAdminPassword] = @protectedAdminPassword, [WorkstationId] = @workstationId,
                    [SystemPolarisUserId] = @userId, [UpdatedUtc] = @updatedUtc
                WHERE [OrganizationId] = 1;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@host", Db(LegacyPolarisHost(row.Text("host"))));
            command.Parameters.AddWithValue("@accessId", LegacyPolarisAccessId(row.Text("accessId")));
            command.Parameters.AddWithValue("@protectedApiKey", Db(apiKey is null ? null : credentialProtector!.Protect(apiKey)));
            command.Parameters.AddWithValue("@staffDomain", Db(row.Text("staffDomain")));
            command.Parameters.AddWithValue("@adminUser", Db(row.Text("adminUser")));
            command.Parameters.AddWithValue("@protectedAdminPassword", Db(adminPassword is null ? null : credentialProtector!.Protect(adminPassword)));
            command.Parameters.AddWithValue("@workstationId", LegacyPolarisIdentity(row, "workstationId"));
            command.Parameters.AddWithValue("@userId", LegacyPolarisIdentity(row, "userId"));
            AddDateTime2Parameter(command, "@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
            transformations.Add(new
            {
                entity = "polaris_settings",
                sourceId = row.RequiredString("id"),
                apiKeyProtected = apiKey is not null,
                adminPasswordProtected = adminPassword is not null,
                retiredRequestingOrganizationSource = row.Text("requestingOrgId"),
                retiredPickupOrganizationSource = row.Text("pickupOrgId"),
                operationContextSource = "owning_request_or_effective_servicing_library"
            });
        }
        importedCounts["polaris_settings"] = rows.Count;
    }

    private static void ImportEmailSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        MigrationCredentialProtector? credentialProtector,
        string? postmarkToken,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var rows = MigrationPackageReader.ReadRows(package, "email-settings.json", "smtp_settings");
        if (rows.Count > 1)
        {
            throw new MigrationOperationException("email_settings_ambiguous", "More than one source smtp_settings row was exported.");
        }
        if (postmarkToken is not null && credentialProtector is null)
        {
            throw new MigrationOperationException(
                "credential_protection_not_configured",
                "A target Postmark token requires the target Data Protection import inputs.");
        }
        transformations.Add(new
        {
            entity = "email_provider_token",
            organizationId = 1,
            postmarkTokenProvisioned = postmarkToken is not null
        });
        var templateRows = MigrationPackageReader.ReadRowsOrEmpty(
            package,
            "email-templates.json",
            "email_templates");
        var systemTemplateRows = templateRows
            .Where(item => string.Equals(item.String("scope"), "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var systemFromAddress = ResolveSenderValue(
            "system templates",
            "fromAddress",
            systemTemplateRows.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromAddress"))))
            ?? MeaningfulSenderText(rows.SingleOrDefault()?.Text("fromAddress"));
        var systemFromName = ResolveSenderValue(
            "system templates",
            "fromName",
            systemTemplateRows.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromName"))))
            ?? MeaningfulSenderText(rows.SingleOrDefault()?.Text("fromName"));

        if (rows.Count > 0 || postmarkToken is not null || systemFromAddress is not null || systemFromName is not null)
        {
            using var command = new SqlCommand(
                """
                UPDATE [asap].[EmailSettings]
                SET [ProtectedServerToken] = @protectedServerToken,
                    [FromAddress] = @fromAddress, [FromName] = @fromName, [UpdatedUtc] = @updatedUtc
                WHERE [OrganizationId] = 1;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "@protectedServerToken",
                Db(postmarkToken is null ? null : credentialProtector!.Protect(postmarkToken)));
            command.Parameters.AddWithValue("@fromAddress", Db(systemFromAddress));
            command.Parameters.AddWithValue("@fromName", Db(systemFromName));
            AddDateTime2Parameter(command, "@updatedUtc", rows.SingleOrDefault()?.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
            if (rows.Count > 0)
            {
                transformations.Add(new
                {
                    entity = "email_settings",
                    sourceId = rows[0].RequiredString("id"),
                    transport = "legacy_smtp_transport_intentionally_dropped",
                    targetTransport = "target_email_sender_selected_by_external_configuration",
                    postmarkTokenProvisioned = postmarkToken is not null
                });
            }
        }

        // Legacy templates could carry a scoped sender. EmailSettings owns that
        // target concern, so preserve each independently resolved scope without
        // importing the legacy SMTP transport fields.
        foreach (var group in templateRows
                     .Where(item => string.Equals(item.String("scope"), "library", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(row => ResolveScopedOrganization(connection, transaction, row, organizationIds)))
        {
            var fromAddress = ResolveSenderValue(
                $"library:{group.Key}",
                "fromAddress",
                group.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromAddress"))));
            var fromName = ResolveSenderValue(
                $"library:{group.Key}",
                "fromName",
                group.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromName"))));
            if (fromAddress is null && fromName is null)
            {
                continue;
            }

            using var command = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId)
                    UPDATE [asap].[EmailSettings]
                    SET [FromAddress] = COALESCE(@fromAddress, [FromAddress]),
                        [FromName] = COALESCE(@fromName, [FromName]),
                        [UpdatedUtc] = @updatedUtc
                    WHERE [OrganizationId] = @organizationId;
                ELSE
                    INSERT INTO [asap].[EmailSettings]
                        ([OrganizationId], [FromAddress], [FromName], [UpdatedUtc])
                    VALUES (@organizationId, @fromAddress, @fromName, @updatedUtc);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", group.Key);
            command.Parameters.AddWithValue("@fromAddress", Db(fromAddress));
            command.Parameters.AddWithValue("@fromName", Db(fromName));
            AddDateTime2Parameter(command, "@updatedUtc", group.Select(row => row.UtcDateTime("updated"))
                .Where(value => value.HasValue)
                .OrderBy(value => value)
                .FirstOrDefault() ?? exportedAtUtc);
            command.ExecuteNonQuery();
            transformations.Add(new
            {
                entity = "email_template_sender",
                sourceIds = group.Select(row => row.RequiredString("id")).Order(StringComparer.Ordinal).ToArray(),
                organizationId = group.Key,
                fromAddress,
                fromName,
                disposition = "moved_to_scoped_email_settings"
            });
        }
        importedCounts["smtp_settings"] = rows.Count;
    }

    private static string? ResolveSenderValue(
        string scope,
        string field,
        IEnumerable<SenderCandidate> candidates)
    {
        var meaningful = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
            .Select(candidate => new SenderCandidate(candidate.SourceId, candidate.Value!.Trim()))
            .ToArray();
        var distinct = meaningful
            .Select(candidate => candidate.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length > 1)
        {
            var sources = meaningful
                .Select(candidate => $"{candidate.SourceId}={candidate.Value}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            throw new MigrationOperationException(
                "email_sender_ambiguous",
                $"The {scope} {field} sender has competing populated values: {string.Join(", ", sources)}.");
        }
        return distinct.SingleOrDefault();
    }

    private static string? MeaningfulSenderText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ImportWorkflowSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> templateIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts)
    {
        var rows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        foreach (var row in rows.OrderBy(item => string.Equals(item.RequiredString("scope").Trim(), "system", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var organizationId = ResolveScopedOrganization(connection, transaction, row, organizationIds);
            var isSystem = organizationId == 1;
            var timeoutTemplate = row.String("outstandingTimeoutRejectionTemplate");
            long? timeoutTemplateId = timeoutTemplate is null
                ? null
                : templateIds.TryGetValue(timeoutTemplate, out var mappedTemplate)
                    ? mappedTemplate
                    : throw new MigrationOperationException("workflow_template_unresolved", "A workflow rejection template reference cannot be resolved.");
            using var command = new SqlCommand(
                isSystem
                    ? """
                      UPDATE [asap].[WorkflowSettings]
                      SET [SuggestionLimit] = @suggestionLimit, [SuggestionLimitMessage] = @suggestionLimitMessage,
                          [OutstandingTimeoutEnabled] = @outstandingEnabled, [OutstandingTimeoutDays] = @outstandingDays,
                          [OutstandingTimeoutSendEmail] = @outstandingEmail, [OutstandingTimeoutRejectionTemplateId] = @timeoutTemplateId,
                          [HoldPickupTimeoutEnabled] = @holdPickupEnabled, [HoldPickupTimeoutDays] = @holdPickupDays,
                          [PendingHoldTimeoutEnabled] = @pendingHoldEnabled, [PendingHoldTimeoutDays] = @pendingHoldDays,
                          [AdditionalCopyTimeoutEnabled] = @additionalCopyEnabled, [AdditionalCopyTimeoutDays] = @additionalCopyDays,
                          [AutoPromote] = @autoPromote, [CommonAuthorsEnabled] = @commonAuthorsEnabled,
                          [CommonAuthorsLabel] = @commonAuthorsLabel, [CommonAuthorsHelp] = @commonAuthorsHelp,
                          [CommonAuthorsMessage] = @commonAuthorsMessage,
                          [AllowPatronAutoholdOptOut] = @allowOptOut, [AllowAnyRegisteredCardLogin] = @allowAnyCard,
                          [PatronCodeEligibilityEnabled] = @patronCodeEnabled, [PatronCodeEligibilityMessage] = @patronCodeMessage,
                          [UpdatedUtc] = @updatedUtc
                      WHERE [OrganizationId] = 1;
                      """
                    : """
                      INSERT INTO [asap].[WorkflowSettings]
                          ([OrganizationId], [SuggestionLimit], [SuggestionLimitMessage],
                           [OutstandingTimeoutEnabled], [OutstandingTimeoutDays], [OutstandingTimeoutSendEmail],
                           [OutstandingTimeoutRejectionTemplateId], [HoldPickupTimeoutEnabled], [HoldPickupTimeoutDays],
                           [PendingHoldTimeoutEnabled], [PendingHoldTimeoutDays], [AdditionalCopyTimeoutEnabled],
                           [AdditionalCopyTimeoutDays], [AutoPromote], [CommonAuthorsEnabled], [CommonAuthorsLabel],
                           [CommonAuthorsHelp], [CommonAuthorsMessage], [AllowPatronAutoholdOptOut],
                           [AllowAnyRegisteredCardLogin], [PatronCodeEligibilityEnabled], [PatronCodeEligibilityMessage], [UpdatedUtc])
                      VALUES
                          (@organizationId, @suggestionLimit, @suggestionLimitMessage,
                           @outstandingEnabled, @outstandingDays, @outstandingEmail,
                           @timeoutTemplateId, @holdPickupEnabled, @holdPickupDays,
                           @pendingHoldEnabled, @pendingHoldDays, @additionalCopyEnabled,
                           @additionalCopyDays, @autoPromote, @commonAuthorsEnabled, @commonAuthorsLabel,
                           @commonAuthorsHelp, @commonAuthorsMessage, @allowOptOut,
                           @allowAnyCard, @patronCodeEnabled, @patronCodeMessage, @updatedUtc);
                      """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@suggestionLimit", Db(isSystem ? row.Int32("suggestionLimit") ?? 5 : row.Int32("suggestionLimit")));
            command.Parameters.AddWithValue("@suggestionLimitMessage", Db(WorkflowScopedText(row, "suggestionLimitMessage", isSystem)));
            command.Parameters.AddWithValue("@outstandingEnabled", Db(Bool(row, "outstandingTimeoutEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@outstandingDays", Db(Int(row, "outstandingTimeoutDays", isSystem, 30)));
            command.Parameters.AddWithValue("@outstandingEmail", Db(Bool(row, "outstandingTimeoutSendEmail", isSystem, false)));
            command.Parameters.AddWithValue("@timeoutTemplateId", Db(timeoutTemplateId));
            command.Parameters.AddWithValue("@holdPickupEnabled", Db(Bool(row, "holdPickupTimeoutEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@holdPickupDays", Db(Int(row, "holdPickupTimeoutDays", isSystem, 14)));
            command.Parameters.AddWithValue("@pendingHoldEnabled", Db(Bool(row, "pendingHoldTimeoutEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@pendingHoldDays", Db(Int(row, "pendingHoldTimeoutDays", isSystem, 14)));
            command.Parameters.AddWithValue("@additionalCopyEnabled", Db(Bool(row, "additionalCopyTimeoutEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@additionalCopyDays", Db(Int(row, "additionalCopyTimeoutDays", isSystem, 14)));
            command.Parameters.AddWithValue("@autoPromote", Db(Bool(row, "autoPromote", isSystem, false)));
            command.Parameters.AddWithValue("@commonAuthorsEnabled", Db(Bool(row, "commonAuthorsEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@commonAuthorsLabel", Db(WorkflowScopedText(row, "commonAuthorsLabel", isSystem)));
            command.Parameters.AddWithValue("@commonAuthorsHelp", Db(WorkflowScopedText(row, "commonAuthorsHelp", isSystem)));
            command.Parameters.AddWithValue("@commonAuthorsMessage", Db(WorkflowScopedText(row, "commonAuthorsMessage", isSystem)));
            command.Parameters.AddWithValue("@allowOptOut", Db(Bool(row, "allowPatronAutoholdOptOut", isSystem, true)));
            command.Parameters.AddWithValue("@allowAnyCard", Db(Bool(row, "allowAnyRegisteredCardLogin", isSystem, false)));
            command.Parameters.AddWithValue("@patronCodeEnabled", Db(Bool(row, "patronCodeEligibilityEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@patronCodeMessage", Db(WorkflowScopedText(row, "patronCodeEligibilityMessage", isSystem)));
            AddDateTime2Parameter(command, "@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();

            ReplaceCommonCreators(connection, transaction, organizationId, row.Text("commonAuthorsList"), isSystem);
            ReplacePatronCodes(connection, transaction, organizationId, row.Text("allowedPatronCodeIds"), isSystem);
            ImportExternalSearch(connection, transaction, organizationId, row, isSystem);
        }
        importedCounts["workflow_settings"] = rows.Count;
    }

    private static void ReplaceCommonCreators(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? sourceValue,
        bool isSystem)
    {
        var values = SplitLegacyCommonCreatorLines(sourceValue);
        if (!isSystem && values.Count == 0)
        {
            return;
        }

        EnsureSet(connection, transaction, "CommonCreatorSet", organizationId);
        Execute(connection, transaction, "DELETE FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = @organizationId;", organizationId);
        for (var index = 0; index < values.Count; index++)
        {
            using var insert = new SqlCommand(
                "INSERT INTO [asap].[CommonCreatorTerm] ([OrganizationId], [Value], [SortOrder]) VALUES (@organizationId, @value, @sortOrder);",
                connection,
                transaction);
            insert.Parameters.AddWithValue("@organizationId", organizationId);
            insert.Parameters.AddWithValue("@value", values[index]);
            insert.Parameters.AddWithValue("@sortOrder", (index + 1) * 10);
            insert.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<int> ParsePatronCodeIds(string? value)
    {
        if (value is null)
        {
            return [];
        }
        var ids = new HashSet<int>();
        foreach (var raw in value.Split(','))
        {
            var token = TrimLegacyConfigurationText(raw);
            if (token.Length == 0)
            {
                continue;
            }
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                !string.Equals(token, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                throw new MigrationOperationException(
                    "source_patron_code_invalid",
                    "Each allowed patron-code ID must be a canonical positive Int32 that the target can preserve exactly.");
            }
            ids.Add(id);
        }
        return ids.Order().ToArray();
    }

    private static void ReplacePatronCodes(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? sourceValue,
        bool isSystem)
    {
        var values = ParsePatronCodeIds(sourceValue);
        if (!isSystem && values.Count == 0)
        {
            return;
        }
        EnsureSet(connection, transaction, "PatronCodeEligibilitySet", organizationId);
        Execute(connection, transaction, "DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId;", organizationId);
        foreach (var value in values.Distinct())
        {
            using var insert = new SqlCommand(
                "INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId]) VALUES (@organizationId, @value);",
                connection,
                transaction);
            insert.Parameters.AddWithValue("@organizationId", organizationId);
            insert.Parameters.AddWithValue("@value", value);
            insert.ExecuteNonQuery();
        }
    }

    private static void ImportExternalSearch(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        bool isSystem)
    {
        var defaults = new[]
        {
            (true, "Search Amazon", "https://www.amazon.com/s?k={{title}}"),
            (true, "Search Goodreads", "https://www.goodreads.com/search?q={{title}}"),
            (true, "Search WorldCat", "https://www.worldcat.org/search?q={{title}}"),
            (false, "", "")
        };
        for (var slot = 1; slot <= 4; slot++)
        {
            var enabledField = $"externalSearch{slot}Enabled";
            var labelField = $"externalSearch{slot}Label";
            var urlField = $"externalSearch{slot}UrlTemplate";
            if (!isSystem && !HasScopedWorkflowValue(row, enabledField) &&
                !HasScopedWorkflowValue(row, labelField) && !HasScopedWorkflowValue(row, urlField))
            {
                continue;
            }

            var providerKey = $"external_search_{slot}";
            long providerId;
            using (var lookup = new SqlCommand(
                       "SELECT [Id] FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = @key;",
                       connection,
                       transaction))
            {
                lookup.Parameters.AddWithValue("@key", providerKey);
                var value = lookup.ExecuteScalar();
                if (value is null or DBNull)
                {
                    throw new MigrationOperationException(
                        "external_provider_seed_missing",
                        $"Target external-search provider {providerKey} is missing from the permitted static seeds.");
                }
                providerId = Convert.ToInt64(value);
            }

            using var command = new SqlCommand(
                isSystem
                    ? """
                      UPDATE [asap].[ExternalSearchProvider]
                      SET [IsEnabled] = @enabled, [Label] = @label, [UrlTemplate] = @url
                      WHERE [Id] = @providerId;
                      """
                    : """
                      IF EXISTS
                      (
                          SELECT 1 FROM [asap].[ExternalSearchProviderOverride]
                          WHERE [LibraryOrganizationId] = @organizationId AND [ExternalSearchProviderId] = @providerId
                      )
                          UPDATE [asap].[ExternalSearchProviderOverride]
                          SET [IsEnabled] = @enabled, [Label] = @label, [UrlTemplate] = @url
                          WHERE [LibraryOrganizationId] = @organizationId AND [ExternalSearchProviderId] = @providerId;
                      ELSE
                          INSERT INTO [asap].[ExternalSearchProviderOverride]
                              ([LibraryOrganizationId], [ExternalSearchProviderId], [IsEnabled], [Label], [UrlTemplate])
                          VALUES (@organizationId, @providerId, @enabled, @label, @url);
                      """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@providerId", providerId);
            command.Parameters.AddWithValue("@enabled", Db(isSystem ? row.Bool(enabledField, defaults[slot - 1].Item1) : row.NullableBool(enabledField)));
            command.Parameters.AddWithValue("@label", Db(ExternalSearchText(row, labelField, isSystem, defaults[slot - 1].Item2)));
            command.Parameters.AddWithValue("@url", Db(ExternalSearchText(row, urlField, isSystem, defaults[slot - 1].Item3)));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new MigrationOperationException(
                    "external_provider_seed_missing",
                    $"Target external-search provider {providerKey} could not be reconciled.");
            }
        }
    }

    // Match the pinned hasScopedWorkflowValue check while retaining the original text for storage.
    private static bool HasScopedWorkflowValue(SourceRow row, string field) =>
        HasLegacyConfigurationText(row.Text(field));

    private static string? WorkflowScopedText(SourceRow row, string field, bool isSystem)
    {
        var value = row.Text(field);
        return isSystem || HasPinnedWorkflowText(value) ? value : null;
    }

    private static bool HasPinnedWorkflowText(string? value) =>
        value is not null && TrimLegacyConfigurationText(value).Length > 0;

    private static string? UnrepresentableWorkflowTextField(SourceRow row)
    {
        if (!string.Equals(
                row.RequiredString("scope").Trim(),
                "library",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var field in ScopedWorkflowTextFields)
        {
            var value = row.Text(field);
            if (HasPinnedWorkflowText(value) && string.IsNullOrWhiteSpace(value))
            {
                return field;
            }
        }
        return null;
    }

    private static string? ExternalSearchText(SourceRow row, string field, bool isSystem, string defaultValue)
    {
        var value = row.Text(field);
        if (isSystem)
        {
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }

        return HasScopedWorkflowValue(row, field) ? value : null;
    }

    private static void ImportPatronConfiguration(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        DateTime exportedAtUtc,
        IDictionary<string, int> importedCounts,
        ICollection<object> transformations)
    {
        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        var resolvedUiRows = new List<(SourceRow Row, int OrganizationId)>(uiRows.Count);
        foreach (var row in uiRows.OrderBy(item => string.Equals(item.RequiredString("scope").Trim(), "system", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            var organizationId = ResolveScopedOrganization(connection, transaction, row, organizationIds);
            resolvedUiRows.Add((row, organizationId));
            UpsertPatronSettings(connection, transaction, organizationId, row, exportedAtUtc);
            if (organizationId != 1)
            {
                var ignoredFields = LegacyUiDuplicateLabelFields
                    .Concat(["systemNotEnabledMessage", "publicationOptions"])
                    .Where(row.HasValue)
                    .ToArray();
                if (ignoredFields.Length > 0)
                {
                    transformations.Add(new
                    {
                        entity = "patron_duplicate_labels",
                        sourceCollection = "ui_settings",
                        sourceId = row.String("id"),
                        organizationId,
                        disposition = "ignored_library_ui_fields_not_effective_at_pinned_source",
                        populatedFields = ignoredFields
                    });
                }
            }
            if (organizationId == 1)
            {
                using var updateSystem = new SqlCommand(
                    "UPDATE [asap].[SystemSettings] SET [SystemNotEnabledMessage] = COALESCE(@message, [SystemNotEnabledMessage]), [UpdatedUtc] = @updatedUtc WHERE [OrganizationId] = 1;",
                    connection,
                    transaction);
                updateSystem.Parameters.AddWithValue("@message", Db(row.Text("systemNotEnabledMessage")));
                AddDateTime2Parameter(updateSystem, "@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
                updateSystem.ExecuteNonQuery();
            }
            if (organizationId == 1)
            {
                ReplacePublicationOptions(connection, transaction, organizationId, row.Text("publicationOptions"));
            }
            UpsertBrandingAlt(connection, transaction, organizationId, row.Text("logoAlt"), row.UtcDateTime("updated") ?? exportedAtUtc);
        }

        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var modernOverrideOrganizations = new HashSet<int>();
        var modernOverrideMessageFieldsByOrganization = new Dictionary<int, HashSet<string>>();
        foreach (var row in overrides.OrderBy(item => item.RequiredString("orgId"), StringComparer.Ordinal))
        {
            var organizationId = row.Int32("orgId") ?? throw new MigrationOperationException("patron_override_org_invalid", "A patron settings override has no organization.");
            if (organizationId == 1 ||
                !organizationIds.Values.Contains(organizationId) ||
                !OrganizationIsLibrary(connection, transaction, organizationId))
            {
                throw new MigrationOperationException(
                    "patron_override_org_invalid",
                    "A patron override references an unknown or system organization.");
            }
            if (row.Text("ebookMessage") is { Length: > 0 } || row.Text("eaudiobookMessage") is { Length: > 0 })
            {
                if (!modernOverrideMessageFieldsByOrganization.TryGetValue(organizationId, out var fields))
                {
                    fields = new HashSet<string>(StringComparer.Ordinal);
                    modernOverrideMessageFieldsByOrganization.Add(organizationId, fields);
                }
                if (row.Text("ebookMessage") is { Length: > 0 })
                {
                    fields.Add("ebookMessage");
                }
                if (row.Text("eaudiobookMessage") is { Length: > 0 })
                {
                    fields.Add("eaudiobookMessage");
                }
            }
            modernOverrideOrganizations.Add(organizationId);
            EnsurePatronSettings(connection, transaction, organizationId, row.UtcDateTime("updated") ?? exportedAtUtc);
            ApplyPatronOverride(connection, transaction, organizationId, row, exportedAtUtc);
            ReplacePublicationOptions(connection, transaction, organizationId, row.Text("publicationOptions"));
            ImportCustomFields(connection, transaction, package, organizationId, row, organizationIds, transformations);
        }

        foreach (var (row, organizationId) in resolvedUiRows.Where(item => item.OrganizationId != 1))
        {
            var fields = PatronUiInheritanceFields(
                row,
                modernOverrideMessageFieldsByOrganization.GetValueOrDefault(organizationId));
            if (fields.Length == 0)
            {
                continue;
            }

            transformations.Add(new
            {
                entity = "patron_ui_inheritance",
                sourceCollection = "ui_settings",
                sourceId = row.RequiredText("id"),
                organizationId,
                affectedFields = fields,
                disposition = "blank_library_ui_fields_inherit_system_value"
            });
        }

        var legacyPatronRows = MigrationPackageReader.ReadRows(
            package,
            "patron-settings.json",
            "patron_library_settings");
        foreach (var row in legacyPatronRows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var organizationId = ResolveLibraryOrganization(connection, transaction, row, "libraryOrganization", organizationIds);
            var modernOverridePresent = modernOverrideOrganizations.Contains(organizationId);
            if (!modernOverridePresent)
            {
                EnsurePatronSettings(connection, transaction, organizationId, row.UtcDateTime("updated") ?? exportedAtUtc);
                ApplyLegacyDuplicateLabels(connection, transaction, organizationId, row, exportedAtUtc);
            }

            transformations.Add(new
            {
                entity = "patron_duplicate_labels",
                sourceCollection = "patron_library_settings",
                sourceId = row.String("id"),
                organizationId,
                disposition = modernOverridePresent
                    ? "ignored_modern_override_present"
                    : "applied_legacy_fallback"
            });
        }

        var dormantLibrarySettings = MigrationPackageReader.ReadRows(
            package,
            "patron-settings.json",
            "library_settings");
        foreach (var row in dormantLibrarySettings.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var organizationId = ResolveLibraryOrganization(connection, transaction, row, "libraryOrganization", organizationIds);
            transformations.Add(new
            {
                entity = "legacy_library_branding",
                sourceCollection = "library_settings",
                sourceId = row.String("id"),
                organizationId,
                disposition = "intentionally_dropped_not_effective_at_pinned_source",
                populatedFields = new[] { "logo", "logoAlt" }.Where(row.HasValue).ToArray()
            });
        }
        importedCounts["ui_settings"] = uiRows.Count;
        importedCounts["patron_settings_overrides"] = overrides.Count;
        importedCounts["patron_library_settings"] = legacyPatronRows.Count;
        importedCounts["library_settings"] = dormantLibrarySettings.Count;
    }

    private static string[] PatronUiInheritanceFields(SourceRow row, IReadOnlySet<string>? modernOverrideMessageFields) =>
        PatronTextFields
            .Where(field => string.IsNullOrWhiteSpace(row.Text(field.Source)) &&
                modernOverrideMessageFields?.Contains(field.Source) != true)
            .Select(field => field.Source)
            .ToArray();

    internal static MigrationConfigurationReconciliation Reconcile(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        IReadOnlyDictionary<string, long> templateIds,
        DateTime exportedAtUtc,
        bool postmarkTokenProvisioned)
    {
        var counter = new ReconciliationCounter();
        ReconcileSystemSettings(connection, transaction, package, counter);
        ReconcilePolarisSettings(connection, transaction, package, counter);
        ReconcileEmailSettings(connection, transaction, package, organizationIds, postmarkTokenProvisioned, counter);
        ReconcileWorkflowSettings(connection, transaction, package, organizationIds, templateIds, counter);
        ReconcilePatronSettings(connection, transaction, package, organizationIds, formatIds, exportedAtUtc, counter);
        ReconcileEmailTemplates(connection, transaction, package, organizationIds, templateIds, counter);
        ReconcileMaterialFormats(
            connection,
            transaction,
            package,
            organizationIds,
            formatIds,
            exportedAtUtc,
            counter);
        return new(counter.Rows, counter.Fields, counter.Relationships);
    }

    private sealed class ReconciliationCounter
    {
        public int Rows { get; set; }
        public int Fields { get; set; }
        public int Relationships { get; set; }
    }

    private static void ReconcileSystemSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        ReconciliationCounter counter)
    {
        var rows = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings");
        var row = rows.SingleOrDefault();
        var runtime = MigrationPackageReader.ReadMetadata(package, "effective-legacy-runtime-config.json");
        var settings = runtime.GetProperty("settings");
        var expectedStaffUrl = settings.GetProperty("StaffApplicationUrl").GetProperty("value").GetString();
        var expectedIconPattern = settings.GetProperty("MaterialTypeIconUrlPattern").GetProperty("value").GetString();
        using var command = new SqlCommand(
            "SELECT [StaffApplicationUrl], [LeapBibUrlPattern], [LeapPatronUrlPattern], [MaterialTypeIconUrlPattern], [SystemNotEnabledMessage], [MisconfiguredMessage] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        EnsureConfiguration(reader.Read(), "system settings");
        EnsureConfiguration(
            StringEquals(reader, 0, expectedStaffUrl) &&
            StringEquals(reader, 1, row?.Text("leapBibUrlPattern")) &&
            StringEquals(reader, 2, LegacyEffectivePatronPattern(row?.Text("leapPatronUrlPattern"))) &&
            StringEquals(reader, 3, expectedIconPattern),
            "system settings");
        counter.Rows++;
        counter.Fields += 4;

        if (row?.HasValue("systemNotEnabledMessage") == true)
        {
            EnsureConfiguration(StringEquals(reader, 4, row.Text("systemNotEnabledMessage")), "system not-enabled message");
            counter.Fields++;
        }
        EnsureConfiguration(!reader.IsDBNull(5) && reader.GetString(5).Length > 0, "system misconfigured message");

        var expectedOrigins = SplitLegacyEmbedOrigins(row?.Text("patronEmbedAllowedOrigins"))
            .Select(NormalizeEmbedOrigin)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        reader.Close();
        using var originsCommand = new SqlCommand(
            "SELECT [NormalizedOrigin] FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;",
            connection,
            transaction);
        using var originsReader = originsCommand.ExecuteReader();
        var actualOrigins = new List<string>();
        while (originsReader.Read())
        {
            actualOrigins.Add(originsReader.GetString(0));
        }
        actualOrigins.Sort(StringComparer.Ordinal);

        EnsureConfiguration(expectedOrigins.SequenceEqual(actualOrigins, StringComparer.Ordinal), "patron embed origins");
        counter.Fields += expectedOrigins.Length;
    }

    private static void ReconcilePolarisSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        ReconciliationCounter counter)
    {
        var row = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings").SingleOrDefault();
        if (row is null)
        {
            return;
        }

        using var command = new SqlCommand(
            "SELECT [Host], [AccessId], [ProtectedApiKey], [StaffDomain], [AdminUser], [ProtectedAdminPassword], [WorkstationId], [SystemPolarisUserId] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        EnsureConfiguration(reader.Read(), "Polaris settings");
        EnsureConfiguration(
            StringEquals(reader, 0, LegacyPolarisHost(row.Text("host"))) &&
            StringEquals(reader, 1, LegacyPolarisAccessId(row.Text("accessId"))) &&
            ProtectedPresenceEquals(reader, 2, row.Text("apiKey") is { Length: > 0 }) &&
            StringEquals(reader, 3, row.Text("staffDomain")) &&
            StringEquals(reader, 4, row.Text("adminUser")) &&
            ProtectedPresenceEquals(reader, 5, row.Text("adminPassword") is { Length: > 0 }) &&
            IntEquals(reader, 6, LegacyPolarisIdentity(row, "workstationId")) &&
            IntEquals(reader, 7, LegacyPolarisIdentity(row, "userId")),
            "Polaris settings");
        counter.Rows++;
        counter.Fields += 8;
    }

    private static void ReconcileEmailSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        bool postmarkTokenProvisioned,
        ReconciliationCounter counter)
    {
        var smtpRows = MigrationPackageReader.ReadRows(package, "email-settings.json", "smtp_settings");
        var templateRows = MigrationPackageReader.ReadRowsOrEmpty(package, "email-templates.json", "email_templates");
        var systemTemplates = templateRows
            .Where(row => string.Equals(row.String("scope"), "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedAddress = ResolveSenderValue(
            "system templates",
            "fromAddress",
            systemTemplates.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromAddress"))))
            ?? MeaningfulSenderText(smtpRows.SingleOrDefault()?.Text("fromAddress"));
        var expectedName = ResolveSenderValue(
            "system templates",
            "fromName",
            systemTemplates.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromName"))))
            ?? MeaningfulSenderText(smtpRows.SingleOrDefault()?.Text("fromName"));

        using (var command = new SqlCommand(
                   "SELECT [ProtectedServerToken], [FromAddress], [FromName] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;",
                   connection,
                   transaction))
        {
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "system email settings");
            EnsureConfiguration(
                ProtectedPresenceEquals(reader, 0, postmarkTokenProvisioned) &&
                StringEquals(reader, 1, expectedAddress) &&
                StringEquals(reader, 2, expectedName),
                "system email settings");
            counter.Rows++;
            counter.Fields += 3;
        }

        foreach (var group in templateRows
                     .Where(row => string.Equals(row.String("scope"), "library", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(row => ResolveScopedOrganization(connection, transaction, row, organizationIds)))
        {
            var expectedAddressForLibrary = ResolveSenderValue(
                $"library:{group.Key}",
                "fromAddress",
                group.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromAddress"))));
            var expectedNameForLibrary = ResolveSenderValue(
                $"library:{group.Key}",
                "fromName",
                group.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromName"))));
            if (expectedAddressForLibrary is null && expectedNameForLibrary is null)
            {
                continue;
            }

            using var command = new SqlCommand(
                "SELECT [FromAddress], [FromName] FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", group.Key);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "library email settings");
            EnsureConfiguration(
                StringEquals(reader, 0, expectedAddressForLibrary) &&
                StringEquals(reader, 1, expectedNameForLibrary),
                "library email settings");
            counter.Rows++;
            counter.Fields += 2;
        }
    }

    private static void ReconcileWorkflowSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> templateIds,
        ReconciliationCounter counter)
    {
        var rows = MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings");
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var organizationId = ResolveScopedOrganization(connection, transaction, row, organizationIds);
            var isSystem = organizationId == 1;
            var sourceTemplate = row.String("outstandingTimeoutRejectionTemplate");
            long? expectedTemplateId = sourceTemplate is null
                ? null
                : templateIds.TryGetValue(sourceTemplate, out var mappedTemplate)
                    ? mappedTemplate
                    : throw new MigrationOperationException(
                        "workflow_template_unresolved",
                        "A workflow rejection template reference cannot be reconciled.");
            using var command = new SqlCommand(
                "SELECT [SuggestionLimit], [SuggestionLimitMessage], [OutstandingTimeoutEnabled], [OutstandingTimeoutDays], [OutstandingTimeoutSendEmail], [OutstandingTimeoutRejectionTemplateId], [HoldPickupTimeoutEnabled], [HoldPickupTimeoutDays], [PendingHoldTimeoutEnabled], [PendingHoldTimeoutDays], [AdditionalCopyTimeoutEnabled], [AdditionalCopyTimeoutDays], [AutoPromote], [CommonAuthorsEnabled], [CommonAuthorsLabel], [CommonAuthorsHelp], [CommonAuthorsMessage], [AllowPatronAutoholdOptOut], [AllowAnyRegisteredCardLogin], [PatronCodeEligibilityEnabled], [PatronCodeEligibilityMessage] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organizationId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            bool matches;
            using (var reader = command.ExecuteReader())
            {
                EnsureConfiguration(reader.Read(), "workflow settings");
                matches =
                    IntEquals(reader, 0, isSystem ? row.Int32("suggestionLimit") ?? 5 : row.Int32("suggestionLimit")) &&
                    StringEquals(reader, 1, WorkflowScopedText(row, "suggestionLimitMessage", isSystem)) &&
                    BoolEquals(reader, 2, Bool(row, "outstandingTimeoutEnabled", isSystem, false)) &&
                    IntEquals(reader, 3, Int(row, "outstandingTimeoutDays", isSystem, 30)) &&
                    BoolEquals(reader, 4, Bool(row, "outstandingTimeoutSendEmail", isSystem, false)) &&
                    LongEquals(reader, 5, expectedTemplateId) &&
                    BoolEquals(reader, 6, Bool(row, "holdPickupTimeoutEnabled", isSystem, false)) &&
                    IntEquals(reader, 7, Int(row, "holdPickupTimeoutDays", isSystem, 14)) &&
                    BoolEquals(reader, 8, Bool(row, "pendingHoldTimeoutEnabled", isSystem, false)) &&
                    IntEquals(reader, 9, Int(row, "pendingHoldTimeoutDays", isSystem, 14)) &&
                    BoolEquals(reader, 10, Bool(row, "additionalCopyTimeoutEnabled", isSystem, false)) &&
                    IntEquals(reader, 11, Int(row, "additionalCopyTimeoutDays", isSystem, 14)) &&
                    BoolEquals(reader, 12, Bool(row, "autoPromote", isSystem, false)) &&
                    BoolEquals(reader, 13, Bool(row, "commonAuthorsEnabled", isSystem, false)) &&
                    StringEquals(reader, 14, WorkflowScopedText(row, "commonAuthorsLabel", isSystem)) &&
                    StringEquals(reader, 15, WorkflowScopedText(row, "commonAuthorsHelp", isSystem)) &&
                    StringEquals(reader, 16, WorkflowScopedText(row, "commonAuthorsMessage", isSystem)) &&
                    BoolEquals(reader, 17, Bool(row, "allowPatronAutoholdOptOut", isSystem, true)) &&
                    BoolEquals(reader, 18, Bool(row, "allowAnyRegisteredCardLogin", isSystem, false)) &&
                    BoolEquals(reader, 19, Bool(row, "patronCodeEligibilityEnabled", isSystem, false)) &&
                    StringEquals(reader, 20, WorkflowScopedText(row, "patronCodeEligibilityMessage", isSystem));
            }
            EnsureConfiguration(matches, "workflow settings");
            counter.Rows++;
            counter.Fields += 21;

            ReconcileCommonCreatorSet(connection, transaction, organizationId, row.Text("commonAuthorsList"), isSystem, counter);
            ReconcilePatronCodeSet(connection, transaction, organizationId, row.Text("allowedPatronCodeIds"), isSystem, counter);
            ReconcileExternalSearch(connection, transaction, organizationId, row, isSystem, counter);
        }
    }

    private static void ReconcileCommonCreatorSet(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? rawValue,
        bool isSystem,
        ReconciliationCounter counter)
    {
        var expected = SplitLegacyCommonCreatorLines(rawValue);
        var expectedSet = isSystem || expected.Count > 0;
        using var setCommand = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = @organizationId;",
            connection,
            transaction);
        setCommand.Parameters.AddWithValue("@organizationId", organizationId);
        EnsureConfiguration(Convert.ToInt32(setCommand.ExecuteScalar()) == (expectedSet ? 1 : 0), "common creator set");
        using var command = new SqlCommand(
            "SELECT [Value] FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = @organizationId ORDER BY [SortOrder], [Id];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
        {
            actual.Add(reader.GetString(0));
        }

        EnsureConfiguration(expectedSet && expected.Count == 0 || expected.SequenceEqual(actual, StringComparer.Ordinal), "common creator set");
        counter.Relationships++;
        counter.Fields += expected.Count;
    }

    private static void ReconcilePatronCodeSet(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? rawValue,
        bool isSystem,
        ReconciliationCounter counter)
    {
        var expected = ParsePatronCodeIds(rawValue).Distinct().ToArray();
        var expectedSet = isSystem || expected.Length > 0;
        using var setCommand = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @organizationId;",
            connection,
            transaction);
        setCommand.Parameters.AddWithValue("@organizationId", organizationId);
        EnsureConfiguration(Convert.ToInt32(setCommand.ExecuteScalar()) == (expectedSet ? 1 : 0), "patron-code eligibility set");
        using var command = new SqlCommand(
            "SELECT [PatronCodeId] FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId ORDER BY [PatronCodeId];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        var actual = new List<int>();
        while (reader.Read())
        {
            actual.Add(reader.GetInt32(0));
        }
        EnsureConfiguration(expected.Order().SequenceEqual(actual), "patron-code eligibility set");
        counter.Relationships++;
        counter.Fields += expected.Length;
    }

    private static void ReconcileExternalSearch(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        bool isSystem,
        ReconciliationCounter counter)
    {
        var defaults = new[]
        {
            (Enabled: true, Label: "Search Amazon", Url: "https://www.amazon.com/s?k={{title}}"),
            (Enabled: true, Label: "Search Goodreads", Url: "https://www.goodreads.com/search?q={{title}}"),
            (Enabled: true, Label: "Search WorldCat", Url: "https://www.worldcat.org/search?q={{title}}"),
            (Enabled: false, Label: "", Url: "")
        };
        for (var slot = 1; slot <= 4; slot++)
        {
            var enabledField = $"externalSearch{slot}Enabled";
            var labelField = $"externalSearch{slot}Label";
            var urlField = $"externalSearch{slot}UrlTemplate";
            if (!isSystem && !HasScopedWorkflowValue(row, enabledField) &&
                !HasScopedWorkflowValue(row, labelField) && !HasScopedWorkflowValue(row, urlField))
            {
                continue;
            }

            var expectedEnabled = isSystem ? row.Bool(enabledField, defaults[slot - 1].Enabled) : row.NullableBool(enabledField);
            var expectedLabel = ExternalSearchText(row, labelField, isSystem, defaults[slot - 1].Label);
            var expectedUrl = ExternalSearchText(row, urlField, isSystem, defaults[slot - 1].Url);
            var providerKey = $"external_search_{slot}";
            using var command = new SqlCommand(
                isSystem
                    ? "SELECT [IsEnabled], [Label], [UrlTemplate] FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = @providerKey;"
                    : "SELECT o.[IsEnabled], o.[Label], o.[UrlTemplate] FROM [asap].[ExternalSearchProvider] p LEFT JOIN [asap].[ExternalSearchProviderOverride] o ON o.[ExternalSearchProviderId] = p.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE p.[ProviderKey] = @providerKey;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@providerKey", providerKey);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "external search provider");
            EnsureConfiguration(
                BoolEquals(reader, 0, expectedEnabled) &&
                StringEquals(reader, 1, expectedLabel) &&
                StringEquals(reader, 2, expectedUrl),
                "external search provider");
            counter.Rows++;
            counter.Fields += 3;
            counter.Relationships++;
        }
    }

    private static void ReconcilePatronSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        DateTime exportedAtUtc,
        ReconciliationCounter counter)
    {
        var expected = new Dictionary<int, Dictionary<string, string?>>();
        var uiRows = MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings");
        foreach (var row in uiRows)
        {
            var organizationId = ResolveScopedOrganization(connection, transaction, row, organizationIds);
            var values = GetExpectedPatronValues(expected, organizationId);
            AddPatronUiValues(values, row, organizationId == 1);
            if (organizationId == 1 && row.HasValue("systemNotEnabledMessage"))
            {
                using var command = new SqlCommand(
                    "SELECT [SystemNotEnabledMessage] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;",
                    connection,
                    transaction);
                using var reader = command.ExecuteReader();
                EnsureConfiguration(reader.Read() && StringEquals(reader, 0, row.Text("systemNotEnabledMessage")), "system not-enabled message");
                counter.Fields++;
            }
        }

        var publicationRows = new Dictionary<int, string?>();
        foreach (var row in uiRows)
        {
            var raw = row.Text("publicationOptions");
            if (!string.IsNullOrWhiteSpace(raw) &&
                ResolveScopedOrganization(connection, transaction, row, organizationIds) == 1)
            {
                publicationRows[1] = raw;
            }
        }

        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var modernOverrideOrganizations = new HashSet<int>();
        foreach (var row in overrides)
        {
            var organizationId = row.Int32("orgId") ?? throw new MigrationOperationException(
                "patron_override_org_invalid",
                "A patron settings override has no organization.");
            if (organizationId == 1 ||
                !organizationIds.Values.Contains(organizationId) ||
                !OrganizationIsLibrary(connection, transaction, organizationId))
            {
                throw new MigrationOperationException(
                    "patron_override_org_invalid",
                    "A patron settings override references an unknown or system organization.");
            }
            modernOverrideOrganizations.Add(organizationId);
            var values = GetExpectedPatronValues(expected, organizationId);
            AddPatronOverrideValues(values, row);
            var raw = row.Text("publicationOptions");
            if (!string.IsNullOrWhiteSpace(raw))
            {
                publicationRows[organizationId] = raw;
            }

            ReconcileCustomFields(
                connection,
                transaction,
                package,
                organizationIds,
                organizationId,
                row,
                counter);
            ReconcileFormatRules(
                connection,
                transaction,
                package,
                organizationIds,
                organizationId,
                row,
                counter);
        }

        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"))
        {
            var organizationId = ResolveLibraryOrganization(connection, transaction, row, "libraryOrganization", organizationIds);
            if (modernOverrideOrganizations.Contains(organizationId))
            {
                continue;
            }

            AddLegacyDuplicateLabelValues(GetExpectedPatronValues(expected, organizationId), row.JsonText("duplicateRequestStatusLabels"));
        }

        foreach (var item in expected.OrderBy(item => item.Key))
        {
            ReconcilePatronValueSet(connection, transaction, item.Key, item.Value, counter);
        }

        foreach (var item in publicationRows.OrderBy(item => item.Key))
        {
            ReconcilePublicationOptions(connection, transaction, item.Key, item.Value!, counter);
        }

        counter.Rows += uiRows.Count + overrides.Count +
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings").Count;
    }

    private static Dictionary<string, string?> GetExpectedPatronValues(
        IDictionary<int, Dictionary<string, string?>> expected,
        int organizationId)
    {
        if (!expected.TryGetValue(organizationId, out var values))
        {
            values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (_, target) in PatronTextFields)
            {
                values[target] = null;
            }

            foreach (var (_, target) in DuplicateLabelFields)
            {
                values[target] = null;
            }

            expected.Add(organizationId, values);
        }
        return values;
    }

    private static readonly (string Source, string Target)[] PatronTextFields =
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

    private static readonly (string Source, string Target)[] DuplicateLabelFields =
    [
        ("suggestion", "SuggestionStatusLabel"),
        ("outstanding_purchase", "OutstandingPurchaseStatusLabel"),
        ("pending_hold", "PendingHoldStatusLabel"),
        ("hold_placed", "HoldPlacedStatusLabel"),
        ("closed", "ClosedStatusLabel"),
        ("rejected", "RejectedStatusLabel"),
        ("hold_completed", "HoldCompletedStatusLabel"),
        ("hold_not_picked_up", "HoldNotPickedUpStatusLabel"),
        ("manual", "ManualStatusLabel"),
        ("silent", "SilentStatusLabel")
    ];

    private static void AddPatronUiValues(
        IDictionary<string, string?> values,
        SourceRow row,
        bool isSystem)
    {
        foreach (var (source, target) in PatronTextFields)
        {
            if (isSystem || row.HasValue(source))
            {
                values[target] = PatronUiScopedText(row, source, isSystem);
            }
        }
        if (isSystem)
        {
            AddDuplicateLabelValues(values, row, null);
        }
    }

    private static void AddPatronOverrideValues(
        IDictionary<string, string?> values,
        SourceRow row)
    {
        var ebookMessage = PinnedPatronMessageOverrideText(row, "ebookMessage");
        if (ebookMessage is not null)
        {
            values["EbookMessage"] = ebookMessage;
        }
        var eaudiobookMessage = PinnedPatronMessageOverrideText(row, "eaudiobookMessage");
        if (eaudiobookMessage is not null)
        {
            values["EaudiobookMessage"] = eaudiobookMessage;
        }
        AddDuplicateLabelValues(values, row, ParseDuplicateLabelObject(row.JsonText("duplicateStatusLabels"), modernOverride: true));
    }

    private static string? PinnedPatronMessageOverrideText(SourceRow row, string field)
    {
        var value = row.Text(field);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? UnrepresentablePatronMessageOverrideField(SourceRow row)
    {
        var ebookMessage = PinnedPatronMessageOverrideText(row, "ebookMessage");
        if (ebookMessage is not null && string.IsNullOrWhiteSpace(ebookMessage))
        {
            return "eBook";
        }

        var eaudiobookMessage = PinnedPatronMessageOverrideText(row, "eaudiobookMessage");
        return eaudiobookMessage is not null && string.IsNullOrWhiteSpace(eaudiobookMessage)
            ? "eAudiobook"
            : null;
    }

    private static void AddLegacyDuplicateLabelValues(
        IDictionary<string, string?> values,
        string? rawLabels)
    {
        AddDuplicateLabelValues(values, null, ParseDuplicateLabelObject(rawLabels, modernOverride: false));
    }

    private static void AddDuplicateLabelValues(
        IDictionary<string, string?> values,
        SourceRow? row,
        IReadOnlyDictionary<string, string>? labels)
    {
        foreach (var (source, target) in DuplicateLabelFields)
        {
            var labelKey = source == "silent" ? "Silently Closed" : source;
            var value = labels is not null && labels.TryGetValue(labelKey, out var label)
                ? label
                : row?.Text($"duplicateLabel{DuplicateLabelSuffix(source)}");
            values[target] = value;
        }
    }

    private static string DuplicateLabelSuffix(string source) => source switch
    {
        "suggestion" => "Suggestion",
        "outstanding_purchase" => "OutstandingPurchase",
        "pending_hold" => "PendingHold",
        "hold_placed" => "HoldPlaced",
        "closed" => "Closed",
        "rejected" => "Rejected",
        "hold_completed" => "HoldCompleted",
        "hold_not_picked_up" => "HoldNotPickedUp",
        "manual" => "Manual",
        "silent" => "Silent",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static void ReconcilePatronValueSet(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        IReadOnlyDictionary<string, string?> expected,
        ReconciliationCounter counter)
    {
        if (expected.Count == 0)
        {
            return;
        }

        using var command = new SqlCommand(
            "SELECT [PageTitle], [BarcodeLabel], [PinLabel], [LoginPrompt], [LoginNote], [SuggestionFormNote], [NoEmailMessage], [SuccessTitle], [SuccessMessage], [AlreadySubmittedMessage], [EbookMessage], [EaudiobookMessage], [SuggestionStatusLabel], [OutstandingPurchaseStatusLabel], [PendingHoldStatusLabel], [HoldPlacedStatusLabel], [ClosedStatusLabel], [RejectedStatusLabel], [HoldCompletedStatusLabel], [HoldNotPickedUpStatusLabel], [ManualStatusLabel], [SilentStatusLabel] FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        EnsureConfiguration(reader.Read(), "patron settings");
        var columns = new[]
        {
            "PageTitle", "BarcodeLabel", "PinLabel", "LoginPrompt", "LoginNote", "SuggestionFormNote",
            "NoEmailMessage", "SuccessTitle", "SuccessMessage", "AlreadySubmittedMessage", "EbookMessage",
            "EaudiobookMessage", "SuggestionStatusLabel", "OutstandingPurchaseStatusLabel",
            "PendingHoldStatusLabel", "HoldPlacedStatusLabel", "ClosedStatusLabel", "RejectedStatusLabel",
            "HoldCompletedStatusLabel", "HoldNotPickedUpStatusLabel", "ManualStatusLabel", "SilentStatusLabel"
        };
        var indexes = columns.Select((name, index) => (name, index)).ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        foreach (var item in expected)
        {
            EnsureConfiguration(StringEquals(reader, indexes[item.Key], item.Value), "patron settings");
            counter.Fields++;
        }
    }

    private static void ReconcilePublicationOptions(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string rawValue,
        ReconciliationCounter counter)
    {
        var options = ParsePublicationOptions(rawValue, isSystem: organizationId == 1);
        var expectedSet = organizationId == 1 || options.Count > 0;
        using var setCommand = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @organizationId;",
            connection,
            transaction);
        setCommand.Parameters.AddWithValue("@organizationId", organizationId);
        EnsureConfiguration(Convert.ToInt32(setCommand.ExecuteScalar()) == (expectedSet ? 1 : 0), "publication option set");
        using var command = new SqlCommand(
            "SELECT [OptionKey], [Label], [IsEnabled], [SortOrder] FROM [asap].[PublicationOption] WHERE [OrganizationId] = @organizationId ORDER BY [SortOrder], [Id];",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        var actual = new List<PublicationImportOption>();
        while (reader.Read())
        {
            actual.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetInt32(3)));
        }
        EnsureConfiguration(options.SequenceEqual(actual), "publication options");
        counter.Relationships++;
        counter.Fields += options.Count * 4;
    }

    private static void ReconcileCustomFields(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        int organizationId,
        SourceRow row,
        ReconciliationCounter counter)
    {
        var definitionsJson = row.JsonText("additionalFieldDefinitions");
        var formatRules = ParseRootObject(row.JsonText("patronFormatRules"));
        if (definitionsJson is null)
        {
            EnsureConfiguration(
                Scalar(connection, transaction, "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId;", organizationId) == 0,
                "custom fields");
            return;
        }

        using var definitions = JsonDocument.Parse(definitionsJson);
        if (definitions.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationOperationException("custom_fields_invalid", "Additional field definitions must be a JSON array.");
        }
        var definitionItems = NormalizeCustomFieldDefinitions(definitions.RootElement);
        EnsureConfiguration(
            Scalar(connection, transaction, "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId;", organizationId) == definitionItems.Count,
            "custom fields");
        foreach (var definition in definitionItems)
        {
            using var command = new SqlCommand(
                "SELECT [Id], [FieldType], [Label], [HelpText], [IsEnabled], [SortOrder] FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@fieldKey", definition.Key);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "custom field");
            var fieldId = reader.GetInt64(0);
            EnsureConfiguration(
                StringEquals(reader, 1, definition.Type) &&
                StringEquals(reader, 2, definition.Label) &&
                StringEquals(reader, 3, definition.HelpText) &&
                reader.GetBoolean(4) == definition.Enabled &&
                reader.GetInt32(5) == definition.SortOrder,
                "custom field");
            counter.Rows++;
            counter.Fields += 5;
            reader.Close();

            using var optionCount = new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @fieldId;",
                connection,
                transaction);
            optionCount.Parameters.AddWithValue("@fieldId", fieldId);
            EnsureConfiguration(Convert.ToInt32(optionCount.ExecuteScalar()) == definition.Options.Count, "custom field options");
            foreach (var option in definition.Options)
            {
                using var optionCommand = new SqlCommand(
                    "SELECT [Label], [IsEnabled], [SortOrder] FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @fieldId AND [OptionKey] = @optionKey;",
                    connection,
                    transaction);
                optionCommand.Parameters.AddWithValue("@fieldId", fieldId);
                optionCommand.Parameters.AddWithValue("@optionKey", option.Key);
                using var optionReader = optionCommand.ExecuteReader();
                EnsureConfiguration(optionReader.Read(), "custom field option");
                EnsureConfiguration(
                    StringEquals(optionReader, 0, option.Label) &&
                    optionReader.GetBoolean(1) == option.Enabled &&
                    optionReader.GetInt32(2) == option.SortOrder,
                    "custom field option");
                counter.Fields += 3;
                counter.Relationships++;
            }
        }

        var scopedFormats = ReadScopedFormats(connection, transaction, organizationId);
        var effectiveSourceCodes = ReadEffectiveSourceFormatCodes(
            connection,
            transaction,
            package,
            organizationId,
            organizationIds,
            scopedFormats);
        var effectiveFormats = scopedFormats
            .GroupBy(format => format.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                .ThenBy(format => format.Id)
                .First())
            .ToArray();
        var expectedRuleCount = effectiveFormats.Length * definitionItems.Count;
        EnsureConfiguration(
            Scalar(connection, transaction, "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId;", organizationId) == expectedRuleCount,
            "custom field format rules");
        foreach (var format in effectiveFormats)
        {
            foreach (var definition in definitionItems)
            {
                var fieldId = ReadCustomFieldId(connection, transaction, organizationId, definition.Key);
                var enabledOptionCount = definition.Options.Count(option => option.Enabled);
                var expectedRule = ResolveCustomFieldRule(
                    formatRules,
                    effectiveSourceCodes.GetValueOrDefault(format.Code, format.Code),
                    definition.Key,
                    definition.Type,
                    definition.Enabled,
                    enabledOptionCount);
                using var ruleCommand = new SqlCommand(
                    "SELECT [Mode], [LabelOverride] FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId AND [MaterialFormatId] = @formatId AND [PatronCustomFieldId] = @fieldId;",
                    connection,
                    transaction);
                ruleCommand.Parameters.AddWithValue("@organizationId", organizationId);
                ruleCommand.Parameters.AddWithValue("@formatId", format.Id);
                ruleCommand.Parameters.AddWithValue("@fieldId", fieldId);
                using var ruleReader = ruleCommand.ExecuteReader();
                EnsureConfiguration(ruleReader.Read(), "custom field format rule");
                EnsureConfiguration(
                    StringEquals(ruleReader, 0, expectedRule.Mode) &&
                    StringEquals(ruleReader, 1, expectedRule.LabelOverride),
                    "custom field format rule");
                counter.Relationships++;
                counter.Fields += 2;
            }
        }
    }

    private static void ReconcileFormatRules(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        int organizationId,
        SourceRow row,
        ReconciliationCounter counter)
    {
        var formatRules = ParseRootObject(row.JsonText("patronFormatRules"));
        if (formatRules is null)
        {
            return;
        }

        var scopedFormats = ReadScopedFormats(connection, transaction, organizationId);
        var effectiveSourceCodes = ReadEffectiveSourceFormatCodes(
            connection,
            transaction,
            package,
            organizationId,
            organizationIds,
            scopedFormats);
        var effectiveCodes = effectiveSourceCodes.Values.ToHashSet(StringComparer.Ordinal);
        var rulesByCode = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in formatRules.Value.EnumerateObject())
        {
            var sourceCode = property.Name;
            if (!effectiveCodes.Contains(sourceCode) && !string.Equals(sourceCode, "book", StringComparison.Ordinal))
            {
                continue;
            }
            if (!rulesByCode.TryAdd(sourceCode, property.Value))
            {
                throw new MigrationOperationException(
                    "format_rule_format_ambiguous",
                    $"More than one patron format rule resolves to {sourceCode}.");
            }
        }

        foreach (var target in EffectiveScopedFormats(scopedFormats, organizationId))
        {
            var sourceCode = effectiveSourceCodes.GetValueOrDefault(target.Code, target.Code);
            var expected = ResolveEffectiveFormatRule(sourceCode, rulesByCode);
            using var command = new SqlCommand(
                "SELECT COALESCE(o.[MessageBehavior], f.[MessageBehavior]), COALESCE(o.[Message], f.[Message]), COALESCE(o.[TitleMode], f.[TitleMode]), COALESCE(o.[TitleLabel], f.[TitleLabel]), COALESCE(o.[AuthorMode], f.[AuthorMode]), COALESCE(o.[AuthorLabel], f.[AuthorLabel]), COALESCE(o.[IdentifierMode], f.[IdentifierMode]), COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]), COALESCE(o.[PublicationMode], f.[PublicationMode]), COALESCE(o.[PublicationLabel], f.[PublicationLabel]) FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE f.[Id] = @formatId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@formatId", target.Id);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "material format rule");
            EnsureConfiguration(
                StringEquals(reader, 0, expected.MessageBehavior) &&
                (StringEquals(reader, 1, expected.Message) || expected.Message.Length == 0 && reader.IsDBNull(1)) &&
                StringEquals(reader, 2, expected.TitleMode) &&
                StringEquals(reader, 3, expected.TitleLabel) &&
                StringEquals(reader, 4, expected.AuthorMode) &&
                StringEquals(reader, 5, expected.AuthorLabel) &&
                StringEquals(reader, 6, expected.IdentifierMode) &&
                StringEquals(reader, 7, expected.IdentifierLabel) &&
                StringEquals(reader, 8, expected.PublicationMode) &&
                StringEquals(reader, 9, expected.PublicationLabel),
                "material format rule");
            counter.Rows++;
            counter.Fields += 10;
        }
    }

    private static long ReadCustomFieldId(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string fieldKey)
    {
        using var command = new SqlCommand(
            "SELECT [Id] FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@fieldKey", fieldKey);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? throw new MigrationOperationException("reconciliation_failed", "An imported custom field has no target identity.")
            : Convert.ToInt64(value);
    }

    private static int ResolveOrganizationId(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        string field,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var sourceValue = row.RequiredString(field);
        int organizationId;
        if (organizationIds.TryGetValue(sourceValue, out var mapped) && mapped != 1)
        {
            organizationId = mapped;
        }
        else if (int.TryParse(sourceValue, out organizationId) &&
                 organizationId != 1 &&
                 organizationIds.Values.Contains(organizationId))
        {
            // The native Organization row below determines whether the reference is a library.
        }
        else
        {
            throw new MigrationOperationException(
                "settings_organization_unresolved",
                $"Source organization reference {sourceValue} cannot be resolved.");
        }
        if (!OrganizationIsLibrary(connection, transaction, organizationId))
        {
            throw new MigrationOperationException(
                "settings_organization_unresolved",
                $"Source organization reference {sourceValue} is not a library.");
        }
        return organizationId;
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
            if (mapped.TryGetValue(explicitSourceId, out var targetId))
            {
                return targetId;
            }

            throw new MigrationOperationException(
                "email_template_source_unresolved",
                $"Email template {row.RequiredString("id")} has an unresolved source template.");
        }
        return isRejection ? null : FindSystemTemplateId(connection, transaction, templateKey);
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
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static string? NormalizeTemplateText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeFormatCode(string value) => TrimLegacyConfigurationText(value).ToLowerInvariant() switch
    {
        "0" => "book",
        "1" => "ebook",
        "2" => "audiobook_cd",
        "3" => "eaudiobook",
        "4" => "dvd",
        "5" => "music_cd",
        var code when code.Length > 0 => code,
        _ => throw new MigrationOperationException("format_code_invalid", "Material format code is blank.")
    };

    private static string? NormalizeOptionalEnum(
        string? value,
        IReadOnlyCollection<string> allowed,
        string errorCode)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        var canonical = allowed.FirstOrDefault(item =>
            string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        return canonical ?? throw new MigrationOperationException(errorCode, $"Unknown source value: {value}");
    }

    private static void ReconcileEmailTemplates(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> templateIds,
        ReconciliationCounter counter)
    {
        var sources = MigrationPackageReader.ReadRows(package, "email-templates.json", "email_templates")
            .Select(row => (Row: row, IsRejection: false))
            .Concat(MigrationPackageReader.ReadRows(package, "email-templates.json", "rejection_templates")
                .Select(row => (Row: row, IsRejection: true)))
            .OrderBy(item => item.Row.RequiredString("id"), StringComparer.Ordinal)
            .ToArray();
        foreach (var source in sources)
        {
            var row = source.Row;
            var sourceId = row.RequiredString("id");
            var scope = row.RequiredString("scope").Trim().ToLowerInvariant();
            var organizationId = scope switch
            {
                "system" => 1,
                "library" => ResolveOrganizationId(connection, transaction, row, "libraryOrganization", organizationIds),
                _ => throw new MigrationOperationException("email_template_scope_invalid", "An email template has an invalid scope.")
            };
            var templateKey = source.IsRejection
                ? "rejection:" + (scope == "library" ? row.String("sourceTemplateId") ?? sourceId : sourceId)
                : row.RequiredString("templateKey");
            var sourceTemplateId = scope == "library"
                ? ResolveSourceTemplateId(connection, transaction, row, source.IsRejection, templateKey, templateIds)
                : null;
            var isCustom = scope == "library" && sourceTemplateId is null;
            using var command = new SqlCommand(
                "SELECT t.[OrganizationId], t.[TemplateKey], t.[SourceTemplateId], t.[DisplayName], t.[SubjectTemplate], t.[BodyTemplate], t.[IsHidden], t.[IsCustom], t.[SortOrder] FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[EmailTemplate] t ON t.[Id] = m.[NewId] WHERE m.[EntityType] = N'email_template' AND m.[PocketBaseId] = @sourceId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "email template");
            EnsureConfiguration(
                reader.GetInt32(0) == organizationId &&
                StringEquals(reader, 1, templateKey) &&
                LongEquals(reader, 2, sourceTemplateId) &&
                StringEquals(reader, 3, row.Text("name")) &&
                StringEquals(reader, 4, NormalizeTemplateText(row.Text("subject"))) &&
                StringEquals(reader, 5, NormalizeTemplateText(row.Text("body"))) &&
                reader.GetBoolean(6) == !row.Bool("enabled", true) &&
                reader.GetBoolean(7) == isCustom &&
                reader.GetInt32(8) == (row.Int32("sortOrder") ?? 0),
                "email template");
            counter.Rows++;
            counter.Fields += 9;
            counter.Relationships++;
        }
    }

    private static void ReconcileMaterialFormats(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyDictionary<string, long> formatIds,
        DateTime exportedAtUtc,
        ReconciliationCounter counter)
    {
        var rows = MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats");
        var librariesWithRuleOverrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides")
            .Where(row => row.JsonText("patronFormatRules") is not null)
            .Select(row => row.Int32("orgId") ?? throw new MigrationOperationException(
                "patron_override_org_invalid",
                "A patron format rule snapshot has no library identity."))
            .ToHashSet();
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var scope = row.RequiredString("scope").Trim().ToLowerInvariant();
            var ownerId = scope switch
            {
                "system" => 1,
                "library" => ResolveOrganizationId(connection, transaction, row, "libraryOrganization", organizationIds),
                _ => throw new MigrationOperationException("format_scope_invalid", "A material format has an invalid scope.")
            };
            if (!formatIds.TryGetValue(sourceId, out var formatId))
            {
                throw new MigrationOperationException("reconciliation_failed", "An imported material format has no target identity.");
            }
            using var command = new SqlCommand(
                "SELECT f.[OwnerOrganizationId], f.[Code], COALESCE(o.[Label], f.[Label]), COALESCE(o.[SortOrder], f.[SortOrder]), COALESCE(o.[IsEnabled], f.[IsEnabled]), COALESCE(o.[MessageBehavior], f.[MessageBehavior]), COALESCE(o.[Message], f.[Message]), COALESCE(o.[TitleMode], f.[TitleMode]), COALESCE(o.[TitleLabel], f.[TitleLabel]), COALESCE(o.[AuthorMode], f.[AuthorMode]), COALESCE(o.[AuthorLabel], f.[AuthorLabel]), COALESCE(o.[IdentifierMode], f.[IdentifierMode]), COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]), COALESCE(o.[PublicationMode], f.[PublicationMode]), COALESCE(o.[PublicationLabel], f.[PublicationLabel]), f.[CreatedUtc], f.[UpdatedUtc], o.[Label], o.[SortOrder], o.[IsEnabled] FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE f.[Id] = @formatId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", ownerId);
            command.Parameters.AddWithValue("@formatId", formatId);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "material format");
            var targetOwnerId = reader.GetInt32(0);
            var sparseOverride = scope == "library" && targetOwnerId == 1;
            var ruleOverridePresent = scope == "library" && librariesWithRuleOverrides.Contains(ownerId);
            EnsureConfiguration(
                sparseOverride || targetOwnerId == ownerId,
                "material format ownership");
            var sourceCode = row.Text("code") ?? string.Empty;
            EnsureConfiguration(StringEquals(reader, 1, NormalizeFormatCode(sourceCode)), "material format code");
            var fields = 2;
            if (!sparseOverride)
            {
                var metadataMatches = StringEquals(reader, 2, LegacyMaterialFormatText(row.Text("label"), sourceCode)) &&
                    IntEquals(reader, 3, row.Int32("sortOrder") ?? 0) &&
                    BoolEquals(reader, 4, row.Bool("enabled", false)) &&
                    DateEquals(reader, 15, row.UtcDateTime("created") ?? exportedAtUtc) &&
                    DateEquals(reader, 16, row.UtcDateTime("updated") ?? exportedAtUtc);
                var formatRulesMatch = ruleOverridePresent || MatchesLegacyMaterialFormatRules(reader, 5, row);
                EnsureConfiguration(metadataMatches && formatRulesMatch, "material format");
                fields += ruleOverridePresent ? 5 : 14;
            }
            else
            {
                EnsureSparseFormatValue(reader, 17, row, value =>
                {
                    var label = value.Text("label");
                    return string.IsNullOrEmpty(label) ? null : label;
                }, "material format label");
                EnsureSparseFormatValue(reader, 18, row, value => value.Int32("sortOrder") ?? 0, "material format sort order");
                EnsureSparseFormatValue(reader, 19, row, value => value.Bool("enabled", false), "material format enabled");
                if (!ruleOverridePresent)
                {
                    EnsureConfiguration(MatchesLegacyMaterialFormatRules(reader, 5, row), "material format defaults");
                }
                var explicitMetadataFields = row.Names.Count(name => name is "label" or "sortOrder" or "enabled");
                fields += explicitMetadataFields + (ruleOverridePresent ? 0 : 9);
            }
            counter.Rows++;
            counter.Fields += fields;
            counter.Relationships++;
        }
    }

    private static void EnsureSparseFormatValue(
        SqlDataReader reader,
        int ordinal,
        SourceRow row,
        Func<SourceRow, object?> expectedValue,
        string entity)
    {
        var expected = expectedValue(row);
        var matches = expected switch
        {
            null => reader.IsDBNull(ordinal),
            int value => !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) == value,
            bool value => !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal) == value,
            _ => StringEquals(reader, ordinal, Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture))
        };
        EnsureConfiguration(matches, entity);
    }

    private static bool MatchesLegacyMaterialFormatRules(
        SqlDataReader reader,
        int start,
        SourceRow row) =>
        StringEquals(reader, start, NormalizeOptionalEnum(
            row.String("messageBehavior"),
            ["none", "message", "ebookMessage", "eaudiobookMessage"],
            "format_message_behavior_invalid") ?? "none") &&
        StringEquals(reader, start + 2, LegacyMaterialFormatTitleMode(row.String("titleMode"))) &&
        StringEquals(reader, start + 3, LegacyMaterialFormatText(row.Text("titleLabel"), "Title")) &&
        StringEquals(reader, start + 4, NormalizeOptionalEnum(
            row.String("authorMode"), ["required", "optional", "hidden"], "format_author_mode_invalid") ?? "required") &&
        StringEquals(reader, start + 5, LegacyMaterialFormatText(row.Text("authorLabel"), "Author")) &&
        StringEquals(reader, start + 6, NormalizeOptionalEnum(
            row.String("identifierMode"), ["required", "optional", "hidden"], "format_identifier_mode_invalid") ?? "optional") &&
        StringEquals(reader, start + 7, LegacyMaterialFormatText(row.Text("identifierLabel"), "Identifier number")) &&
        StringEquals(reader, start + 8, NormalizeOptionalEnum(
            row.String("publicationMode"), ["required", "optional", "hidden"], "format_publication_mode_invalid") ?? "required") &&
        StringEquals(reader, start + 9, LegacyMaterialFormatText(row.Text("publicationLabel"), "Publication Timing"));

    private static string LegacyMaterialFormatTitleMode(string? value)
    {
        _ = NormalizeOptionalEnum(value, ["required", "optional", "hidden"], "format_title_mode_invalid");
        return "required";
    }

    private static string LegacyMaterialFormatText(string? value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static void UpsertPatronSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        DateTime exportedAtUtc)
    {
        EnsurePatronSettings(connection, transaction, organizationId, row.UtcDateTime("updated") ?? exportedAtUtc);
        using var command = new SqlCommand(
            """
            UPDATE [asap].[PatronSettings]
            SET [PageTitle] = @pageTitle, [BarcodeLabel] = @barcodeLabel, [PinLabel] = @pinLabel,
                [LoginPrompt] = @loginPrompt, [LoginNote] = @loginNote, [SuggestionFormNote] = @suggestionFormNote,
                [NoEmailMessage] = @noEmailMessage, [SuccessTitle] = @successTitle,
                [SuccessMessage] = @successMessage, [AlreadySubmittedMessage] = @alreadySubmittedMessage,
                [EbookMessage] = @ebookMessage, [EaudiobookMessage] = @eaudiobookMessage,
                [SuggestionStatusLabel] = @suggestionLabel,
                [OutstandingPurchaseStatusLabel] = @outstandingLabel,
                [PendingHoldStatusLabel] = @pendingLabel, [HoldPlacedStatusLabel] = @placedLabel,
                [ClosedStatusLabel] = @closedLabel, [RejectedStatusLabel] = @rejectedLabel,
                [HoldCompletedStatusLabel] = @completedLabel, [HoldNotPickedUpStatusLabel] = @notPickedUpLabel,
                [ManualStatusLabel] = @manualLabel, [SilentStatusLabel] = @silentLabel,
                [UpdatedUtc] = @updatedUtc
            WHERE [OrganizationId] = @organizationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        var isSystem = organizationId == 1;
        command.Parameters.AddWithValue("@pageTitle", Db(PatronUiScopedText(row, "pageTitle", isSystem)));
        command.Parameters.AddWithValue("@barcodeLabel", Db(PatronUiScopedText(row, "barcodeLabel", isSystem)));
        command.Parameters.AddWithValue("@pinLabel", Db(PatronUiScopedText(row, "pinLabel", isSystem)));
        command.Parameters.AddWithValue("@loginPrompt", Db(PatronUiScopedText(row, "loginPrompt", isSystem)));
        command.Parameters.AddWithValue("@loginNote", Db(PatronUiScopedText(row, "loginNote", isSystem)));
        command.Parameters.AddWithValue("@suggestionFormNote", Db(PatronUiScopedText(row, "suggestionFormNote", isSystem)));
        command.Parameters.AddWithValue("@noEmailMessage", Db(PatronUiScopedText(row, "noEmailMessage", isSystem)));
        command.Parameters.AddWithValue("@successTitle", Db(PatronUiScopedText(row, "successTitle", isSystem)));
        command.Parameters.AddWithValue("@successMessage", Db(PatronUiScopedText(row, "successMessage", isSystem)));
        command.Parameters.AddWithValue("@alreadySubmittedMessage", Db(PatronUiScopedText(row, "alreadySubmittedMessage", isSystem)));
        command.Parameters.AddWithValue("@ebookMessage", Db(PatronUiScopedText(row, "ebookMessage", isSystem)));
        command.Parameters.AddWithValue("@eaudiobookMessage", Db(PatronUiScopedText(row, "eaudiobookMessage", isSystem)));
        AddDuplicateLabelParameters(command, isSystem ? row : null, null);
        AddDateTime2Parameter(command, "@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void ApplyPatronOverride(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        DateTime exportedAtUtc)
    {
        var labels = ParseDuplicateLabelObject(row.JsonText("duplicateStatusLabels"), modernOverride: true);
        using var command = new SqlCommand(
            """
            UPDATE [asap].[PatronSettings]
            SET [EbookMessage] = COALESCE(@ebookMessage, [EbookMessage]),
                [EaudiobookMessage] = COALESCE(@eaudiobookMessage, [EaudiobookMessage]),
                [SuggestionStatusLabel] = @suggestionLabel,
                [OutstandingPurchaseStatusLabel] = @outstandingLabel,
                [PendingHoldStatusLabel] = @pendingLabel, [HoldPlacedStatusLabel] = @placedLabel,
                [ClosedStatusLabel] = @closedLabel, [RejectedStatusLabel] = @rejectedLabel,
                [HoldCompletedStatusLabel] = @completedLabel, [HoldNotPickedUpStatusLabel] = @notPickedUpLabel,
                [ManualStatusLabel] = @manualLabel, [SilentStatusLabel] = @silentLabel,
                [UpdatedUtc] = @updatedUtc
            WHERE [OrganizationId] = @organizationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@ebookMessage", Db(PinnedPatronMessageOverrideText(row, "ebookMessage")));
        command.Parameters.AddWithValue("@eaudiobookMessage", Db(PinnedPatronMessageOverrideText(row, "eaudiobookMessage")));
        AddDuplicateLabelParameters(command, row, labels);
        AddDateTime2Parameter(command, "@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void AddDuplicateLabelParameters(
        SqlCommand command,
        SourceRow? row,
        IReadOnlyDictionary<string, string>? labels)
    {
        string? Pick(string objectKey, string rowField) =>
            labels is not null && labels.TryGetValue(objectKey, out var value) ? value : row?.Text(rowField);
        command.Parameters.AddWithValue("@suggestionLabel", Db(Pick("suggestion", "duplicateLabelSuggestion")));
        command.Parameters.AddWithValue("@outstandingLabel", Db(Pick("outstanding_purchase", "duplicateLabelOutstandingPurchase")));
        command.Parameters.AddWithValue("@pendingLabel", Db(Pick("pending_hold", "duplicateLabelPendingHold")));
        command.Parameters.AddWithValue("@placedLabel", Db(Pick("hold_placed", "duplicateLabelHoldPlaced")));
        command.Parameters.AddWithValue("@closedLabel", Db(Pick("closed", "duplicateLabelClosed")));
        command.Parameters.AddWithValue("@rejectedLabel", Db(Pick("rejected", "duplicateLabelRejected")));
        command.Parameters.AddWithValue("@completedLabel", Db(Pick("hold_completed", "duplicateLabelHoldCompleted")));
        command.Parameters.AddWithValue("@notPickedUpLabel", Db(Pick("hold_not_picked_up", "duplicateLabelHoldNotPickedUp")));
        command.Parameters.AddWithValue("@manualLabel", Db(Pick("manual", "duplicateLabelManual")));
        command.Parameters.AddWithValue("@silentLabel", Db(Pick("Silently Closed", "duplicateLabelSilent")));
    }

    private static void ApplyLegacyDuplicateLabels(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        DateTime exportedAtUtc)
    {
        var labels = ParseDuplicateLabelObject(row.JsonText("duplicateRequestStatusLabels"), modernOverride: false);
        using var command = new SqlCommand(
            """
            UPDATE [asap].[PatronSettings]
            SET [SuggestionStatusLabel] = @suggestionLabel,
                [OutstandingPurchaseStatusLabel] = @outstandingLabel,
                [PendingHoldStatusLabel] = @pendingLabel, [HoldPlacedStatusLabel] = @placedLabel,
                [ClosedStatusLabel] = @closedLabel, [RejectedStatusLabel] = @rejectedLabel,
                [HoldCompletedStatusLabel] = @completedLabel, [HoldNotPickedUpStatusLabel] = @notPickedUpLabel,
                [ManualStatusLabel] = @manualLabel, [SilentStatusLabel] = @silentLabel,
                [UpdatedUtc] = @updatedUtc
            WHERE [OrganizationId] = @organizationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        AddDuplicateLabelParameters(command, null, labels);
        AddDateTime2Parameter(command, "@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void EnsurePatronSettings(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        DateTime updatedUtc)
    {
        using var command = new SqlCommand(
            "IF NOT EXISTS (SELECT 1 FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId) INSERT INTO [asap].[PatronSettings] ([OrganizationId], [UpdatedUtc]) VALUES (@organizationId, @updatedUtc);",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        AddDateTime2Parameter(command, "@updatedUtc", updatedUtc);
        command.ExecuteNonQuery();
    }

    private static void ReplacePublicationOptions(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? rawValue)
    {
        if (rawValue is null || !HasLegacyConfigurationText(rawValue))
        {
            return;
        }
        var options = ParsePublicationOptions(rawValue, isSystem: organizationId == 1);
        if (organizationId != 1 && options.Count == 0)
        {
            // An explicit empty whole-set value means "remove the replacement",
            // not "replace the system list with an empty list".
            Execute(connection, transaction, "DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @organizationId;", organizationId);
            return;
        }
        EnsureSet(connection, transaction, "PublicationOptionSet", organizationId);
        Execute(connection, transaction, "DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = @organizationId;", organizationId);
        foreach (var option in options)
        {
            using var insert = new SqlCommand(
                "INSERT INTO [asap].[PublicationOption] ([OrganizationId], [OptionKey], [Label], [IsEnabled], [SortOrder]) VALUES (@organizationId, @key, @label, @enabled, @sortOrder);",
                connection,
                transaction);
            insert.Parameters.AddWithValue("@organizationId", organizationId);
            insert.Parameters.AddWithValue("@key", option.Key);
            insert.Parameters.AddWithValue("@label", option.Label);
            insert.Parameters.AddWithValue("@enabled", option.Enabled);
            insert.Parameters.AddWithValue("@sortOrder", option.SortOrder);
            insert.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<PublicationImportOption> ParsePublicationOptions(string rawValue, bool isSystem)
    {
        var rawOptions = new List<(string? Id, string Label, bool Enabled, int? SortOrder)>();
        var trimmed = TrimLegacyConfigurationText(rawValue);
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new MigrationOperationException(
                        "publication_options_invalid",
                        "Publication options must be an array or newline-delimited labels.");
                }
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        rawOptions.Add((null, item.GetString() ?? string.Empty, true, null));
                        continue;
                    }
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        throw new MigrationOperationException(
                            "publication_options_invalid",
                            "Publication options must contain only strings or objects.");
                    }
                    var label = ReadPublicationOptionLabel(item);
                    if (label.Length == 0)
                    {
                        throw new MigrationOperationException(
                            "publication_options_invalid",
                            "A publication option must have a nonblank label.");
                    }
                    var sortOrder = item.TryGetProperty("sortOrder", out var sortElement) &&
                        sortElement.ValueKind != JsonValueKind.Null
                            ? sortElement.ValueKind == JsonValueKind.Number && sortElement.TryGetInt32(out var parsedOrder)
                                ? parsedOrder
                                : throw new MigrationOperationException(
                                    "publication_options_invalid",
                                    "A publication option sortOrder must be an integer.")
                            : (int?)null;
                    rawOptions.Add((
                        ReadPublicationOptionId(item),
                        label,
                        JsonBoolStrict(item, "enabled", true),
                        sortOrder));
                }
            }
            catch (JsonException)
            {
                throw new MigrationOperationException(
                    "publication_options_invalid",
                    "Publication options contain invalid JSON.");
            }
        }
        else
        {
            rawOptions.AddRange(
                trimmed.Split('\n')
                    .Select(TrimLegacyConfigurationText)
                    .Where(label => label.Length > 0)
                    .Select(label => ((string?)null, label, true, (int?)null)));
        }

        var result = new List<PublicationImportOption>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rawOptions.Count; index++)
        {
            var raw = rawOptions[index];
            var label = TrimLegacyConfigurationText(raw.Label);
            if (label.Length == 0 || !seenLabels.Add(label))
            {
                throw new MigrationOperationException(
                    "publication_options_conflict",
                    "Publication options contain a blank or duplicate label.");
            }
            var key = raw.Id is null
                ? OptionIdFromLabel(label, $"option_{index + 1}")
                : raw.Id;
            if (key.Length == 0 || !seenIds.Add(key))
            {
                throw new MigrationOperationException(
                    "publication_options_conflict",
                    "Publication options contain a blank or duplicate stable ID.");
            }
            result.Add(new PublicationImportOption(
                key,
                label,
                raw.Enabled,
                raw.SortOrder is null or 0 ? (index + 1) * 10 : raw.SortOrder.Value));
        }
        if (result.Count == 0 || IsNumericLabelFallback(result))
        {
            return isSystem ? SystemPublicationOptionDefaults() : [];
        }
        return result.OrderBy(option => option.SortOrder).ToArray();
    }

    private static string ReadPublicationOptionLabel(JsonElement item)
    {
        foreach (var name in new[] { "label", "name", "value" })
        {
            if (!item.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            if (property.ValueKind != JsonValueKind.String)
            {
                throw new MigrationOperationException("publication_options_invalid", $"JSON property {name} must be a string.");
            }
            var value = property.GetString() ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }
            var label = TrimLegacyConfigurationText(value);
            if (label.Length == 0)
            {
                throw new MigrationOperationException(
                    "publication_options_invalid",
                    "A publication option's first nonempty label alias contains only whitespace and is not representable by the target.");
            }
            return label;
        }
        return string.Empty;
    }

    private static string? ReadPublicationOptionId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new MigrationOperationException("publication_options_invalid", "JSON property id must be a string.");
        }
        var value = property.GetString()!;
        if (value.Length == 0)
        {
            return null;
        }
        var normalized = TrimLegacyConfigurationText(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsNumericLabelFallback(IReadOnlyList<PublicationImportOption> options) =>
        options.Count > 3 && options.Count(option => option.Label.Length > 0 && option.Label.All(character => character is >= '0' and <= '9')) * 2 > options.Count;

    private static IReadOnlyList<PublicationImportOption> SystemPublicationOptionDefaults() =>
    [
        new("already_published", "Already published", true, 10),
        new("coming_soon", "Coming soon", true, 20),
        new("published_a_while_back", "Published a while back", true, 30)
    ];

    private static bool JsonBoolStrict(JsonElement value, string property, bool defaultValue)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item))
        {
            return defaultValue;
        }
        if (item.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }
        if (item.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return item.GetBoolean();
        }
        throw new MigrationOperationException(
            "publication_options_invalid",
            $"Publication option {property} must be a boolean.");
    }

    private static string? JsonStringStrict(JsonElement value, string property, string errorCode)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) ||
            item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (item.ValueKind != JsonValueKind.String)
        {
            throw new MigrationOperationException(errorCode, $"JSON property {property} must be a string.");
        }
        var result = item.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string OptionIdFromLabel(string label, string fallback)
    {
        var key = System.Text.RegularExpressions.Regex.Replace(
            TrimLegacyConfigurationText(label).Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant(),
            "[^a-z0-9]+",
            "-").Trim('-');
        return key.Length == 0 ? fallback : key;
    }

    private static void ImportCustomFields(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        int organizationId,
        SourceRow row,
        IReadOnlyDictionary<string, int> organizationIds,
        ICollection<object> transformations)
    {
        var formatRules = ParseRootObject(row.JsonText("patronFormatRules"));
        var scopedFormats = ReadScopedFormats(connection, transaction, organizationId);
        var effectiveSourceCodes = ReadEffectiveSourceFormatCodes(
            connection,
            transaction,
            package,
            organizationId,
            organizationIds,
            scopedFormats);
        if (formatRules is not null)
        {
            var rulesByCode = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var effectiveCodes = effectiveSourceCodes.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var ruleProperty in formatRules.Value.EnumerateObject())
            {
                var sourceCode = ruleProperty.Name;
                if (!effectiveCodes.Contains(sourceCode) && !string.Equals(sourceCode, "book", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!rulesByCode.TryAdd(sourceCode, ruleProperty.Value))
                {
                    throw new MigrationOperationException(
                        "format_rule_format_ambiguous",
                        $"More than one patron format rule resolves to {sourceCode}.");
                }
            }

            foreach (var target in EffectiveScopedFormats(scopedFormats, organizationId))
            {
                var sourceCode = effectiveSourceCodes.GetValueOrDefault(target.Code, target.Code);
                var normalized = ResolveEffectiveFormatRule(sourceCode, rulesByCode);
                ApplyFormatFieldRules(connection, transaction, organizationId, target, normalized);
            }
        }

        var definitionsJson = row.JsonText("additionalFieldDefinitions");
        if (definitionsJson is null)
        {
            if (formatRules is not null)
            {
                transformations.Add(new
                {
                    entity = "patron_format_rules",
                    organizationId,
                    customFields = 0,
                    formats = formatRules.Value.EnumerateObject().Select(property => property.Name).ToArray()
                });
            }
            return;
        }
        using var definitions = JsonDocument.Parse(definitionsJson);
        if (definitions.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationOperationException("custom_fields_invalid", "Additional field definitions must be a JSON array.");
        }
        var definitionItems = NormalizeCustomFieldDefinitions(definitions.RootElement);
        var fields = new List<(long Id, string Key, string Type, bool Enabled, int EnabledOptionCount)>();
        foreach (var definition in definitionItems)
        {
            using var insert = new SqlCommand(
                """
                INSERT INTO [asap].[PatronCustomField]
                    ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [HelpText], [IsEnabled], [SortOrder])
                OUTPUT inserted.[Id]
                VALUES (@organizationId, @key, @type, @label, @help, @enabled, @sortOrder);
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("@organizationId", organizationId);
            insert.Parameters.AddWithValue("@key", definition.Key);
            insert.Parameters.AddWithValue("@type", definition.Type);
            insert.Parameters.AddWithValue("@label", definition.Label);
            insert.Parameters.AddWithValue("@help", Db(definition.HelpText));
            insert.Parameters.AddWithValue("@enabled", definition.Enabled);
            insert.Parameters.AddWithValue("@sortOrder", definition.SortOrder);
            var fieldId = Convert.ToInt64(insert.ExecuteScalar());
            foreach (var option in definition.Options)
            {
                using var optionInsert = new SqlCommand(
                    "INSERT INTO [asap].[PatronCustomFieldOption] ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder]) VALUES (@fieldId, @key, @label, @enabled, @sortOrder);",
                    connection,
                    transaction);
                optionInsert.Parameters.AddWithValue("@fieldId", fieldId);
                optionInsert.Parameters.AddWithValue("@key", option.Key);
                optionInsert.Parameters.AddWithValue("@label", option.Label);
                optionInsert.Parameters.AddWithValue("@enabled", option.Enabled);
                optionInsert.Parameters.AddWithValue("@sortOrder", option.SortOrder);
                optionInsert.ExecuteNonQuery();
            }
            fields.Add((fieldId, definition.Key, definition.Type, definition.Enabled, definition.Options.Count(option => option.Enabled)));
        }

        var effectiveFormats = scopedFormats
            .GroupBy(format => format.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                .ThenBy(format => format.Id)
                .First())
            .OrderBy(format => format.Id)
            .ToArray();
        var downgradedRequiredSelectRules = 0;
        foreach (var format in effectiveFormats)
        {
            foreach (var field in fields)
            {
                var (mode, labelOverride, downgradedRequiredSelect) = ResolveCustomFieldRule(
                    formatRules,
                    effectiveSourceCodes.GetValueOrDefault(format.Code, format.Code),
                    field.Key,
                    field.Type,
                    field.Enabled,
                    field.EnabledOptionCount);
                if (downgradedRequiredSelect)
                {
                    downgradedRequiredSelectRules++;
                }
                using var ruleInsert = new SqlCommand(
                    """
                    INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                        ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                    VALUES (@organizationId, @formatId, @fieldId, @mode, @labelOverride);
                    """,
                    connection,
                    transaction);
                ruleInsert.Parameters.AddWithValue("@organizationId", organizationId);
                ruleInsert.Parameters.AddWithValue("@formatId", format.Id);
                ruleInsert.Parameters.AddWithValue("@fieldId", field.Id);
                ruleInsert.Parameters.AddWithValue("@mode", mode);
                ruleInsert.Parameters.AddWithValue("@labelOverride", Db(labelOverride));
                ruleInsert.ExecuteNonQuery();
            }
        }
        transformations.Add(new
        {
            entity = "patron_custom_fields",
            organizationId,
            fields = fields.Count,
            formatRules = effectiveFormats.Length * fields.Count,
            absentOrDisabledRule = "hidden",
            requiredSelectWithoutEnabledOptions = "optional",
            downgradedRequiredSelectRules
        });
    }

    private static IReadOnlyList<ScopedFormat> ReadScopedFormats(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId)
    {
        using var command = new SqlCommand(
            "SELECT [Id], [Code], [OwnerOrganizationId] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] IN (1, @organizationId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        using var reader = command.ExecuteReader();
        var result = new List<ScopedFormat>();
        while (reader.Read())
        {
            result.Add(new ScopedFormat(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return result;
    }

    private static Dictionary<string, string> ReadEffectiveSourceFormatCodes(
        SqlConnection connection,
        SqlTransaction transaction,
        ValidatedMigrationPackage package,
        int organizationId,
        IReadOnlyDictionary<string, int> organizationIds,
        IReadOnlyList<ScopedFormat> scopedFormats)
    {
        var systemCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var libraryCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats"))
        {
            var scope = row.RequiredString("scope");
            var code = row.Text("code") ?? string.Empty;
            var normalizedCode = NormalizeFormatCode(code);
            var ownerId = scope.Equals("system", StringComparison.OrdinalIgnoreCase)
                ? 1
                : scope.Equals("library", StringComparison.OrdinalIgnoreCase)
                    ? ResolveLibraryOrganization(connection, transaction, row, "libraryOrganization", organizationIds)
                    : throw new MigrationOperationException("format_scope_invalid", "A source material format has an unknown scope.");
            if (ownerId == organizationId)
            {
                libraryCodes[normalizedCode] = code;
            }
            else if (ownerId == 1)
            {
                systemCodes[normalizedCode] = code;
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in EffectiveScopedFormats(scopedFormats, organizationId))
        {
            result[format.Code] = libraryCodes.TryGetValue(format.Code, out var localCode)
                ? localCode
                : systemCodes.GetValueOrDefault(format.Code, format.Code);
        }
        return result;
    }

    private static (string Mode, string? LabelOverride, bool DowngradedRequiredSelect) ResolveCustomFieldRule(
        JsonElement? formatRules,
        string formatCode,
        string fieldKey,
        string fieldType,
        bool fieldEnabled,
        int enabledOptionCount)
    {
        if (!fieldEnabled || formatRules is null ||
            !TryGetCustomFieldRuleFormat(formatRules.Value, formatCode, out var format) ||
            format.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ("hidden", null, false);
        }
        if (format.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException(
                "custom_field_rule_invalid",
                $"Material format {formatCode} custom-field rules must be an object.");
        }
        if (!format.TryGetProperty("customFields", out var customFields) ||
            customFields.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ("hidden", null, false);
        }
        if (customFields.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException(
                "custom_field_rule_invalid",
                $"Material format {formatCode} customFields must be an object.");
        }
        if (!customFields.TryGetProperty(fieldKey, out var rule))
        {
            return ("hidden", null, false);
        }
        if (rule.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ("hidden", null, false);
        }
        if (rule.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException(
                "custom_field_rule_invalid",
                $"Custom-field rule for {fieldKey} must be an object or null.");
        }
        var incomingMode = LegacyConfigurationJsonString(rule, "mode", "custom_field_rule_invalid");
        var mode = incomingMode is "required" or "optional" or "hidden" ? incomingMode : "hidden";
        var downgradedRequiredSelect = fieldEnabled && fieldType == "select" &&
            enabledOptionCount == 0 && mode == "required";
        if (downgradedRequiredSelect)
        {
            mode = "optional";
        }
        return (
            mode,
            LegacyConfigurationJsonString(rule, "label", "custom_field_rule_invalid"),
            downgradedRequiredSelect);
    }

    private static void ApplyFormatFieldRules(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        ScopedFormat target,
        NormalizedFormatRule normalized) =>
        ApplyNormalizedFormatFieldRules(connection, transaction, organizationId, target, normalized);

    private static void ApplyNormalizedFormatFieldRules(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        ScopedFormat target,
        NormalizedFormatRule normalized)
    {
        if (target.OwnerOrganizationId == organizationId || organizationId == 1)
        {
            UpdateOwnedFormat(connection, transaction, target, normalized);
            return;
        }

        if (target.OwnerOrganizationId != 1)
        {
            throw new MigrationOperationException(
                "format_scope_invalid",
                $"The patron format rule for {target.Code} resolved outside the system or selected library scope.");
        }

        var baseColumns = ReadFormatColumns(connection, transaction, target.Id);
        var differences = new StoredFormatColumns(
            Different(normalized.MessageBehavior, baseColumns.MessageBehavior ?? "none") ? normalized.MessageBehavior : null,
            Different(normalized.Message, baseColumns.Message ?? string.Empty) ? normalized.Message : null,
            Different(normalized.TitleMode, baseColumns.TitleMode ?? "required") ? normalized.TitleMode : null,
            Different(normalized.TitleLabel, baseColumns.TitleLabel ?? "Title") ? normalized.TitleLabel : null,
            Different(normalized.AuthorMode, baseColumns.AuthorMode ?? "optional") ? normalized.AuthorMode : null,
            Different(normalized.AuthorLabel, baseColumns.AuthorLabel ?? "Author") ? normalized.AuthorLabel : null,
            Different(normalized.IdentifierMode, baseColumns.IdentifierMode ?? "optional") ? normalized.IdentifierMode : null,
            Different(normalized.IdentifierLabel, baseColumns.IdentifierLabel ?? "Identifier number") ? normalized.IdentifierLabel : null,
            Different(normalized.PublicationMode, baseColumns.PublicationMode ?? "optional") ? normalized.PublicationMode : null,
            Different(normalized.PublicationLabel, baseColumns.PublicationLabel ?? "Publication Timing") ? normalized.PublicationLabel : null);

        using var existingCommand = new SqlCommand(
            "SELECT [Id] FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId AND [MaterialFormatId] = @formatId;",
            connection,
            transaction);
        existingCommand.Parameters.AddWithValue("@organizationId", organizationId);
        existingCommand.Parameters.AddWithValue("@formatId", target.Id);
        var existingId = existingCommand.ExecuteScalar();
        if (existingId is null or DBNull && AllNull(differences))
        {
            return;
        }

        if (existingId is null or DBNull)
        {
            using var insert = new SqlCommand(
                """
                INSERT INTO [asap].[MaterialFormatOverride]
                    ([LibraryOrganizationId], [MaterialFormatId], [MessageBehavior], [Message], [TitleMode], [TitleLabel],
                     [AuthorMode], [AuthorLabel], [IdentifierMode], [IdentifierLabel], [PublicationMode], [PublicationLabel])
                VALUES
                    (@organizationId, @formatId, @messageBehavior, @message, @titleMode, @titleLabel,
                     @authorMode, @authorLabel, @identifierMode, @identifierLabel, @publicationMode, @publicationLabel);
                """,
                connection,
                transaction);
            AddStoredFormatParameters(insert, organizationId, target.Id, differences);
            insert.ExecuteNonQuery();
            return;
        }

        using var update = new SqlCommand(
            """
            UPDATE [asap].[MaterialFormatOverride]
            SET [MessageBehavior] = @messageBehavior, [Message] = @message,
                [TitleMode] = @titleMode, [TitleLabel] = @titleLabel,
                [AuthorMode] = @authorMode, [AuthorLabel] = @authorLabel,
                [IdentifierMode] = @identifierMode, [IdentifierLabel] = @identifierLabel,
                [PublicationMode] = @publicationMode, [PublicationLabel] = @publicationLabel
            WHERE [Id] = @id AND [LibraryOrganizationId] = @organizationId AND [MaterialFormatId] = @formatId;
            """,
            connection,
            transaction);
        AddStoredFormatParameters(update, organizationId, target.Id, differences);
        update.Parameters.AddWithValue("@id", Convert.ToInt64(existingId));
        update.ExecuteNonQuery();
    }

    private static void UpdateOwnedFormat(
        SqlConnection connection,
        SqlTransaction transaction,
        ScopedFormat target,
        NormalizedFormatRule normalized)
    {
        using var command = new SqlCommand(
            """
            UPDATE [asap].[MaterialFormat]
            SET [MessageBehavior] = @messageBehavior, [Message] = @message,
                [TitleMode] = @titleMode, [TitleLabel] = @titleLabel,
                [AuthorMode] = @authorMode, [AuthorLabel] = @authorLabel,
                [IdentifierMode] = @identifierMode, [IdentifierLabel] = @identifierLabel,
                [PublicationMode] = @publicationMode, [PublicationLabel] = @publicationLabel
            WHERE [Id] = @id AND [OwnerOrganizationId] = @organizationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", target.Id);
        command.Parameters.AddWithValue("@organizationId", target.OwnerOrganizationId);
        AddNormalizedFormatParameters(command, normalized);
        if (command.ExecuteNonQuery() == 0)
        {
            throw new MigrationOperationException(
                "format_rule_format_unresolved",
                $"The patron format rule for {target.Code} could not update its target material format.");
        }
    }

    private static StoredFormatColumns ReadFormatColumns(
        SqlConnection connection,
        SqlTransaction transaction,
        long formatId)
    {
        using var command = new SqlCommand(
            """
            SELECT [MessageBehavior], [Message], [TitleMode], [TitleLabel], [AuthorMode], [AuthorLabel],
                   [IdentifierMode], [IdentifierLabel], [PublicationMode], [PublicationLabel]
            FROM [asap].[MaterialFormat]
            WHERE [Id] = @id AND [OwnerOrganizationId] = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", formatId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new MigrationOperationException(
                "format_rule_format_unresolved",
                "A system patron format rule has no system material format.");
        }
        return new StoredFormatColumns(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private static void AddNormalizedFormatParameters(SqlCommand command, NormalizedFormatRule value)
    {
        command.Parameters.AddWithValue("@messageBehavior", value.MessageBehavior);
        command.Parameters.AddWithValue("@message", value.Message);
        command.Parameters.AddWithValue("@titleMode", value.TitleMode);
        command.Parameters.AddWithValue("@titleLabel", value.TitleLabel);
        command.Parameters.AddWithValue("@authorMode", value.AuthorMode);
        command.Parameters.AddWithValue("@authorLabel", value.AuthorLabel);
        command.Parameters.AddWithValue("@identifierMode", value.IdentifierMode);
        command.Parameters.AddWithValue("@identifierLabel", value.IdentifierLabel);
        command.Parameters.AddWithValue("@publicationMode", value.PublicationMode);
        command.Parameters.AddWithValue("@publicationLabel", value.PublicationLabel);
    }

    private static void AddStoredFormatParameters(
        SqlCommand command,
        int organizationId,
        long formatId,
        StoredFormatColumns value)
    {
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@formatId", formatId);
        command.Parameters.AddWithValue("@messageBehavior", Db(value.MessageBehavior));
        command.Parameters.AddWithValue("@message", Db(value.Message));
        command.Parameters.AddWithValue("@titleMode", Db(value.TitleMode));
        command.Parameters.AddWithValue("@titleLabel", Db(value.TitleLabel));
        command.Parameters.AddWithValue("@authorMode", Db(value.AuthorMode));
        command.Parameters.AddWithValue("@authorLabel", Db(value.AuthorLabel));
        command.Parameters.AddWithValue("@identifierMode", Db(value.IdentifierMode));
        command.Parameters.AddWithValue("@identifierLabel", Db(value.IdentifierLabel));
        command.Parameters.AddWithValue("@publicationMode", Db(value.PublicationMode));
        command.Parameters.AddWithValue("@publicationLabel", Db(value.PublicationLabel));
    }

    private static bool AllNull(StoredFormatColumns value) =>
        value.MessageBehavior is null && value.Message is null && value.TitleMode is null && value.TitleLabel is null &&
        value.AuthorMode is null && value.AuthorLabel is null && value.IdentifierMode is null && value.IdentifierLabel is null &&
        value.PublicationMode is null && value.PublicationLabel is null;

    private static bool Different(string value, string baseline) =>
        !string.Equals(value, baseline, StringComparison.Ordinal);

    private static NormalizedFormatRule NormalizeFormatRule(JsonElement format, string formatCode)
    {
        var defaults = DefaultFormatRule(formatCode);
        if (format.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaults;
        }
        if (format.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException(
                "format_rule_invalid",
                $"Material format {formatCode} rule must be an object or null.");
        }
        var fields = format.TryGetProperty("fields", out var incomingFields)
            ? incomingFields.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined
                ? incomingFields
                : throw new MigrationOperationException(
                    "format_field_rule_invalid",
                    $"Material format {formatCode} fields must be an object.")
            : default;
        var title = NormalizeFormatField(fields, "title", defaults.TitleMode, defaults.TitleLabel, forceRequired: true);
        var author = NormalizeFormatField(fields, "author", defaults.AuthorMode, defaults.AuthorLabel, forceRequired: false);
        var identifier = NormalizeFormatField(fields, "identifier", defaults.IdentifierMode, defaults.IdentifierLabel, forceRequired: false);
        var publication = NormalizeFormatField(fields, "publication", defaults.PublicationMode, defaults.PublicationLabel, forceRequired: false);
        var behavior = NormalizeFormatEnum(
            LegacyConfigurationJsonString(format, "messageBehavior", "format_message_behavior_invalid"),
            defaults.MessageBehavior,
            ["none", "message", "ebookMessage", "eaudiobookMessage"],
            "format_message_behavior_invalid");
        return new NormalizedFormatRule(
            behavior,
            NormalizeLegacyFormatMessage(format, defaults.Message),
            title.Mode,
            title.Label,
            author.Mode,
            author.Label,
            identifier.Mode,
            identifier.Label,
            publication.Mode,
            publication.Label);
    }

    private static (string Mode, string Label) NormalizeFormatField(
        JsonElement fields,
        string key,
        string defaultMode,
        string defaultLabel,
        bool forceRequired)
    {
        var field = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(key, out var incoming)
            ? incoming.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined
                ? incoming
                : throw new MigrationOperationException(
                    "format_field_rule_invalid",
                    $"Material format field {key} must be an object.")
            : default;
        var mode = NormalizeFormatEnum(
            LegacyConfigurationJsonString(field, "mode", "format_field_mode_invalid"),
            defaultMode,
            ["required", "optional", "hidden"],
            "format_field_mode_invalid");
        return (
            forceRequired ? "required" : mode,
            LegacyConfigurationJsonString(field, "label", "format_field_label_invalid") ?? defaultLabel);
    }

    private static string NormalizeFormatEnum(
        string? value,
        string fallback,
        string[] allowed,
        string errorCode)
    {
        if (value is null)
        {
            return fallback;
        }

        var normalized = TrimLegacyConfigurationText(value);
        var canonical = allowed.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.Ordinal));
        return canonical ?? fallback;
    }

    private static string NormalizeLegacyFormatMessage(JsonElement format, string fallback)
    {
        if (format.ValueKind != JsonValueKind.Object ||
            !format.TryGetProperty("message", out var message) ||
            message.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return fallback;
        }
        if (message.ValueKind != JsonValueKind.String)
        {
            throw new MigrationOperationException("format_message_invalid", "JSON property message must be a string.");
        }

        var value = TrimLegacyConfigurationText(message.GetString()!);
        return value.Length == 0 ? fallback : value;
    }

    private static bool IsLegacyBuiltInFormatCode(string formatCode) =>
        formatCode is "book" or "audiobook_cd" or "dvd" or "music_cd" or "ebook" or "eaudiobook";

    private static bool TryGetCustomFieldRuleFormat(
        JsonElement rules,
        string formatCode,
        out JsonElement format)
    {
        if (rules.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rules.EnumerateObject())
            {
                if (string.Equals(property.Name, formatCode, StringComparison.Ordinal))
                {
                    format = property.Value;
                    return true;
                }
            }
        }
        if (!IsLegacyBuiltInFormatCode(formatCode))
        {
            return TryGetExactProperty(rules, "book", out format);
        }

        format = default;
        return false;
    }

    private static bool TryGetExactProperty(JsonElement value, string propertyName, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
            {
                if (string.Equals(item.Name, propertyName, StringComparison.Ordinal))
                {
                    property = item.Value;
                    return true;
                }
            }
        }
        property = default;
        return false;
    }

    private static IReadOnlyList<ScopedFormat> EffectiveScopedFormats(
        IReadOnlyList<ScopedFormat> formats,
        int organizationId) => formats
            .GroupBy(format => format.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                .ThenBy(format => format.Id)
                .First())
            .OrderBy(format => format.Id)
            .ToArray();

    private static NormalizedFormatRule ResolveEffectiveFormatRule(
        string sourceFormatCode,
        IReadOnlyDictionary<string, JsonElement> rulesByCode)
    {
        if (rulesByCode.TryGetValue(sourceFormatCode, out var directRule))
        {
            return NormalizeFormatRule(directRule, sourceFormatCode);
        }
        if (!IsLegacyBuiltInFormatCode(sourceFormatCode) && rulesByCode.TryGetValue("book", out var bookRule))
        {
            return NormalizeFormatRule(bookRule, "book");
        }
        return DefaultFormatRule(IsLegacyBuiltInFormatCode(sourceFormatCode) ? sourceFormatCode : "book");
    }

    private static NormalizedFormatRule DefaultFormatRule(string formatCode) =>
        formatCode switch
        {
            "book" or "audiobook_cd" => new("none", string.Empty, "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
            "dvd" => new("none", string.Empty, "required", "Title", "required", "Director/Actors/Producer", "hidden", "UPC", "required", "Publication Timing"),
            "music_cd" => new("none", string.Empty, "required", "Title", "required", "Artist", "hidden", "UPC", "required", "Publication Timing"),
            "ebook" => new("message", "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>", "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
            "eaudiobook" => new("message", "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>", "required", "Title", "required", "Author", "optional", "Identifier number", "required", "Publication Timing"),
            _ => new("none", string.Empty, "required", "Title", "optional", "Author", "optional", "Identifier", "optional", "Publication")
        };

    private static void UpsertBrandingAlt(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? alt,
        DateTime updatedUtc)
    {
        if (alt is null)
        {
            return;
        }

        using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM [asap].[Branding] WHERE [OrganizationId] = @organizationId)
                UPDATE [asap].[Branding] SET [LogoAltText] = @alt, [UpdatedUtc] = @updatedUtc WHERE [OrganizationId] = @organizationId;
            ELSE
                INSERT INTO [asap].[Branding] ([OrganizationId], [LogoAltText], [UpdatedUtc]) VALUES (@organizationId, @alt, @updatedUtc);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@alt", alt);
        AddDateTime2Parameter(command, "@updatedUtc", updatedUtc);
        command.ExecuteNonQuery();
    }

    private static int ResolveScopedOrganization(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var scope = row.RequiredString("scope").ToLowerInvariant();
        if (scope == "system")
        {
            return 1;
        }

        if (scope != "library")
        {
            throw new MigrationOperationException("settings_scope_invalid", "A settings row has an invalid scope.");
        }

        return ResolveOrganizationId(connection, transaction, row, "libraryOrganization", organizationIds);
    }

    private static bool OrganizationIsLibrary(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId)
    {
        using var command = new SqlCommand(
            "SELECT CASE WHEN [Id] > 1 AND [OrganizationCodeId] = 2 THEN 1 ELSE 0 END FROM [asap].[Organization] WHERE [Id] = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", organizationId);
        return command.ExecuteScalar() is { } value and not DBNull && Convert.ToInt32(value) == 1;
    }

    private static int ResolveLibraryOrganization(
        SqlConnection connection,
        SqlTransaction transaction,
        SourceRow row,
        string field,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        return ResolveOrganizationId(connection, transaction, row, field, organizationIds);
    }

    private static void EnsureSet(SqlConnection connection, SqlTransaction transaction, string table, int organizationId)
    {
        var allowed = table switch
        {
            "CommonCreatorSet" => "CommonCreatorSet",
            "PatronCodeEligibilitySet" => "PatronCodeEligibilitySet",
            "PublicationOptionSet" => "PublicationOptionSet",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        using var command = new SqlCommand(
            $"IF NOT EXISTS (SELECT 1 FROM [asap].[{allowed}] WHERE [OrganizationId] = @organizationId) INSERT INTO [asap].[{allowed}] ([OrganizationId]) VALUES (@organizationId);",
            connection,
            transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqlConnection connection, SqlTransaction transaction, string sql, int organizationId)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.ExecuteNonQuery();
    }

    private static bool? Bool(SourceRow row, string field, bool isSystem, bool defaultValue) =>
        row.HasValue(field) ? row.Bool(field) : isSystem ? defaultValue : null;

    private static int? Int(SourceRow row, string field, bool isSystem, int defaultValue) =>
        row.Int32(field) ?? (isSystem ? defaultValue : null);

    private static string? PatronUiScopedText(SourceRow row, string field, bool isSystem)
    {
        var value = row.Text(field);
        return isSystem || !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static IReadOnlyList<string> SplitValues(string? source) =>
        string.IsNullOrWhiteSpace(source)
            ? []
            : source.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value.Length > 0)
                .ToArray();

    private static IReadOnlyList<string> SplitLegacyEmbedOrigins(string? source) =>
        source is null
            ? []
            : source.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(TrimLegacyConfigurationText)
                .Where(value => value.Length > 0)
                .ToArray();

    private static IReadOnlyList<string> SplitLegacyCommonCreatorLines(string? source) =>
        source is null
            ? []
            : source.Split('\n')
                .Select(TrimLegacyConfigurationText)
                .Where(value => value.Length > 0)
                .ToArray();

    private static bool HasLegacyConfigurationText(string? value) =>
        value is not null && TrimLegacyConfigurationText(value).Length > 0;

    private static IReadOnlyDictionary<string, string> ParseDuplicateLabelObject(string? json, bool modernOverride)
    {
        if (json is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException("settings_json_invalid", "A settings JSON value must be an object.");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new MigrationOperationException(
                    "settings_json_invalid",
                    $"Settings JSON property {property.Name} must be a string.");
            }
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            var value = property.Value.GetString()!;
            if (!result.TryAdd(property.Name, value))
            {
                throw new MigrationOperationException(
                    "settings_json_invalid",
                    $"Settings JSON contains duplicate property {property.Name}.");
            }
        }
        var hasAnyOverrideLabel = result.Values.Any(value => TrimLegacyConfigurationText(value).Length > 0);
        if (modernOverride && !hasAnyOverrideLabel)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        foreach (var value in result.Values)
        {
            if (TrimLegacyConfigurationText(value).Length == 0 || string.IsNullOrWhiteSpace(value))
            {
                throw new MigrationOperationException(
                    "duplicate_labels_invalid",
                    "Duplicate-status label whitespace cannot be represented with the target's scoped fallback behavior.");
            }
        }
        return result;
    }

    private static JsonElement? ParseRootObject(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException("settings_json_invalid", "A settings JSON value must be an object.");
        }
        return document.RootElement.Clone();
    }

    private static string RequiredJsonString(JsonElement value, string property, string errorCode) =>
        JsonStringStrict(value, property, errorCode) is { Length: > 0 } result
            ? result
            : throw new MigrationOperationException(errorCode, $"Required JSON property {property} is missing.");

    private static string? JsonString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!.Trim()
            : null;

    private static bool JsonBool(JsonElement value, string property, bool defaultValue) =>
        value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) ||
        item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? defaultValue
            : item.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? item.GetBoolean()
                : throw new MigrationOperationException(
                    "custom_fields_invalid",
                    $"JSON property {property} must be a boolean.");

    private static int JsonInt(JsonElement value, string property, int defaultValue) =>
        value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) ||
        item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? defaultValue
            : item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var parsed)
                ? parsed
                : throw new MigrationOperationException(
                    "custom_fields_invalid",
                    $"JSON property {property} must be an integer.");

    private static int CustomFieldSortOrder(JsonElement value, int index) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("sortOrder", out var item) &&
        item.ValueKind == JsonValueKind.Null
            ? 0
            : JsonInt(value, "sortOrder", checked((index + 1) * 10));

    private static IReadOnlyList<NormalizedCustomFieldDefinition> NormalizeCustomFieldDefinitions(JsonElement definitions)
    {
        if (definitions.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationOperationException("custom_fields_invalid", "Additional field definitions must be a JSON array.");
        }

        var normalized = new List<NormalizedCustomFieldDefinition>();
        var definitionKeys = new HashSet<string>(StringComparer.Ordinal);
        var definitionIndex = 0;
        foreach (var definition in definitions.EnumerateArray())
        {
            if (definition.ValueKind != JsonValueKind.Object)
            {
                throw new MigrationOperationException("custom_fields_invalid", "A custom-field definition must be an object.");
            }

            var label = RequiredLegacyCustomFieldString(definition, "label");
            var type = RequiredLegacyCustomFieldString(definition, "type");
            if (type is not ("text" or "textarea" or "select"))
            {
                throw new MigrationOperationException("custom_fields_invalid", "A custom-field type is unsupported.");
            }

            var rawKey = OptionalCustomFieldIdentity(definition, "key");
            var key = NormalizeCustomFieldIdentity(rawKey ?? label);
            if (key.Length == 0 || !definitionKeys.Add(key))
            {
                throw new MigrationOperationException(
                    "custom_fields_identity_invalid",
                    "Custom-field definition keys must remain unique and nonempty after legacy normalization.");
            }

            var options = type == "select"
                ? NormalizeCustomFieldOptions(definition)
                : Array.Empty<NormalizedCustomFieldOption>();
            normalized.Add(new(
                key,
                type,
                label,
                OptionalCustomFieldText(definition, "helpText"),
                JsonBool(definition, "enabled", defaultValue: true),
                CustomFieldSortOrder(definition, definitionIndex),
                options));
            definitionIndex++;
        }

        return normalized
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Label, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static IReadOnlyList<NormalizedCustomFieldOption> NormalizeCustomFieldOptions(JsonElement definition)
    {
        if (!definition.TryGetProperty("options", out var options) || options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<NormalizedCustomFieldOption>();
        }
        if (options.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationOperationException("custom_fields_invalid", "A select custom-field options value must be an array or null.");
        }

        var normalized = new List<NormalizedCustomFieldOption>();
        var optionKeys = new HashSet<string>(StringComparer.Ordinal);
        var optionIndex = 0;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                throw new MigrationOperationException("custom_fields_invalid", "A select custom-field option must be an object.");
            }

            var label = CustomFieldOptionLabel(option);
            var rawKey = OptionalCustomFieldIdentity(option, "id");
            var key = NormalizeCustomFieldIdentity(rawKey ?? label);
            if (key.Length == 0 || !optionKeys.Add(key))
            {
                throw new MigrationOperationException(
                    "custom_fields_identity_invalid",
                    "Custom-field option IDs must remain unique and nonempty after legacy normalization.");
            }

            normalized.Add(new(
                key,
                label,
                JsonBool(option, "enabled", defaultValue: true),
                CustomFieldSortOrder(option, optionIndex)));
            optionIndex++;
        }

        return normalized
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Label, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static string CustomFieldOptionLabel(JsonElement option)
    {
        foreach (var property in new[] { "label", "name", "value" })
        {
            if (!option.TryGetProperty(property, out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new MigrationOperationException("custom_fields_invalid", $"Custom-field option {property} must be a string.");
            }
            if (item.GetString() is { Length: > 0 } text)
            {
                var label = TrimLegacyConfigurationText(text);
                if (label.Length > 0)
                {
                    return label;
                }
                throw new MigrationOperationException("custom_fields_invalid", "A custom-field option label must not be blank.");
            }
        }

        throw new MigrationOperationException("custom_fields_invalid", "A custom-field option must have a label, name, or value.");
    }

    private static string? OptionalCustomFieldIdentity(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (item.ValueKind != JsonValueKind.String)
        {
            throw new MigrationOperationException("custom_fields_invalid", $"Custom-field {property} must be a string.");
        }
        var text = item.GetString()!;
        return text.Length == 0 ? null : text;
    }

    private static string? OptionalCustomFieldText(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (item.ValueKind != JsonValueKind.String)
        {
            throw new MigrationOperationException("custom_fields_invalid", $"Custom-field {property} must be a string.");
        }
        var text = TrimLegacyConfigurationText(item.GetString()!);
        return text.Length == 0 ? null : text;
    }

    private static string NormalizeCustomFieldIdentity(string value) =>
        Regex.Replace(
            TrimLegacyConfigurationText(value).Replace("\u0130", "i\u0307", StringComparison.Ordinal).ToLowerInvariant(),
            "[^a-z0-9]+",
            "_",
            RegexOptions.CultureInvariant).Trim('_');

    private static string RequiredLegacyCustomFieldString(JsonElement value, string property) =>
        LegacyConfigurationJsonString(value, property, "custom_fields_invalid") is { Length: > 0 } result
            ? result
            : throw new MigrationOperationException("custom_fields_invalid", $"Required JSON property {property} is missing.");

    private static string? LegacyConfigurationJsonString(JsonElement value, string property, string errorCode)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) ||
            item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (item.ValueKind != JsonValueKind.String)
        {
            throw new MigrationOperationException(errorCode, $"JSON property {property} must be a string.");
        }

        var result = TrimLegacyConfigurationText(item.GetString()!);
        return result.Length > 0 ? result : null;
    }

    private static string TrimLegacyConfigurationText(string value)
    {
        var start = 0;
        while (start < value.Length && IsLegacyConfigurationWhitespace(value[start]))
        {
            start++;
        }
        var end = value.Length;
        while (end > start && IsLegacyConfigurationWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsLegacyConfigurationWhitespace(char value) => value is
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or
        '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or
        '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028' or '\u2029' or
        '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static int Scalar(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        int organizationId)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void AddDateTime2Parameter(SqlCommand command, string name, DateTime? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
        parameter.Scale = 7;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static bool StringEquals(SqlDataReader reader, int ordinal, string? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : string.Equals(reader.GetString(ordinal), expected, StringComparison.Ordinal);

    private static bool IntEquals(SqlDataReader reader, int ordinal, int? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : reader.GetInt32(ordinal) == expected;

    private static bool LongEquals(SqlDataReader reader, int ordinal, long? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : reader.GetInt64(ordinal) == expected;

    private static bool BoolEquals(SqlDataReader reader, int ordinal, bool? expected) =>
        reader.IsDBNull(ordinal) ? expected is null : expected is not null && reader.GetBoolean(ordinal) == expected.Value;

    private static bool DateEquals(SqlDataReader reader, int ordinal, DateTime? expected) =>
        reader.IsDBNull(ordinal)
            ? expected is null
            : expected is not null && reader.GetDateTime(ordinal).Ticks == expected.Value.Ticks;

    private static bool ProtectedPresenceEquals(SqlDataReader reader, int ordinal, string? sourceValue) =>
        reader.IsDBNull(ordinal) == (sourceValue is null);

    private static bool ProtectedPresenceEquals(SqlDataReader reader, int ordinal, bool expectedPresent) =>
        !reader.IsDBNull(ordinal) == expectedPresent;

    private static void EnsureConfiguration(bool condition, string entity)
    {
        if (!condition)
        {
            throw new MigrationOperationException(
                "reconciliation_failed",
                $"Imported {entity} does not match the immutable source package.");
        }
    }

    private static object Db(object? value) => value ?? DBNull.Value;
}
