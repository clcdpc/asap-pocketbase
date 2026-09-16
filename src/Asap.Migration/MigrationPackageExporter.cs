using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Shared;
using Microsoft.Data.Sqlite;

namespace Asap.Migration;

public sealed record MigrationExportOptions(
    string SourceDatabasePath,
    string StoragePath,
    string OutputPath,
    string SourceGitSha,
    DateTimeOffset ExportedAtUtc,
    bool SourceStoppedConfirmed);

public static class MigrationPackageExporter
{
    public const int FormatVersion = 1;
    internal const string LegacySmtpTransportExcludedWarning = "legacy_smtp_transport_excluded";
    internal const string PocketBaseAuthSessionSchedulerStateExcludedWarning =
        "pocketbase_auth_session_scheduler_state_excluded";

    internal static IReadOnlySet<string> ManifestWarningCodes { get; } =
        new HashSet<string>(
            [
                LegacySmtpTransportExcludedWarning,
                PocketBaseAuthSessionSchedulerStateExcludedWarning
            ],
            StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyList<MigrationDomain> Domains =
    [
        new("organizations", "organizations.json", ["polaris_organizations"]),
        new("staff-users", "staff-users.json", ["staff_users"]),
        new("system-settings", "system-settings.json", ["system_settings"]),
        new("polaris-settings", "polaris-settings.json", ["polaris_settings"]),
        new("workflow-settings", "workflow-settings.json", ["workflow_settings"]),
        new("patron-settings", "patron-settings.json", ["ui_settings", "patron_settings_overrides", "patron_library_settings", "library_settings"]),
        new("email-settings", "email-settings.json", ["smtp_settings"]),
        new("material-formats", "material-formats.json", ["material_formats"]),
        new("format-auto-claim-rules", "format-auto-claim-rules.json", ["format_claim_rules"]),
        new("workflow-tags", "workflow-tags.json", ["workflow_tags"]),
        new("title-requests", "title-requests.json", ["title_requests"]),
        new("title-request-tags", "title-request-tags.json", ["title_request_tags"]),
        new("title-request-events", "title-request-events.json", ["request_statuses", "request_close_reasons", "title_request_events"]),
        new("email-templates", "email-templates.json", ["email_templates", "rejection_templates"]),
        new("email-delivery-events", "email-delivery-events.json", ["email_delivery_events"]),
        new("deleted-request-audit", "deleted-request-audit.json", ["deleted_request_audit"]),
        new("additional-copy-requests", "additional-copy-requests.json", ["additional_copy_requests"])
    ];

    internal static IReadOnlySet<string> RequiredPackageFiles { get; } =
        Domains
            .Select(domain => domain.FileName)
            .Concat(new[]
            {
                "branding.json",
                "effective-legacy-runtime-config.json",
                "effective-legacy-operational-config.json"
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> RequiredCollections { get; } =
        Domains.ToDictionary(
            domain => domain.FileName,
            domain => (IReadOnlySet<string>)domain.Collections.ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, HashSet<string>> ExcludedColumns =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["staff_users"] = new(
                ["password", "tokenKey", "authTokenKey", "resetTokenKey", "verificationTokenKey", "emailChangeTokenKey"],
                StringComparer.OrdinalIgnoreCase),
            ["smtp_settings"] = new(
                ["host", "port", "username", "password", "tls"],
                StringComparer.OrdinalIgnoreCase),
            ["system_settings"] = new(["allowedStaffUsers"], StringComparer.OrdinalIgnoreCase)
        };

    public static void Export(MigrationExportOptions options)
    {
        ValidateOptions(options);
        Directory.CreateDirectory(options.OutputPath);
        if (Directory.EnumerateFileSystemEntries(options.OutputPath).Any())
        {
            throw new MigrationOperationException("export_output_not_empty", "The export output directory must be empty.");
        }

        var sourceDatabasePath = Path.GetFullPath(options.SourceDatabasePath);
        var sourceSnapshot = CaptureSourceSnapshot(sourceDatabasePath);
        var entityCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var manifestFiles = new List<object>();

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = sourceDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ConnectionString);
        connection.Open();
        VerifyIntegrity(connection);
        using var transaction = connection.BeginTransaction();
        var sourceSchemaVersion = ReadSourceSchemaVersion(connection, transaction);
        ValidateRequiredSourceCollections(connection, transaction);

        foreach (var domain in Domains)
        {
            var collections = new SortedDictionary<string, IReadOnlyList<Dictionary<string, object?>>>(StringComparer.Ordinal);
            foreach (var collection in domain.Collections)
            {
                var rows = ReadCollection(connection, transaction, collection);
                collections.Add(collection, rows);
                entityCounts[collection] = rows.Count;
            }

            var path = Path.Combine(options.OutputPath, domain.FileName);
            WriteJson(path, new
            {
                formatVersion = FormatVersion,
                domain = domain.Name,
                collections
            });
            manifestFiles.Add(FileManifest(options.OutputPath, path));
        }

        var brandingRows = ExportBranding(
            connection,
            transaction,
            options.StoragePath,
            options.OutputPath,
            manifestFiles);
        entityCounts["branding"] = brandingRows.Count;
        var brandingPath = Path.Combine(options.OutputPath, "branding.json");
        WriteJson(brandingPath, new
        {
            formatVersion = FormatVersion,
            domain = "branding",
            collections = new { branding = brandingRows }
        });
        manifestFiles.Add(FileManifest(options.OutputPath, brandingPath));

        var runtimePath = Path.Combine(options.OutputPath, "effective-legacy-runtime-config.json");
        WriteJson(
            runtimePath,
            MigrationEffectiveConfiguration.Runtime(
                ReadCollection(connection, transaction, "system_settings"),
                ReadCollection(connection, transaction, "polaris_settings"),
                options.SourceGitSha,
                options.ExportedAtUtc));
        manifestFiles.Add(FileManifest(options.OutputPath, runtimePath));

        var operationalPath = Path.Combine(options.OutputPath, "effective-legacy-operational-config.json");
        WriteJson(
            operationalPath,
            MigrationEffectiveConfiguration.Operational(options.SourceGitSha, options.ExportedAtUtc));
        manifestFiles.Add(FileManifest(options.OutputPath, operationalPath));

        transaction.Commit();
        transaction.Dispose();
        connection.Dispose();
        EnsureSourceSnapshotUnchanged(sourceDatabasePath, sourceSnapshot);

        WriteJson(Path.Combine(options.OutputPath, "manifest.json"), new
        {
            formatVersion = FormatVersion,
            contractVersion = MigrationContract.ContractVersion,
            pocketBaseSourceGitSha = options.SourceGitSha.ToLowerInvariant(),
            pocketBaseSourceSchemaVersion = sourceSchemaVersion,
            exportedAtUtc = options.ExportedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            sourceDatabase = new
            {
                fileName = sourceSnapshot.Main.FileName,
                length = sourceSnapshot.Main.Length,
                sha256 = sourceSnapshot.Main.Sha256,
                wal = WalManifest(sourceSnapshot.Wal)
            },
            entityCounts,
            files = manifestFiles,
            warnings = new[]
            {
                LegacySmtpTransportExcludedWarning,
                PocketBaseAuthSessionSchedulerStateExcludedWarning
            }
        });
    }

    private static IReadOnlyList<Dictionary<string, object?>> ExportBranding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string storagePath,
        string outputPath,
        ICollection<object> manifestFiles)
    {
        if (!TableExists(connection, transaction, "ui_settings"))
        {
            return [];
        }
        if (!TableExists(connection, transaction, "_collections"))
        {
            throw new MigrationOperationException(
                "source_collection_metadata_missing",
                "PocketBase collection metadata is required to export branding assets.");
        }

        string? collectionId;
        using (var collection = connection.CreateCommand())
        {
            collection.Transaction = transaction;
            collection.CommandText =
                "SELECT [id] FROM [_collections] WHERE [name] = 'ui_settings' LIMIT 1;";
            collectionId = Convert.ToString(collection.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            throw new MigrationOperationException(
                "source_collection_metadata_missing",
                "The PocketBase ui_settings collection ID could not be resolved.");
        }

        var rows = ReadCollection(connection, transaction, "ui_settings");
        var exported = new List<Dictionary<string, object?>>();
        foreach (var row in rows.OrderBy(item => TextValue(item, "id"), StringComparer.Ordinal))
        {
            var fileName = TextValue(row, "logo");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                string.Equals(Path.GetExtension(fileName), ".svg", StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException(
                    "branding_asset_unsupported",
                    "A source branding logo has an unsupported file name or SVG content.");
            }
            var recordId = TextValue(row, "id")
                ?? throw new MigrationOperationException("branding_record_invalid", "A branding record has no source ID.");
            var sourcePath = ResolveWithin(
                storagePath,
                Path.Combine(collectionId, recordId, fileName));
            if (!File.Exists(sourcePath))
            {
                throw new MigrationOperationException(
                    "branding_asset_missing",
                    "A configured PocketBase branding logo is missing from file storage.");
            }

            var sourceData = File.ReadAllBytes(sourcePath);
            if (!LogoImageValidator.TryDetectContentType(sourceData, out var contentType))
            {
                throw new MigrationOperationException(
                    "branding_asset_invalid",
                    "A source branding logo is not a supported PNG, JPEG, or GIF image.");
            }
            if (!LogoImageValidator.TryValidate(sourceData, contentType, out _, out var imageError))
            {
                throw new MigrationOperationException("branding_asset_invalid", imageError);
            }
            var relativeAssetPath = Path.Combine("assets", "branding", recordId, fileName);
            var targetPath = ResolveWithin(outputPath, relativeAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: false);
            var hash = HashFile(targetPath);
            var length = new FileInfo(targetPath).Length;
            exported.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceCollectionId"] = collectionId,
                ["sourceRecordId"] = recordId,
                ["scope"] = TextValue(row, "scope"),
                ["libraryOrganization"] = TextValue(row, "libraryOrganization"),
                ["fileName"] = fileName,
                ["contentType"] = contentType,
                ["length"] = length,
                ["sha256"] = hash,
                ["assetPath"] = relativeAssetPath.Replace(Path.DirectorySeparatorChar, '/'),
                ["logoAlt"] = TextValue(row, "logoAlt")
            });
            manifestFiles.Add(FileManifest(outputPath, targetPath));
        }
        return exported;
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException(
                "branding_asset_path_invalid",
                "A branding asset path escapes its configured root.");
        }
        return fullPath;
    }

    private static string? TextValue(IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static void ValidateOptions(MigrationExportOptions options)
    {
        if (!options.SourceStoppedConfirmed)
        {
            throw new MigrationOperationException(
                "source_stop_not_confirmed",
                "Export requires --confirm-source-stopped after PocketBase writes and jobs are stopped.");
        }

        var sourceDatabasePath = Path.GetFullPath(options.SourceDatabasePath);
        var storagePath = Path.GetFullPath(options.StoragePath);
        var outputPath = Path.GetFullPath(options.OutputPath);

        if (!File.Exists(sourceDatabasePath))
        {
            throw new MigrationOperationException("source_database_missing", "The PocketBase SQLite database does not exist.");
        }

        if (!Directory.Exists(storagePath))
        {
            throw new MigrationOperationException("source_storage_missing", "The PocketBase storage directory does not exist.");
        }

        if (string.IsNullOrWhiteSpace(options.SourceGitSha) ||
            options.SourceGitSha.Length != 40 ||
            !options.SourceGitSha.All(Uri.IsHexDigit))
        {
            throw new MigrationOperationException("source_git_sha_invalid", "The deployed PocketBase Git SHA must be a full 40-character SHA.");
        }

        if (options.ExportedAtUtc == default || options.ExportedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new MigrationOperationException("export_time_not_utc", "The frozen export time must include an explicit UTC offset.");
        }

        if (PathsOverlap(outputPath, sourceDatabasePath) || PathsOverlap(outputPath, storagePath))
        {
            throw new MigrationOperationException(
                "export_output_overlaps_source",
                "The export output must be separate from the source database and storage so the stopped source remains immutable.");
        }

        if (HasReparsePointInPath(outputPath))
        {
            throw new MigrationOperationException(
                "export_output_path_invalid",
                "The export output path must not traverse symbolic links or reparse points.");
        }
    }

    private static SourceSnapshot CaptureSourceSnapshot(string sourceDatabasePath)
    {
        // SQLite commits may live in -wal; -shm is only a rebuildable WAL index.
        var walPath = sourceDatabasePath + "-wal";
        return new(
            CaptureFileSnapshot(sourceDatabasePath),
            File.Exists(walPath) ? CaptureFileSnapshot(walPath) : null);
    }

    private static SourceFileSnapshot CaptureFileSnapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new(info.Name, info.Length, HashFile(path));
        }
        catch (IOException)
        {
            throw new MigrationOperationException(
                "source_snapshot_unavailable",
                "The stopped source database snapshot could not be read.");
        }
    }

    private static void EnsureSourceSnapshotUnchanged(string sourceDatabasePath, SourceSnapshot expected)
    {
        SourceSnapshot actual;
        try
        {
            actual = CaptureSourceSnapshot(sourceDatabasePath);
        }
        catch (MigrationOperationException)
        {
            throw new MigrationOperationException(
                "source_snapshot_changed",
                "The stopped source database or its WAL sidecar changed while export was reading it.");
        }

        if (!expected.Equals(actual))
        {
            throw new MigrationOperationException(
                "source_snapshot_changed",
                "The stopped source database or its WAL sidecar changed while export was reading it.");
        }
    }

    private static object? WalManifest(SourceFileSnapshot? wal) =>
        wal is null
            ? null
            : new
            {
                fileName = wal.FileName,
                length = wal.Length,
                sha256 = wal.Sha256
            };

    private static bool PathsOverlap(string candidate, string source)
    {
        var normalizedSource = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, normalizedSource, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointInPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
        {
            throw new MigrationOperationException("source_integrity_failed", "SQLite integrity_check did not return ok.");
        }
    }

    private static string ReadSourceSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!TableExists(connection, transaction, "_migrations"))
        {
            throw new MigrationOperationException("source_schema_version_missing", "PocketBase _migrations is missing.");
        }

        var columns = ReadColumns(connection, transaction, "_migrations");
        var versionColumn = columns.FirstOrDefault(column =>
            string.Equals(column, "file", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "name", StringComparison.OrdinalIgnoreCase));
        if (versionColumn is null)
        {
            throw new MigrationOperationException("source_schema_version_missing", "PocketBase _migrations has no migration name column.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT [{EscapeIdentifier(versionColumn)}] FROM [_migrations] ORDER BY [{EscapeIdentifier(versionColumn)}] DESC LIMIT 1;";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new MigrationOperationException("source_schema_version_missing", "PocketBase has no applied migration version.");
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReadCollection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string collection)
    {
        if (!TableExists(connection, transaction, collection))
        {
            return [];
        }

        var excluded = ExcludedColumns.TryGetValue(collection, out var configured)
            ? configured
            : [];
        var columns = ReadColumns(connection, transaction, collection)
            .Where(column => !excluded.Contains(column))
            .ToArray();
        if (columns.Length == 0)
        {
            return [];
        }

        var orderColumn = columns.FirstOrDefault(column => string.Equals(column, "id", StringComparison.OrdinalIgnoreCase));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(column => $"[{EscapeIdentifier(column)}]"))} " +
            $"FROM [{EscapeIdentifier(collection)}]" +
            (orderColumn is null ? " ORDER BY rowid;" : $" ORDER BY [{EscapeIdentifier(orderColumn)}] COLLATE BINARY;");
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < columns.Length; index++)
            {
                row.Add(
                    columns[index],
                    reader.IsDBNull(index)
                        ? null
                        : NormalizeSqliteValue(reader.GetValue(index), collection, columns[index]));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static void ValidateRequiredSourceCollections(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var missing = Domains
            .SelectMany(domain => domain.Collections)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(collection => !TableExists(connection, transaction, collection))
            .OrderBy(collection => collection, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new MigrationOperationException(
                "source_collection_missing",
                $"The pinned PocketBase source is missing required collection table(s): {string.Join(", ", missing)}.");
        }
    }

    private static object NormalizeSqliteValue(object value, string collection, string column)
    {
        if (value is not byte[] bytes)
        {
            return value;
        }
        try
        {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MigrationOperationException(
                "source_binary_value_unsupported",
                $"Source field {collection}.{column} contains non-UTF-8 binary data that requires an explicit migration rule.");
        }
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static IReadOnlyList<string> ReadColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{EscapeIdentifier(table)}]);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static object FileManifest(string root, string path)
    {
        var info = new FileInfo(path);
        return new
        {
            path = Path.GetRelativePath(root, path).Replace('\\', '/'),
            length = info.Length,
            sha256 = HashFile(path)
        };
    }

    private static void WriteJson(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string EscapeIdentifier(string value) => value.Replace("]", "]]", StringComparison.Ordinal);

    private sealed record SourceSnapshot(SourceFileSnapshot Main, SourceFileSnapshot? Wal);

    private sealed record SourceFileSnapshot(string FileName, long Length, string Sha256);

    private sealed record MigrationDomain(string Name, string FileName, IReadOnlyList<string> Collections);
}

public sealed class MigrationOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
