using System.Text.Json;

namespace Asap.Migration;

internal static class MigrationCustomFieldsSnapshot
{
    public static string? Read(SourceRow row)
    {
        try
        {
            return Validate(row.JsonText("customFields"));
        }
        catch (MigrationOperationException exception) when (exception.Code == "source_json_invalid")
        {
            throw InvalidSnapshot();
        }
    }

    public static string? Validate(string? json)
    {
        if (json is null)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            MigrationPackageValidator.EnsureNoDuplicateProperties(
                document.RootElement,
                "custom_fields_snapshot_invalid");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidSnapshot();
            }

            foreach (var field in root.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(field.Name) || field.Value.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidSnapshot();
                }

                ValidateField(field.Value);
            }

            return root.GetRawText();
        }
        catch (JsonException)
        {
            throw InvalidSnapshot();
        }
    }

    private static void ValidateField(JsonElement field)
    {
        string? type = null;
        var labelSeen = false;
        var typeSeen = false;
        var valueSeen = false;

        foreach (var property in field.EnumerateObject())
        {
            switch (property.Name)
            {
                case "label":
                    labelSeen = true;
                    ValidateNullableString(property.Value);
                    break;
                case "type":
                    typeSeen = true;
                    ValidateNullableString(property.Value);
                    type = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;
                    break;
                case "value":
                    valueSeen = true;
                    ValidateNullableString(property.Value);
                    break;
                case "displayValue":
                    ValidateNullableString(property.Value);
                    break;
                default:
                    throw InvalidSnapshot();
            }
        }

        if (!labelSeen || !typeSeen || !valueSeen || type is not ("text" or "textarea" or "select"))
        {
            throw InvalidSnapshot();
        }
    }

    private static void ValidateNullableString(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw InvalidSnapshot();
        }
    }

    private static MigrationOperationException InvalidSnapshot() =>
        new("custom_fields_snapshot_invalid", "A title-request custom-fields snapshot does not match the supported legacy schema.");
}
