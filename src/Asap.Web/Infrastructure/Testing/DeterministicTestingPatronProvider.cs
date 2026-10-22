using System.Runtime.ExceptionServices;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Infrastructure.Testing;

public enum TestingPolarisOperation
{
    Connection, Organizations, PatronCodes, Authenticate, Refresh, PatronId, PickupBranches,
    PickupUpdate, IdentifierLookup, BibValidation, BibSearch, PatronSearch, BibHoldings,
    PatronHolds, PatronCheckouts, HoldCreate, HoldReply
}

public sealed record TestingPolarisCall(
    TestingPolarisOperation Operation, int OrganizationId, string? Key = null, int? NativeId = null,
    string? Mode = null, string? Title = null, string? Author = null);

// Empty by default: fixtures explicitly declare data, contexts and every external write.
// Unexpected calls are programming failures, never simulated provider outages.
public sealed class DeterministicTestingPatronProvider : IPatronProvider, IStaffPolarisProvider, IPolarisReferenceProvider
{
    private readonly object sync = new();
    private readonly Dictionary<TestingPolarisCall, object?> responses = [];
    private readonly List<TestingPolarisCall> calls = [];
    private readonly Queue<(HoldCreateCommand Command, HoldProviderResult Result)> creates = [];
    private readonly Queue<(HoldReplyCommand Command, HoldProviderResult Result)> replies = [];
    private readonly List<HoldCreateCommand> createCommands = [];
    private readonly List<HoldReplyCommand> replyCommands = [];

    public IReadOnlyList<TestingPolarisCall> Calls { get { lock (sync) { return calls.ToArray(); } } }
    public IReadOnlyList<HoldCreateCommand> CreateCommands { get { lock (sync) { return createCommands.ToArray(); } } }
    public IReadOnlyList<HoldReplyCommand> ReplyCommands { get { lock (sync) { return replyCommands.ToArray(); } } }

    public void SetReferenceData(IReadOnlyList<PolarisOrganizationSnapshot> organizations,
        IReadOnlyList<PolarisPatronCodeSnapshot> patronCodes)
    {
        Set(new(TestingPolarisOperation.Organizations, LibraryScope.SystemOrganizationId), organizations.ToArray());
        Set(new(TestingPolarisOperation.PatronCodes, LibraryScope.SystemOrganizationId), patronCodes.ToArray());
        Set(new(TestingPolarisOperation.Connection, LibraryScope.SystemOrganizationId),
            new PolarisConnectionTestResult(true, organizations.Count));
    }

    public void AddPatron(PatronSnapshot patron, IReadOnlyList<PickupBranch> branches, params int[] servicingOrganizations)
    {
        if (servicingOrganizations.Length == 0 || servicingOrganizations.Any(id => id <= LibraryScope.SystemOrganizationId))
        {
            throw new ArgumentException("Declare positive member-library servicing contexts.", nameof(servicingOrganizations));
        }
        Set(new(TestingPolarisOperation.Authenticate, LibraryScope.SystemOrganizationId, patron.Barcode), patron);
        foreach (var organizationId in servicingOrganizations.Distinct())
        {
            Set(new(TestingPolarisOperation.Refresh, organizationId, patron.Barcode), patron);
            Set(new(TestingPolarisOperation.PatronId, organizationId, patron.Barcode), (int?)patron.PatronId);
            Set(new(TestingPolarisOperation.PickupBranches, organizationId, patron.Barcode), branches.ToArray());
            Set(new(TestingPolarisOperation.PatronHolds, organizationId, patron.Barcode), Array.Empty<PolarisHoldSnapshot>());
            Set(new(TestingPolarisOperation.PatronCheckouts, organizationId, patron.Barcode), Array.Empty<PolarisCheckoutSnapshot>());
        }
    }

    public void AllowPickupUpdate(string barcode, int organizationId, int branchId) =>
        Set(new(TestingPolarisOperation.PickupUpdate, organizationId, barcode, branchId), true);

