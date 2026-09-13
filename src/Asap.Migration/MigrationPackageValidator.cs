using System.Security.Cryptography;
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

    [JsonPropertyName("entityCounts")]
    public Dictionary<string, int> EntityCounts { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("files")]
    public List<MigrationPackageFile> Files { get; init; } = [];
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static ValidatedMigrationPackage Validate(string packagePath)
    {
        if (!Directory.Exists(packagePath))
        {
            throw new MigrationOperationException("package_missing", "The migration package directory does not exist.");
        }

        var root = Path.GetFullPath(packagePath);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new MigrationOperationException("package_manifest_missing", "manifest.json is missing.");
        }

        MigrationPackageManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MigrationPackageManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions) ?? throw new JsonException("Manifest was null.");
        }
        catch (JsonException exception)
        {
            throw new MigrationOperationException("package_manifest_invalid", exception.Message);
        }

        if (manifest.FormatVersion != MigrationPackageExporter.FormatVersion ||
            !string.Equals(manifest.ContractVersion, MigrationContract.ContractVersion, StringComparison.Ordinal) ||
            manifest.PocketBaseSourceGitSha.Length != 40 ||
            !manifest.PocketBaseSourceGitSha.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(manifest.PocketBaseSourceSchemaVersion) ||
            manifest.ExportedAtUtc == default)
        {
            throw new MigrationOperationException("package_manifest_invalid", "Manifest identity or source metadata is invalid.");
        }

        var listedPaths = new HashSet<string>(StringComparer.Ordinal);
        var observedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(file.Path);
            if (!listedPaths.Add(relativePath))
            {
                throw new MigrationOperationException("package_manifest_invalid", $"Duplicate file entry: {relativePath}");
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
                ValidateMetadataFile(fullPath);
            }
            else
            {
                ValidateDomainFile(fullPath, observedCounts);
            }
        }

        var actualPaths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !string.Equals(path, "manifest.json", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(listedPaths))
        {
            throw new MigrationOperationException("package_unlisted_file", "Package files do not exactly match the manifest.");
        }

        if (manifest.EntityCounts.Count != observedCounts.Count ||
            manifest.EntityCounts.Any(item => !observedCounts.TryGetValue(item.Key, out var count) || count != item.Value))
        {
            throw new MigrationOperationException("package_count_mismatch", "Manifest entity counts do not match domain files.");
        }

        return new ValidatedMigrationPackage(root, manifest);
    }

    internal static string ComputePackageIdentitySha256(ValidatedMigrationPackage package) =>
        HashFile(Path.Combine(package.RootPath, "manifest.json"));

    private static void ValidateDomainFile(string path, IDictionary<string, int> observedCounts)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var formatVersion) ||
                formatVersion.GetInt32() != MigrationPackageExporter.FormatVersion ||
                !root.TryGetProperty("domain", out var domain) ||
                string.IsNullOrWhiteSpace(domain.GetString()) ||
                !root.TryGetProperty("collections", out var collections) ||
                collections.ValueKind != JsonValueKind.Object)
            {
                throw new MigrationOperationException("package_domain_invalid", $"Invalid domain file: {Path.GetFileName(path)}");
            }

            if (ContainsProperty(root, "ProtectedServerToken"))
            {
                throw new MigrationOperationException("package_secret_forbidden", "Target Postmark token material must not appear in an export package.");
            }

            foreach (var collection in collections.EnumerateObject())
            {
                if (collection.Value.ValueKind != JsonValueKind.Array ||
                    !observedCounts.TryAdd(collection.Name, collection.Value.GetArrayLength()))
                {
                    throw new MigrationOperationException("package_domain_invalid", $"Invalid or duplicate collection: {collection.Name}");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new MigrationOperationException("package_domain_invalid", exception.Message);
        }
    }

    private static void ValidateMetadataFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                ContainsProperty(document.RootElement, "ProtectedServerToken") ||
                ContainsProperty(document.RootElement, "secretFingerprint"))
            {
                throw new MigrationOperationException(
                    "package_metadata_invalid",
                    $"Invalid or secret-bearing metadata file: {Path.GetFileName(path)}");
            }
        }
        catch (JsonException)
        {
            throw new MigrationOperationException(
                "package_metadata_invalid",
                $"Invalid metadata file: {Path.GetFileName(path)}");
        }
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

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new MigrationOperationException("package_path_invalid", "Manifest paths must be relative.");
        }
        return path.Replace('\\', '/');
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationOperationException("package_path_invalid", "Manifest path escapes the package directory.");
        }
        return fullPath;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
