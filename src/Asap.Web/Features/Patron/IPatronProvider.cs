namespace Asap.Web.Features.Patron;

public interface IPatronProvider
{
    // Authentication is a system-scope bootstrap before the home/effective library is known.
    Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken);

    Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken);

    Task<int?> GetPatronIdAsync(string barcode, int organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<int?>(new PolarisOperationalException(
            "patron_id_read_unimplemented",
            "The selected patron provider does not implement a patron-ID read."));
    }

    Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        int organizationId, CancellationToken cancellationToken);

    Task UpdatePreferredPickupBranchAsync(
        string barcode,
        int pickupBranchId,
        int organizationId, CancellationToken cancellationToken);

    Task<IdentifierLookupResult> LookupIdentifierAsync(
        string identifier,
        int organizationId, CancellationToken cancellationToken);
}

public sealed record PatronSnapshot(
    int PatronId,
    string Barcode,
    string? Email,
    string? NameFirst,
    string? NameLast,
    int? PatronCodeId,
    string? PatronCodeDescription,
    int PatronOrganizationId,
    int HomeLibraryOrganizationId,
    string HomeLibraryOrganizationName,
    int? PreferredPickupBranchId,
    string? FormerBarcode = null,
    string? RequestedBarcode = null,
    PatronPickupPreferenceState? PickupPreferenceState = null)
{
    public PatronPickupPreferenceState EffectivePickupPreferenceState =>
        PickupPreferenceState ?? (PreferredPickupBranchId.HasValue
            ? PatronPickupPreferenceState.Current
            : PatronPickupPreferenceState.Absent);

    public IReadOnlyList<string> KnownBarcodeAliases
    {
        get
        {
            var aliases = new List<string>(3);
            foreach (var candidate in new[] { Barcode, FormerBarcode, RequestedBarcode })
            {
                var value = candidate?.Trim();
                if (!string.IsNullOrEmpty(value) &&
                    !aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    aliases.Add(value);
                }
            }

            return aliases;
        }
    }
}

public enum PatronPickupPreferenceState
{
    Current,
    Absent,
    ExplicitInvalid
}

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
    int? BibId = null,
    bool MultipleMatches = false,
    string? ErrorCode = null,
    bool FilteredByMaterialType = false,
    string? CatalogTitle = null,
    string? CatalogAuthor = null);

public sealed class PatronAuthenticationException(string message) : Exception(message);

public class PolarisOperationalException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
