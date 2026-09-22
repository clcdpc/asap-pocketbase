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
        return Task.FromResult(CreatePatron(barcode));
    }

    public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Branches);
    }

    public Task UpdatePreferredPickupBranchAsync(
        PatronSnapshot patron,
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
        return Task.FromResult(new BibValidationResult(bibId > 0, $"Catalog title {bibId}", "Catalog author"));
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

    private static PatronSnapshot CreatePatron(string barcode) => new(
        7001,
        barcode.Trim(),
        $"{barcode.Trim()}@example.org",
        "Test",
        "Patron",
        "1",
        "Adult",
        101,
        2,
        "Test Library",
        101);
}
