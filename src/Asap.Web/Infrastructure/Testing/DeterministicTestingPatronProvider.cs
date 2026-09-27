using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Web.Infrastructure.Testing;

public sealed class DeterministicTestingPatronProvider : IPatronProvider, IStaffPolarisProvider, IPolarisReferenceProvider
{
    private static readonly IReadOnlyList<PolarisPatronCodeSnapshot> PatronCodes =
    [
        new("1", "Adult"),
        new("2", "Juvenile"),
        new("3", "Guest"),
        new("14", "Young adult"),
        new("28", "Video/VG Restricted"),
        new("91", "Adult legacy"),
        new("92", "Young adult legacy")
    ];

    private static readonly IReadOnlyList<PickupBranch> Branches =
    [
        new(101, "Main Library"),
        new(102, "North Branch")
    ];

    public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PolarisConnectionTestResult(true, 2));
    }

    public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>(
        [
            new(1, "System", null, 0, null),
            new(2, "Test Library", "Test", 2, 1)
        ]);
    }

    public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PatronCodes);
    }

    public Task<PatronSnapshot> AuthenticateAsync(
        string barcode,
        string pin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(pin, "1234", StringComparison.Ordinal))
        {
            throw new PatronAuthenticationException("Incorrect Login - Please try again");
        }

        return Task.FromResult(CreatePatron(barcode));
    }

    public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (barcode.Trim() is "MULTIPLE" or "INELIGIBLE" or "PROVIDER" or "UNRESOLVED")
        {
            throw new PolarisOperationalException(
                "polaris_patron_not_found",
                "The search term is not an exact patron barcode.");
        }

        if (string.Equals(barcode.Trim(), "ALPHAFAIL", StringComparison.Ordinal))
        {
            throw new PolarisOperationalException(
                "polaris_patron_refresh_failed",
                "The deterministic patron provider is unavailable.");
        }

        if (string.Equals(barcode.Trim(), "PROVIDER123", StringComparison.Ordinal))
        {
            throw new PolarisOperationalException(
                "polaris_patron_refresh_failed",
                "The deterministic patron provider is unavailable.");
        }

        return Task.FromResult(barcode.Trim() switch
        {
            "20000000000001" => CreatePatron(barcode, "Alex", "Example"),
            "20000000000002" => CreatePatron(barcode, "Avery", "Example"),
            "30000000000001" => CreatePatron(barcode, "Out", "Ofscope", 3, "Other Library"),
            _ => CreatePatron(barcode)
        });
    }

    public Task<int?> GetPatronIdAsync(string barcode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<int?>(CreatePatron(barcode).PatronId);
    }

    public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Branches);
    }

    public Task UpdatePreferredPickupBranchAsync(
        string barcode,
        int pickupBranchId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Branches.Any(branch => branch.Id == pickupBranchId))
        {
            throw new PolarisOperationalException("invalid_pickup_branch", "The pickup branch is not available.");
        }

        return Task.CompletedTask;
    }

    public Task<IdentifierLookupResult> LookupIdentifierAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = identifier.Trim().ToUpperInvariant();
        var result = normalized switch
        {
            "FOUND" or "9780000000001" => new IdentifierLookupResult(
                IdentifierLookupOutcome.Found,
                "9001"),
            "MULTIPLE" => new IdentifierLookupResult(
                IdentifierLookupOutcome.Found,
                "9002",
                MultipleMatches: true),
            "TRANSIENT" => new IdentifierLookupResult(
                IdentifierLookupOutcome.TransientFailure,
                ErrorCode: "testing_transient"),
            "OPERATIONAL" => new IdentifierLookupResult(
                IdentifierLookupOutcome.OperationalFailure,
                ErrorCode: "testing_operational"),
            _ => new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound)
        };
        return Task.FromResult(result);
    }

    public Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BibValidationResult(bibId > 0, $"Catalog title {bibId}",
            "Catalog author", "2026", "Book", "9780000000001", "Catalog publisher"));
    }

    public Task<StaffBibSearchResult> SearchBibsAsync(
        string mode, string query, string title, string author, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var search = mode == "title_author" ? $"{title} {author}" : query;
        var results = search.Contains("NO MATCH", StringComparison.OrdinalIgnoreCase)
            ? []
            : new[] { new StaffBibSearchRow("9001", "Catalog title 9001", "Catalog author",
                "2026", "Book", "9780000000001") };
        return Task.FromResult(new StaffBibSearchResult(results, results.Length));
    }

    public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Contains("PROVIDER", StringComparison.OrdinalIgnoreCase))
        {
            throw new PolarisOperationalException(
                "testing_provider_unavailable",
                "The deterministic patron provider is unavailable.");
        }

        if (query.Contains("UNRESOLVED", StringComparison.OrdinalIgnoreCase))
        {
            throw new PolarisOperationalException(
                "polaris_home_library_missing",
                "The deterministic patron provider could not resolve the home library.");
        }

        if (query.Contains("NO MATCH", StringComparison.OrdinalIgnoreCase) ||
            query.Contains("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<PatronSnapshot>>([]);
        }

        if (query.Contains("MULTIPLE", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<PatronSnapshot>>(
            [
                CreatePatron("20000000000001", "Alex", "Example"),
                CreatePatron("20000000000002", "Avery", "Example")
            ]);
        }

        if (query.Contains("INELIGIBLE", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<PatronSnapshot>>(
                [CreatePatron("30000000000001", "Out", "Ofscope", 3, "Other Library")]);
        }

        return Task.FromResult<IReadOnlyList<PatronSnapshot>>([CreatePatron("20000000000003", "One", "Result")]);
    }

    public Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(
        int bibId, int organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StaffBibHoldingsSummary(1, 2, 3, true, true));
    }

    public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PolarisHoldSnapshot>>([]);
    }

    public Task<HoldProviderResult> CreateHoldAsync(
        HoldCreateCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HoldProviderResult(
            HoldProviderOutcome.FinalSuccess,
            Guid.NewGuid().ToString(),
            (command.BibId + 100000).ToString(),
            null,
            null,
            2,
            0,
            "testing_documented_create_success"));
    }

    public Task<HoldProviderResult> ReplyToHoldAsync(
        HoldReplyCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HoldProviderResult(
            HoldProviderOutcome.FinalSuccess,
            command.RequestGuid.ToString(),
            null,
            command.TxnGroupQualifier,
            command.TxnQualifier,
            2,
            0,
            "testing_documented_reply_success"));
    }

    private static PatronSnapshot CreatePatron(
        string barcode,
        string firstName = "Test",
        string lastName = "Patron",
        int homeLibraryOrganizationId = 2,
        string homeLibraryOrganizationName = "Test Library",
        int patronOrganizationId = 101) => new(
        7001,
        barcode.Trim(),
        $"{barcode.Trim()}@example.org",
        firstName,
        lastName,
        "1",
        "Adult",
        patronOrganizationId,
        homeLibraryOrganizationId,
        homeLibraryOrganizationName,
        101);
}
