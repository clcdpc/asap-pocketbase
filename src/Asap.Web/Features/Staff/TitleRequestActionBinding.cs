using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asap.Web.Features.Staff;

public sealed class TitleRequestActionInput
{
    public string? Version { get; init; }
    public string? Action { get; init; }
    public string? Status { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public JsonElement Identifier { get; init; }
    public string? Publication { get; init; }
    public JsonElement ExactPublicationDate { get; init; }
    public JsonElement CustomFields { get; init; }
    public JsonElement Autohold { get; init; }
    private int? bibid;
    private int? staffSelectedBibId;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Bibid
    {
        get => bibid;
        init
        {
            bibid = value;
            BibidSupplied = true;
        }
    }

    [JsonIgnore]
    public bool BibidSupplied { get; private set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? StaffSelectedBibId
    {
        get => staffSelectedBibId;
        init
        {
            staffSelectedBibId = value;
            StaffSelectedBibIdSupplied = true;
        }
    }

    [JsonIgnore]
    public bool StaffSelectedBibIdSupplied { get; private set; }
    public string? Notes { get; init; }
    public string? Format { get; init; }
    public bool EmailPurchaseReminder { get; init; }
    public string? RejectionTemplateId { get; init; }
    public TitleRequestActionCommand ToCommand() => TitleRequestActionBinding.Bind(this);
}

public readonly record struct SuppliedValue<T>(bool IsSupplied, T Value);

public sealed record TitleRequestActionCommand
{
    public string? Version { get; init; }
    public string? Action { get; init; }
    public string? Status { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public SuppliedValue<string?> Identifier { get; init; }
    public string? Publication { get; init; }
    public SuppliedValue<DateOnly?> ExactPublicationDate { get; init; }
    public SuppliedValue<IReadOnlyDictionary<string, string?>?> CustomFields { get; init; }
    public SuppliedValue<bool> Autohold { get; init; }
    public int? Bibid { get; init; }
    public bool BibidSupplied { get; init; }
    public int? StaffSelectedBibId { get; init; }
    public bool StaffSelectedBibIdSupplied { get; init; }
    public string? Notes { get; init; }
    public string? Format { get; init; }
    public bool EmailPurchaseReminder { get; init; }
    public string? RejectionTemplateId { get; init; }
    public string? ValidationError { get; init; }
}

internal sealed record CustomFieldSnapshot(
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("displayValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayValue = null);

// Only this transport boundary interprets JSON token kinds and legacy nested values.
internal static class TitleRequestActionBinding
{
    public static TitleRequestActionCommand Bind(TitleRequestActionInput input)
    {
        string? error = null;
        DateOnly? date = null;
        if (input.ExactPublicationDate.ValueKind == JsonValueKind.String)
        {
            if (!DateOnly.TryParseExact(input.ExactPublicationDate.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                error = "invalid_exact_publication_date";
            }
            else
            {
                date = parsed;
            }
        }
        else if (input.ExactPublicationDate.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            error = "invalid_exact_publication_date";
        }
        if (input.Identifier.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
        {
            error ??= "invalid_identifier";
        }
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (input.CustomFields.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in input.CustomFields.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var nested))
                {
                    value = nested;
                }
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    error ??= "invalid_custom_fields";
                    continue;
                }
                if (!fields.TryAdd(property.Name, value.ValueKind == JsonValueKind.String ? value.GetString() : null))
                {
                    error ??= "invalid_custom_fields";
                }
            }
        }
        else if (input.CustomFields.ValueKind != JsonValueKind.Undefined)
        {
            error ??= "invalid_custom_fields";
        }
        if (input.Autohold.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
        {
            error ??= "invalid_autohold";
        }
        return new TitleRequestActionCommand {
            Version = input.Version, Action = input.Action, Status = input.Status, Title = input.Title, Author = input.Author,
            Identifier = new(input.Identifier.ValueKind != JsonValueKind.Undefined,
                input.Identifier.ValueKind == JsonValueKind.String ? input.Identifier.GetString() : null),
            Publication = input.Publication,
            ExactPublicationDate = new(input.ExactPublicationDate.ValueKind != JsonValueKind.Undefined, date),
            CustomFields = new(input.CustomFields.ValueKind != JsonValueKind.Undefined, fields),
            Autohold = new(input.Autohold.ValueKind is JsonValueKind.True or JsonValueKind.False,
                input.Autohold.ValueKind == JsonValueKind.True),
            Bibid = input.Bibid, BibidSupplied = input.BibidSupplied,
            StaffSelectedBibId = input.StaffSelectedBibId, StaffSelectedBibIdSupplied = input.StaffSelectedBibIdSupplied,
            Notes = input.Notes, Format = input.Format, EmailPurchaseReminder = input.EmailPurchaseReminder,
            RejectionTemplateId = input.RejectionTemplateId, ValidationError = error };
    }
}
