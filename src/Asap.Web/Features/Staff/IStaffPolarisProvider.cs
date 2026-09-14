using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Staff;

public interface IStaffPolarisProvider
{
    Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<PolarisOrganizationSnapshot>>(
            new PolarisOperationalException(
                "polaris_organization_read_unimplemented",
                "The selected Polaris provider does not implement organization reference reads."));

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

    Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(
        string barcode,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<PolarisCheckoutSnapshot>>(
            new PolarisOperationalException(
                "polaris_checkout_read_unimplemented",
                "The selected Polaris provider does not implement checkout evidence."));
}

public sealed record BibValidationResult(bool IsValid, string? Title = null, string? Author = null);

public sealed record PolarisHoldSnapshot(
    int HoldRequestId,
    int BibId,
    int StatusId,
    string? StatusDescription,
    int PickupBranchId,
    string? PatronBarcode = null);

public sealed record PolarisCheckoutSnapshot(
    int BibId,
    string? HoldRequestId = null,
    string? PatronBarcode = null);

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
