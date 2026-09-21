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

    Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(int bibId, int organizationId, CancellationToken cancellationToken) =>
        Task.FromException<StaffBibHoldingsSummary>(new PolarisOperationalException(
            "polaris_bib_holdings_unimplemented", "The selected Polaris provider does not implement BIB holdings."));

    Task<StaffBibSearchResult> SearchBibsAsync(
        string mode, string query, string title, string author, CancellationToken cancellationToken) =>
        Task.FromException<StaffBibSearchResult>(new PolarisOperationalException(
            "polaris_bib_search_unimplemented", "The selected Polaris provider does not implement BIB searches."));

    Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(string query, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<PatronSnapshot>>(new PolarisOperationalException(
            "polaris_patron_search_unimplemented", "The selected Polaris provider does not implement patron searches."));

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

public sealed record BibValidationResult(bool IsValid, string? Title = null, string? Author = null,
    string? Publication = null, string? Format = null, string? Identifier = null, string? Publisher = null);

public sealed record StaffBibSearchRow(string BibId, string? Title, string? Author,
    string? Publication, string? Format, string? Identifier);

public sealed record StaffBibSearchResult(IReadOnlyList<StaffBibSearchRow> Results, int TotalMatches);

public sealed record StaffBibHoldingsSummary(int MyLibraryCount, int OtherLibraryCount, int ConsortiumCount,
    bool IsHoldable, bool HasHoldableAtMyLibrary);

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