    public void SetIdentifierResult(string identifier, int organizationId, IdentifierLookupResult result) =>
        Set(new(TestingPolarisOperation.IdentifierLookup, organizationId, identifier), result);

    public void SetBib(int bibId, int organizationId, BibValidationResult result, StaffBibHoldingsSummary? holdings = null)
    {
        Set(new(TestingPolarisOperation.BibValidation, organizationId, NativeId: bibId), result);
        if (holdings is not null)
        {
            Set(new(TestingPolarisOperation.BibHoldings, organizationId, NativeId: bibId), holdings);
        }
    }

    public void SetBibSearch(string mode, string query, string title, string author, int organizationId,
        StaffBibSearchResult result) =>
        Set(new(TestingPolarisOperation.BibSearch, organizationId, query, Mode: mode, Title: title, Author: author), result);

    public void SetPatronSearch(string query, int organizationId, IReadOnlyList<PatronSnapshot> patrons) =>
        Set(new(TestingPolarisOperation.PatronSearch, organizationId, query), patrons.ToArray());

    public void SetHolds(string barcode, int organizationId, IReadOnlyList<PolarisHoldSnapshot> holds) =>
        Set(new(TestingPolarisOperation.PatronHolds, organizationId, barcode), holds.ToArray());

    public void SetCheckouts(string barcode, int organizationId, IReadOnlyList<PolarisCheckoutSnapshot> checkouts) =>
        Set(new(TestingPolarisOperation.PatronCheckouts, organizationId, barcode), checkouts.ToArray());

    public void SetFailure(TestingPolarisCall call, Exception failure) => Set(call, failure);

    public void ExpectCreate(HoldCreateCommand command, HoldProviderResult result)
    {
        lock (sync)
        {
            creates.Enqueue((command, result));
        }
    }

    public void ExpectReply(HoldReplyCommand command, HoldProviderResult result)
    {
        lock (sync)
        {
            replies.Enqueue((command, result));
        }
    }

    public void VerifyNoOutstandingWrites()
    {
        lock (sync)
        {
            if (creates.Count != 0 || replies.Count != 0)
            {
                throw new InvalidOperationException("Expected hold writes were not invoked.");
            }
        }
    }

    public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
        Read<PolarisConnectionTestResult>(new(TestingPolarisOperation.Connection, LibraryScope.SystemOrganizationId), cancellationToken);

