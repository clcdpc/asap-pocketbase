using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("search", "return_cancelled")]
    [DataRow("search", "operation_cancelled")]
    [DataRow("search", "failure_cancelled")]
    [DataRow("search", "failure_uncancelled")]
    [DataRow("search", "generic_failure_cancelled")]
    [DataRow("search", "generic_failure_uncancelled")]
    [DataRow("refresh", "return_cancelled")]
    [DataRow("refresh", "operation_cancelled")]
    [DataRow("refresh", "failure_cancelled")]
    [DataRow("refresh", "failure_uncancelled")]
    [DataRow("refresh", "generic_failure_cancelled")]
    [DataRow("refresh", "generic_failure_uncancelled")]
    [DataRow("branches", "return_cancelled")]
    [DataRow("branches", "operation_cancelled")]
    [DataRow("branches", "failure_cancelled")]
    [DataRow("branches", "failure_uncancelled")]
    [DataRow("branches", "generic_failure_cancelled")]
    [DataRow("branches", "generic_failure_uncancelled")]
    public async Task StaffSuggestionProviderFaultPreservesProvenanceAtLookupBoundary(
        string stage,
        string providerMode)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var cancellation = new CancellationTokenSource();
        var provider = new BoundaryStaffSuggestionProvider(stage, providerMode, cancellation);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        var lookup = scopedFactory.Services.GetRequiredService<StaffSuggestionService>();
        var input = stage == "search"
            ? new StaffPatronLookupInput("Jordan Example", null, 2)
            : new StaffPatronLookupInput(null, "20000000001994", 2);

        if (providerMode is "failure_cancelled" or "failure_uncancelled")
        {
            var failure = await Assert.ThrowsExactlyAsync<StaffSuggestionException>(() =>
                lookup.LookupAsync(actor, input, cancellation.Token));
            Assert.AreEqual(stage == "branches" ? "pickup_branches_unavailable" : "polaris_unavailable", failure.Code);
            Assert.IsInstanceOfType<PolarisOperationalException>(failure.InnerException);
            Assert.AreEqual(providerMode == "failure_cancelled", cancellation.IsCancellationRequested);
        }
        else if (providerMode is "generic_failure_cancelled" or "generic_failure_uncancelled")
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                lookup.LookupAsync(actor, input, cancellation.Token));
            Assert.AreSame(provider.GenericFailure, failure);
            Assert.AreEqual(providerMode == "generic_failure_cancelled", cancellation.IsCancellationRequested);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                lookup.LookupAsync(actor, input, cancellation.Token));
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }

        Assert.AreEqual(stage == "search" ? 1 : 0, provider.SearchCount);
        Assert.AreEqual(stage == "search" ? 0 : 1, provider.RefreshCount);
        Assert.AreEqual(stage == "branches" ? 1 : 0, provider.BranchCount);
        Assert.AreEqual(0, provider.UpdateCount);
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    [DataRow("generic_failure_cancelled")]
    [DataRow("generic_failure_uncancelled")]
    public async Task StaffSuggestionCreateStopsBeforeMutationWhenProviderCancelsOrFails(string providerMode)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var cancellation = new CancellationTokenSource();
        var provider = new BoundaryStaffSuggestionProvider("refresh", providerMode, cancellation);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        var service = scopedFactory.Services.GetRequiredService<StaffSuggestionService>();
        var title = $"Provider cancellation before staff mutation {Guid.NewGuid():N}";
        var input = StaffBibInput(title, 9001);

        if (providerMode is "failure_cancelled" or "failure_uncancelled")
        {
            var failure = await Assert.ThrowsExactlyAsync<StaffSuggestionException>(() =>
                service.CreateAsync(actor, input, cancellation.Token));
            Assert.AreEqual("polaris_unavailable", failure.Code);
            Assert.IsInstanceOfType<PolarisOperationalException>(failure.InnerException);
            Assert.AreEqual(providerMode == "failure_cancelled", cancellation.IsCancellationRequested);
        }
        else if (providerMode is "generic_failure_cancelled" or "generic_failure_uncancelled")
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CreateAsync(actor, input, cancellation.Token));
            Assert.AreSame(provider.GenericFailure, failure);
            Assert.AreEqual(providerMode == "generic_failure_cancelled", cancellation.IsCancellationRequested);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.CreateAsync(actor, input, cancellation.Token));
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }

        Assert.AreEqual(1, provider.RefreshCount);
        Assert.AreEqual(0, provider.UpdateCount);
        await AssertNoStaffBibRequestAsync(title);
    }

    [TestMethod]
    public async Task StaffSuggestionPreIntentCancellationDoesNotCallPatronProviders()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new BoundaryStaffSuggestionProvider("refresh", "failure_uncancelled", cancellation);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        var lookup = scopedFactory.Services.GetRequiredService<StaffSuggestionService>();

        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup.LookupAsync(
            actor,
            new StaffPatronLookupInput(null, "20000000001994", 2),
            cancellation.Token));

        Assert.AreEqual(0, provider.SearchCount);
        Assert.AreEqual(0, provider.RefreshCount);
        Assert.AreEqual(0, provider.BranchCount);
        Assert.AreEqual(0, provider.UpdateCount);
    }

    private sealed class BoundaryStaffSuggestionProvider(
        string boundary,
        string mode,
        CancellationTokenSource cancellation) : IPatronProvider, IStaffPolarisProvider
    {
        private readonly PatronSnapshot patron = new(
            7004,
            "20000000001994",
            "pickup@example.org",
            "Jordan",
            "Example",
            1,
            "Adult",
            101,
            2,
            "Test Library",
            101);

        public int SearchCount { get; private set; }
        public int RefreshCount { get; private set; }
        public int BranchCount { get; private set; }
        public int UpdateCount { get; private set; }
        public InvalidOperationException GenericFailure { get; } =
            new("The provider programming failure must retain its original exception.");

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken) =>
            RefreshAsync(barcode, 2, cancellationToken);

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken)
        {
            RefreshCount++;
            ApplyBoundary("refresh", cancellationToken);
            return Task.FromResult(patron with { Barcode = barcode });
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot currentPatron,
            int organizationId,
            CancellationToken cancellationToken)
        {
            BranchCount++;
            ApplyBoundary("branches", cancellationToken);
            return Task.FromResult<IReadOnlyList<PickupBranch>>([new PickupBranch(101, "Main Library")]);
        }

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId,
            CancellationToken cancellationToken)
        {
            UpdateCount++;
            return Task.CompletedTask;
        }

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));

        public Task<BibValidationResult> ValidateBibAsync(
            int bibId,
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BibValidationResult(true));

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode,
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisHoldSnapshot>>([]);

        public Task<HoldProviderResult> CreateHoldAsync(
            HoldCreateCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<HoldProviderResult>(new NotSupportedException());

        public Task<HoldProviderResult> ReplyToHoldAsync(
            HoldReplyCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<HoldProviderResult>(new NotSupportedException());

        public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(
            string query,
            int organizationId,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            ApplyBoundary("search", cancellationToken);
            return Task.FromResult<IReadOnlyList<PatronSnapshot>>([]);
        }

        private void ApplyBoundary(string currentBoundary, CancellationToken cancellationToken)
        {
            if (boundary != currentBoundary)
            {
                return;
            }

            switch (mode)
            {
                case "return_cancelled":
                    cancellation.Cancel();
                    return;
                case "operation_cancelled":
                    cancellation.Cancel();
                    throw new OperationCanceledException("The provider cancelled after caller cancellation.", cancellationToken);
                case "failure_cancelled":
                    cancellation.Cancel();
                    throw new PolarisOperationalException("provider_failed", "The provider failed after caller cancellation.");
                case "failure_uncancelled":
                    throw new PolarisOperationalException("provider_failed", "The provider failed without caller cancellation.");
                case "generic_failure_cancelled":
                    cancellation.Cancel();
                    throw GenericFailure;
                case "generic_failure_uncancelled":
                    throw GenericFailure;
                default:
                    throw new AssertFailedException($"Unknown provider mode {mode}.");
            }
        }
    }
}
