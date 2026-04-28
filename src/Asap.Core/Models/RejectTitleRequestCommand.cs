namespace Asap.Core.Models;

public sealed class RejectTitleRequestCommand
{
    public required string RequestId { get; init; }
    public required string CloseReason { get; init; }
    public string? CloseMessage { get; init; }
    public required string StaffUserId { get; init; }
}
