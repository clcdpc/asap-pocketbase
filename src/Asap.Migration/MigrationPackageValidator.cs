using Asap.Shared;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asap.Migration;

public sealed record ValidatedMigrationPackage(string RootPath, MigrationPackageManifest Manifest);

public sealed class MigrationPackageManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("contractVersion")]
    public string ContractVersion { get; init; } = string.Empty;

    [JsonPropertyName("pocketBaseSourceGitSha")]
    public string PocketBaseSourceGitSha { get; init; } = string.Empty;

    [JsonPropertyName("pocketBaseSourceSchemaVersion")]
    public string PocketBaseSourceSchemaVersion { get; init; } = string.Empty;

    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; init; }

    [JsonPropertyName("sourceDatabase")]
    public MigrationPackageSourceDatabase? SourceDatabase { get; init; }

    [JsonPropertyName("entityCounts")]
    public Dictionary<string, int> EntityCounts { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("files")]
    public List<MigrationPackageFile> Files { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];
}

public sealed class MigrationPackageSourceDatabase
{
    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("length")]
    public long Length { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class MigrationPackageFile
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("length")]
    public long Length { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public static class MigrationPackageValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private static readonly IReadOnlyDictionary<string, string> ScheduleTargets =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["asap-hold-check"] = "WorkflowProcessing",
            ["asap-isbn-check"] = "IdentifierProcessing",
            ["asap-organization-sync"] = "OrganizationRefresh",
            ["asap-weekly-staff-action-summary"] = "WeeklyStaffSummary"
        };

    private static readonly IReadOnlyDictionary<string, (string Target, string Family)> QueueTargets =
        new Dictionary<string, (string Target, string Family)>(StringComparer.Ordinal)
        {
            ["pending_suggestion_isbn_checks"] = ("IdentifierProcessing", "global"),
            ["outstanding_purchases"] = ("PurchasePromotion", "global"),
            ["pending_holds"] = ("HoldPlacement", "global"),
            ["checked_out"] = ("FulfillmentTracking", "global"),
            ["outstanding_timeout"] = ("OutstandingTimeout", "timeouts"),
            ["pending_hold_timeout"] = ("PendingHoldTimeout", "timeouts"),
            ["hold_pickup_timeout"] = ("HoldPickupTimeout", "timeouts"),
            ["additional_copy_timeout"] = ("AdditionalCopyTimeout", "timeouts")
        };

    public static ValidatedMigrationPackage Validate(string packagePath)
    {
        if (!Directory.Exists(packagePath))
        {
            throw new MigrationOperationException("package_missing", "The migration package directory does not exist.");
        }

        var root = Path.GetFullPath(packagePath);
        EnsureNoReparsePoint(root, root, "package_path_invalid");
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new MigrationOperationException("package_manifest_missing", "manifest.json is missing.");
        }
        EnsureNoReparsePoint(root, manifestPath, "package_path_invalid");

        MigrationPackageManifest manifest;
        using (var manifestDocument = ReadJsonDocument(manifestPath, "package_manifest_invalid"))
        {
            EnsureNoDuplicateProperties(manifestDocument.RootElement, "package_manifest_invalid");
            ValidateManifestSecrets(manifestDocument.RootElement);
            ValidateManifestShape(manifestDocument.RootElement);
            try
            {
                manifest = manifestDocument.RootElement.Deserialize<MigrationPackageManifest>(JsonOptions)
                    ?? throw new JsonException("Manifest was null.");
            }
            catch (JsonException exception)
            {
                throw new MigrationOperationException("package_manifest_invalid", exception.Message);
            }
        }

        ValidateManifestIdentity(manifest, manifestPath);

        var listedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw new MigrationOperationException("package_manifest_invalid", "The manifest contains a null file entry.");
            }

            var relativePath = NormalizeRelativePath(file.Path);
            if (!listedPaths.Add(relativePath))
            {
                throw new MigrationOperationException("package_manifest_invalid", $"Duplicate file entry: {relativePath}");
            }

            if (file.Length < 0 || !IsSha256(file.Sha256))
            {
                throw new MigrationOperationException("package_manifest_invalid", $"Invalid length or SHA-256 for: {relativePath}");
            }

            var fullPath = ResolveWithin(root, relativePath);
            if (!File.Exists(fullPath))
            {
                throw new MigrationOperationException("package_file_missing", $"Package file is missing: {relativePath}");
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.Length ||
                !string.Equals(HashFile(fullPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException("package_hash_mismatch", $"Package file hash or length changed: {relativePath}");
            }

            if (!relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (relativePath is "effective-legacy-runtime-config.json" or
                "effective-legacy-operational-config.json")
            {
                ValidateMetadataFile(fullPath, relativePath, manifest);
            }
            else
            {
                ValidateDomainFile(fullPath, relativePath, observedCounts);
            }
        }

        foreach (var requiredPath in MigrationPackageExporter.RequiredPackageFiles)
        {
            if (!listedPaths.Contains(requiredPath))
            {
                throw new MigrationOperationException("package_required_file_missing", $"Required package file is not listed: {requiredPath}");
            }
        }

        var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rawRelativePath = Path.GetRelativePath(root, path);
            if (string.Equals(rawRelativePath, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(rawRelativePath);
            ResolveWithin(root, relativePath);
            actualPaths.Add(relativePath);
        }

        if (!actualPaths.SetEquals(listedPaths))
        {
            throw new MigrationOperationException("package_unlisted_file", "Package files do not exactly match the manifest.");
        }

        if (manifest.EntityCounts.Count != observedCounts.Count ||
            manifest.EntityCounts.Any(item => !observedCounts.TryGetValue(item.Key, out var count) || count != item.Value))
        {
            throw new MigrationOperationException("package_count_mismatch", "Manifest entity counts do not match domain files.");
        }

        ValidateBrandingAssets(root);

        return new ValidatedMigrationPackage(root, manifest);
    }

    internal static string ComputePackageIdentitySha256(ValidatedMigrationPackage package) =>
        HashFile(Path.Combine(package.RootPath, "manifest.json"));

    private static void ValidateManifestIdentity(MigrationPackageManifest manifest, string manifestPath)
    {
        if (manifest.SourceDatabase is null ||
            manifest.EntityCounts is null ||
            manifest.Files is null ||
            manifest.FormatVersion != MigrationPackageExporter.FormatVersion ||
            !string.Equals(manifest.ContractVersion, MigrationContract.ContractVersion, StringComparison.Ordinal) ||
            !IsGitSha(manifest.PocketBaseSourceGitSha) ||
            !IsPocketBaseMigrationName(manifest.PocketBaseSourceSchemaVersion) ||
            manifest.ExportedAtUtc == default ||
            manifest.ExportedAtUtc.Offset != TimeSpan.Zero ||
            !IsExplicitUtcTimestamp(manifestPath) ||
            !IsSourceDatabaseFileName(manifest.SourceDatabase.FileName) ||
            manifest.SourceDatabase.Length < 0 ||
            !IsSha256(manifest.SourceDatabase.Sha256) ||
            manifest.EntityCounts.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
        {
            throw new MigrationOperationException("package_manifest_invalid", "Manifest identity or source snapshot metadata is invalid.");
        }
    }

    private static void ValidateManifestShape(JsonElement root)
    {
        EnsureManifestObject(root, ManifestProperties, "manifest");
        RequireManifestKind(root, "formatVersion", JsonValueKind.Number);
        RequireManifestKind(root, "contractVersion", JsonValueKind.String);
        RequireManifestKind(root, "pocketBaseSourceGitSha", JsonValueKind.String);
        RequireManifestKind(root, "pocketBaseSourceSchemaVersion", JsonValueKind.String);
        RequireManifestKind(root, "exportedAtUtc", JsonValueKind.String);

        var sourceDatabase = RequiredManifestProperty(root, "sourceDatabase");
        EnsureManifestObject(sourceDatabase, SourceDatabaseProperties, "sourceDatabase");
        RequireManifestKind(sourceDatabase, "fileName", JsonValueKind.String);
        RequireManifestKind(sourceDatabase, "length", JsonValueKind.Number);
        RequireManifestKind(sourceDatabase, "sha256", JsonValueKind.String);

        var entityCounts = RequiredManifestProperty(root, "entityCounts");
        if (entityCounts.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException("package_manifest_invalid", "Manifest entityCounts must be an object.");
        }
        foreach (var count in entityCounts.EnumerateObject())
        {
            if (count.Value.ValueKind != JsonValueKind.Number || !count.Value.TryGetInt32(out _))
            {
                throw new MigrationOperationException("package_manifest_invalid", "Manifest entity counts must be integers.");
            }
        }

        var files = RequiredManifestProperty(root, "files");
        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationOperationException("package_manifest_invalid", "Manifest files must be an array.");
        }
        foreach (var file in files.EnumerateArray())
        {
            EnsureManifestObject(file, FileProperties, "files");
            RequireManifestKind(file, "path", JsonValueKind.String);
            RequireManifestKind(file, "length", JsonValueKind.Number);
            RequireManifestKind(file, "sha256", JsonValueKind.String);
        }

        if (root.TryGetProperty("warnings", out var warnings))
        {
            if (warnings.ValueKind != JsonValueKind.Array)
            {
                throw new MigrationOperationException("package_manifest_invalid", "Manifest warnings must be an array of strings.");
            }

            foreach (var warning in warnings.EnumerateArray())
            {
                if (warning.ValueKind != JsonValueKind.String)
                {
                    throw new MigrationOperationException("package_manifest_invalid", "Manifest warnings must be an array of strings.");
                }
                if (!MigrationPackageExporter.ManifestWarningCodes.Contains(warning.GetString()!))
                {
                    throw new MigrationOperationException(
                        "package_manifest_invalid",
                        "Manifest warnings must contain only supported non-secret codes.");
                }
            }
        }
    }

    private static readonly IReadOnlySet<string> ManifestProperties =
        new HashSet<string>(
            [
                "formatVersion",
                "contractVersion",
                "pocketBaseSourceGitSha",
                "pocketBaseSourceSchemaVersion",
                "exportedAtUtc",
                "sourceDatabase",
                "entityCounts",
                "files",
                "warnings"
            ],
            StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> SourceDatabaseProperties =
        new HashSet<string>(["fileName", "length", "sha256"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> FileProperties =
        new HashSet<string>(["path", "length", "sha256"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ManifestSecretValueProperties =
        new HashSet<string>(
            ["pocketBaseSourceSchemaVersion", "fileName", "path", "warnings"],
            StringComparer.Ordinal);

    private static void EnsureManifestObject(
        JsonElement value,
        IReadOnlySet<string> allowedProperties,
        string objectName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException("package_manifest_invalid", $"Manifest {objectName} must be an object.");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!allowedProperties.Contains(property.Name))
            {
                throw new MigrationOperationException("package_manifest_invalid", $"Manifest contains an unsupported member in {objectName}.");
            }
        }
    }

    private static JsonElement RequiredManifestProperty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw new MigrationOperationException("package_manifest_invalid", $"Manifest property is missing: {name}.");
        }
        return value;
    }

    private static void RequireManifestKind(JsonElement parent, string name, JsonValueKind kind)
    {
        var value = RequiredManifestProperty(parent, name);
        if (value.ValueKind != kind)
        {
            throw new MigrationOperationException("package_manifest_invalid", $"Manifest property has an invalid shape: {name}.");
        }
    }

    private static bool IsExplicitUtcTimestamp(string manifestPath)
    {
        using var document = ReadJsonDocument(manifestPath, "package_manifest_invalid");
        if (!document.RootElement.TryGetProperty("exportedAtUtc", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = timestamp.GetString();
        return !string.IsNullOrWhiteSpace(text) &&
            (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || text.EndsWith("+00:00", StringComparison.Ordinal));
    }

    private static void ValidateDomainFile(
        string path,
        string relativePath,
        IDictionary<string, int> observedCounts)
    {
        using var document = ReadJsonDocument(path, "package_domain_invalid");
        EnsureNoDuplicateProperties(document.RootElement, "package_domain_invalid");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("formatVersion", out var formatVersion) ||
            formatVersion.ValueKind != JsonValueKind.Number ||
            !formatVersion.TryGetInt32(out var format) ||
            format != MigrationPackageExporter.FormatVersion ||
            !root.TryGetProperty("domain", out var domain) ||
            domain.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(domain.GetString()) ||
            !root.TryGetProperty("collections", out var collections) ||
            collections.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException("package_domain_invalid", $"Invalid domain file: {Path.GetFileName(path)}");
        }

        if (ContainsProperty(root, "ProtectedServerToken") ||
            ContainsProperty(root, "secretFingerprint") ||
            ContainsProperty(root, "postmarkToken"))
        {
            throw new MigrationOperationException("package_secret_forbidden", "Protected target credential material must not appear in an export package.");
        }

        var expectedCollections = ExpectedCollections(relativePath);
        var collectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in collections.EnumerateObject())
        {
            if (!collectionNames.Add(collection.Name) ||
                collection.Value.ValueKind != JsonValueKind.Array ||
                !expectedCollections.Contains(collection.Name))
            {
                throw new MigrationOperationException("package_domain_invalid", $"Invalid or unexpected collection: {collection.Name}");
            }

            foreach (var row in collection.Value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    throw new MigrationOperationException(
                        "package_domain_invalid",
                        $"Collection {collection.Name} contains a non-object row.");
                }
            }

            if (!observedCounts.TryAdd(collection.Name, collection.Value.GetArrayLength()))
            {
                throw new MigrationOperationException("package_domain_invalid", $"Duplicate collection: {collection.Name}");
            }
        }

        if (collectionNames.Count != expectedCollections.Count ||
            expectedCollections.Any(expected => !collectionNames.Contains(expected)))
        {
            throw new MigrationOperationException(
                "package_domain_invalid",
                $"Domain file {Path.GetFileName(path)} does not contain its complete collection set.");
        }
    }

    private static IReadOnlySet<string> ExpectedCollections(string relativePath)
    {
        if (string.Equals(relativePath, "branding.json", StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(["branding"], StringComparer.OrdinalIgnoreCase);
        }

        if (MigrationPackageExporter.RequiredCollections.TryGetValue(relativePath, out var collections))
        {
            return collections;
        }

        throw new MigrationOperationException("package_domain_invalid", $"Unexpected JSON file: {relativePath}");
    }

    private static void ValidateMetadataFile(
        string path,
        string relativePath,
        MigrationPackageManifest manifest)
    {
        using var document = ReadJsonDocument(path, "package_metadata_invalid");
        EnsureNoDuplicateProperties(document.RootElement, "package_metadata_invalid");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException("package_metadata_invalid", $"Metadata file is not an object: {relativePath}");
        }

        ValidateCapturedMetadata(root, manifest, relativePath);
        ValidateMetadataSecrets(root);
        if (string.Equals(relativePath, "effective-legacy-runtime-config.json", StringComparison.Ordinal))
        {
            ValidateRuntimeShape(root);
        }
        else
        {
            ValidateOperationalShape(root);
        }
    }

    private static void ValidateCapturedMetadata(
        JsonElement root,
        MigrationPackageManifest manifest,
        string relativePath)
    {
        var expectedSha = manifest.PocketBaseSourceGitSha.ToLowerInvariant();
        var expectedTime = manifest.ExportedAtUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (!TryGetString(root, "capturedFromPocketBaseSha", out var actualSha) ||
            !string.Equals(actualSha, expectedSha, StringComparison.Ordinal) ||
            !TryGetString(root, "capturedAtUtc", out var actualTime) ||
            !string.Equals(actualTime, expectedTime, StringComparison.Ordinal))
        {
            throw new MigrationOperationException(
                "package_metadata_conflict",
                $"Frozen metadata identity does not match the package manifest: {relativePath}");
        }
    }

    private static void ValidateRuntimeShape(JsonElement root)
    {
        var settings = RequiredObject(root, "settings", "package_metadata_invalid");
        ValidateResolvedValue(settings, "StaffApplicationUrl");
        ValidateResolvedValue(settings, "MaterialTypeIconUrlPattern");
        ValidateSecretPresence(settings, "PolarisApiKey");
        ValidateSecretPresence(settings, "PolarisAdminPassword");
    }

    private static void ValidateOperationalShape(JsonElement root)
    {
        var schedules = RequiredObject(root, "schedules", "package_metadata_invalid");
        foreach (var schedule in ScheduleTargets)
        {
            var value = RequiredObject(schedules, schedule.Key, "package_metadata_invalid");
            RequireString(value, "value", "package_metadata_invalid");
            RequireString(value, "provenance", "package_metadata_invalid");
            RequireString(value, "source", "package_metadata_invalid");
            if (!TryGetString(value, "targetScheduleKey", out var target) ||
                !string.Equals(target, schedule.Value, StringComparison.Ordinal))
            {
                throw new MigrationOperationException("package_metadata_conflict", $"Schedule target mapping is invalid: {schedule.Key}");
            }
        }
        if (schedules.EnumerateObject().Count() != ScheduleTargets.Count)
        {
            throw new MigrationOperationException("package_metadata_invalid", "The frozen operational artifact must contain exactly four schedules.");
        }

        var limits = RequiredObject(root, "processingLimits", "package_metadata_invalid");
        ValidateLimitGroup(RequiredObject(limits, "global", "package_metadata_invalid"));
        ValidateLimitGroup(RequiredObject(limits, "timeouts", "package_metadata_invalid"));

        var queues = RequiredObject(limits, "effectiveQueues", "package_metadata_invalid");
        foreach (var queue in QueueTargets)
        {
            var value = RequiredObject(queues, queue.Key, "package_metadata_invalid");
            if (!TryGetString(value, "targetQueue", out var target) ||
                !string.Equals(target, queue.Value.Target, StringComparison.Ordinal) ||
                !TryGetString(value, "fallbackFamily", out var family) ||
                !string.Equals(family, queue.Value.Family, StringComparison.Ordinal))
            {
                throw new MigrationOperationException("package_metadata_conflict", $"Queue target mapping is invalid: {queue.Key}");
            }
            ValidateLimit(RequiredObject(value, "pageSize", "package_metadata_invalid"), "queueOverrideSource");
            ValidateLimit(RequiredObject(value, "maxPerRun", "package_metadata_invalid"), "queueOverrideSource");
        }
        if (queues.EnumerateObject().Count() != QueueTargets.Count)
        {
            throw new MigrationOperationException("package_metadata_invalid", "The frozen operational artifact must contain exactly eight queues.");
        }

        var obsolete = RequiredObject(limits, "obsoletePathOverrides", "package_metadata_invalid");
        var retired = RequiredObject(obsolete, "pending_isbn_checks", "package_metadata_invalid");
        ValidateOptionalLimit(retired, "pageSize");
        ValidateOptionalLimit(retired, "maxPerRun");
        RequireString(retired, "resolution", "package_metadata_invalid");
        RequireString(retired, "pageSizeEnvKey", "package_metadata_invalid");
        RequireString(retired, "maxPerRunEnvKey", "package_metadata_invalid");
    }

    private static void ValidateLimitGroup(JsonElement group)
    {
        ValidateLimit(RequiredObject(group, "pageSize", "package_metadata_invalid"), "source");
        ValidateLimit(RequiredObject(group, "maxPerRun", "package_metadata_invalid"), "source");
    }

    private static void ValidateLimit(JsonElement value, string sourceProperty)
    {
        if (!value.TryGetProperty("value", out var number) ||
            !number.TryGetInt32(out var parsed) ||
            parsed < 1 ||
            !TryGetString(value, "provenance", out _) ||
            !TryGetString(value, sourceProperty, out _))
        {
            throw new MigrationOperationException("package_metadata_invalid", "A frozen processing limit is invalid.");
        }
    }

    private static void ValidateOptionalLimit(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        var limit = RequiredObject(parent, name, "package_metadata_invalid");
        if (!limit.TryGetProperty("value", out var number) || !number.TryGetInt32(out var parsed) || parsed < 1)
        {
            throw new MigrationOperationException("package_metadata_invalid", $"Retired override {name} is invalid.");
        }
    }

    private static void ValidateResolvedValue(JsonElement parent, string name)
    {
        var value = RequiredObject(parent, name, "package_metadata_invalid");
        RequireString(value, "value", "package_metadata_invalid");
        RequireString(value, "provenance", "package_metadata_invalid");
        RequireString(value, "source", "package_metadata_invalid");
    }

    private static void ValidateSecretPresence(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException(
                "package_secret_forbidden",
                $"Secret material must be represented only by presence metadata: {name}");
        }
        if (!value.TryGetProperty("hasValue", out var hasValue) ||
            hasValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new MigrationOperationException("package_secret_forbidden", $"Secret presence metadata is invalid: {name}");
        }
        RequireString(value, "provenance", "package_metadata_invalid");
        RequireString(value, "source", "package_metadata_invalid");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, string code)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new MigrationOperationException(code, $"Required object is missing or invalid: {name}");
        }
        return value;
    }

    private static void RequireString(JsonElement parent, string name, string code)
    {
        if (!TryGetString(parent, name, out _))
        {
            throw new MigrationOperationException(code, $"Required string is missing or invalid: {name}");
        }
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        if (parent.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void ValidateMetadataSecrets(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "ProtectedServerToken", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "secretFingerprint", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MigrationOperationException(
                        "package_secret_forbidden",
                        "Protected target credential material or fingerprints must not appear in effective artifacts.");
                }

                if (LooksLikeSecretMaterial(property.Name))
                {
                    ValidateSecretPresence(element, property.Name);
                }
                ValidateMetadataSecrets(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateMetadataSecrets(item);
        }
    }

    private static void ValidateManifestSecrets(JsonElement element, string? propertyName = null)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (LooksLikeSecretMaterial(property.Name))
                {
                    throw new MigrationOperationException(
                        "package_secret_forbidden",
                        "Credential material must not appear in the migration package manifest.");
                }
                ValidateManifestSecrets(property.Value, property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateManifestSecrets(item, propertyName);
        }
        else if (element.ValueKind == JsonValueKind.String &&
                 propertyName is not null &&
                 ManifestSecretValueProperties.Contains(propertyName) &&
                 LooksLikeSecretMaterialValue(element.GetString()!))
        {
            throw new MigrationOperationException(
                "package_secret_forbidden",
                "Credential material must not appear in the migration package manifest.");
        }
    }

    private static bool LooksLikeSecretMaterialValue(string value)
    {
        var normalized = value
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("postmarktoken", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSecretMaterial(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("credential", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateBrandingAssets(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "branding.json");
        using var document = ReadJsonDocument(path, "package_domain_invalid");
        var root = document.RootElement;
        var collections = RequiredObject(root, "collections", "package_domain_invalid");
        var rows = RequiredArray(collections, "branding", "package_domain_invalid");
        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !TryGetString(row, "sourceRecordId", out _) ||
                !TryGetString(row, "fileName", out _) ||
                !TryGetString(row, "assetPath", out var assetPath) ||
                !TryGetString(row, "contentType", out var contentType) ||
                !row.TryGetProperty("length", out var lengthValue) ||
                !lengthValue.TryGetInt64(out var expectedLength) ||
                expectedLength < 1 ||
                !TryGetString(row, "sha256", out var expectedHash) ||
                !IsSha256(expectedHash))
            {
                throw new MigrationOperationException("branding_asset_invalid", "A branding asset is missing actionable validation metadata.");
            }

            var normalizedAssetPath = NormalizeRelativePath(assetPath, "branding_asset_path_invalid");
            if (!assetPaths.Add(normalizedAssetPath))
            {
                throw new MigrationOperationException("branding_asset_invalid", "Branding assets must not reference the same package path more than once.");
            }
            if (!normalizedAssetPath.StartsWith("assets/branding/", StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException(
                    "branding_asset_path_invalid",
                    "Branding assets must be stored below assets/branding/ in the package.");
            }

            var fullPath = ResolveWithin(packageRoot, normalizedAssetPath, "branding_asset_path_invalid");
            if (!File.Exists(fullPath))
            {
                throw new MigrationOperationException(
                    "branding_asset_missing",
                    $"Branding asset is missing from the package: {normalizedAssetPath}");
            }

            var data = File.ReadAllBytes(fullPath);
            var imageIsValid = LogoImageValidator.TryValidate(data, contentType, out _, out var imageError);
            if (data.LongLength != expectedLength ||
                !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), expectedHash, StringComparison.OrdinalIgnoreCase) ||
                !imageIsValid)
            {
                throw new MigrationOperationException(
                    "branding_asset_invalid",
                    $"Branding asset failed length, SHA-256, or image-content validation: {normalizedAssetPath}. {imageError}");
            }
        }
    }

    private static JsonElement RequiredArray(JsonElement parent, string name, string code)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new MigrationOperationException(code, $"Required array is missing or invalid: {name}");
        }
        return value;
    }

    private static bool ContainsProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    ContainsProperty(property.Value, name))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(item => ContainsProperty(item, name));
        }

        return false;
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string code)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new MigrationOperationException(code, $"Duplicate JSON property: {property.Name}");
                }
                EnsureNoDuplicateProperties(property.Value, code);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) EnsureNoDuplicateProperties(item, code);
        }
    }

    private static JsonDocument ReadJsonDocument(string path, string errorCode)
    {
        try
        {
            var text = StrictUtf8.GetString(File.ReadAllBytes(path));
            if (text.Length > 0 && text[0] == '\ufeff')
            {
                throw new MigrationOperationException(errorCode, $"JSON file contains a UTF-8 BOM: {Path.GetFileName(path)}");
            }
            return JsonDocument.Parse(text);
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            throw new MigrationOperationException(errorCode, $"JSON file is not valid UTF-8: {Path.GetFileName(path)}");
        }
        catch (JsonException exception)
        {
            throw new MigrationOperationException(errorCode, exception.Message);
        }
    }

    private static string NormalizeRelativePath(string path, string errorCode = "package_path_invalid")
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new MigrationOperationException(errorCode, "Manifest paths must be relative.");
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(":", StringComparison.Ordinal) ||
            segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            string.Equals(normalized, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException(errorCode, "Manifest paths must be normalized relative paths inside the package.");
        }
        return normalized;
    }

    private static string ResolveWithin(string root, string relativePath, string errorCode = "package_path_invalid")
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException(errorCode, "Package path escapes the package directory.");
        }
        EnsureNoReparsePoint(fullRoot, fullPath, errorCode);
        return fullPath;
    }

    private static void EnsureNoReparsePoint(string root, string path, string errorCode)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new MigrationOperationException(errorCode, "Package paths must not use symbolic links or reparse points.");
            }
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return;
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative is "." or "") return;
        var current = fullRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new MigrationOperationException(errorCode, "Package paths must not use symbolic links or reparse points.");
            }
        }
    }

    private static bool IsGitSha(string? value) =>
        value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsPocketBaseMigrationName(string? value) =>
        IsManifestFileName(value, ".js");

    private static bool IsSourceDatabaseFileName(string? value) =>
        IsManifestFileName(value, ".db");

    private static bool IsManifestFileName(string? value, string requiredExtension) =>
        value is not null &&
        value.Length > requiredExtension.Length &&
        value.Length <= 255 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
        value.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