    public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken cancellationToken) =>
        Read<IReadOnlyList<PolarisOrganizationSnapshot>>(new(TestingPolarisOperation.Organizations, LibraryScope.SystemOrganizationId), cancellationToken);

    public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(CancellationToken cancellationToken) =>
        Read<IReadOnlyList<PolarisPatronCodeSnapshot>>(new(TestingPolarisOperation.PatronCodes, LibraryScope.SystemOrganizationId), cancellationToken);

    public async Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken)
    {
        var patron = await Read<PatronSnapshot>(new(TestingPolarisOperation.Authenticate, LibraryScope.SystemOrganizationId, barcode), cancellationToken);
        if (!string.Equals(pin, "1234", StringComparison.Ordinal))
        {
            throw new PatronAuthenticationException("Incorrect Login - Please try again");
        }
        return patron;
    }

    public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
        Read<PatronSnapshot>(new(TestingPolarisOperation.Refresh, organizationId, barcode), cancellationToken);

    public Task<int?> GetPatronIdAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
        Read<int?>(new(TestingPolarisOperation.PatronId, organizationId, barcode), cancellationToken);

    public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron,
        int organizationId, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<PickupBranch>>(new(TestingPolarisOperation.PickupBranches, organizationId, patron.Barcode), cancellationToken);

    public async Task UpdatePreferredPickupBranchAsync(string barcode, int pickupBranchId,
        int organizationId, CancellationToken cancellationToken)
    {
        await Read<bool>(new(TestingPolarisOperation.PickupUpdate, organizationId, barcode, pickupBranchId), cancellationToken);
        lock (sync)
        {
            var key = Normalize(new(TestingPolarisOperation.PickupBranches, organizationId, barcode));
            if (!responses.TryGetValue(key, out var value) || value is not IReadOnlyList<PickupBranch> branches ||
                !branches.Any(branch => branch.Id == pickupBranchId))
            {
                throw new InvalidOperationException("The expected pickup write targets an undeclared branch.");
            }
            var patronKeys = responses.Keys.Where(item =>
                item.Key == key.Key && item.Operation is TestingPolarisOperation.Refresh or TestingPolarisOperation.Authenticate).ToArray();
            foreach (var patronKey in patronKeys)
            {
                if (responses[patronKey] is PatronSnapshot patron)
                {
                    responses[patronKey] = patron with { PreferredPickupBranchId = pickupBranchId };
                }
            }
        }
    }

    public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, int organizationId, CancellationToken cancellationToken) =>
        Read<IdentifierLookupResult>(new(TestingPolarisOperation.IdentifierLookup, organizationId, identifier), cancellationToken);

    public Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken cancellationToken) =>
        Read<BibValidationResult>(new(TestingPolarisOperation.BibValidation, organizationId, NativeId: bibId), cancellationToken);

    public Task<StaffBibSearchResult> SearchBibsAsync(string mode, string query, string title, string author,
        int organizationId, CancellationToken cancellationToken) =>
        Read<StaffBibSearchResult>(new(TestingPolarisOperation.BibSearch, organizationId, query, Mode: mode, Title: title, Author: author), cancellationToken);

    public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(string query, int organizationId, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<PatronSnapshot>>(new(TestingPolarisOperation.PatronSearch, organizationId, query), cancellationToken);

    public Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(int bibId, int organizationId, CancellationToken cancellationToken) =>
        Read<StaffBibHoldingsSummary>(new(TestingPolarisOperation.BibHoldings, organizationId, NativeId: bibId), cancellationToken);

    public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<PolarisHoldSnapshot>>(new(TestingPolarisOperation.PatronHolds, organizationId, barcode), cancellationToken);

    public Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<PolarisCheckoutSnapshot>>(new(TestingPolarisOperation.PatronCheckouts, organizationId, barcode), cancellationToken);

    public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            calls.Add(new(TestingPolarisOperation.HoldCreate, command.RequestingOrganizationId, NativeId: command.BibId));
            createCommands.Add(command);
            if (creates.Count == 0 || creates.Peek().Command != command)
            {
                throw new InvalidOperationException($"Unexpected test provider hold create: {command}.");
            }
            return Task.FromResult(creates.Dequeue().Result);
        }
    }

    public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            calls.Add(new(TestingPolarisOperation.HoldReply, command.RequestingOrganizationId));
            replyCommands.Add(command);
            if (replies.Count == 0 || replies.Peek().Command != command)
            {
                throw new InvalidOperationException($"Unexpected test provider hold reply: {command}.");
            }
            return Task.FromResult(replies.Dequeue().Result);
        }
    }

    private Task<T> Read<T>(TestingPolarisCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            calls.Add(call);
            if (!responses.TryGetValue(Normalize(call), out var value))
            {
                throw new InvalidOperationException($"Unexpected test provider call/context: {call}.");
            }
            if (value is Exception failure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return Task.FromResult((T)value!);
        }
    }

    private void Set(TestingPolarisCall call, object? value)
    {
        lock (sync)
        {
            responses[Normalize(call)] = value;
        }
    }

    private static TestingPolarisCall Normalize(TestingPolarisCall call) => call with
    {
        Key = call.Key?.Trim().ToUpperInvariant(),
        Title = call.Title?.Trim().ToUpperInvariant(),
        Author = call.Author?.Trim().ToUpperInvariant()
    };
}
