using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Staff;

public interface IStaffPatronLookupProvider
{
    Task<IReadOnlyList<StaffPatronSearchCandidate>> SearchAsync(
        string query,
        int? organizationId,
        CancellationToken cancellationToken);
}

public interface IStaffCatalogSearchProvider
{
    Task<IReadOnlyList<StaffCatalogSearchCandidate>> SearchAsync(
        string query,
        string mode,
        CancellationToken cancellationToken);
}

public sealed record StaffPatronSearchCandidate(
    string Barcode,
    string DisplayName,
    int RegisteredOrganizationId);

public sealed record StaffCatalogSearchCandidate(
    int BibId,
    string Title,
    string? Author,
    string? Identifier,
    string? PublicationDate);

public sealed record StaffCatalogSearchInput(
    int? LibraryOrgId,
    string? Query,
    string? Mode);

public sealed record StaffPatronLookupInput(
    string? Query,
    string? Barcode,
    int? LibraryOrgId);

public sealed record StaffPatronMatch(
    string Barcode,
    string Name,
    int PatronOrganizationId,
    int HomeLibraryOrganizationId,
    string HomeLibraryOrganizationName);

public sealed record StaffPatronContext(
    StaffPatronMatch Patron,
    string? Email,
    int? CurrentPreferredPickupBranchId,
    string? CurrentPreferredPickupBranchName,
    IReadOnlyList<PickupBranch> PickupBranches,
    DateTimeOffset PickupBranchesRefreshedAt,
    string? PickupWarning,
    bool PickupOptionsUnavailable,
    int LibraryOrgId,
    string LibraryOrgName,
    bool SearchLibraryLimited);

public sealed record StaffPatronLookupResult(
    string Status,
    int LibraryOrgId,
    string LibraryOrgName,
    bool SearchLibraryLimited,
    StaffPatronContext? Patron,
    IReadOnlyList<StaffPatronMatch> Matches);

public sealed record StaffSuggestionInput(
    int? LibraryOrgId,
    string? Barcode,
    string? Format,
    string? Title,
    string? Author,
    string? Identifier,
    string? Publication,
    DateOnly? ExactPublicationDate,
    string? Notes,
    int? PreferredPickupBranchId,
    int? CurrentPreferredPickupBranchIdAtLoad,
    bool? Autohold,
    bool EmailPatronConfirmation,
    IReadOnlyDictionary<string, string?>? CustomFields);

public sealed class StaffSuggestionException(
    int statusCode,
    string code,
    string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
