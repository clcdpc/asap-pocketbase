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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
        try
        {
            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            Assert.AreEqual(seed.CursorIds[1], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var cursorRow = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[1]);
                mutate.TitleRequests.Remove(cursorRow);
                await mutate.SaveChangesAsync();
            }

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            Assert.AreEqual(seed.CursorIds[3], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);
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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
        long lateId = 0;
        long highId = 0;
        await using var lateContext = await contextFactory.CreateDbContextAsync();
        await using var lateTransaction = await lateContext.Database.BeginTransactionAsync();
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

            var formatId = await lateContext.MaterialFormats.Where(item => item.Code == "book")
                .Select(item => item.Id).SingleAsync();
            var late = new TitleRequest
            {
                LibraryOrganizationId = 2,
                Barcode = $"s5-late-{Guid.NewGuid():N}",
                Title = "Slice 5 late commit",
                MaterialFormatId = formatId,
                Status = "found" is not null ? "suggestion" : "suggestion",
                BibId = "s5-late-bib",
                IsbnCheckStatus = "found",
                CreatedUtc = DateTime.UtcNow.AddDays(-10),
                UpdatedUtc = DateTime.UtcNow.AddDays(-10)
            };
            lateContext.TitleRequests.Add(late);
            await lateContext.SaveChangesAsync();
            lateId = late.Id;

            await using (var highContext = await contextFactory.CreateDbContextAsync())
            {
                var high = new TitleRequest
                {
                    LibraryOrganizationId = 2,
                    Barcode = $"s5-high-{Guid.NewGuid():N}",
                    Title = "Slice 5 committed watermark row",
                    MaterialFormatId = formatId,
                    Status = "closed",
                    CloseReason = "manual",
                    CreatedUtc = DateTime.UtcNow.AddDays(-9),
                    UpdatedUtc = DateTime.UtcNow.AddDays(-9)
                };
                highContext.TitleRequests.Add(high);
                await highContext.SaveChangesAsync();
                highId = high.Id;
            }

            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            Assert.AreEqual(highId, (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).CycleMaxId);

            await lateTransaction.CommitAsync();
            await using (var makeLateActionable = await contextFactory.CreateDbContextAsync())
            {
                var request = await makeLateActionable.TitleRequests.SingleAsync(item => item.Id == lateId);
                request.BibId = null;
                request.IsbnCheckStatus = "pending";
                await makeLateActionable.SaveChangesAsync();
            }

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            Assert.AreEqual("skipped_no_isbn", await ReadStatusAsync(contextFactory, lateId));
            Assert.AreEqual(lateId, (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);
        }
        finally
        {
            await DeleteFairnessRowsAsync(contextFactory, QueueNames.IdentifierProcessing,
                seed with { RequestIds = seed.RequestIds.Concat([lateId, highId]).ToList() });
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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
        try
        {
            var first = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(lowCapFactory.Services);
            var firstResult = await first.ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual("completed", firstResult.Code);
            Assert.AreEqual(seed.CursorIds[1], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);

            configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 1;
            var second = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(lowCapFactory.Services);
            var secondResult = await second.ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual("completed", secondResult.Code);
            Assert.AreEqual(seed.CursorIds[2], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);

            var third = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(lowCapFactory.Services);
            await third.ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual(seed.CursorIds[3], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);
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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
        var extraIds = new List<long>();
        try
        {
            var service = lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            var cursorAfterFirst = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, 2);

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var deleted = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[2]);
                mutate.TitleRequests.Remove(deleted);
                var backdated = new TitleRequest
                {
                    LibraryOrganizationId = 2,
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
                    LibraryOrganizationId = 2,
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

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            Assert.AreEqual(seed.CursorIds[4], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);
            var beforeWrap = await ReadStatusesAsync(contextFactory, extraIds);
            Assert.IsTrue(beforeWrap.All(item => item != "skipped_no_isbn"),
                "Backdated and late-inserted rows must remain outside the committed watermark cycle.");

            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            var afterWrap = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, 2);
            Assert.IsNull(afterWrap.CycleMaxId);

            for (var attempt = 0; attempt < 4; attempt++)
            {
                await service.ProcessIdentifierAsync(2, CancellationToken.None);
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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
        try
        {
            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var first = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[0]);
                first.Identifier = "slice5-operational-identifier";
                await mutate.SaveChangesAsync();
            }

            var result = await workflowFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual(1, result.Visited);
            var progress = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2);
            Assert.AreEqual(seed.CursorIds[0], progress.ItemId);
            Assert.AreEqual("operational_failure", await ReadQueueOutcomeAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2));
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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
        try
        {
            var service = workflowFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await service.ProcessIdentifierAsync(2, CancellationToken.None)).Code);
            var beforeFailure = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, 2);
            await ExecuteNonQueryAsync(
                "CREATE TRIGGER [asap].[TR_Slice5QueueProgressRollback] ON [asap].[QueueProgress] AFTER UPDATE AS THROW 51055, 'Slice 5 queue rollback probe', 1;");
            try
            {
                var result = await service.ProcessIdentifierAsync(2, CancellationToken.None);
                Assert.AreEqual("sql_failure", result.Code);
                var afterFailure = await ReadQueueCursorAsync(contextFactory, QueueNames.IdentifierProcessing, 2);
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
        await IsolateOtherWorkflowQueuesAsync(contextFactory, QueueNames.IdentifierProcessing);
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
            await service.ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual(seed.CursorIds[2], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var behind = await mutate.TitleRequests.SingleAsync(item => item.Id == seed.CursorIds[0]);
                behind.IsbnCheckStatus = "pending";
                behind.BibId = null;
                await mutate.SaveChangesAsync();
            }

            await service.ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual(seed.CursorIds[4], (await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2)).ItemId);
            Assert.AreEqual("pending", await ReadStatusAsync(contextFactory, seed.CursorIds[0]));

            await service.ProcessIdentifierAsync(2, CancellationToken.None);
            await service.ProcessIdentifierAsync(2, CancellationToken.None);
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
            await service.ProcessIdentifierAsync(2, CancellationToken.None);
            var libraryProgress = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2);
            Assert.AreEqual(librarySeed.CursorIds[1], libraryProgress.ItemId);

            await service.ProcessIdentifierAsync(null, CancellationToken.None);
            var globalAfter = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 1);
            Assert.AreEqual(globalIds[1], globalAfter.ItemId);
            var libraryAfterGlobal = await ReadQueueCursorAsync(
                contextFactory, QueueNames.IdentifierProcessing, 2);
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
