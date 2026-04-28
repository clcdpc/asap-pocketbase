namespace Asap.Web.Models;

public sealed class Suggestion
{
    public int Id { get; init; }
    public string PatronBarcode { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
}
