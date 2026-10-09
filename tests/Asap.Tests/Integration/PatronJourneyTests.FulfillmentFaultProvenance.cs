using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("initial_identity")]
    [DataRow("checkout_read")]
    [DataRow("checkout_owner")]
    [DataRow("hold_read")]
    [DataRow("terminal_hold_owner")]
    public async Task FulfillmentTypedProviderFaultSurvivesDiagnosticCancellation(string boundary)
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var barcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var bibId = Random.Shared.Next(90000, 99999);
        var holdId = bibId;
        using var cancellation = new CancellationTokenSource();
        var provider = new CancelThenFaultFulfillmentProvider(
            boundary,
            cancellation,
            new PolarisOperationalException("checkout_read_failed", "Synthetic typed provider fault."),
            barcode,
            bibId,
            holdId);
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            $"cancelled-diagnostic-{boundary}",
            barcode,
            bibId,
            holdRequestId: holdId,
            organizationId: scope);
        try
        {
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, scope, seeded.RequestId);
            var checkpointBefore = await ReadFulfillmentCheckpointAsync(scope);
            var operationBefore = await ReadFulfillmentOperationAsync(seeded.OperationId);

            var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() =>
                workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                    .ProcessWorkflowAsync(scope, cancellation.Token));

            Assert.AreSame(provider.Fault, failure);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            CollectionAssert.AreEqual(provider.ExpectedCalls, provider.Calls.ToArray());
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual(0, await CountForRequestAsync(
                "[asap].[TitleRequestEvent]", "[TitleRequestId]", seeded.RequestId));

            var operationAfter = await ReadFulfillmentOperationAsync(seeded.OperationId);
            Assert.AreEqual(operationBefore.State, operationAfter.State);
            Assert.AreEqual(operationBefore.Phase, operationAfter.Phase);
            Assert.AreEqual(operationBefore.LastErrorCode, operationAfter.LastErrorCode);
            CollectionAssert.AreEqual(operationBefore.RowVersion, operationAfter.RowVersion);

            var checkpointAfter = await ReadFulfillmentCheckpointAsync(scope);
            Assert.AreEqual(checkpointBefore.LastOutcomeCode, checkpointAfter.LastOutcomeCode);
            Assert.AreEqual(checkpointBefore.LastOutcomeItemId, checkpointAfter.LastOutcomeItemId);
            CollectionAssert.AreEqual(checkpointBefore.RowVersion, checkpointAfter.RowVersion);
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    private async Task<(string State, string Phase, string? LastErrorCode, byte[] RowVersion)>
        ReadFulfillmentOperationAsync(long operationId)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var operation = await context.HoldPlacementOperations.AsNoTracking()
            .Where(row => row.Id == operationId)
            .Select(row => new { row.State, row.Phase, row.LastErrorCode, row.RowVersion })
            .SingleAsync();
        return (operation.State, operation.Phase, operation.LastErrorCode, operation.RowVersion.ToArray());
    }

    private async Task<(string? LastOutcomeCode, long? LastOutcomeItemId, byte[] RowVersion)>
        ReadFulfillmentCheckpointAsync(int scope)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var checkpoint = await context.QueueProgress.AsNoTracking()
            .Where(row => row.QueueName == QueueNames.FulfillmentTracking && row.ScopeOrganizationId == scope)
            .Select(row => new { row.LastOutcomeCode, row.LastOutcomeItemId, row.RowVersion })
            .SingleAsync();
        return (checkpoint.LastOutcomeCode, checkpoint.LastOutcomeItemId, checkpoint.RowVersion.ToArray());
    }

    private sealed class CancelThenFaultFulfillmentProvider : IStaffPolarisProvider, IPatronProvider
    {
        private readonly string boundary;
        private readonly CancellationTokenSource cancellation;
        private readonly string barcode;
        private readonly int bibId;
        private readonly int holdId;

        public CancelThenFaultFulfillmentProvider(
            string boundary,
            CancellationTokenSource cancellation,
            PolarisOperationalException fault,
            string barcode,
            int bibId,
            int holdId)
        {
            this.boundary = boundary;
            this.cancellation = cancellation;
            Fault = fault;
            this.barcode = barcode;
            this.bibId = bibId;
            this.holdId = holdId;
        }

        public PolarisOperationalException Fault { get; }
        public List<string> Calls { get; } = [];
        public string[] ExpectedCalls => boundary switch
        {
            "initial_identity" => ["refresh:1", "typed_fault"],
            "checkout_read" => ["refresh:1", "checkouts", "typed_fault"],
            "checkout_owner" => ["refresh:1", "checkouts", "refresh:2", "typed_fault"],
            "hold_read" => ["refresh:1", "checkouts", "holds", "typed_fault"],
            "terminal_hold_owner" => ["refresh:1", "checkouts", "holds", "refresh:2", "typed_fault"],
            _ => throw new AssertFailedException($"Unknown fulfillment boundary {boundary}.")
        };

        public Task<PatronSnapshot> RefreshAsync(
            string requestedBarcode,
            int requestedOrganizationId,
            CancellationToken cancellationToken)
        {
            var call = $"refresh:{Calls.Count(value => value.StartsWith("refresh:", StringComparison.Ordinal)) + 1}";
            Calls.Add(call);
            if ((boundary == "initial_identity" && call == "refresh:1") ||
                ((boundary is "checkout_owner" or "terminal_hold_owner") && call == "refresh:2"))
            {
                ThrowAfterCallerCancellation(cancellationToken);
            }

            return Task.FromResult(new PatronSnapshot(
                7001,
                requestedBarcode,
                null,
                null,
                null,
                null,
                null,
                requestedOrganizationId,
                requestedOrganizationId,
                "Fulfillment test library",
                null,
                RequestedBarcode: requestedBarcode));
        }

        public Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(
            string requestedBarcode,
            int requestedOrganizationId,
            CancellationToken cancellationToken)
        {
            Calls.Add("checkouts");
            if (boundary == "checkout_read")
            {
                ThrowAfterCallerCancellation(cancellationToken);
            }

            IReadOnlyList<PolarisCheckoutSnapshot> checkouts = boundary == "checkout_owner"
                ? [new PolarisCheckoutSnapshot(bibId, holdId, barcode)]
                : [];
            return Task.FromResult(checkouts);
        }

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string requestedBarcode,
            int requestedOrganizationId,
            CancellationToken cancellationToken)
        {
            Calls.Add("holds");
            if (boundary == "hold_read")
            {
                ThrowAfterCallerCancellation(cancellationToken);
            }

            IReadOnlyList<PolarisHoldSnapshot> holds = boundary == "terminal_hold_owner"
                ? [new PolarisHoldSnapshot(holdId, bibId, 16, "Unknown translation", 101, barcode)]
                : [];
            return Task.FromResult(holds);
        }

        public Task<PatronSnapshot> AuthenticateAsync(string requestedBarcode, string pin, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not authenticate a patron.");

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int requestedOrganizationId,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not read pickup branches.");

        public Task UpdatePreferredPickupBranchAsync(
            string requestedBarcode,
            int pickupBranchId,
            int requestedOrganizationId,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not update pickup preferences.");

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int requestedOrganizationId,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not perform identifier lookup.");

        public Task<BibValidationResult> ValidateBibAsync(
            int requestedBibId,
            int requestedOrganizationId,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not validate a BIB.");

        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not create a hold.");

        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Fulfillment must not reply to a hold.");

        private void ThrowAfterCallerCancellation(CancellationToken cancellationToken)
        {
            Calls.Add("typed_fault");
            cancellation.Cancel();
            Assert.IsTrue(cancellationToken.IsCancellationRequested);
            throw Fault;
        }
    }
}
