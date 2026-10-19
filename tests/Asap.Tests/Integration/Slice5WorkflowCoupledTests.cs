using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task HourlyWorkflowGivesRecoveryAndNewPlacementIndependentPageBudgets()
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
                services.RemoveAll<Asap.Web.Features.Patron.IPatronProvider>();
                services.AddSingleton<Asap.Web.Features.Patron.IPatronProvider>(provider);
                services.RemoveAll<Asap.Web.Features.Staff.IStaffPolarisProvider>();
                services.AddSingleton<Asap.Web.Features.Staff.IStaffPolarisProvider>(provider);
            }));
        var contextFactory = lowCapFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var seed = await SeedCoupledRowsAsync(contextFactory, scope, recoveryCount: 3, placementCount: 3);
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.HoldRecovery, scope);
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.HoldPlacement, scope);
        try
        {
            var result = await lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(4, provider.CreateCount,
                "Recovery and new placement each get two provider mutation opportunities in one invocation.");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var recovery = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.HoldRecovery && item.ScopeOrganizationId == scope);
            var placement = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.HoldPlacement && item.ScopeOrganizationId == scope);
            Assert.AreEqual(seed.OperationIds[1], recovery.LastItemId);
            Assert.AreEqual(seed.PlacementRequestIds[1], placement.LastItemId);
            Assert.IsNotNull(recovery.CycleMaxId);
            Assert.IsNotNull(placement.CycleMaxId);
        }
        finally
        {
            await DeleteCoupledRowsAsync(contextFactory, seed, scope);
        }
    }

    [TestMethod]
    public async Task GlobalWorkflowFinishesInactiveAcquiredRecoveryButBlocksNewPlacement()
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 1;
        var provider = new QueueFairnessProvider();
        await using var lowCapFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<Asap.Web.Features.Patron.IPatronProvider>();
                services.AddSingleton<Asap.Web.Features.Patron.IPatronProvider>(provider);
                services.RemoveAll<Asap.Web.Features.Staff.IStaffPolarisProvider>();
                services.AddSingleton<Asap.Web.Features.Staff.IStaffPolarisProvider>(provider);
            }));
        var contextFactory = lowCapFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var originalQueueProgress = await SnapshotGlobalQueueProgressAsync(contextFactory);
        CoupledSeed? seed = null;
        var organizationStateCaptured = false;
        var wasActive = false;
        long legacyRecoveryRequestId = 0;
        try
        {
            var currentSeed = await SeedCoupledRowsAsync(
                contextFactory, scope: 1, recoveryCount: 1, placementCount: 1, requestLibraryOrganizationId: 2);
            seed = currentSeed;
            await using (var legacyRecoveryContext = await contextFactory.CreateDbContextAsync())
            {
                legacyRecoveryRequestId = await legacyRecoveryContext.HoldPlacementOperations
                    .Where(item => item.Id == currentSeed.OperationIds.Single())
                    .Select(item => item.TitleRequestId)
                    .SingleAsync();
                var request = await legacyRecoveryContext.TitleRequests.SingleAsync(item => item.Id == legacyRecoveryRequestId);
                request.PatronOrganizationId = null;
                await legacyRecoveryContext.SaveChangesAsync();
            }

            await PinGlobalWorkflowQueuesAsync(contextFactory, currentSeed);
            wasActive = await SetOrganizationActiveAsync(contextFactory, 2);
            organizationStateCaptured = true;
            await SetOrganizationActiveAsync(contextFactory, 2, false);

            var result = await lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(null, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(1, provider.CreateCount,
                "Inactive recovery may finish its acquired operation; inactive new placement must not call Polaris.");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var recoveredOperation = await verify.HoldPlacementOperations.AsNoTracking()
                .SingleAsync(item => item.Id == currentSeed.OperationIds.Single());
            Assert.IsTrue(recoveredOperation.CompletedUtc.HasValue);
            Assert.AreEqual(101, recoveredOperation.RequestingOrganizationIdSnapshot,
                "Recovery uses current verified provider routing while retaining the unknown historical request registration.");
            Assert.IsNull(await verify.TitleRequests.AsNoTracking()
                .Where(item => item.Id == legacyRecoveryRequestId)
                .Select(item => item.PatronOrganizationId).SingleAsync(),
                "Recovery may use current provider evidence without fabricating a missing historical registration.");
            var placementRequestId = currentSeed.PlacementRequestIds.Single();
            Assert.AreEqual("pending_hold", await verify.TitleRequests.AsNoTracking()
                .Where(item => item.Id == placementRequestId).Select(item => item.Status).SingleAsync());
            Assert.AreEqual(0, await verify.HoldPlacementOperations.AsNoTracking()
                .CountAsync(item => item.TitleRequestId == placementRequestId),
                "The inactive placement candidate must not create an acquired operation or call Polaris.");
            var recoveryProgress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.HoldRecovery && item.ScopeOrganizationId == 1);
            Assert.AreEqual(currentSeed.OperationIds.Single(), recoveryProgress.LastOutcomeItemId,
                "The global run must visit the acquired recovery candidate.");
            var placementProgress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.HoldPlacement && item.ScopeOrganizationId == 1);
            Assert.AreEqual(placementRequestId, placementProgress.LastOutcomeItemId,
                "The global run must visit the inactive placement candidate.");
            Assert.AreEqual("organization_inactive", placementProgress.LastOutcomeCode);
        }
        finally
        {
            try
            {
                if (organizationStateCaptured)
                {
                    await SetOrganizationActiveAsync(contextFactory, 2, wasActive);
                }
            }
            finally
            {
                try
                {
                    if (seed is not null)
                    {
                        await DeleteCoupledRowsAsync(contextFactory, seed, 1);
                    }
                }
                finally
                {
                    await RestoreGlobalQueueProgressAsync(contextFactory, originalQueueProgress);
                }
            }
        }
    }

    private async Task<IReadOnlyList<GlobalQueueProgressSnapshot>> SnapshotGlobalQueueProgressAsync(
        IDbContextFactory<AsapDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.QueueProgress.AsNoTracking()
            .Where(item => item.ScopeOrganizationId == 1)
            .Select(item => new GlobalQueueProgressSnapshot(
                item.QueueName,
                item.ScopeOrganizationId,
                item.CycleMaxId,
                item.LastCreatedUtc,
                item.LastItemId,
                item.LastOutcomeItemId,
                item.LastOutcomeCode,
                item.LastOutcomeUtc,
                item.UpdatedUtc))
            .ToArrayAsync();
    }

    private async Task PinGlobalWorkflowQueuesAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        CoupledSeed seed)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var operation = await context.HoldPlacementOperations.AsNoTracking()
            .Where(item => item.Id == seed.OperationIds.Single())
            .Select(item => new { item.Id, item.RequestStartedUtc })
            .SingleAsync();
        var placement = await context.TitleRequests.AsNoTracking()
            .Where(item => item.Id == seed.PlacementRequestIds.Single())
            .Select(item => new { item.Id, item.CreatedUtc })
            .SingleAsync();
        var progressRows = await context.QueueProgress
            .Where(item => item.ScopeOrganizationId == 1)
            .ToListAsync();
        var now = timeProvider!.GetUtcNow().UtcDateTime;

        QueueProgress ProgressFor(string queueName)
        {
            var progress = progressRows.SingleOrDefault(item => item.QueueName == queueName);
            if (progress is not null)
            {
                return progress;
            }

            progress = new QueueProgress { QueueName = queueName, ScopeOrganizationId = 1 };
            context.QueueProgress.Add(progress);
            progressRows.Add(progress);
            return progress;
        }

        foreach (var queueName in QueueNames.Configured.Append(QueueNames.HoldRecovery))
        {
            if (queueName is QueueNames.HoldRecovery or QueueNames.HoldPlacement)
            {
                continue;
            }

            var progress = ProgressFor(queueName);
            // A nonpositive cycle restarts a global scan. Give unrelated queues a
            // positive cycle and terminal key so they remain empty in this run.
            progress.CycleMaxId = long.MaxValue;
            progress.LastCreatedUtc = DateTime.MaxValue;
            progress.LastItemId = long.MaxValue;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = "test_isolated_empty";
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = now;
        }

        var recoveryProgress = ProgressFor(QueueNames.HoldRecovery);
        recoveryProgress.CycleMaxId = operation.Id;
        recoveryProgress.LastCreatedUtc = operation.RequestStartedUtc;
        recoveryProgress.LastItemId = operation.Id - 1;
        recoveryProgress.LastOutcomeItemId = null;
        recoveryProgress.LastOutcomeCode = null;
        recoveryProgress.LastOutcomeUtc = null;
        recoveryProgress.UpdatedUtc = now;

        var placementProgress = ProgressFor(QueueNames.HoldPlacement);
        placementProgress.CycleMaxId = placement.Id;
        placementProgress.LastCreatedUtc = placement.CreatedUtc;
        placementProgress.LastItemId = placement.Id - 1;
        placementProgress.LastOutcomeItemId = null;
        placementProgress.LastOutcomeCode = null;
        placementProgress.LastOutcomeUtc = null;
        placementProgress.UpdatedUtc = now;
        await context.SaveChangesAsync();
    }

    private async Task RestoreGlobalQueueProgressAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        IReadOnlyList<GlobalQueueProgressSnapshot> original)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var progressRows = await context.QueueProgress
            .Where(item => item.ScopeOrganizationId == 1)
            .ToListAsync();
        var originalByQueue = original.ToDictionary(item => item.QueueName, StringComparer.Ordinal);
        context.QueueProgress.RemoveRange(progressRows.Where(item => !originalByQueue.ContainsKey(item.QueueName)));

        foreach (var snapshot in original)
        {
            var progress = progressRows.SingleOrDefault(item => item.QueueName == snapshot.QueueName);
            if (progress is null)
            {
                progress = new QueueProgress
                {
                    QueueName = snapshot.QueueName,
                    ScopeOrganizationId = snapshot.ScopeOrganizationId
                };
                context.QueueProgress.Add(progress);
            }

            progress.CycleMaxId = snapshot.CycleMaxId;
            progress.LastCreatedUtc = snapshot.LastCreatedUtc;
            progress.LastItemId = snapshot.LastItemId;
            progress.LastOutcomeItemId = snapshot.LastOutcomeItemId;
            progress.LastOutcomeCode = snapshot.LastOutcomeCode;
            progress.LastOutcomeUtc = snapshot.LastOutcomeUtc;
            progress.UpdatedUtc = snapshot.UpdatedUtc;
        }

        await context.SaveChangesAsync();
    }

    private async Task<CoupledSeed> SeedCoupledRowsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        int scope,
        int recoveryCount,
        int placementCount,
        int? requestLibraryOrganizationId = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.Code == "book");
        var baseUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-10);
        var requestScope = requestLibraryOrganizationId ?? scope;
        var registeredBranchId = requestScope == 2 ? 101 : checked(requestScope + 1);
        var recoveryRequests = Enumerable.Range(0, recoveryCount).Select(index => new TitleRequest
        {
            LibraryOrganizationId = requestScope,
            PatronOrganizationId = registeredBranchId,
            Barcode = $"s5-cr-{Guid.NewGuid():N}",
            Title = $"Slice 5 coupled recovery {index}",
            MaterialFormatId = format.Id,
            Status = "pending_hold",
            BibId = (93000 + index),
            BibIdStaffVerified = true,
            AutoHold = true,
            IsbnCheckStatus = "found",
            CreatedUtc = baseUtc.AddTicks(200 + index),
            UpdatedUtc = baseUtc.AddTicks(200 + index)
        }).ToList();
        var placementRequests = Enumerable.Range(0, placementCount).Select(index => new TitleRequest
        {
            LibraryOrganizationId = requestScope,
            PatronOrganizationId = registeredBranchId,
            Barcode = $"s5-cp-{Guid.NewGuid():N}",
            Title = $"Slice 5 coupled placement {index}",
            MaterialFormatId = format.Id,
            Status = "pending_hold",
            BibId = (94000 + index),
            BibIdStaffVerified = true,
            AutoHold = true,
            IsbnCheckStatus = "found",
            CreatedUtc = baseUtc.AddTicks(100 + index),
            UpdatedUtc = baseUtc.AddTicks(100 + index)
        }).ToList();
        context.TitleRequests.AddRange(recoveryRequests);
        context.TitleRequests.AddRange(placementRequests);
        await context.SaveChangesAsync();
        var operations = recoveryRequests.Select((request, index) => new HoldPlacementOperation
        {
            TitleRequestId = request.Id,
            PatronBarcodeSnapshot = request.Barcode,
            BibIdSnapshot = request.BibId!.Value,
            PickupBranchIdSnapshot = 101,
            AttemptNumber = 1,
            State = "in_progress",
            Phase = "acquired",
            ExecutionEpoch = 1,
            RequestStartedUtc = baseUtc.AddTicks(index),
            DetailJson = "{}"
        }).ToList();
        context.HoldPlacementOperations.AddRange(operations);
        await context.SaveChangesAsync();
        foreach (var queueName in new[] { QueueNames.HoldRecovery, QueueNames.HoldPlacement })
        {
            var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == queueName && item.ScopeOrganizationId == scope);
            progress ??= new QueueProgress { QueueName = queueName, ScopeOrganizationId = scope };
            if (progress.RowVersion.Length == 0) context.QueueProgress.Add(progress);
            progress.CycleMaxId = null;
            progress.LastCreatedUtc = null;
            progress.LastItemId = null;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = null;
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime;
        }
        await context.SaveChangesAsync();
        return new CoupledSeed(
            operations.Select(item => item.Id).ToArray(),
            placementRequests.Select(item => item.Id).ToArray(),
            recoveryRequests.Concat(placementRequests).Select(item => item.Id).ToArray());
    }

    private async Task<bool> SetOrganizationActiveAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        int organizationId,
        bool? active = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var organization = await context.Organizations.SingleAsync(item => item.Id == organizationId);
        var previous = organization.IsActive;
        if (active.HasValue)
        {
            organization.IsActive = active.Value;
            await context.SaveChangesAsync();
        }
        return previous;
    }

    private async Task DeleteCoupledRowsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        CoupledSeed seed,
        int scope)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.TitleRequestEvents.RemoveRange(context.TitleRequestEvents.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
        context.TitleRequestWorkflowTags.RemoveRange(context.TitleRequestWorkflowTags.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
        context.HoldPlacementOperations.RemoveRange(context.HoldPlacementOperations.Where(item => seed.RequestIds.Contains(item.TitleRequestId)));
        context.TitleRequests.RemoveRange(context.TitleRequests.Where(item => seed.RequestIds.Contains(item.Id)));
        await context.SaveChangesAsync();
        foreach (var queueName in new[] { QueueNames.HoldRecovery, QueueNames.HoldPlacement })
        {
            var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == queueName && item.ScopeOrganizationId == scope);
            if (progress is null) continue;
            progress.CycleMaxId = null;
            progress.LastCreatedUtc = null;
            progress.LastItemId = null;
            progress.LastOutcomeItemId = null;
            progress.LastOutcomeCode = null;
            progress.LastOutcomeUtc = null;
            progress.UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime;
        }
        await context.SaveChangesAsync();
    }

    private sealed record CoupledSeed(
        IReadOnlyList<long> OperationIds,
        IReadOnlyList<long> PlacementRequestIds,
        IReadOnlyList<long> RequestIds);

    private sealed record GlobalQueueProgressSnapshot(
        string QueueName,
        int ScopeOrganizationId,
        long? CycleMaxId,
        DateTime? LastCreatedUtc,
        long? LastItemId,
        long? LastOutcomeItemId,
        string? LastOutcomeCode,
        DateTime? LastOutcomeUtc,
        DateTime UpdatedUtc);
}
