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
        ImportPatronConfiguration(
            connection,
            transaction,
            package,
            organizationIds,
            formatIds,
            exportedAtUtc,
            importedCounts,
            transformations);
        ImportBranding(
            connection,
            transaction,
            package,
            organizationIds,
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
            var organizationId = ResolveScopedOrganization(row, organizationIds);
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
            command.Parameters.AddWithValue("@updatedUtc", exportedAtUtc);
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

    private static void ValidateConfigurationRows(ValidatedMigrationPackage package)
    {
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "workflow-settings.json", "workflow_settings"),
            "workflow settings",
            row => ScopedKey(row, "libraryOrganization"));
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "ui_settings"),
            "patron UI settings",
            row => ScopedKey(row, "libraryOrganization"));
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides"),
            "patron settings overrides",
            row => row.RequiredString("orgId"));
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"),
            "legacy patron settings",
            row => row.RequiredString("libraryOrganization"));
        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "patron-settings.json", "library_settings"),
            "legacy library settings",
            row => row.RequiredString("libraryOrganization"));

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

        ValidateUniqueScope(
            MigrationPackageReader.ReadRows(package, "material-formats.json", "material_formats"),
            "material formats",
            row => $"{ScopedKey(row, "libraryOrganization")}|{row.RequiredString("code").Trim().ToLowerInvariant()}");
    }

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
            command.Parameters.AddWithValue("@bibPattern", Db(row?.String("leapBibUrlPattern")));
            command.Parameters.AddWithValue("@patronPattern", Db(row?.String("leapPatronUrlPattern")));
            command.Parameters.AddWithValue("@iconPattern", iconPattern);
            command.Parameters.AddWithValue("@updatedUtc", row?.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
        }

        var origins = SplitValues(row?.String("patronEmbedAllowedOrigins"))
            .Select(NormalizeEmbedOrigin)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var origin in origins)
        {
            using var insert = new SqlCommand(
                "INSERT INTO [asap].[PatronEmbedAllowedOrigin] ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc]) VALUES (1, @origin, @normalized, @createdUtc);",
                connection,
                transaction);
            insert.Parameters.AddWithValue("@origin", origin);
            insert.Parameters.AddWithValue("@normalized", origin);
            insert.Parameters.AddWithValue("@createdUtc", exportedAtUtc);
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
        var origin = value.Trim();
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
            var host = wildcard.Groups[2].Value.ToLowerInvariant();
            if (scheme != "https" ||
                host.Contains('/') || host.Contains('?') || host.Contains('#') ||
                host.Contains("..", StringComparison.Ordinal) || !host.Contains('.') ||
                !Regex.IsMatch(host, "^[a-z0-9.-]+(?::[0-9]+)?$", RegexOptions.CultureInvariant))
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
        if (plainHost.Length == 0 ||
            plainHost.Contains('@') ||
            !Regex.IsMatch(plainHost, "^[a-z0-9.:[\\]-]+$", RegexOptions.CultureInvariant))
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
            var apiKey = row.String("apiKey");
            var adminPassword = row.String("adminPassword");
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
            command.Parameters.AddWithValue("@host", Db(row.String("host")));
            command.Parameters.AddWithValue("@accessId", Db(row.String("accessId")));
            command.Parameters.AddWithValue("@protectedApiKey", Db(apiKey is null ? null : credentialProtector!.Protect(apiKey)));
            command.Parameters.AddWithValue("@staffDomain", Db(row.String("staffDomain")));
            command.Parameters.AddWithValue("@adminUser", Db(row.String("adminUser")));
            command.Parameters.AddWithValue("@protectedAdminPassword", Db(adminPassword is null ? null : credentialProtector!.Protect(adminPassword)));
            command.Parameters.AddWithValue("@workstationId", Db(row.Int32("workstationId")));
            command.Parameters.AddWithValue("@userId", Db(row.Int32("userId")));
            command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
            transformations.Add(new
            {
                entity = "polaris_settings",
                sourceId = row.RequiredString("id"),
                apiKeyProtected = apiKey is not null,
                adminPasswordProtected = adminPassword is not null,
                intentionallyDroppedFields = new[] { "pickupOrgId", "requestingOrgId" }
                    .Where(row.HasValue)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
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
            command.Parameters.AddWithValue("@updatedUtc", rows.SingleOrDefault()?.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
            if (rows.Count > 0)
            {
                transformations.Add(new
                {
                    entity = "email_settings",
                    sourceId = rows[0].RequiredString("id"),
                    transport = "legacy_smtp_transport_intentionally_dropped",
                    targetTransport = "file_email_sender",
                    postmarkTokenProvisioned = postmarkToken is not null
                });
            }
        }

        // Legacy templates could carry a scoped sender. EmailSettings owns that
        // target concern, so preserve each independently resolved scope without
        // importing the legacy SMTP transport fields.
        foreach (var group in templateRows
                     .Where(item => string.Equals(item.String("scope"), "library", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(row => ResolveScopedOrganization(row, organizationIds)))
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
            command.Parameters.AddWithValue("@updatedUtc", group.Select(row => row.UtcDateTime("updated"))
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
        foreach (var row in rows.OrderBy(item => item.RequiredString("scope") == "system" ? 0 : 1))
        {
            var organizationId = ResolveScopedOrganization(row, organizationIds);
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
            command.Parameters.AddWithValue("@suggestionLimitMessage", Db(ScopedText(row, "suggestionLimitMessage", isSystem)));
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
            command.Parameters.AddWithValue("@commonAuthorsLabel", Db(ScopedText(row, "commonAuthorsLabel", isSystem)));
            command.Parameters.AddWithValue("@commonAuthorsHelp", Db(ScopedText(row, "commonAuthorsHelp", isSystem)));
            command.Parameters.AddWithValue("@commonAuthorsMessage", Db(ScopedText(row, "commonAuthorsMessage", isSystem)));
            command.Parameters.AddWithValue("@allowOptOut", Db(Bool(row, "allowPatronAutoholdOptOut", isSystem, true)));
            command.Parameters.AddWithValue("@allowAnyCard", Db(Bool(row, "allowAnyRegisteredCardLogin", isSystem, false)));
            command.Parameters.AddWithValue("@patronCodeEnabled", Db(Bool(row, "patronCodeEligibilityEnabled", isSystem, false)));
            command.Parameters.AddWithValue("@patronCodeMessage", Db(ScopedText(row, "patronCodeEligibilityMessage", isSystem)));
            command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();

            ReplaceCommonCreators(connection, transaction, organizationId, row.String("commonAuthorsList"), isSystem);
            ReplacePatronCodes(connection, transaction, organizationId, row.String("allowedPatronCodeIds"), isSystem);
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
        var values = SplitValues(sourceValue);
        if (!isSystem && values.Count == 0) return;
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

    private static void ReplacePatronCodes(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? sourceValue,
        bool isSystem)
    {
        var values = SplitValues(sourceValue);
        if (!isSystem && values.Count == 0) return;
        EnsureSet(connection, transaction, "PatronCodeEligibilitySet", organizationId);
        Execute(connection, transaction, "DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId;", organizationId);
        foreach (var value in values.Distinct(StringComparer.Ordinal))
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
            if (!isSystem && !row.HasValue(enabledField) && !row.HasValue(labelField) && !row.HasValue(urlField)) continue;
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
            command.Parameters.AddWithValue("@label", Db(isSystem ? row.Text(labelField) ?? defaults[slot - 1].Item2 : ScopedText(row, labelField, false)));
            command.Parameters.AddWithValue("@url", Db(isSystem ? row.Text(urlField) ?? defaults[slot - 1].Item3 : ScopedText(row, urlField, false)));
            if (command.ExecuteNonQuery() == 0)
            {
                throw new MigrationOperationException(
                    "external_provider_seed_missing",
                    $"Target external-search provider {providerKey} could not be reconciled.");
            }
        }
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
        foreach (var row in uiRows.OrderBy(item => item.RequiredString("scope") == "system" ? 0 : 1))
        {
            var organizationId = ResolveScopedOrganization(row, organizationIds);
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
                updateSystem.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
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
        foreach (var row in overrides.OrderBy(item => item.RequiredString("orgId"), StringComparer.Ordinal))
        {
            var organizationId = row.Int32("orgId") ?? throw new MigrationOperationException("patron_override_org_invalid", "A patron settings override has no organization.");
            if (organizationId == 1 || !organizationIds.Values.Contains(organizationId))
            {
                throw new MigrationOperationException(
                    "patron_override_org_invalid",
                    "A patron override references an unknown or system organization.");
            }
            modernOverrideOrganizations.Add(organizationId);
            EnsurePatronSettings(connection, transaction, organizationId, row.UtcDateTime("updated") ?? exportedAtUtc);
            ApplyPatronOverride(connection, transaction, organizationId, row, exportedAtUtc);
            ReplacePublicationOptions(connection, transaction, organizationId, row.Text("publicationOptions"));
            ImportCustomFields(connection, transaction, organizationId, row, transformations);
        }

        var legacyPatronRows = MigrationPackageReader.ReadRows(
            package,
            "patron-settings.json",
            "patron_library_settings");
        foreach (var row in legacyPatronRows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var organizationId = ResolveLibraryOrganization(row, "libraryOrganization", organizationIds);
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
            var organizationId = ResolveLibraryOrganization(row, "libraryOrganization", organizationIds);
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
            StringEquals(reader, 1, row?.String("leapBibUrlPattern")) &&
            StringEquals(reader, 2, row?.String("leapPatronUrlPattern")) &&
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

        var expectedOrigins = SplitValues(row?.String("patronEmbedAllowedOrigins"))
            .Select(NormalizeEmbedOrigin)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        reader.Close();
        using var originsCommand = new SqlCommand(
            "SELECT [NormalizedOrigin] FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1 ORDER BY [NormalizedOrigin];",
            connection,
            transaction);
        using var originsReader = originsCommand.ExecuteReader();
        var actualOrigins = new List<string>();
        while (originsReader.Read()) actualOrigins.Add(originsReader.GetString(0));
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
        if (row is null) return;
        using var command = new SqlCommand(
            "SELECT [Host], [AccessId], [ProtectedApiKey], [StaffDomain], [AdminUser], [ProtectedAdminPassword], [WorkstationId], [SystemPolarisUserId] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        EnsureConfiguration(reader.Read(), "Polaris settings");
        EnsureConfiguration(
            StringEquals(reader, 0, row.String("host")) &&
            StringEquals(reader, 1, row.String("accessId")) &&
            ProtectedPresenceEquals(reader, 2, row.String("apiKey")) &&
            StringEquals(reader, 3, row.String("staffDomain")) &&
            StringEquals(reader, 4, row.String("adminUser")) &&
            ProtectedPresenceEquals(reader, 5, row.String("adminPassword")) &&
            IntEquals(reader, 6, row.Int32("workstationId")) &&
            IntEquals(reader, 7, row.Int32("userId")),
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
                     .GroupBy(row => ResolveScopedOrganization(row, organizationIds)))
        {
            var expectedAddressForLibrary = ResolveSenderValue(
                $"library:{group.Key}",
                "fromAddress",
                group.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromAddress"))));
            var expectedNameForLibrary = ResolveSenderValue(
                $"library:{group.Key}",
                "fromName",
                group.Select(row => new SenderCandidate(row.RequiredString("id"), row.Text("fromName"))));
            if (expectedAddressForLibrary is null && expectedNameForLibrary is null) continue;
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
            var organizationId = ResolveScopedOrganization(row, organizationIds);
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
                    StringEquals(reader, 1, ScopedText(row, "suggestionLimitMessage", isSystem)) &&
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
                    StringEquals(reader, 14, ScopedText(row, "commonAuthorsLabel", isSystem)) &&
                    StringEquals(reader, 15, ScopedText(row, "commonAuthorsHelp", isSystem)) &&
                    StringEquals(reader, 16, ScopedText(row, "commonAuthorsMessage", isSystem)) &&
                    BoolEquals(reader, 17, Bool(row, "allowPatronAutoholdOptOut", isSystem, true)) &&
                    BoolEquals(reader, 18, Bool(row, "allowAnyRegisteredCardLogin", isSystem, false)) &&
                    BoolEquals(reader, 19, Bool(row, "patronCodeEligibilityEnabled", isSystem, false)) &&
                    StringEquals(reader, 20, ScopedText(row, "patronCodeEligibilityMessage", isSystem));
            }
            EnsureConfiguration(matches, "workflow settings");
            counter.Rows++;
            counter.Fields += 21;

            ReconcileCommonCreatorSet(connection, transaction, organizationId, row.String("commonAuthorsList"), isSystem, counter);
            ReconcilePatronCodeSet(connection, transaction, organizationId, row.String("allowedPatronCodeIds"), isSystem, counter);
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
        var expected = SplitValues(rawValue);
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
        while (reader.Read()) actual.Add(reader.GetString(0));
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
        var expected = SplitValues(rawValue).Distinct(StringComparer.Ordinal).ToArray();
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
        var actual = new List<string>();
        while (reader.Read()) actual.Add(reader.GetString(0));
        EnsureConfiguration(expected.Order(StringComparer.Ordinal).SequenceEqual(actual, StringComparer.Ordinal), "patron-code eligibility set");
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
            if (!isSystem && !row.HasValue(enabledField) && !row.HasValue(labelField) && !row.HasValue(urlField)) continue;
            var expectedEnabled = isSystem ? row.Bool(enabledField, defaults[slot - 1].Enabled) : row.NullableBool(enabledField);
            var expectedLabel = isSystem ? row.Text(labelField) ?? defaults[slot - 1].Label : ScopedText(row, labelField, false);
            var expectedUrl = isSystem ? row.Text(urlField) ?? defaults[slot - 1].Url : ScopedText(row, urlField, false);
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
            var organizationId = ResolveScopedOrganization(row, organizationIds);
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
                ResolveScopedOrganization(row, organizationIds) == 1)
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
            if (organizationId == 1 || !organizationIds.Values.Contains(organizationId))
            {
                throw new MigrationOperationException(
                    "patron_override_org_invalid",
                    "A patron settings override references an unknown or system organization.");
            }
            modernOverrideOrganizations.Add(organizationId);
            var values = GetExpectedPatronValues(expected, organizationId);
            AddPatronOverrideValues(values, row);
            var raw = row.Text("publicationOptions");
            if (!string.IsNullOrWhiteSpace(raw)) publicationRows[organizationId] = raw;
            ReconcileCustomFields(
                connection,
                transaction,
                organizationId,
                row,
                counter);
            ReconcileFormatRules(
                connection,
                transaction,
                organizationId,
                row,
                counter);
        }

        foreach (var row in MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_library_settings"))
        {
            var organizationId = ResolveLibraryOrganization(row, "libraryOrganization", organizationIds);
            if (modernOverrideOrganizations.Contains(organizationId)) continue;
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
            foreach (var (_, target) in PatronTextFields) values[target] = null;
            foreach (var (_, target) in DuplicateLabelFields) values[target] = null;
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
            if (isSystem || row.HasValue(source)) values[target] = ScopedText(row, source, isSystem);
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
        values["EbookMessage"] = ScopedText(row, "ebookMessage", false);
        values["EaudiobookMessage"] = ScopedText(row, "eaudiobookMessage", false);
        AddDuplicateLabelValues(values, row, ParseObject(row.JsonText("duplicateStatusLabels")));
    }

    private static void AddLegacyDuplicateLabelValues(
        IDictionary<string, string?> values,
        string? rawLabels)
    {
        AddDuplicateLabelValues(values, null, ParseObject(rawLabels));
    }

    private static void AddDuplicateLabelValues(
        IDictionary<string, string?> values,
        SourceRow? row,
        IReadOnlyDictionary<string, string>? labels)
    {
        foreach (var (source, target) in DuplicateLabelFields)
        {
            var value = labels is not null && labels.TryGetValue(source, out var label)
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
        if (expected.Count == 0) return;
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
        var options = ParsePublicationOptions(rawValue);
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
        var definitionItems = definitions.RootElement.EnumerateArray().ToArray();
        EnsureConfiguration(
            Scalar(connection, transaction, "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId;", organizationId) == definitionItems.Length,
            "custom fields");
        foreach (var definition in definitionItems)
        {
            var key = RequiredJsonString(definition, "key", "custom_fields_invalid");
            var type = RequiredJsonString(definition, "type", "custom_fields_invalid");
            var label = RequiredJsonString(definition, "label", "custom_fields_invalid");
            var help = JsonStringStrict(definition, "helpText", "custom_fields_invalid");
            var enabled = JsonBool(definition, "enabled", true);
            var sortOrder = JsonInt(definition, "sortOrder", 0);
            using var command = new SqlCommand(
                "SELECT [Id], [FieldType], [Label], [HelpText], [IsEnabled], [SortOrder] FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId AND [FieldKey] = @fieldKey;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@fieldKey", key);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "custom field");
            var fieldId = reader.GetInt64(0);
            EnsureConfiguration(
                StringEquals(reader, 1, type) &&
                StringEquals(reader, 2, label) &&
                StringEquals(reader, 3, help) &&
                reader.GetBoolean(4) == enabled &&
                reader.GetInt32(5) == sortOrder,
                "custom field");
            counter.Rows++;
            counter.Fields += 5;
            reader.Close();

            var options = definition.TryGetProperty("options", out var optionElement) &&
                          optionElement.ValueKind == JsonValueKind.Array
                ? optionElement.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
            using var optionCount = new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @fieldId;",
                connection,
                transaction);
            optionCount.Parameters.AddWithValue("@fieldId", fieldId);
            EnsureConfiguration(Convert.ToInt32(optionCount.ExecuteScalar()) == options.Length, "custom field options");
            foreach (var option in options)
            {
                var optionKey = RequiredJsonString(option, "id", "custom_fields_invalid");
                var optionLabel = RequiredJsonString(option, "label", "custom_fields_invalid");
                var optionEnabled = JsonBool(option, "enabled", true);
                var optionSortOrder = JsonInt(option, "sortOrder", 0);
                using var optionCommand = new SqlCommand(
                    "SELECT [Label], [IsEnabled], [SortOrder] FROM [asap].[PatronCustomFieldOption] WHERE [PatronCustomFieldId] = @fieldId AND [OptionKey] = @optionKey;",
                    connection,
                    transaction);
                optionCommand.Parameters.AddWithValue("@fieldId", fieldId);
                optionCommand.Parameters.AddWithValue("@optionKey", optionKey);
                using var optionReader = optionCommand.ExecuteReader();
                EnsureConfiguration(optionReader.Read(), "custom field option");
                EnsureConfiguration(
                    StringEquals(optionReader, 0, optionLabel) &&
                    optionReader.GetBoolean(1) == optionEnabled &&
                    optionReader.GetInt32(2) == optionSortOrder,
                    "custom field option");
                counter.Fields += 3;
                counter.Relationships++;
            }
        }

        var scopedFormats = ReadScopedFormats(connection, transaction, organizationId);
        var effectiveFormats = scopedFormats
            .GroupBy(format => format.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                .ThenBy(format => format.Id)
                .First())
            .ToArray();
        var expectedRuleCount = effectiveFormats.Length * definitionItems.Length;
        EnsureConfiguration(
            Scalar(connection, transaction, "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId;", organizationId) == expectedRuleCount,
            "custom field format rules");
        foreach (var format in effectiveFormats)
        {
            foreach (var definition in definitionItems)
            {
                var fieldKey = RequiredJsonString(definition, "key", "custom_fields_invalid");
                var fieldId = ReadCustomFieldId(connection, transaction, organizationId, fieldKey);
                var expectedRule = ResolveCustomFieldRule(formatRules, format.Code, fieldKey, JsonBool(definition, "enabled", true));
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
        int organizationId,
        SourceRow row,
        ReconciliationCounter counter)
    {
        var formatRules = ParseRootObject(row.JsonText("patronFormatRules"));
        if (formatRules is null) return;
        var scopedFormats = ReadScopedFormats(connection, transaction, organizationId);
        foreach (var property in formatRules.Value.EnumerateObject())
        {
            var target = scopedFormats
                .Where(format => string.Equals(format.Code, property.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                .ThenBy(format => format.Id)
                .FirstOrDefault();
            if (target is null)
            {
                throw new MigrationOperationException(
                    "format_rule_format_unresolved",
                    $"The patron format rule for {property.Name} has no system or same-library material format.");
            }
            var expected = NormalizeFormatRule(property.Value, target.Code);
            using var command = new SqlCommand(
                "SELECT COALESCE(o.[MessageBehavior], f.[MessageBehavior]), COALESCE(o.[Message], f.[Message]), COALESCE(o.[TitleMode], f.[TitleMode]), COALESCE(o.[TitleLabel], f.[TitleLabel]), COALESCE(o.[AuthorMode], f.[AuthorMode]), COALESCE(o.[AuthorLabel], f.[AuthorLabel]), COALESCE(o.[IdentifierMode], f.[IdentifierMode]), COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]), COALESCE(o.[PublicationMode], f.[PublicationMode]), COALESCE(o.[PublicationLabel], f.[PublicationLabel]) FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE f.[Id] = @formatId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@formatId", target.Id);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "material format rule");
            EnsureConfiguration(
                StringEquals(reader, 0, EmptyAsNull(expected.MessageBehavior)) &&
                StringEquals(reader, 1, EmptyAsNull(expected.Message)) &&
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
        SourceRow row,
        string field,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var sourceValue = row.RequiredString(field);
        if (organizationIds.TryGetValue(sourceValue, out var mapped) && mapped != 1) return mapped;
        if (int.TryParse(sourceValue, out var organizationId) &&
            organizationId != 1 &&
            organizationIds.Values.Contains(organizationId))
        {
            return organizationId;
        }
        throw new MigrationOperationException(
            "settings_organization_unresolved",
            $"Source organization reference {sourceValue} cannot be resolved.");
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

    private static string NormalizeFormatCode(string value) => value.Trim().ToLowerInvariant() switch
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
        if (value is null) return null;
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
                "library" => ResolveOrganizationId(row, "libraryOrganization", organizationIds),
                _ => throw new MigrationOperationException("email_template_scope_invalid", "An email template has an invalid scope.")
            };
            var templateKey = source.IsRejection
                ? "rejection:" + (row.String("sourceTemplateId") ?? sourceId)
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
        foreach (var row in rows.OrderBy(item => item.RequiredString("id"), StringComparer.Ordinal))
        {
            var sourceId = row.RequiredString("id");
            var scope = row.RequiredString("scope").Trim().ToLowerInvariant();
            var ownerId = scope switch
            {
                "system" => 1,
                "library" => ResolveOrganizationId(row, "libraryOrganization", organizationIds),
                _ => throw new MigrationOperationException("format_scope_invalid", "A material format has an invalid scope.")
            };
            if (!formatIds.TryGetValue(sourceId, out var formatId))
            {
                throw new MigrationOperationException("reconciliation_failed", "An imported material format has no target identity.");
            }
            using var command = new SqlCommand(
                "SELECT f.[OwnerOrganizationId], f.[Code], COALESCE(o.[Label], f.[Label]), COALESCE(o.[SortOrder], f.[SortOrder]), COALESCE(o.[IsEnabled], f.[IsEnabled]), COALESCE(o.[MessageBehavior], f.[MessageBehavior]), COALESCE(o.[Message], f.[Message]), COALESCE(o.[TitleMode], f.[TitleMode]), COALESCE(o.[TitleLabel], f.[TitleLabel]), COALESCE(o.[AuthorMode], f.[AuthorMode]), COALESCE(o.[AuthorLabel], f.[AuthorLabel]), COALESCE(o.[IdentifierMode], f.[IdentifierMode]), COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]), COALESCE(o.[PublicationMode], f.[PublicationMode]), COALESCE(o.[PublicationLabel], f.[PublicationLabel]), f.[CreatedUtc], f.[UpdatedUtc] FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = @organizationId WHERE f.[Id] = @formatId;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", ownerId);
            command.Parameters.AddWithValue("@formatId", formatId);
            using var reader = command.ExecuteReader();
            EnsureConfiguration(reader.Read(), "material format");
            var targetOwnerId = reader.GetInt32(0);
            var sparseOverride = scope == "library" && targetOwnerId == 1;
            EnsureConfiguration(
                sparseOverride || targetOwnerId == ownerId,
                "material format ownership");
            EnsureConfiguration(StringEquals(reader, 1, NormalizeFormatCode(row.RequiredString("code"))), "material format code");
            var fields = 2;
            if (!sparseOverride)
            {
                EnsureConfiguration(
                    StringEquals(reader, 2, row.RequiredText("label")) &&
                    IntEquals(reader, 3, row.Int32("sortOrder") ?? 0) &&
                    BoolEquals(reader, 4, row.Bool("enabled", true)) &&
                    StringEquals(reader, 5, NormalizeOptionalEnum(row.String("messageBehavior"), ["none", "message", "ebookMessage", "eaudiobookMessage"], "format_message_behavior_invalid")) &&
                    StringEquals(reader, 6, row.Text("message")) &&
                    StringEquals(reader, 7, NormalizeOptionalEnum(row.String("titleMode"), ["required"], "format_title_mode_invalid") ?? "required") &&
                    StringEquals(reader, 8, row.Text("titleLabel")) &&
                    StringEquals(reader, 9, NormalizeOptionalEnum(row.String("authorMode"), ["required", "optional", "hidden"], "format_author_mode_invalid")) &&
                    StringEquals(reader, 10, row.Text("authorLabel")) &&
                    StringEquals(reader, 11, NormalizeOptionalEnum(row.String("identifierMode"), ["required", "optional", "hidden"], "format_identifier_mode_invalid")) &&
                    StringEquals(reader, 12, row.Text("identifierLabel")) &&
                    StringEquals(reader, 13, NormalizeOptionalEnum(row.String("publicationMode"), ["required", "optional", "hidden"], "format_publication_mode_invalid")) &&
                    StringEquals(reader, 14, row.Text("publicationLabel")) &&
                    DateEquals(reader, 15, row.UtcDateTime("created") ?? exportedAtUtc) &&
                    DateEquals(reader, 16, row.UtcDateTime("updated") ?? exportedAtUtc),
                    "material format");
                fields += 15;
            }
            else
            {
                EnsureSparseFormatValue(reader, 2, row, "label", value => value, "material format label");
                EnsureSparseFormatValue(reader, 3, row, "sortOrder", value => value.Int32("sortOrder"), "material format sort order");
                EnsureSparseFormatValue(reader, 4, row, "enabled", value => value.NullableBool("enabled"), "material format enabled");
                EnsureSparseFormatValue(reader, 5, row, "messageBehavior", value => NormalizeOptionalEnum(value.String("messageBehavior"), ["none", "message", "ebookMessage", "eaudiobookMessage"], "format_message_behavior_invalid"), "material format message behavior");
                EnsureSparseFormatValue(reader, 6, row, "message", value => value.String("message"), "material format message");
                EnsureSparseFormatValue(reader, 7, row, "titleMode", value => NormalizeOptionalEnum(value.String("titleMode"), ["required"], "format_title_mode_invalid"), "material format title mode");
                EnsureSparseFormatValue(reader, 8, row, "titleLabel", value => value.String("titleLabel"), "material format title label");
                EnsureSparseFormatValue(reader, 9, row, "authorMode", value => NormalizeOptionalEnum(value.String("authorMode"), ["required", "optional", "hidden"], "format_author_mode_invalid"), "material format author mode");
                EnsureSparseFormatValue(reader, 10, row, "authorLabel", value => value.String("authorLabel"), "material format author label");
                EnsureSparseFormatValue(reader, 11, row, "identifierMode", value => NormalizeOptionalEnum(value.String("identifierMode"), ["required", "optional", "hidden"], "format_identifier_mode_invalid"), "material format identifier mode");
                EnsureSparseFormatValue(reader, 12, row, "identifierLabel", value => value.String("identifierLabel"), "material format identifier label");
                EnsureSparseFormatValue(reader, 13, row, "publicationMode", value => NormalizeOptionalEnum(value.String("publicationMode"), ["required", "optional", "hidden"], "format_publication_mode_invalid"), "material format publication mode");
                EnsureSparseFormatValue(reader, 14, row, "publicationLabel", value => value.String("publicationLabel"), "material format publication label");
                fields += row.Names.Count(name => name is "label" or "sortOrder" or "enabled" or "messageBehavior" or "message" or "titleMode" or "titleLabel" or "authorMode" or "authorLabel" or "identifierMode" or "identifierLabel" or "publicationMode" or "publicationLabel");
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
        string sourceField,
        Func<SourceRow, object?> expectedValue,
        string entity)
    {
        if (!row.HasValue(sourceField)) return;
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
        command.Parameters.AddWithValue("@pageTitle", Db(ScopedText(row, "pageTitle", isSystem)));
        command.Parameters.AddWithValue("@barcodeLabel", Db(ScopedText(row, "barcodeLabel", isSystem)));
        command.Parameters.AddWithValue("@pinLabel", Db(ScopedText(row, "pinLabel", isSystem)));
        command.Parameters.AddWithValue("@loginPrompt", Db(ScopedText(row, "loginPrompt", isSystem)));
        command.Parameters.AddWithValue("@loginNote", Db(ScopedText(row, "loginNote", isSystem)));
        command.Parameters.AddWithValue("@suggestionFormNote", Db(ScopedText(row, "suggestionFormNote", isSystem)));
        command.Parameters.AddWithValue("@noEmailMessage", Db(ScopedText(row, "noEmailMessage", isSystem)));
        command.Parameters.AddWithValue("@successTitle", Db(ScopedText(row, "successTitle", isSystem)));
        command.Parameters.AddWithValue("@successMessage", Db(ScopedText(row, "successMessage", isSystem)));
        command.Parameters.AddWithValue("@alreadySubmittedMessage", Db(ScopedText(row, "alreadySubmittedMessage", isSystem)));
        command.Parameters.AddWithValue("@ebookMessage", Db(ScopedText(row, "ebookMessage", isSystem)));
        command.Parameters.AddWithValue("@eaudiobookMessage", Db(ScopedText(row, "eaudiobookMessage", isSystem)));
        AddDuplicateLabelParameters(command, isSystem ? row : null, null);
        command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
        command.ExecuteNonQuery();
    }

    private static void ApplyPatronOverride(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        DateTime exportedAtUtc)
    {
        var labels = ParseObject(row.JsonText("duplicateStatusLabels"));
        using var command = new SqlCommand(
            """
            UPDATE [asap].[PatronSettings]
            SET [EbookMessage] = @ebookMessage, [EaudiobookMessage] = @eaudiobookMessage,
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
        command.Parameters.AddWithValue("@ebookMessage", Db(ScopedText(row, "ebookMessage", isSystem: false)));
        command.Parameters.AddWithValue("@eaudiobookMessage", Db(ScopedText(row, "eaudiobookMessage", isSystem: false)));
        AddDuplicateLabelParameters(command, row, labels);
        command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
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
        var labels = ParseObject(row.JsonText("duplicateRequestStatusLabels"));
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
        command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
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
        command.Parameters.AddWithValue("@updatedUtc", updatedUtc);
        command.ExecuteNonQuery();
    }

    private static void ReplacePublicationOptions(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }
        var options = ParsePublicationOptions(rawValue);
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

    private static IReadOnlyList<PublicationImportOption> ParsePublicationOptions(string rawValue)
    {
        var rawOptions = new List<(string? Id, string Label, bool Enabled, int? SortOrder)>();
        var trimmed = rawValue.Trim();
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
                    var label = JsonStringStrict(item, "label", "publication_options_invalid") ??
                                JsonStringStrict(item, "name", "publication_options_invalid") ??
                                JsonStringStrict(item, "value", "publication_options_invalid") ??
                                string.Empty;
                    if (label.Length == 0)
                    {
                        throw new MigrationOperationException(
                            "publication_options_invalid",
                            "A publication option must have a nonblank label.");
                    }
                    var sortOrder = item.TryGetProperty("sortOrder", out var sortElement)
                        ? sortElement.ValueKind == JsonValueKind.Number && sortElement.TryGetInt32(out var parsedOrder)
                            ? parsedOrder
                            : throw new MigrationOperationException(
                                "publication_options_invalid",
                                "A publication option sortOrder must be an integer.")
                        : (int?)null;
                    rawOptions.Add((
                        JsonStringStrict(item, "id", "publication_options_invalid"),
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
                trimmed
                    .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(label => ((string?)null, label, true, (int?)null)));
        }

        var result = new List<PublicationImportOption>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rawOptions.Count; index++)
        {
            var raw = rawOptions[index];
            var label = raw.Label.Trim();
            if (label.Length == 0 || !seenLabels.Add(label))
            {
                throw new MigrationOperationException(
                    "publication_options_conflict",
                    "Publication options contain a blank or duplicate label.");
            }
            var key = string.IsNullOrWhiteSpace(raw.Id)
                ? OptionIdFromLabel(label, $"option_{index + 1}")
                : raw.Id.Trim();
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
                raw.SortOrder ?? (index + 1) * 10));
        }
        return result;
    }

    private static bool JsonBoolStrict(JsonElement value, string property, bool defaultValue)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item))
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
            label.Trim().ToLowerInvariant(),
            "[^a-z0-9]+",
            "-").Trim('-');
        return key.Length == 0 ? fallback : key;
    }

    private static void ImportCustomFields(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        SourceRow row,
        ICollection<object> transformations)
    {
        var formatRules = ParseRootObject(row.JsonText("patronFormatRules"));
        var scopedFormats = ReadScopedFormats(connection, transaction, organizationId);
        if (formatRules is not null)
        {
            foreach (var ruleProperty in formatRules.Value.EnumerateObject())
            {
                var target = scopedFormats
                    .Where(format => string.Equals(format.Code, ruleProperty.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                    .ThenBy(format => format.Id)
                    .FirstOrDefault();
                if (target is null)
                {
                    throw new MigrationOperationException(
                        "format_rule_format_unresolved",
                        $"The patron format rule for {ruleProperty.Name} has no system or same-library material format.");
                }
                ApplyFormatFieldRules(
                    connection,
                    transaction,
                    organizationId,
                    target,
                    ruleProperty.Value);
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
        var fields = new List<(long Id, string Key, bool Enabled)>();
        var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions.RootElement.EnumerateArray())
        {
            var key = RequiredJsonString(definition, "key", "custom_fields_invalid");
            var type = RequiredJsonString(definition, "type", "custom_fields_invalid");
            if (!fieldKeys.Add(key))
            {
                throw new MigrationOperationException(
                    "custom_fields_conflict",
                    $"Library {organizationId} contains duplicate custom field key {key}.");
            }
            if (type is not ("text" or "textarea" or "select"))
            {
                throw new MigrationOperationException("custom_fields_invalid", $"Custom field {key} has an invalid type.");
            }
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
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@type", type);
            insert.Parameters.AddWithValue("@label", RequiredJsonString(definition, "label", "custom_fields_invalid"));
            insert.Parameters.AddWithValue("@help", Db(JsonStringStrict(definition, "helpText", "custom_fields_invalid")));
            var enabled = JsonBool(definition, "enabled", true);
            insert.Parameters.AddWithValue("@enabled", enabled);
            insert.Parameters.AddWithValue("@sortOrder", JsonInt(definition, "sortOrder", 0));
            var fieldId = Convert.ToInt64(insert.ExecuteScalar());
            fields.Add((fieldId, key, enabled));

            if (definition.TryGetProperty("options", out var options) &&
                options.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new MigrationOperationException(
                    "custom_fields_invalid",
                    $"Custom field {key} options must be an array.");
            }
            if (definition.TryGetProperty("options", out options) && options.ValueKind == JsonValueKind.Array)
            {
                var optionKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in options.EnumerateArray())
                {
                    var optionKey = RequiredJsonString(option, "id", "custom_fields_invalid");
                    if (!optionKeys.Add(optionKey))
                    {
                        throw new MigrationOperationException(
                            "custom_fields_conflict",
                            $"Custom field {key} contains duplicate option key {optionKey}.");
                    }
                    using var optionInsert = new SqlCommand(
                        "INSERT INTO [asap].[PatronCustomFieldOption] ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder]) VALUES (@fieldId, @key, @label, @enabled, @sortOrder);",
                        connection,
                        transaction);
                    optionInsert.Parameters.AddWithValue("@fieldId", fieldId);
                    optionInsert.Parameters.AddWithValue("@key", optionKey);
                    optionInsert.Parameters.AddWithValue("@label", RequiredJsonString(option, "label", "custom_fields_invalid"));
                    optionInsert.Parameters.AddWithValue("@enabled", JsonBool(option, "enabled", true));
                    optionInsert.Parameters.AddWithValue("@sortOrder", JsonInt(option, "sortOrder", 0));
                    optionInsert.ExecuteNonQuery();
                }
            }
        }

        var effectiveFormats = scopedFormats
            .GroupBy(format => format.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(format => format.OwnerOrganizationId == organizationId ? 0 : 1)
                .ThenBy(format => format.Id)
                .First())
            .OrderBy(format => format.Id)
            .ToArray();
        foreach (var format in effectiveFormats)
        {
            foreach (var field in fields)
            {
                var (mode, labelOverride) = ResolveCustomFieldRule(formatRules, format.Code, field.Key, field.Enabled);
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
            absentOrDisabledRule = "hidden"
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

    private static (string Mode, string? LabelOverride) ResolveCustomFieldRule(
        JsonElement? formatRules,
        string formatCode,
        string fieldKey,
        bool fieldEnabled)
    {
        if (!fieldEnabled || formatRules is null ||
            !formatRules.Value.TryGetProperty(formatCode, out var format) ||
            !format.TryGetProperty("customFields", out var customFields) ||
            !customFields.TryGetProperty(fieldKey, out var rule))
        {
            return ("hidden", null);
        }
        var mode = (JsonStringStrict(rule, "mode", "custom_field_rule_invalid") ?? "hidden").ToLowerInvariant();
        if (mode is not ("required" or "optional" or "hidden"))
        {
            throw new MigrationOperationException("custom_field_rule_invalid", $"Custom field {fieldKey} has an invalid mode.");
        }
        return (
            mode,
            JsonStringStrict(rule, "labelOverride", "custom_field_rule_invalid") ??
            JsonStringStrict(rule, "label", "custom_field_rule_invalid"));
    }

    private static void ApplyFormatFieldRules(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        ScopedFormat target,
        JsonElement format)
    {
        if (format.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var normalized = NormalizeFormatRule(format, target.Code);
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
            Different(normalized.Message, baseColumns.Message ?? string.Empty) ? EmptyAsNull(normalized.Message) : null,
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
        command.Parameters.AddWithValue("@message", EmptyAsNull(value.Message) is { } message ? message : DBNull.Value);
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
        !string.Equals(value.Trim(), baseline.Trim(), StringComparison.Ordinal);

    private static string? EmptyAsNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static NormalizedFormatRule NormalizeFormatRule(JsonElement format, string formatCode)
    {
        var defaults = DefaultFormatRule(formatCode);
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
            JsonStringStrict(format, "messageBehavior", "format_message_behavior_invalid"),
            defaults.MessageBehavior,
            ["none", "message", "ebookMessage", "eaudiobookMessage"],
            "format_message_behavior_invalid");
        return new NormalizedFormatRule(
            behavior,
            JsonStringStrict(format, "message", "format_message_invalid") ?? defaults.Message,
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
            JsonStringStrict(field, "mode", "format_field_mode_invalid"),
            defaultMode,
            ["required", "optional", "hidden"],
            "format_field_mode_invalid");
        return (
            forceRequired ? "required" : mode,
            JsonStringStrict(field, "label", "format_field_label_invalid") ?? defaultLabel);
    }

    private static string NormalizeFormatEnum(
        string? value,
        string fallback,
        string[] allowed,
        string errorCode)
    {
        if (value is null) return fallback;
        var normalized = value.Trim();
        var canonical = allowed.FirstOrDefault(
            item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        return canonical ?? throw new MigrationOperationException(
            errorCode,
            $"Unknown material-format value: {value}");
    }

    private static NormalizedFormatRule DefaultFormatRule(string formatCode) =>
        formatCode.ToLowerInvariant() switch
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
        if (alt is null) return;
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
        command.Parameters.AddWithValue("@updatedUtc", updatedUtc);
        command.ExecuteNonQuery();
    }

    private static int ResolveScopedOrganization(SourceRow row, IReadOnlyDictionary<string, int> organizationIds)
    {
        var scope = row.RequiredString("scope").ToLowerInvariant();
        if (scope == "system") return 1;
        if (scope != "library") throw new MigrationOperationException("settings_scope_invalid", "A settings row has an invalid scope.");
        var sourceOrganization = row.RequiredString("libraryOrganization");
        if (organizationIds.TryGetValue(sourceOrganization, out var organizationId) && organizationId != 1) return organizationId;
        if (int.TryParse(sourceOrganization, out organizationId) &&
            organizationId != 1 &&
            organizationIds.Values.Contains(organizationId))
        {
            return organizationId;
        }
        throw new MigrationOperationException("settings_organization_unresolved", "A library settings row has an unresolved organization.");
    }

    private static int ResolveLibraryOrganization(
        SourceRow row,
        string field,
        IReadOnlyDictionary<string, int> organizationIds)
    {
        var sourceOrganization = row.RequiredString(field);
        if (organizationIds.TryGetValue(sourceOrganization, out var organizationId) && organizationId != 1)
        {
            return organizationId;
        }
        if (int.TryParse(sourceOrganization, out organizationId) &&
            organizationId != 1 &&
            organizationIds.Values.Contains(organizationId))
        {
            return organizationId;
        }
        throw new MigrationOperationException(
            "settings_organization_unresolved",
            "A library settings row has an unresolved organization.");
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

    private static string? ScopedText(SourceRow row, string field, bool isSystem) =>
        isSystem
            ? row.Text(field)
            : string.IsNullOrWhiteSpace(row.Text(field)) ? null : row.Text(field)!.Trim();

    private static IReadOnlyList<string> SplitValues(string? source) =>
        string.IsNullOrWhiteSpace(source)
            ? []
            : source.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => value.Length > 0)
                .ToArray();

    private static IReadOnlyDictionary<string, string> ParseObject(string? json)
    {
        if (json is null) return new Dictionary<string, string>(StringComparer.Ordinal);
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
            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()?.Trim()
                : null;
            if (!string.IsNullOrWhiteSpace(value) && !result.TryAdd(property.Name, value))
            {
                throw new MigrationOperationException(
                    "settings_json_invalid",
                    $"Settings JSON contains duplicate property {property.Name}.");
            }
        }
        return result;
    }

    private static JsonElement? ParseRootObject(string? json)
    {
        if (json is null) return null;
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
