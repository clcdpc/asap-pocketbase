using Asap.Web.Infrastructure.Configuration;
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
    public async Task IdentifierScanHonorsLowInvocationCapAndReachesLaterRows()
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Hangfire.ProcessingLimits.Default!.PageSize = 1;
        configuration.Hangfire.ProcessingLimits.Default.MaxPerRun = 2;

        await using var lowCapFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ExternalConfiguration>();
                services.AddSingleton(configuration);
            }));

        var contextFactory = lowCapFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestIds = new List<long>();
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var previous = await seed.TitleRequests.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == 2 && item.Status == "suggestion" && item.IsbnCheckStatus == "pending")
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync();
            var baseUtc = previous?.CreatedUtc.AddTicks(1) ?? timeProvider!.GetUtcNow().UtcDateTime;
            var rows = Enumerable.Range(0, 5).Select(index => new TitleRequest
            {
                LibraryOrganizationId = 2,
                Barcode = $"slice5-cap-{Guid.NewGuid():N}-{index}",
                Title = $"Slice 5 cap {index}",
                Author = "Slice Five",
                MaterialFormatId = format.Id,
                Status = "suggestion",
                IsbnCheckStatus = "pending",
                Identifier = null,
                AutoHold = false,
                CreatedUtc = baseUtc.AddTicks(index + 1),
                UpdatedUtc = baseUtc.AddTicks(index + 1)
            }).ToList();
            seed.TitleRequests.AddRange(rows);
            await seed.SaveChangesAsync();
            requestIds.AddRange(rows.Select(item => item.Id));

            var progress = await seed.QueueProgress.SingleOrDefaultAsync(item =>
                item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == 2);
            progress ??= new QueueProgress { QueueName = QueueNames.IdentifierProcessing, ScopeOrganizationId = 2 };
            if (progress.RowVersion.Length == 0) seed.QueueProgress.Add(progress);
            progress.CycleMaxId = rows[^1].Id;
            progress.LastCreatedUtc = previous?.CreatedUtc ?? baseUtc;
            progress.LastItemId = previous?.Id ?? 0;
            progress.LastOutcomeCode = "test_low_cap_started";
            progress.UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime;
            await seed.SaveChangesAsync();
            timeProvider!.SetUtcNow(baseUtc.AddHours(1));
        }

        try
        {
            var service = lowCapFactory.Services.GetRequiredService<WorkflowProcessingService>();
            var first = await service.ProcessIdentifierAsync(2, CancellationToken.None);
            var second = await service.ProcessIdentifierAsync(2, CancellationToken.None);
            var third = await service.ProcessIdentifierAsync(2, CancellationToken.None);
            Assert.AreEqual(2, first.Visited);
            Assert.AreEqual(2, second.Visited);
            Assert.AreEqual(1, third.Visited);
            Assert.AreEqual(5, first.Visited + second.Visited + third.Visited);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var rows = await verify.TitleRequests.AsNoTracking()
                .Where(item => requestIds.Contains(item.Id))
                .ToListAsync();
            Assert.IsTrue(rows.All(item => item.IsbnCheckStatus == "skipped_no_isbn"));
            var progress = await verify.QueueProgress.AsNoTracking().SingleAsync(item =>
                item.QueueName == QueueNames.IdentifierProcessing && item.ScopeOrganizationId == 2);
            Assert.IsNull(progress.CycleMaxId);
            Assert.AreEqual("cycle_complete", progress.LastOutcomeCode);
            Assert.AreEqual(requestIds[^1], progress.LastOutcomeItemId);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] IN (SELECT [Id] FROM [asap].[TitleRequest] WHERE [Id] IN (@a,@b,@c,@d,@e)); DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN (@a,@b,@c,@d,@e); DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@a,@b,@c,@d,@e);",
                ("@a", requestIds[0]), ("@b", requestIds[1]), ("@c", requestIds[2]), ("@d", requestIds[3]), ("@e", requestIds[4]));
        }
    }

    [TestMethod]
    public async Task QueueProgressUsesIndependentCompositeRowsForAllQueuesAndScopes()
    {
        var progress = factory!.Services.GetRequiredService<QueueProgressService>();
        var queueNames = QueueNames.Configured.Append(QueueNames.HoldRecovery).ToArray();
        foreach (var queue in queueNames)
        {
            await progress.GetOrCreateAsync(queue, 1, CancellationToken.None);
            await progress.GetOrCreateAsync(queue, 2, CancellationToken.None);
        }

        await using var context = await factory.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var rows = await context.QueueProgress.AsNoTracking()
            .Where(item => queueNames.Contains(item.QueueName) && (item.ScopeOrganizationId == 1 || item.ScopeOrganizationId == 2))
            .ToListAsync();
        Assert.AreEqual(queueNames.Length * 2, rows.Count);
        Assert.AreEqual(queueNames.Length, rows.Select(item => item.QueueName).Distinct().Count());
        Assert.IsTrue(rows.All(item => item.ScopeOrganizationId is 1 or 2));
    }

    [TestMethod]
    public async Task ManualWorkflowRejectsActorContractionAfterProviderEvidence()
    {
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.BlockCreate();
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Asap.Web.Features.Patron.IPatronProvider>();
                services.RemoveAll<Asap.Web.Features.Staff.IStaffPolarisProvider>();
                services.AddSingleton<Asap.Web.Features.Patron.IPatronProvider>(provider);
                services.AddSingleton<Asap.Web.Features.Staff.IStaffPolarisProvider>(provider);
            }));

        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var seeded = await SeedPendingHoldRequestAsync("manual-actor-final-boundary");
        await PrepareSingleItemCycleAsync(QueueNames.HoldPlacement, 2, seeded.RequestId);
        try
        {
            var job = workflowFactory.Services.GetRequiredService<BackgroundWorkflowJobs>();
            var execution = job.ProcessManualWorkflowAsync(
                new StaffJobEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId),
                2,
                CancellationToken.None);
            await provider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;", ("@id", admin.Id));
            provider.CompleteBlockedCreate(new HoldProviderResult(
                HoldProviderOutcome.FinalSuccess,
                null,
                "8123",
                null,
                null,
                2,
                0,
                "documented_final_success"));
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("pending_hold", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;", "@id", seeded.RequestId));
            Assert.AreEqual(0, await CountForRequestAsync(
                "[asap].[TitleRequestEvent]", "[TitleRequestId]", seeded.RequestId));
            Assert.AreEqual(1, provider.CreateCount);
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }
}
