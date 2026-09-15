using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Features.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task LaterFulfillmentDefersRowsTheBoundedPickupTimeoutScanDidNotReach()
    {
        const int scope = 99004;
        var configuration = CreateLowCapConfiguration(pageSize: 1, maxPerRun: 2);
        configuration.Hangfire.ProcessingLimits.Timeouts!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Timeouts.MaxPerRun = 1;
        var provider = new FulfillmentEvidenceProvider();
        await using var workflowFactory = CreateQueueTestFactory(configuration, new QueueFairnessProvider());
        await using var evidenceFactory = workflowFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        var contextFactory = evidenceFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var requestIds = new List<long>();
        WorkflowSettings? originalSettings = null;
        try
        {
            var now = timeProvider!.GetUtcNow().UtcDateTime;
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var settings = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
                if (settings is not null)
                {
                    originalSettings = new WorkflowSettings
                    {
                        OrganizationId = scope,
                        HoldPickupTimeoutEnabled = settings.HoldPickupTimeoutEnabled,
                        HoldPickupTimeoutDays = settings.HoldPickupTimeoutDays
                    };
                }
                else
                {
                    settings = new WorkflowSettings { OrganizationId = scope };
                    seed.WorkflowSettings.Add(settings);
                }
                settings.HoldPickupTimeoutEnabled = true;
                settings.HoldPickupTimeoutDays = 1;
                var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var recent = NewRequest(scope, $"s5-def-recent-{Guid.NewGuid():N}"[..28], "hold_placed",
                    now.AddDays(-3), now.AddHours(-1));
                recent.MaterialFormatId = formatId;
                recent.BibId = "99041";
                var due = NewRequest(scope, $"s5-def-due-{Guid.NewGuid():N}"[..28], "hold_placed",
                    now.AddDays(-2), now.AddDays(-2));
                due.MaterialFormatId = formatId;
                due.BibId = "99042";
                seed.TitleRequests.AddRange(recent, due);
                await seed.SaveChangesAsync();
                requestIds.AddRange([recent.Id, due.Id]);

                await PrepareTimeoutCycleAsync(
                    contextFactory,
                    QueueNames.HoldPickupTimeout,
                    due.Id,
                    scope,
                    recent.CreatedUtc.AddTicks(-1));
                await PrepareTimeoutCycleAsync(
                    contextFactory,
                    QueueNames.FulfillmentTracking,
                    due.Id,
                    scope,
                    recent.CreatedUtc.AddTicks(-1));
            }

            var result = await evidenceFactory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(1, provider.CheckoutReadCount);
            Assert.AreEqual("hold_placed", await ReadRequestStatusAsync(contextFactory, requestIds[0]));
            Assert.AreEqual("hold_placed", await ReadRequestStatusAsync(contextFactory, requestIds[1]));

            await using var verify = await contextFactory.CreateDbContextAsync();
            var fulfillmentProgress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.FulfillmentTracking && item.ScopeOrganizationId == scope);
            Assert.AreEqual(requestIds[1], fulfillmentProgress.LastItemId);
            Assert.AreEqual("deferred_timeout", fulfillmentProgress.LastOutcomeCode);
        }
        finally
        {
            await DeleteRequestIdsAsync(contextFactory, requestIds);
            await using var restore = await contextFactory.CreateDbContextAsync();
            var settings = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            if (originalSettings is null)
            {
                if (settings is not null) restore.WorkflowSettings.Remove(settings);
            }
            else if (settings is not null)
            {
                settings.HoldPickupTimeoutEnabled = originalSettings.HoldPickupTimeoutEnabled;
                settings.HoldPickupTimeoutDays = originalSettings.HoldPickupTimeoutDays;
            }
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task TimeoutUsesSparseSystemSettingsButHonorsLibraryDisableOverride()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var requestIds = new List<long>();
        bool? originalSystemEnabled;
        int? originalSystemDays;
        bool? originalLibraryEnabled;
        int? originalLibraryDays;
        var createdLibrarySettings = false;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var system = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 1)
                ?? new WorkflowSettings { OrganizationId = 1 };
            if (system.RowVersion.Length == 0) seed.WorkflowSettings.Add(system);
            var library = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            createdLibrarySettings = library is null;
            library ??= new WorkflowSettings { OrganizationId = 2 };
            if (createdLibrarySettings) seed.WorkflowSettings.Add(library);
            originalSystemEnabled = system.OutstandingTimeoutEnabled;
            originalSystemDays = system.OutstandingTimeoutDays;
            originalLibraryEnabled = library.OutstandingTimeoutEnabled;
            originalLibraryDays = library.OutstandingTimeoutDays;
            system.OutstandingTimeoutEnabled = true;
            system.OutstandingTimeoutDays = 1;
            library.OutstandingTimeoutEnabled = null;
            library.OutstandingTimeoutDays = null;
            var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                .Select(item => item.Id).SingleAsync();
            var inherited = NewRequest(2, $"s5-inh-{Guid.NewGuid():N}", "suggestion", now.AddDays(-2), now.AddDays(-2));
            inherited.MaterialFormatId = formatId;
            seed.TitleRequests.Add(inherited);
            await seed.SaveChangesAsync();
            requestIds.Add(inherited.Id);
        }

        try
        {
            await PrepareTimeoutCycleAsync(contextFactory, QueueNames.OutstandingTimeout, requestIds[0]);
            var service = factory.Services.GetRequiredService<WorkflowProcessingService>();
            await service.ProcessWorkflowAsync(2, CancellationToken.None);
            Assert.AreEqual("closed", await ReadRequestStatusAsync(contextFactory, requestIds[0]));

            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var library = await seed.WorkflowSettings.SingleAsync(item => item.OrganizationId == 2);
                library.OutstandingTimeoutEnabled = false;
                var disabled = NewRequest(2, $"s5-dis-{Guid.NewGuid():N}", "suggestion", now.AddDays(-2), now.AddDays(-2));
                disabled.MaterialFormatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                seed.TitleRequests.Add(disabled);
                await seed.SaveChangesAsync();
                requestIds.Add(disabled.Id);
            }
            await PrepareTimeoutCycleAsync(contextFactory, QueueNames.OutstandingTimeout, requestIds[1]);
            await service.ProcessWorkflowAsync(2, CancellationToken.None);
            Assert.AreEqual("suggestion", await ReadRequestStatusAsync(contextFactory, requestIds[1]));
        }
        finally
        {
            await DeleteRequestIdsAsync(contextFactory, requestIds);
            await using var restore = await contextFactory.CreateDbContextAsync();
            var system = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (system is not null)
            {
                system.OutstandingTimeoutEnabled = originalSystemEnabled;
                system.OutstandingTimeoutDays = originalSystemDays;
            }
            var library = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (createdLibrarySettings)
            {
                if (library is not null) restore.WorkflowSettings.Remove(library);
            }
            else if (library is not null)
            {
                library.OutstandingTimeoutEnabled = originalLibraryEnabled;
                library.OutstandingTimeoutDays = originalLibraryDays;
            }
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task TimeoutUsesStrictSqlCutoffEqualityWithInjectedClock()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var configuration = TestConfigurationFactory.Create();
        var now = timeProvider!.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration.Application.BusinessTimeZone!);
        var cutoff = TimeoutSemantics.CutoffUtc(now, zone, 1);
        var requestIds = new List<long>();
        bool? originalEnabled;
        int? originalDays;
        bool? originalSendEmail;
        var createdLibrarySettings = false;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var settings = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            createdLibrarySettings = settings is null;
            settings ??= new WorkflowSettings { OrganizationId = 2 };
            if (createdLibrarySettings) seed.WorkflowSettings.Add(settings);
            originalEnabled = settings.OutstandingTimeoutEnabled;
            originalDays = settings.OutstandingTimeoutDays;
            originalSendEmail = settings.OutstandingTimeoutSendEmail;
            settings.OutstandingTimeoutEnabled = true;
            settings.OutstandingTimeoutDays = 1;
            settings.OutstandingTimeoutSendEmail = false;
            var exact = NewRequest(2, $"s5-exact-{Guid.NewGuid():N}", "suggestion", cutoff, cutoff);
            exact.MaterialFormatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                .Select(item => item.Id).SingleAsync();
            seed.TitleRequests.Add(exact);
            await seed.SaveChangesAsync();
            requestIds.Add(exact.Id);
            await PrepareTimeoutCycleAsync(contextFactory, QueueNames.OutstandingTimeout, exact.Id);
            await factory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);
            Assert.AreEqual("suggestion", await ReadRequestStatusAsync(contextFactory, exact.Id));
        }

        try
        {
            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var request = await mutate.TitleRequests.SingleAsync(item => item.Id == requestIds[0]);
                request.CreatedUtc = cutoff.AddTicks(-1);
                request.UpdatedUtc = cutoff.AddTicks(-1);
                var settings = await mutate.WorkflowSettings.SingleAsync(item => item.OrganizationId == 2);
                settings.OutstandingTimeoutEnabled = true;
                settings.OutstandingTimeoutDays = 1;
                settings.OutstandingTimeoutSendEmail = false;
                await mutate.SaveChangesAsync();
            }
            await PrepareTimeoutCycleAsync(contextFactory, QueueNames.OutstandingTimeout, requestIds[0]);
            await factory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);
            Assert.AreEqual("closed", await ReadRequestStatusAsync(contextFactory, requestIds[0]));
        }
        finally
        {
            await DeleteRequestIdsAsync(contextFactory, requestIds);
            await using var restore = await contextFactory.CreateDbContextAsync();
            var settings = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (createdLibrarySettings)
            {
                if (settings is not null) restore.WorkflowSettings.Remove(settings);
            }
            else if (settings is not null)
            {
                settings.OutstandingTimeoutEnabled = originalEnabled;
                settings.OutstandingTimeoutDays = originalDays;
                settings.OutstandingTimeoutSendEmail = originalSendEmail;
            }
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task TimeoutUsesBusinessCalendarAcrossSpringForwardThroughTheSqlQueue()
    {
        const int scope = 99006;
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var originalNow = timeProvider!.GetUtcNow();
        var dstNow = new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero);
        timeProvider.SetUtcNow(dstNow);
        var configuration = TestConfigurationFactory.Create();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration.Application.BusinessTimeZone!);
        var cutoff = TimeoutSemantics.CutoffUtc(dstNow, zone, 1);
        var requestIds = new List<long>();
        WorkflowSettings? originalSettings = null;
        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var settings = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
                if (settings is not null)
                {
                    originalSettings = new WorkflowSettings
                    {
                        OrganizationId = scope,
                        OutstandingTimeoutEnabled = settings.OutstandingTimeoutEnabled,
                        OutstandingTimeoutDays = settings.OutstandingTimeoutDays,
                        OutstandingTimeoutSendEmail = settings.OutstandingTimeoutSendEmail
                    };
                }
                else
                {
                    settings = new WorkflowSettings { OrganizationId = scope };
                    seed.WorkflowSettings.Add(settings);
                }
                settings.OutstandingTimeoutEnabled = true;
                settings.OutstandingTimeoutDays = 1;
                settings.OutstandingTimeoutSendEmail = false;
                var request = NewRequest(scope, $"s5-dst-{Guid.NewGuid():N}", "suggestion", cutoff, cutoff);
                request.MaterialFormatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                seed.TitleRequests.Add(request);
                await seed.SaveChangesAsync();
                requestIds.Add(request.Id);
            }

            await PrepareTimeoutCycleAsync(
                contextFactory, QueueNames.OutstandingTimeout, requestIds[0], scope);
            var service = factory.Services.GetRequiredService<WorkflowProcessingService>();
            await service.ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("suggestion", await ReadRequestStatusAsync(contextFactory, requestIds[0]));

            await using (var mutate = await contextFactory.CreateDbContextAsync())
            {
                var request = await mutate.TitleRequests.SingleAsync(item => item.Id == requestIds[0]);
                request.CreatedUtc = cutoff.AddTicks(-1);
                request.UpdatedUtc = cutoff.AddTicks(-1);
                await mutate.SaveChangesAsync();
            }
            await PrepareTimeoutCycleAsync(
                contextFactory, QueueNames.OutstandingTimeout, requestIds[0], scope);
            await service.ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("closed", await ReadRequestStatusAsync(contextFactory, requestIds[0]));
        }
        finally
        {
            await DeleteRequestIdsAsync(contextFactory, requestIds);
            await using var restore = await contextFactory.CreateDbContextAsync();
            var settings = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            if (originalSettings is null)
            {
                if (settings is not null) restore.WorkflowSettings.Remove(settings);
            }
            else if (settings is not null)
            {
                settings.OutstandingTimeoutEnabled = originalSettings.OutstandingTimeoutEnabled;
                settings.OutstandingTimeoutDays = originalSettings.OutstandingTimeoutDays;
                settings.OutstandingTimeoutSendEmail = originalSettings.OutstandingTimeoutSendEmail;
            }
            await restore.SaveChangesAsync();
            timeProvider.SetUtcNow(originalNow);
        }
    }

    private static async Task PrepareTimeoutCycleAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        string queueName,
        long requestId,
        int scope = 2,
        DateTime? cursorCreatedUtc = null,
        long cursorItemId = 0)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var progress = await context.QueueProgress.SingleOrDefaultAsync(item =>
            item.QueueName == queueName && item.ScopeOrganizationId == scope);
        progress ??= new QueueProgress { QueueName = queueName, ScopeOrganizationId = scope };
        if (progress.RowVersion.Length == 0) context.QueueProgress.Add(progress);
        progress.CycleMaxId = requestId;
        progress.LastCreatedUtc = cursorCreatedUtc ?? request.CreatedUtc.AddTicks(-1);
        progress.LastItemId = cursorItemId;
        progress.LastOutcomeItemId = null;
        progress.LastOutcomeCode = "test_cycle_started";
        progress.LastOutcomeUtc = request.CreatedUtc;
        progress.UpdatedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    private static async Task<string?> ReadRequestStatusAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        long requestId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.TitleRequests.AsNoTracking()
            .Where(item => item.Id == requestId)
            .Select(item => item.Status)
            .SingleAsync();
    }

    private static async Task DeleteRequestIdsAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        IReadOnlyCollection<long> requestIds)
    {
        if (requestIds.Count == 0) return;
        await using var context = await contextFactory.CreateDbContextAsync();
        context.TitleRequestEvents.RemoveRange(context.TitleRequestEvents.Where(item => requestIds.Contains(item.TitleRequestId)));
        context.TitleRequestWorkflowTags.RemoveRange(context.TitleRequestWorkflowTags.Where(item => requestIds.Contains(item.TitleRequestId)));
        context.HoldPlacementOperations.RemoveRange(context.HoldPlacementOperations.Where(item => requestIds.Contains(item.TitleRequestId)));
        await context.SaveChangesAsync();

        context.TitleRequests.RemoveRange(context.TitleRequests.Where(item => requestIds.Contains(item.Id)));
        await context.SaveChangesAsync();
    }
}
