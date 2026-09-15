using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task QueueCursorRowDeletionDoesNotResetThePersistedCheckpoint()
    {
        var configuration = CreateLowCapConfiguration(pageSize: 1, maxPerRun: 2);
        var provider = new QueueFairnessProvider();
        await using var workflowFactory = CreateQueueTestFactory(configuration, provider);
        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        try
        {
            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            Assert.AreEqual(seed.CursorIds[1], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var cursorRow = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[1]);
                mutate.TitleRequests.Remove(cursorRow);
                await mutate.SaveChangesAsync();
            }

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            Assert.AreEqual(seed.CursorIds[3], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing, seed);
        }
    }

    [TestMethod]
    public async Task LateCommittedRowBelowWatermarkIsVisitedOnTheSameFiniteCycle()
    {
        var configuration = CreateLowCapConfiguration(pageSize: 1, maxPerRun: 1);
        var provider = new QueueFairnessProvider();
        await using var workflowFactory = CreateQueueTestFactory(configuration, provider);
        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        const long lateId = 900000;
        const long highId = 900001;
        try
        {
            await using (var hideSeedRows = await contextFactory.CreateDbContextAsync())
            {
                foreach (var request in await hideSeedRows.TitleRequests
                             .Where(item => seed.RequestIds.Contains(item.Id)).ToListAsync())
                {
                    request.IsbnCheckStatus = "found";
                    request.BibId = $"s5-{request.Id}";
                }
                await hideSeedRows.SaveChangesAsync();
            }

            var highCreatedUtc = DateTime.UtcNow.AddDays(-9);
            await InsertExplicitIdentifierRequestAsync(
                contextFactory, scope, highId, "Slice 5 committed watermark row", highCreatedUtc);

            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            Assert.AreEqual(highId, (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).CycleMaxId);

            await InsertExplicitIdentifierRequestAsync(
                contextFactory, scope, lateId, "Slice 5 late commit", DateTime.UtcNow.AddDays(-8));

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            Assert.AreEqual("skipped_no_isbn", await ReadStatusAsync(contextFactory, lateId));
            Assert.AreEqual(lateId, (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing,
                seed with { RequestIds = seed.RequestIds.Concat([lateId, highId]).ToList() });
        }
    }

    [TestMethod]
    public async Task FirstIdentifierCycleProcessesImportedBusinessRowsWithEmptyQueueProgress()
    {
        const int scope = 99003;
        var configuration = CreateLowCapConfiguration(pageSize: 1, maxPerRun: 2);
        var provider = new QueueFairnessProvider();
        await using var workflowFactory = CreateQueueTestFactory(configuration, provider);
        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var requestId = 0L;
        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var existingProgress = await seed.QueueProgress.SingleOrDefaultAsync(item =>
                    item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == scope);
                if (existingProgress is not null)
                {
                    seed.QueueProgress.Remove(existingProgress);
                    await seed.SaveChangesAsync();
                }

                var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var imported = new TitleRequest
                {
                    LegacyId = $"slice5-imported-{Guid.NewGuid():N}",
                    LibraryOrganizationId = scope,
                    Barcode = $"s5-imported-{Guid.NewGuid():N}"[..28],
                    Title = "Imported business row for first cycle",
                    Author = "Slice Five",
                    MaterialFormatId = formatId,
                    Status = "suggestion",
                    IsbnCheckStatus = "pending",
                    CreatedUtc = DateTime.UtcNow.AddDays(-30),
                    UpdatedUtc = DateTime.UtcNow.AddDays(-30)
                };
                seed.TitleRequests.Add(imported);
                await seed.SaveChangesAsync();
                requestId = imported.Id;
                seed.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = requestId,
                    EventType = "legacy_status_imported",
                    Status = "suggestion",
                    ActorType = "system",
                    Message = "Imported business row retained for the first target cycle.",
                    MetadataJson = "{\"source\":\"slice5-test-import\"}",
                    CreatedUtc = imported.CreatedUtc
                });
                await seed.SaveChangesAsync();
            }

            await using (var empty = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsNull(await empty.QueueProgress.SingleOrDefaultAsync(item =>
                    item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == scope));
            }

            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(1, result.Visited);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
            Assert.AreEqual("skipped_no_isbn", request.IsbnCheckStatus);
            var progress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == scope);
            Assert.IsNull(progress.CycleMaxId);
            Assert.AreEqual(requestId, progress.LastOutcomeItemId);
            Assert.AreEqual("cycle_complete", progress.LastOutcomeCode);
        }
        finally
        {
            await DeleteRequestIdsAsync(contextFactory, [requestId]);
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            var progress = await cleanup.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == scope);
            if (progress is not null)
            {
                cleanup.QueueProgress.Remove(progress);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    private static async Task InsertExplicitIdentifierRequestAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        int scope,
        long id,
        string title,
        DateTime createdUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var formatId = await context.MaterialFormats.Where(item => item.Code == "book")
            .Select(item => item.Id).SingleAsync();
        await context.Database.OpenConnectionAsync();
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [asap].[TitleRequest] ON;");
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [asap].[TitleRequest]
                    ([Id], [LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId],
                     [Status], [BibId], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES ({id}, {scope}, {$"slice5-explicit-{id}"}, {title}, 0, {formatId},
                        N'suggestion', NULL, N'pending', {createdUtc}, {createdUtc});
                """);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT [asap].[TitleRequest] OFF;");
        }
    }

    [TestMethod]
    public async Task QueueCheckpointSurvivesNewServiceAndChangedLimitWithoutReset()
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
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        try
        {
            var first = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(lowCapFactory.Services);
            var firstResult = await first.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", firstResult.Code);
            Assert.AreEqual(seed.CursorIds[1], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);

            configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 1;
            var second = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(lowCapFactory.Services);
            var secondResult = await second.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", secondResult.Code);
            Assert.AreEqual(seed.CursorIds[2], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);

            var third = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(lowCapFactory.Services);
            await third.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual(seed.CursorIds[3], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing, seed);
        }
    }

    [TestMethod]
    public async Task QueueWatermarkExcludesDeletionBackdatedAndLateInsertedRowsUntilNextCycle()
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
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        var extraIds = new List<long>();
        try
        {
            var service = lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            var cursorAfterFirst = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, scope);

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var deleted = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[2]);
                mutate.TitleRequests.Remove(deleted);
                var backdated = new TitleRequest
                {
                    LibraryOrganizationId = scope,
                    Barcode = $"slice5-backdated-{Guid.NewGuid():N}",
                    Title = "Slice 5 backdated",
                    Author = "Slice Five",
                    MaterialFormatId = await mutate.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).SingleAsync(),
                    Status = "suggestion",
                    IsbnCheckStatus = "pending",
                    CreatedUtc = cursorAfterFirst.CreatedUtc.AddTicks(-1),
                    UpdatedUtc = cursorAfterFirst.CreatedUtc.AddTicks(-1)
                };
                var late = new TitleRequest
                {
                    LibraryOrganizationId = scope,
                    Barcode = $"slice5-late-{Guid.NewGuid():N}",
                    Title = "Slice 5 late insert",
                    Author = "Slice Five",
                    MaterialFormatId = backdated.MaterialFormatId,
                    Status = "suggestion",
                    IsbnCheckStatus = "pending",
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                mutate.TitleRequests.AddRange(backdated, late);
                await mutate.SaveChangesAsync();
                extraIds.Add(backdated.Id);
                extraIds.Add(late.Id);
            }

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            Assert.AreEqual(seed.CursorIds[4], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);
            var beforeWrap = await ReadStatusesAsync(contextFactory, extraIds);
            Assert.IsTrue(beforeWrap.All(item => item != "skipped_no_isbn"),
                "Backdated and late-inserted rows must remain outside the committed watermark cycle.");

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            var afterWrap = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
            Assert.IsNull(afterWrap.CycleMaxId);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                await service.ProcessIdentifierAsync(scope, CancellationToken.None);
            }
            var afterNextCycle = await ReadStatusesAsync(contextFactory, extraIds);
            Assert.IsTrue(afterNextCycle.All(item => item == "skipped_no_isbn"));
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing,
                seed with { RequestIds = seed.RequestIds.Concat(extraIds).ToList() });
        }
    }

    [TestMethod]
    public async Task HandledOperationalFailureCheckpointsTheItemAndStopsTheInvocation()
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 2;
        var provider = new QueueFairnessProvider
        {
            IdentifierResult = new IdentifierLookupResult(
                IdentifierLookupOutcome.OperationalFailure,
                ErrorCode: "slice5_provider_down")
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        try
        {
            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var first = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[0]);
                first.Identifier = "slice5-operational-identifier";
                await mutate.SaveChangesAsync();
            }

            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual(1, result.Visited);
            var progress = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope);
            Assert.AreEqual(seed.CursorIds[0], progress.ItemId);
            Assert.AreEqual("operational_failure", await ReadQueueOutcomeAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope));
            await using var verify = await contextFactory.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seed.CursorIds[0]);
            Assert.AreEqual("slice5_provider_down", request.IsbnCheckLastErrorCode);
            Assert.IsTrue(request.LastCheckedUtc.HasValue);
            Assert.AreEqual("pending", request.IsbnCheckStatus);
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing, seed);
        }
    }

    [TestMethod]
    public async Task SqlFailureRollsBackLocalOutcomeAndDoesNotAdvanceQueueProgress()
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
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        try
        {
            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(scope, CancellationToken.None)).Code);
            var beforeFailure = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
            await ExecuteNonQueryAsync(
                "CREATE TRIGGER [asap].[TR_Slice5QueueProgressRollback] ON [asap].[QueueProgress] AFTER UPDATE AS THROW 51055, 'Slice 5 queue rollback probe', 1;");
            try
            {
                var result = await service.ProcessIdentifierAsync(scope, CancellationToken.None);
                Assert.AreEqual("sql_failure", result.Code);
                var afterFailure = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
                Assert.AreEqual(beforeFailure.ItemId, afterFailure.ItemId);
                Assert.AreEqual("pending", await ReadStatusAsync(
                    contextFactory, seed.CursorIds[1]));
            }
            finally
            {
                await ExecuteNonQueryAsync("DROP TRIGGER [asap].[TR_Slice5QueueProgressRollback];");
            }
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing, seed);
        }
    }

    [TestMethod]
    public async Task EligibilityBehindCursorWaitsForNextCycleWhileAheadRowsContinue()
    {
        var configuration = CreateLowCapConfiguration(pageSize: 1, maxPerRun: 2);
        var provider = new QueueFairnessProvider();
        await using var workflowFactory = CreateQueueTestFactory(configuration, provider);
        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var seed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var scope = seed.ScopeOrganizationId;
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope);
        try
        {
            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var behind = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[0]);
                behind.IsbnCheckStatus = "found";
                behind.BibId = "99901";
                await mutate.SaveChangesAsync();
            }

            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            await service.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual(seed.CursorIds[2], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var behind = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[0]);
                behind.IsbnCheckStatus = "pending";
                behind.BibId = null;
                await mutate.SaveChangesAsync();
            }

            await service.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual(seed.CursorIds[4], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, scope)).ItemId);
            Assert.AreEqual("pending", await ReadStatusAsync(contextFactory, seed.CursorIds[0]));

            await service.ProcessIdentifierAsync(scope, CancellationToken.None);
            await service.ProcessIdentifierAsync(scope, CancellationToken.None);
            Assert.AreEqual("skipped_no_isbn", await ReadStatusAsync(contextFactory, seed.CursorIds[0]));
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing, seed);
        }
    }

    [TestMethod]
    public async Task GlobalAndLibraryQueueProgressRemainIndependentDuringRuntimeScans()
    {
        var configuration = CreateLowCapConfiguration(pageSize: 1, maxPerRun: 2);
        var provider = new QueueFairnessProvider();
        await using var workflowFactory = CreateQueueTestFactory(configuration, provider);
        var contextFactory = workflowFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var librarySeed = await SeedFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing);
        var globalIds = new List<long>();
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing, scope: 1);
        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                seed.Organizations.Add(new Organization
                {
                    Id = 102,
                    DisplayName = "Slice 5 Global Test Library",
                    Abbreviation = "S5G",
                    IsActive = true
                });
                await seed.SaveChangesAsync();
                var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var baseUtc = DateTime.UtcNow.AddDays(-80);
                var globalRows = Enumerable.Range(0, 2).Select(index => new TitleRequest
                {
                    LibraryOrganizationId = 102,
                    Barcode = $"slice5-global-{Guid.NewGuid():N}-{index}",
                    Title = $"Slice 5 global {index}",
                    Author = "Slice Five",
                    MaterialFormatId = formatId,
                    Status = "suggestion",
                    IsbnCheckStatus = "pending",
                    CreatedUtc = baseUtc.AddTicks(index),
                    UpdatedUtc = baseUtc.AddTicks(index)
                }).ToList();
                seed.TitleRequests.AddRange(globalRows);
                await seed.SaveChangesAsync();
                globalIds.AddRange(globalRows.Select(item => item.Id));
            }

            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            await service.ProcessIdentifierAsync(librarySeed.ScopeOrganizationId, CancellationToken.None);
            var libraryProgress = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, librarySeed.ScopeOrganizationId);
            Assert.AreEqual(librarySeed.CursorIds[1], libraryProgress.ItemId);

            await service.ProcessIdentifierAsync(null, CancellationToken.None);
            var globalAfter = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 1);
            Assert.AreEqual(globalIds[1], globalAfter.ItemId);
            var libraryAfterGlobal = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, librarySeed.ScopeOrganizationId);
            Assert.AreEqual(libraryProgress.ItemId, libraryAfterGlobal.ItemId);
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing,
                librarySeed with { RequestIds = librarySeed.RequestIds.Concat(globalIds).ToList() });
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            var organization = await cleanup.Organizations.SingleOrDefaultAsync(item => item.Id == 102);
            if (organization is not null)
            {
                cleanup.Organizations.Remove(organization);
                await cleanup.SaveChangesAsync();
            }
        }
    }

    private ExternalConfiguration CreateLowCapConfiguration(int pageSize, int maxPerRun)
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = pageSize;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = maxPerRun;
        return configuration;
    }

    private WebApplicationFactory<Program> CreateQueueTestFactory(
        ExternalConfiguration configuration,
        QueueFairnessProvider provider) =>
        factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

    private static async Task<(DateTime CreatedUtc, long ItemId, long? CycleMaxId)> ReadQueueCursorAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        string queueName,
        int scope)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var progress = await context.QueueProgress.AsNoTracking().SingleAsync(item =>
            item.QueueName == queueName && item.ScopeOrganizationId == scope);
        return (progress.LastCreatedUtc ?? DateTime.MinValue, progress.LastItemId ?? 0, progress.CycleMaxId);
    }

    private static async Task<IReadOnlyList<string?>> ReadStatusesAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        IReadOnlyCollection<long> ids)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.TitleRequests.AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .Select(item => item.IsbnCheckStatus)
            .ToListAsync();
    }

    private static async Task<string?> ReadQueueOutcomeAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        string queueName,
        int scope)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.QueueProgress.AsNoTracking()
            .Where(item => item.QueueName == queueName && item.ScopeOrganizationId == scope)
            .Select(item => item.LastOutcomeCode)
            .SingleAsync();
    }

    private static async Task<string?> ReadStatusAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        long requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.TitleRequests.AsNoTracking()
            .Where(item => item.Id == requestId)
            .Select(item => item.IsbnCheckStatus)
            .SingleAsync();
    }
}
