using Asap.Web.Features.Patron;

namespace Asap.Web.Features.Staff;

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
    IReadOnlyList<StaffPatronMatch> Matches,
    string? Message = null);

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
    IReadOnlyDictionary<string, string?>? CustomFields,
    int? VerifiedBibId = null,
    bool? CurrentPreferredPickupBranchObservedAtLoad = null);

public sealed record StaffSuggestionConfiguration(
    int LibraryOrgId,
    string LibraryOrgName,
    object EffectiveConfiguration);

public sealed class StaffSuggestionException(
    int statusCode,
    string code,
    string message,
    object? response = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public object? Response { get; } = response;
}
