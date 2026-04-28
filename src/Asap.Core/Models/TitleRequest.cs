namespace Asap.Core.Models;

public sealed class TitleRequest
{
    public required string Id { get; init; }
    public required string PatronName { get; init; }
    public string? PatronEmail { get; init; }
    public required string LibraryOrgId { get; init; }
    public required string Status { get; init; }
    public string? CloseReason { get; init; }
    public string? CloseMessage { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
    public byte[]? RowVersion { get; init; }
}
