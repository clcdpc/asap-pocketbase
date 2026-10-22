using System.Text.Json.Serialization;

namespace Asap.Web.Features.Staff;

public sealed record OrganizationChoice(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name);
