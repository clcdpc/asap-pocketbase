using Asap.Shared;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

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
        ImportSystemSettings(connection, transaction, package, exportedAtUtc, importedCounts);
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
        IDictionary<string, int> importedCounts)
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
        var staffUrl = settings
            .GetProperty("StaffApplicationUrl")
            .GetProperty("value")
            .GetString();
        var iconPattern = settings
            .GetProperty("MaterialTypeIconUrlPattern")
            .GetProperty("value")
            .GetString();
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
                    [SystemPolarisUserId] = @userId, [OrganizationIdForRequests] = @requestingOrgId,
                    [PickupOrganizationId] = @pickupOrgId, [UpdatedUtc] = @updatedUtc
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
            command.Parameters.AddWithValue("@requestingOrgId", Db(row.Int32("requestingOrgId")));
            command.Parameters.AddWithValue("@pickupOrgId", Db(row.Int32("pickupOrgId")));
            command.Parameters.AddWithValue("@updatedUtc", row.UtcDateTime("updated") ?? exportedAtUtc);
            command.ExecuteNonQuery();
            transformations.Add(new
            {
                entity = "polaris_settings",
                sourceId = row.RequiredString("id"),
                apiKeyProtected = apiKey is not null,
                adminPasswordProtected = adminPassword is not null
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
            using var command = new SqlCommand(
                isSystem
                    ? """
                      UPDATE [asap].[ExternalSearchProvider]
                      SET [IsEnabled] = @enabled, [Label] = @label, [UrlTemplate] = @url
                      WHERE [ProviderKey] = @key;
                      """
                    : """
                      INSERT INTO [asap].[ExternalSearchProviderOverride]
                          ([LibraryOrganizationId], [ExternalSearchProviderId], [IsEnabled], [Label], [UrlTemplate])
                      SELECT @organizationId, [Id], @enabled, @label, @url
                      FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = @key;
                      """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            command.Parameters.AddWithValue("@key", $"external_search_{slot}");
            command.Parameters.AddWithValue("@enabled", Db(isSystem ? row.Bool(enabledField, defaults[slot - 1].Item1) : row.NullableBool(enabledField)));
            command.Parameters.AddWithValue("@label", Db(isSystem ? row.Text(labelField) ?? defaults[slot - 1].Item2 : ScopedText(row, labelField, false)));
            command.Parameters.AddWithValue("@url", Db(isSystem ? row.Text(urlField) ?? defaults[slot - 1].Item3 : ScopedText(row, urlField, false)));
            command.ExecuteNonQuery();
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
                var ignoredFields = LegacyUiDuplicateLabelFields.Where(row.HasValue).ToArray();
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
            ReplacePublicationOptions(connection, transaction, organizationId, row.Text("publicationOptions"));
            UpsertBrandingAlt(connection, transaction, organizationId, row.Text("logoAlt"), row.UtcDateTime("updated") ?? exportedAtUtc);
        }

        var overrides = MigrationPackageReader.ReadRows(package, "patron-settings.json", "patron_settings_overrides");
        var modernOverrideOrganizations = new HashSet<int>();
        foreach (var row in overrides.OrderBy(item => item.RequiredString("orgId"), StringComparer.Ordinal))
        {
            var organizationId = row.Int32("orgId") ?? throw new MigrationOperationException("patron_override_org_invalid", "A patron settings override has no organization.");
            if (organizationId == 1) throw new MigrationOperationException("patron_override_org_invalid", "A patron override cannot belong to the system organization.");
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
                        continue;
                    }
                    var label = JsonString(item, "label") ??
                                JsonString(item, "name") ??
                                JsonString(item, "value") ??
                                string.Empty;
                    rawOptions.Add((
                        JsonString(item, "id"),
                        label,
                        JsonBool(item, "enabled", true),
                        item.TryGetProperty("sortOrder", out var sortOrder) &&
                        sortOrder.TryGetInt32(out var parsedOrder) &&
                        parsedOrder != 0
                            ? parsedOrder
                            : null));
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
                continue;
            }
            var key = string.IsNullOrWhiteSpace(raw.Id)
                ? OptionIdFromLabel(label, $"option_{index + 1}")
                : raw.Id.Trim();
            var baseKey = key;
            var suffix = 2;
            while (!seenIds.Add(key))
            {
                key = $"{baseKey}_{suffix++}";
            }
            result.Add(new PublicationImportOption(
                key,
                label,
                raw.Enabled,
                raw.SortOrder ?? (index + 1) * 10));
        }
        return result;
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
        foreach (var definition in definitions.RootElement.EnumerateArray())
        {
            var key = RequiredJsonString(definition, "key", "custom_fields_invalid");
            var type = RequiredJsonString(definition, "type", "custom_fields_invalid");
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
            insert.Parameters.AddWithValue("@help", Db(JsonString(definition, "helpText")));
            var enabled = JsonBool(definition, "enabled", true);
            insert.Parameters.AddWithValue("@enabled", enabled);
            insert.Parameters.AddWithValue("@sortOrder", JsonInt(definition, "sortOrder", 0));
            var fieldId = Convert.ToInt64(insert.ExecuteScalar());
            fields.Add((fieldId, key, enabled));

            if (definition.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in options.EnumerateArray())
                {
                    using var optionInsert = new SqlCommand(
                        "INSERT INTO [asap].[PatronCustomFieldOption] ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder]) VALUES (@fieldId, @key, @label, @enabled, @sortOrder);",
                        connection,
                        transaction);
                    optionInsert.Parameters.AddWithValue("@fieldId", fieldId);
                    optionInsert.Parameters.AddWithValue("@key", RequiredJsonString(option, "id", "custom_fields_invalid"));
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
        var mode = JsonString(rule, "mode") ?? "hidden";
        if (mode is not ("required" or "optional" or "hidden"))
        {
            throw new MigrationOperationException("custom_field_rule_invalid", $"Custom field {fieldKey} has an invalid mode.");
        }
        return (mode, JsonString(rule, "labelOverride") ?? JsonString(rule, "label"));
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
        var fields = format.TryGetProperty("fields", out var incomingFields) &&
                     incomingFields.ValueKind == JsonValueKind.Object
            ? incomingFields
            : default;
        var title = NormalizeFormatField(fields, "title", defaults.TitleMode, defaults.TitleLabel, forceRequired: true);
        var author = NormalizeFormatField(fields, "author", defaults.AuthorMode, defaults.AuthorLabel, forceRequired: false);
        var identifier = NormalizeFormatField(fields, "identifier", defaults.IdentifierMode, defaults.IdentifierLabel, forceRequired: false);
        var publication = NormalizeFormatField(fields, "publication", defaults.PublicationMode, defaults.PublicationLabel, forceRequired: false);
        var behavior = NormalizeFormatEnum(
            JsonString(format, "messageBehavior"),
            defaults.MessageBehavior,
            ["none", "message", "ebookMessage", "eaudiobookMessage"],
            "format_message_behavior_invalid");
        return new NormalizedFormatRule(
            behavior,
            JsonString(format, "message") ?? defaults.Message,
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
        var field = fields.ValueKind == JsonValueKind.Object &&
                    fields.TryGetProperty(key, out var incoming) &&
                    incoming.ValueKind == JsonValueKind.Object
            ? incoming
            : default;
        var mode = NormalizeFormatEnum(
            JsonString(field, "mode"),
            defaultMode,
            ["required", "optional", "hidden"],
            "format_field_mode_invalid");
        return (forceRequired ? "required" : mode, JsonString(field, "label") ?? defaultLabel);
    }

    private static string NormalizeFormatEnum(
        string? value,
        string fallback,
        string[] allowed,
        string errorCode)
    {
        if (value is null) return fallback;
        return allowed.Contains(value) ? value : fallback;
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
        if (int.TryParse(sourceOrganization, out organizationId) && organizationId != 1) return organizationId;
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
        if (int.TryParse(sourceOrganization, out organizationId) && organizationId != 1)
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
        return document.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
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
        JsonString(value, property) is { Length: > 0 } result
            ? result
            : throw new MigrationOperationException(errorCode, $"Required JSON property {property} is missing.");

    private static string? JsonString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!.Trim()
            : null;

    private static bool JsonBool(JsonElement value, string property, bool defaultValue) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? item.GetBoolean()
            : defaultValue;

    private static int JsonInt(JsonElement value, string property, int defaultValue) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var parsed)
            ? parsed
            : defaultValue;

    private static object Db(object? value) => value ?? DBNull.Value;
}
