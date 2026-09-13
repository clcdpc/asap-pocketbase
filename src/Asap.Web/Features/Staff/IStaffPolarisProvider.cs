namespace Asap.Web.Features.Staff;

public interface IStaffPolarisProvider
{
    Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
        string barcode,
        CancellationToken cancellationToken);

    Task<HoldProviderResult> CreateHoldAsync(
        HoldCreateCommand command,
        CancellationToken cancellationToken);

    Task<HoldProviderResult> ReplyToHoldAsync(
        HoldReplyCommand command,
        CancellationToken cancellationToken);
}

public sealed record BibValidationResult(bool IsValid, string? Title = null, string? Author = null);

public sealed record PolarisHoldSnapshot(
    int HoldRequestId,
    int BibId,
    int StatusId,
    string? StatusDescription,
    int PickupBranchId);

public sealed record HoldCreateCommand(
    int PatronId,
    int BibId,
    int PickupBranchId,
    int RequestingOrganizationId,
    int WorkstationId,
    int PolarisUserId);

public sealed record HoldReplyCommand(
    Guid RequestGuid,
    string TxnGroupQualifier,
    string TxnQualifier,
    int RequestingOrganizationId);

public enum HoldProviderOutcome
{
    FinalSuccess,
    ReplyRequired,
    DefinitiveNoEffect,
    Ambiguous
}

public sealed record HoldProviderResult(
    HoldProviderOutcome Outcome,
    string? RequestGuid,
    string? HoldRequestId,
    string? TxnGroupQualifier,
    string? TxnQualifier,
    int? StatusType,
    int? StatusValue,
    string EvidenceKind,
    string? SafeErrorCode = null);
