using System.Globalization;
using System.Text.Json;

namespace Asap.Migration;

internal static class MigrationPackageReader
{
    public static JsonElement ReadMetadata(
        ValidatedMigrationPackage package,
        string fileName)
    {
        var path = Path.Combine(package.RootPath, fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<SourceRow> ReadRows(
        ValidatedMigrationPackage package,
        string fileName,
        string collectionName)
    {
        var path = Path.Combine(package.RootPath, fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rows = document.RootElement
            .GetProperty("collections")
            .GetProperty(collectionName);
        return rows.EnumerateArray()
            .Select(row => new SourceRow(
                row.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static IReadOnlyList<SourceRow> ReadRowsOrEmpty(
        ValidatedMigrationPackage package,
        string fileName,
        string collectionName)
    {
        var path = Path.Combine(package.RootPath, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("collections", out var collections) ||
            !collections.TryGetProperty(collectionName, out var rows))
        {
            return [];
        }
        return rows.EnumerateArray()
            .Select(row => new SourceRow(
                row.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }
}

internal sealed class SourceRow(IReadOnlyDictionary<string, JsonElement> values)
{
    private readonly Dictionary<string, int?> positiveInt32Values = new(StringComparer.Ordinal);

    public IEnumerable<string> Names => values.Keys;

    public int? PositiveInt32(string name, string errorCode = "source_polaris_id_invalid")
    {
        var key = name.ToUpperInvariant();
        if (!positiveInt32Values.TryGetValue(key, out var parsed))
        {
            parsed = ParsePositiveInt32(String(name), $"{String("id")}/{name}", errorCode);
            positiveInt32Values.Add(key, parsed);
        }
        return parsed;
    }

    public int? JsonPropertyPositiveInt32(string name, string propertyName, string errorCode)
    {
        var key = $"json:{name.ToUpperInvariant()}.{propertyName}";
        if (!positiveInt32Values.TryGetValue(key, out var parsed))
        {
            parsed = ParsePositiveInt32(JsonPropertyString(name, propertyName), $"{String("id")}/{key}", errorCode);
            positiveInt32Values.Add(key, parsed);
        }
        return parsed;
    }

    public static int? ParsePositiveInt32(string? source, string field, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }
        if (!int.TryParse(source.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new MigrationOperationException(errorCode,
                $"Source field {field} must be a positive Int32 Polaris identity; received '{source}'.");
        }
        return parsed;
    }

    public bool HasValue(string name) =>
        values.TryGetValue(name, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
        !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    public string RequiredText(string name)
    {
        var value = Text(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new MigrationOperationException("source_value_missing", $"Source field {name} is required.");
    }

    public string RequiredString(string name)
    {
        var value = String(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new MigrationOperationException("source_value_missing", $"Source field {name} is required.");
    }

    public string? String(string name)
    {
        return Clean(Text(name));
    }

    public string? Text(string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
    }

    public string? JsonText(string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return value.GetRawText();
        }
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new MigrationOperationException("source_json_invalid", $"Source field {name} is not valid JSON.");
        }
    }

    public IReadOnlyList<string> JsonPropertyNames(string name)
    {
        var json = JsonText(name);
        if (json is null)
        {
            return [];
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
    }

    public string? JsonPropertyString(string name, string propertyName)
    {
        var json = JsonText(name);
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var properties = document.RootElement.EnumerateObject()
            .Where(property => property.Name == propertyName).ToArray();
        if (properties.Length > 1)
        {
            throw new MigrationOperationException("source_json_invalid",
                $"Source field {name} contains duplicate property {propertyName}.");
        }
        if (properties.Length == 0 || properties[0].Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        var value = properties[0].Value;
        return value.ValueKind == JsonValueKind.String ? Clean(value.GetString()) : Clean(value.GetRawText());
    }

    public int? Int32(string name)
    {
        var raw = String(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new MigrationOperationException("source_value_invalid", $"Source field {name} is not an integer.");
    }

    public bool Bool(string name, bool defaultValue = false)
    {
        var raw = String(name);
        if (raw is null)
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new MigrationOperationException("source_value_invalid", $"Source field {name} is not a boolean.")
        };
    }

    public bool? NullableBool(string name) => HasValue(name) ? Bool(name) : null;

    public DateTime? UtcDateTime(string name)
    {
        var raw = String(name);
        if (raw is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new MigrationOperationException("source_value_invalid", $"Source field {name} is not a UTC timestamp.");
        }
        return parsed.UtcDateTime;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
