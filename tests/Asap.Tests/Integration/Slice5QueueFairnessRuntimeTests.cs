using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private const int Slice5IsolatedLibraryId = 99001;

    private static async Task EnsureSlice5IsolatedLibraryAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        int organizationId = Slice5IsolatedLibraryId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var organization = await context.Organizations.SingleOrDefaultAsync(item => item.Id == organizationId);
        if (organization is null)
        {
            context.Organizations.Add(new Organization
            {
                Id = organizationId,
                DisplayName = "Slice 5 Isolated Library",
                Abbreviation = "S5I",
                IsActive = true
            });
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    [DataRow(QueueNames.IdentifierProcessing)]
    [DataRow(QueueNames.PurchasePromotion)]
    [DataRow(QueueNames.HoldPlacement)]
    [DataRow(QueueNames.FulfillmentTracking)]
    [DataRow(QueueNames.OutstandingTimeout)]
    [DataRow(QueueNames.PendingHoldTimeout)]
    [DataRow(QueueNames.HoldPickupTimeout)]
    [DataRow(QueueNames.AdditionalCopyTimeout)]
    [DataRow(QueueNames.HoldRecovery)]
    public async Task QueueFairnessRunsFiveRowsWithPageSizeOneAndMaxPerRunTwo(string queueName)
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 2;

        var provider = new QueueFairnessProvider();
        await using var lowCapFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var contextFactory = lowCapFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedFairnessRowsAsync(contextFactory, queueName);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, queueName, scope);
        try
        {
            var service = lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>();
            for (var invocation = 0; invocation < 3; invocation++)
            {
                var result = queueName == QueueNames.IdentifierProcessing
                    ? await service.ProcessIdentifierAsync(scope, CancellationToken.None)
                    : await service.ProcessWorkflowAsync(scope, CancellationToken.None);
                Assert.AreNotEqual("sql_failure", result.Code);
                Assert.AreNotEqual("stale_progress_fence", result.Code);

                await using var progressContext = await contextFactory.CreateDbContextAsync();
                var progress = await progressContext.QueueProgress.AsNoTracking().SingleAsync(item =>
                    item.QueueName == queueName && item.ScopeOrganizationId == scope);
                if (invocation < 2)
                {
                    var expectedIndex = invocation * 2 + 1;
                    Assert.AreEqual(seed.CursorIds[expectedIndex], progress.LastItemId,
                        $"{queueName} must advance exactly two keyset rows on invocation {invocation + 1}.");
                    Assert.IsTrue(progress.CycleMaxId >= seed.CursorIds[^1]);
                }
                else
                {
                    Assert.IsNull(progress.CycleMaxId, $"{queueName} must wrap only after its fifth row.");
                    Assert.AreEqual("cycle_complete", progress.LastOutcomeCode);
                    Assert.AreEqual(seed.CursorIds[^1], progress.LastOutcomeItemId);
                }
            }

            await using var verify = await contextFactory.CreateDbContextAsync();
            if (queueName == QueueNames.HoldRecovery)
            {
                var operations = await verify.HoldPlacementOperations.AsNoTracking()
                    .Where(item => seed.OperationIds.Contains(item.Id)).ToListAsync();
                Assert.IsTrue(operations.All(item => item.CompletedUtc.HasValue));
            }
            else if (queueName == QueueNames.IdentifierProcessing)
            {
                var rows = await verify.TitleRequests.AsNoTracking()
                    .Where(item => seed.CursorIds.Contains(item.Id)).ToListAsync();
                Assert.IsTrue(rows.All(item => item.IsbnCheckStatus == "skipped_no_isbn"));
            }
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, queueName, seed);
        }
    }

    private static async Task IsolateOtherWorkflowQueuesAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        string targetQueue,
        int scope = 2)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var queueNames = QueueNames.Configured.Append(QueueNames.HoldRecovery)
            .Where(item => item != targetQueue).ToArray();
        foreach (var queueName in queueNames)
        {
            var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == queueName && item.ScopeOrganizationId == scope);
            progress ??= new QueueProgress { QueueName = queueName, ScopeOrganizationId = scope };
            if (progress.RowVersion.Length == 0) context.QueueProgress.Add(progress);
            progress.CycleMaxId = 0;
            progress.LastCreatedUtc = null;
            progress.LastItemId = null;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = "test_isolated_empty";
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = DateTime.UtcNow;
        }
        await context.SaveChangesAsync();
    }

    private static async Task<FairnessSeed> SeedFairnessRowsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        string queueName,
        int scope = Slice5IsolatedLibraryId)
    {
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        await using var context = await contextFactory.CreateDbContextAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.Code == "book");
        var baseUtc = DateTime.UtcNow.AddDays(-60);
        var requests = new List<TitleRequest>();
        var operations = new List<HoldPlacementOperation>();
        var copies = new List<AdditionalCopyRequest>();
        for (var index = 0; index < 5; index++)
        {
            var status = queueName switch
            {
                QueueNames.IdentifierProcessing or QueueNames.OutstandingTimeout => "suggestion",
                QueueNames.PurchasePromotion => "outstanding_purchase",
                _ => "hold_placed"
            };
            if (queueName == QueueNames.PendingHoldTimeout || queueName == QueueNames.HoldPlacement)
            {
                status = "pending_hold";
            }
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"slice5-fairness-{Guid.NewGuid():N}-{index}",
                Title = $"Slice 5 fairness {queueName} {index}",
                Author = "Slice Five",
                MaterialFormatId = format.Id,
                Status = status,
                BibId = queueName == QueueNames.IdentifierProcessing ? null : (90000 + index).ToString(),
                AutoHold = queueName == QueueNames.HoldPlacement,
                IsbnCheckStatus = queueName == QueueNames.IdentifierProcessing ? "pending" : "found",
                CreatedUtc = baseUtc.AddTicks(index),
                UpdatedUtc = baseUtc.AddTicks(index)
            };
            requests.Add(request);
        }

        if (queueName == QueueNames.AdditionalCopyTimeout)
        {
            requests.Clear();
            for (var index = 0; index < 5; index++)
            {
                copies.Add(new AdditionalCopyRequest
                {
                    LibraryOrganizationId = scope,
                    BibId = (91000 + index).ToString(),
                    Title = $"Slice 5 fairness copy {index}",
                    Status = "open",
                    CreatedUtc = baseUtc.AddTicks(index),
                    UpdatedUtc = baseUtc.AddTicks(index)
                });
            }
            context.AdditionalCopyRequests.AddRange(copies);
            await context.SaveChangesAsync();
        }
        else if (queueName == QueueNames.HoldRecovery)
        {
            requests.Clear();
            for (var index = 0; index < 5; index++)
            {
                requests.Add(new TitleRequest
                {
                    LibraryOrganizationId = scope,
                    Barcode = $"slice5-recovery-{Guid.NewGuid():N}-{index}",
                    Title = $"Slice 5 recovery {index}",
                    MaterialFormatId = format.Id,
                    Status = "suggestion",
                    BibId = (92000 + index).ToString(),
                    IsbnCheckStatus = "found",
                    CreatedUtc = baseUtc.AddTicks(index),
                    UpdatedUtc = baseUtc.AddTicks(index)
                });
            }
            context.TitleRequests.AddRange(requests);
            await context.SaveChangesAsync();
            operations.AddRange(requests.Select((request, index) => new HoldPlacementOperation
            {
                TitleRequestId = request.Id,
                PatronBarcodeSnapshot = request.Barcode,
                BibIdSnapshot = request.BibId!,
                AttemptNumber = 1,
                State = "ambiguous",
                Phase = "result_recorded",
                ExecutionEpoch = 1,
                RequestStartedUtc = baseUtc.AddTicks(index),
                ResultCode = "definitive_no_effect",
                OutcomeEvidenceKind = "provider_final_no_effect",
                DetailJson = "{}"
            }));
            context.HoldPlacementOperations.AddRange(operations);
            await context.SaveChangesAsync();
        }
        else
        {
            context.TitleRequests.AddRange(requests);
            context.AdditionalCopyRequests.AddRange(copies);
            await context.SaveChangesAsync();
        }

        var cursorIds = queueName == QueueNames.AdditionalCopyTimeout
            ? copies.Select(item => item.Id).ToList()
            : queueName == QueueNames.HoldRecovery
                ? operations.Select(item => item.Id).ToList()
                : requests.Select(item => item.Id).ToList();
        var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
            item.QueueName == queueName && item.ScopeOrganizationId == scope);
        progress ??= new QueueProgress { QueueName = queueName, ScopeOrganizationId = scope };
        if (progress.RowVersion.Length == 0) context.QueueProgress.Add(progress);
        progress.CycleMaxId = null;
        progress.LastCreatedUtc = null;
        progress.LastItemId = null;
        progress.LastOutcomeItemId = null;
        progress.LastOutcomeCode = "test_cycle_started";
        progress.LastOutcomeUtc = null;
        progress.UpdatedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return new FairnessSeed(scope, cursorIds, operations.Select(item => item.Id).ToList(), requests.Select(item => item.Id).ToList(), copies.Select(item => item.Id).ToList());
    }

    private static async Task DeleteFairnessRowsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        string queueName,
        FairnessSeed seed)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        if (seed.RequestIds.Count != 0)
        {
            context.TitleRequestEvents.RemoveRange(context.TitleRequestEvents.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
            context.TitleRequestWorkflowTags.RemoveRange(context.TitleRequestWorkflowTags.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
            context.HoldPlacementOperations.RemoveRange(context.HoldPlacementOperations.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
            context.TitleRequests.RemoveRange(context.TitleRequests.Where(item => seed.RequestIds.Contains(item.Id)));
        }
        if (seed.CopyIds.Count != 0)
        {
            context.AdditionalCopyRequests.RemoveRange(context.AdditionalCopyRequests.Where(item => seed.CopyIds.Contains(item.Id)));
        }
        await context.SaveChangesAsync();
        var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
            item.QueueName == queueName && item.ScopeOrganizationId == seed.ScopeOrganizationId);
        if (progress is not null)
        {
            progress.CycleMaxId = null;
            progress.LastCreatedUtc = null;
            progress.LastItemId = null;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = null;
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    private sealed record FairnessSeed(
        int ScopeOrganizationId,
        IReadOnlyList<long> CursorIds,
        IReadOnlyList<long> OperationIds,
        IReadOnlyList<long> RequestIds,
        IReadOnlyList<long> CopyIds);

    private sealed class QueueFairnessProvider : IPatronProvider, IStaffPolarisProvider
    {
        public int CreateCount { get; private set; }

        public IdentifierLookupResult IdentifierResult { get; set; } =
            new(IdentifierLookupOutcome.DefinitiveNotFound);

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken) => RefreshAsync(barcode, cancellationToken);

        public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken) =>
            Task.FromResult(new PatronSnapshot(7105, barcode, "fairness@example.org", "Fair", "Tester", "1", "Standard", 2, 2, "Library", 101));

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickupBranch>>([new PickupBranch(101, "Main Library")]);

        public Task UpdatePreferredPickupBranchAsync(PatronSnapshot patron, int pickupBranchId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, CancellationToken cancellationToken) =>
            Task.FromResult(IdentifierResult);

        public Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken) =>
            Task.FromResult(new BibValidationResult(true));

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisHoldSnapshot>>([]);

        public Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(string barcode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisCheckoutSnapshot>>([]);

        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(new HoldProviderResult(
                HoldProviderOutcome.DefinitiveNoEffect,
                Guid.NewGuid().ToString(), null, null, null, 2, 0,
                "provider_final_no_effect"));
        }

        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new HoldProviderResult(
                HoldProviderOutcome.DefinitiveNoEffect,
                command.RequestGuid.ToString(), null, command.TxnGroupQualifier, command.TxnQualifier, 2, 0,
                "provider_final_no_effect"));
    }
}
