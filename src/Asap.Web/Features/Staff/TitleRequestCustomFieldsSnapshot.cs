using System.Text.Json;

namespace Asap.Web.Features.Staff;

internal static class TitleRequestCustomFieldsSnapshot
{
    public static Dictionary<string, JsonElement> Parse(string? json)
    {
        if (json is null)
        {
            return new(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidSnapshot();
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Object ||
                !fields.TryAdd(property.Name, property.Value.Clone()))
            {
                throw InvalidSnapshot();
            }

            ValidateField(property.Value);
        }

        return fields;
    }

    private static void ValidateField(JsonElement field)
    {
        string? type = null;
        var labelSeen = false;
        var typeSeen = false;
        var valueSeen = false;
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in field.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw InvalidSnapshot();
            }

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

    private static JsonException InvalidSnapshot() =>
        new("The title-request custom-fields snapshot does not match the supported schema.");
}
