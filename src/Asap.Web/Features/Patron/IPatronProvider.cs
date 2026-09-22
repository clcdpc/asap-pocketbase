namespace Asap.Web.Features.Patron;

public interface IPatronProvider
{
    Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken);

    Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken);

    Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        CancellationToken cancellationToken);

    Task UpdatePreferredPickupBranchAsync(
        string barcode,
        int pickupBranchId,
        CancellationToken cancellationToken);

    Task<IdentifierLookupResult> LookupIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken);
}

public sealed record PatronSnapshot(
    int PatronId,
    string Barcode,
    string? Email,
    string? NameFirst,
    string? NameLast,
    string? PatronCodeId,
    string? PatronCodeDescription,
    int PatronOrganizationId,
    int HomeLibraryOrganizationId,
    string HomeLibraryOrganizationName,
    int? PreferredPickupBranchId);

public sealed record PickupBranch(int Id, string Label);

public enum IdentifierLookupOutcome
{
    Found,
    DefinitiveNotFound,
    NotFound = DefinitiveNotFound,
    TransientFailure,
    OperationalFailure
}

public sealed record IdentifierLookupResult(
    IdentifierLookupOutcome Outcome,
    string? BibId = null,
    bool MultipleMatches = false,
    string? ErrorCode = null,
    bool FilteredByMaterialType = false,
    string? CatalogTitle = null,
    string? CatalogAuthor = null);

public sealed class PatronAuthenticationException(string message) : Exception(message);

public sealed class PolarisOperationalException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
