using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task WeeklySummaryRunNowFalseQueuesManualOrdinaryInvocationWithoutManualRunIdAndPreservesDedupe()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var jobs = factory.Services.GetRequiredService<IBackgroundJobClient>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var jobIds = new List<string>();
        long requestId = 0;
        string? oldStaffUrl = null;

        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == admin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = "weekly-endpoint-normal@example.org";
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime;
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"s5-endpoint-normal-{Guid.NewGuid():N}"[..40],
                Title = "Endpoint ordinary weekly summary",
                MaterialFormatId = format.Id,
                Status = "suggestion",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            seed.TitleRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.EntraObjectId);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            using (var forged = await client.PostAsync(
                       $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId={scope + 1}&force=false",
                       content: null))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, forged.StatusCode);
                using var body = JsonDocument.Parse(await forged.Content.ReadAsStringAsync());
                Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
            }

            for (var index = 0; index < 2; index++)
            {
                using var response = await client.PostAsync(
                    $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId={scope}&force=false",
                    content: null);
                Assert.AreEqual(System.Net.HttpStatusCode.Accepted, response.StatusCode,
                    await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("queued", body.RootElement.GetProperty("code").GetString());
                Assert.AreEqual(scope, body.RootElement.GetProperty("organizationId").GetInt32());
                Assert.IsFalse(body.RootElement.TryGetProperty("manualRunId", out _));
                var jobId = body.RootElement.GetProperty("jobId").GetString();
                Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));
                jobIds.Add(jobId!);

                var job = ReadEnqueuedHangfireJob(storage, jobId!);
                Assert.AreEqual(typeof(BackgroundWorkflowJobs), job.Type);
                Assert.AreEqual(nameof(BackgroundWorkflowJobs.SendManualWeeklyStaffSummaryAsync), job.Method.Name);
                Assert.AreEqual(3, job.Args.Count);
                var queuedEvidence = job.Args[0] as StaffJobEvidence;
                Assert.IsNotNull(queuedEvidence);
                Assert.AreEqual(actor.Id, queuedEvidence!.StaffUserId);
                Assert.AreEqual(actor.EntraTenantId, queuedEvidence.TenantId);
                Assert.AreEqual(actor.EntraObjectId, queuedEvidence.ObjectId);
                Assert.AreEqual(scope, Convert.ToInt32(job.Args[1]));
            }

            var queuedJobs = jobIds.Select(jobId => ReadEnqueuedHangfireJob(storage, jobId)).ToArray();
            var workflowJobs = factory.Services.GetRequiredService<BackgroundWorkflowJobs>();
            foreach (var job in queuedJobs)
            {
                var result = await workflowJobs.SendManualWeeklyStaffSummaryAsync(
                    (StaffJobEvidence)job.Args[0],
                    Convert.ToInt32(job.Args[1]),
                    CancellationToken.None);
                Assert.AreEqual("completed", result.Code);
                Assert.IsNull(result.ManualRunId);
            }

            await using var verify = await contextFactory.CreateDbContextAsync();
            var normalPrefix = $"weekly-summary:{admin.Id}:";
            var ordinaryRows = await verify.EmailOutbox.AsNoTracking()
                .Where(item => item.BusinessKey != null && item.BusinessKey.StartsWith(normalPrefix))
                .ToListAsync();
            Assert.AreEqual(1, ordinaryRows.Count);
            Assert.IsTrue(ordinaryRows.All(item => !item.BusinessKey!.StartsWith("weekly-summary-force:", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteHangfireJobs(jobs, jobIds);
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            var normalPrefix = $"weekly-summary:{admin.Id}:";
            var outbox = await cleanup.EmailOutbox
                .Where(item => item.BusinessKey != null && item.BusinessKey.StartsWith(normalPrefix))
                .ToListAsync();
            cleanup.EmailOutbox.RemoveRange(outbox);
            var request = await cleanup.TitleRequests.SingleOrDefaultAsync(item => item.Id == requestId);
            if (request is not null) cleanup.TitleRequests.Remove(request);
            var system = await cleanup.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = oldStaffUrl;
            await cleanup.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    [TestMethod]
    [DataRow("deactivate")]
    [DataRow("demote")]
    [DataRow("move")]
    [DataRow("rebind")]
    public async Task ManualWeeklySummaryRejectsActorContractionBeforeExecution(string mutation)
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var jobs = factory.Services.GetRequiredService<IBackgroundJobClient>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var jobIds = new List<string>();
        long requestId = 0;
        string? oldStaffUrl = null;
        var businessKeyPrefix = $"weekly-summary:{admin.Id}:";

        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == admin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = $"weekly-auth-{mutation}@example.org";
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime;
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"s5-weekly-auth-{Guid.NewGuid():N}"[..40],
                Title = $"Weekly authorization {mutation}",
                MaterialFormatId = format.Id,
                Status = "suggestion",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            seed.TitleRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.EntraObjectId);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsync(
                $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId={scope}&force=false",
                content: null);
            Assert.AreEqual(System.Net.HttpStatusCode.Accepted, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var jobId = body.RootElement.GetProperty("jobId").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));
            jobIds.Add(jobId!);

            var job = ReadEnqueuedHangfireJob(storage, jobId!);
            Assert.AreEqual(nameof(BackgroundWorkflowJobs.SendManualWeeklyStaffSummaryAsync), job.Method.Name);
            var evidence = job.Args[0] as StaffJobEvidence;
            Assert.IsNotNull(evidence);
            Assert.AreEqual(scope, Convert.ToInt32(job.Args[1]));

            await MutateManualActorAsync(admin.Id, mutation);
            var result = await factory.Services.GetRequiredService<BackgroundWorkflowJobs>()
                .SendManualWeeklyStaffSummaryAsync(evidence!, scope, CancellationToken.None);
            Assert.AreEqual("staff_scope_forbidden", result.Code);
            Assert.IsNull(result.ManualRunId);

            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith(businessKeyPrefix)));
        }
        finally
        {
            DeleteHangfireJobs(jobs, jobIds);
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;",
                ("@prefix", businessKeyPrefix + "%"));
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                ("@id", requestId));
            await using var restore = await contextFactory.CreateDbContextAsync();
            var system = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = oldStaffUrl;
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    [TestMethod]
    public async Task WeeklySummaryRunNowTrueQueuesForcedInvocationWithDistinctStableManualRunIds()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var jobs = factory.Services.GetRequiredService<IBackgroundJobClient>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var jobIds = new List<string>();
        var manualRunIds = new List<string>();

        try
        {
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.EntraObjectId);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            for (var index = 0; index < 2; index++)
            {
                using var response = await client.PostAsync(
                    $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId={scope}&force=true",
                    content: null);
                Assert.AreEqual(System.Net.HttpStatusCode.Accepted, response.StatusCode,
                    await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var manualRunId = body.RootElement.GetProperty("manualRunId").GetString();
                Assert.IsFalse(string.IsNullOrWhiteSpace(manualRunId));
                var jobId = body.RootElement.GetProperty("jobId").GetString();
                Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));
                jobIds.Add(jobId!);
                manualRunIds.Add(manualRunId!);

                var job = ReadEnqueuedHangfireJob(storage, jobId!);
                Assert.AreEqual(typeof(BackgroundWorkflowJobs), job.Type);
                Assert.AreEqual(nameof(BackgroundWorkflowJobs.SendForcedWeeklyStaffSummaryAsync), job.Method.Name);
                Assert.AreEqual(4, job.Args.Count);
                Assert.AreEqual(manualRunId, job.Args[2] as string);
                Assert.AreEqual(manualRunId, ReadEnqueuedHangfireJob(storage, jobId!).Args[2] as string);
            }

            Assert.AreNotEqual(manualRunIds[0], manualRunIds[1]);
        }
        finally
        {
            DeleteHangfireJobs(jobs, jobIds);
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    [TestMethod]
    public async Task NormalWeeklySummaryUsesBusinessPeriodAndQueuesOnlyOnceForCurrentRecipient()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var requestId = 0L;
        string? oldStaffUrl;
        var externalConfiguration = factory.Services.GetRequiredService<ExternalConfiguration>();
        var businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(externalConfiguration.Application.BusinessTimeZone!);
        var localNow = TimeZoneInfo.ConvertTime(timeProvider!.GetUtcNow(), businessTimeZone);
        var periodEndLocal = localNow.Date.AddDays(1);
        var periodStartLocal = periodEndLocal.AddDays(-7);
        var periodStart = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(periodStartLocal, DateTimeKind.Unspecified), businessTimeZone);
        var periodEnd = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(periodEndLocal, DateTimeKind.Unspecified), businessTimeZone);
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == admin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = "weekly-normal@example.org";
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1);
            var submission = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"s5-wn-{Guid.NewGuid():N}",
                Title = "Slice 5 normal weekly submission",
                MaterialFormatId = format.Id,
                Status = "suggestion",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            seed.TitleRequests.Add(submission);
            await seed.SaveChangesAsync();
            requestId = submission.Id;
        }

        var businessKeyPrefix = $"weekly-summary:{admin.Id}:";
        try
        {
            var service = factory.Services.GetRequiredService<WorkflowProcessingService>();
            var first = await service.SendWeeklyStaffSummaryAsync(null, scope, CancellationToken.None);
            var second = await service.SendWeeklyStaffSummaryAsync(null, scope, CancellationToken.None);
            Assert.AreEqual("completed", first.Code);
            Assert.AreEqual("completed", second.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var queued = await verify.EmailOutbox.AsNoTracking()
                .Where(item => item.BusinessKey != null && item.BusinessKey.StartsWith(businessKeyPrefix))
                .ToListAsync();
            var expectedNew = await verify.TitleRequests.CountAsync(item =>
                item.LibraryOrganizationId == scope && item.Status == "suggestion");
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("pending", queued[0].Status);
            Assert.AreEqual("weekly_summary", queued[0].RecipientAddressKind);
            Assert.AreEqual(
                $"weekly-summary:{admin.Id}:{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}",
                queued[0].BusinessKey);
            StringAssert.Contains(queued[0].Subject!, $"{expectedNew} new");
            StringAssert.Contains(queued[0].BodyText!, "stage=submitted");
            StringAssert.Contains(queued[0].BodyText!, "Slice 5 normal weekly submission");
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;",
                ("@prefix", businessKeyPrefix + "%"));
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @current;",
                ("@current", requestId));
            await using var restore = await contextFactory.CreateDbContextAsync();
            var system = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = oldStaffUrl;
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    [TestMethod]
    public async Task WeeklySummaryUsesAddressFromTheLockedCurrentRecipient()
    {
        const int scope = 99005;
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var sender = new MutatingWeeklyRecipientEmailSender(admin.Id, "current-weekly@example.org");
        await using var scoped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
            }));
        var scopedContextFactory = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestId = 0L;
        string? oldStaffUrl;
        var businessPrefix = $"weekly-summary:{admin.Id}:";
        await using (var seed = await scopedContextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == admin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = "snapshot-weekly@example.org";
            var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                .Select(item => item.Id).SingleAsync();
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"s5-weekly-race-{Guid.NewGuid():N}"[..28],
                Title = "Weekly recipient race",
                MaterialFormatId = formatId,
                Status = "suggestion",
                CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            seed.TitleRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        try
        {
            var result = await scoped.Services.GetRequiredService<WorkflowProcessingService>()
                .SendWeeklyStaffSummaryAsync(null, scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(1, sender.MutationCount);

            await using var verify = await scopedContextFactory.CreateDbContextAsync();
            var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith(businessPrefix));
            Assert.AreEqual("current-weekly@example.org", outbox.ToAddress);
            Assert.AreEqual("pending", outbox.Status);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;",
                ("@prefix", businessPrefix + "%"));
            await DeleteRequestIdsAsync(scopedContextFactory, [requestId]);
            await using var restore = await scopedContextFactory.CreateDbContextAsync();
            var system = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = oldStaffUrl;
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    [TestMethod]
    [DataRow("rebind")]
    public async Task ManualWeeklySummaryPassesEvidenceIntoLockedActorAuthorization(string mutation)
    {
        const int scope = 99006;
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var initiatingAdmin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var recipientAdmin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var actor = await ReadCorrectiveStaffAsync(initiatingAdmin);
        var sender = new MutatingWeeklyActorEmailSender(initiatingAdmin.Id, mutation);
        await using var scoped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
        }));
        var scopedContextFactory = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestId = 0L;
        string? oldStaffUrl = null;
        var businessPrefix = $"weekly-summary:{recipientAdmin.Id}:";

        await using (var seed = await scopedContextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == recipientAdmin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = "weekly-locked-actor@example.org";
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime;
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope,
                Barcode = $"s5-weekly-locked-actor-{Guid.NewGuid():N}"[..40],
                Title = "Weekly locked actor authorization",
                MaterialFormatId = format.Id,
                Status = "suggestion",
                CreatedUtc = now,
                UpdatedUtc = now
            };
            seed.TitleRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }

        try
        {
            var result = await scoped.Services.GetRequiredService<BackgroundWorkflowJobs>()
                .SendManualWeeklyStaffSummaryAsync(
                    new StaffJobEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId),
                    scope,
                    CancellationToken.None);
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(1, sender.MutationCount);

            await using var verify = await scopedContextFactory.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith(businessPrefix)));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;",
                ("@prefix", businessPrefix + "%"));
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                ("@id", requestId));
            await using var restore = await scopedContextFactory.CreateDbContextAsync();
            var system = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = oldStaffUrl;
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(initiatingAdmin.Id);
            await DeactivateCorrectiveStaffAsync(recipientAdmin.Id);
        }
    }

    [TestMethod]
    public async Task ForcedWeeklySummaryUsesOpenActionCountsSamplesLinksAndDurableManualId()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var actor = await ReadCorrectiveStaffAsync(admin);
        const string manualRunId = "slice5-weekly-manual-01";
        var businessPrefix = $"weekly-summary-force:{manualRunId}:";
        var requestIds = new List<long>();
        var copyIds = new List<long>();
        string? oldStaffUrl;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == admin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = "weekly-slice5@example.org";
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-1);
            var submission = new TitleRequest
            {
                LibraryOrganizationId = scope, Barcode = "slice5-weekly-submission", Title = "Weekly submission",
                Author = "Summary Author", MaterialFormatId = format.Id, Status = "suggestion",
                AutoHold = false, CreatedUtc = now, UpdatedUtc = now
            };
            var purchase = new TitleRequest
            {
                LibraryOrganizationId = scope, Barcode = "slice5-weekly-purchase", Title = "Weekly purchase",
                Author = "Purchase Author", MaterialFormatId = format.Id, Status = "outstanding_purchase",
                AutoHold = false, CreatedUtc = now, UpdatedUtc = now
            };
            seed.TitleRequests.AddRange(submission, purchase);
            var copy = new AdditionalCopyRequest
            {
                LibraryOrganizationId = scope, BibId = "9001", Title = "Weekly additional copy",
                Author = "Copy Author", Status = "open", CreatedUtc = now, UpdatedUtc = now
            };
            seed.AdditionalCopyRequests.Add(copy);
            await seed.SaveChangesAsync();
            requestIds.AddRange([submission.Id, purchase.Id]);
            copyIds.Add(copy.Id);
        }

        try
        {
            var service = factory.Services.GetRequiredService<WorkflowProcessingService>();
            var result = await service.SendWeeklyStaffSummaryAsync(
                manualRunId,
                scope,
                CancellationToken.None,
                new StaffIdentityEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId));
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(manualRunId, result.ManualRunId);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var outbox = await verify.EmailOutbox.AsNoTracking()
                .SingleAsync(item => item.BusinessKey == $"{businessPrefix}{actor.Id}");
            var expectedNew = await verify.TitleRequests.CountAsync(item =>
                item.LibraryOrganizationId == scope && item.Status == "suggestion");
            var expectedPurchases = await verify.TitleRequests.CountAsync(item =>
                item.LibraryOrganizationId == scope && item.Status == "outstanding_purchase" && string.IsNullOrWhiteSpace(item.BibId));
            var expectedCopies = await verify.AdditionalCopyRequests.CountAsync(item =>
                item.LibraryOrganizationId == scope && item.Status == "open");
            Assert.AreEqual("pending", outbox.Status);
            Assert.AreEqual("weekly_summary", outbox.RecipientAddressKind);
            Assert.AreEqual(actor.EntraTenantId, outbox.RecipientEntraTenantId);
            StringAssert.Contains(outbox.Subject!, $"{expectedNew} new");
            StringAssert.Contains(outbox.Subject!, $"{expectedPurchases} awaiting bibs");
            StringAssert.Contains(outbox.Subject!, $"{expectedCopies} additional copies");
            StringAssert.Contains(outbox.BodyText!, "Weekly submission");
            StringAssert.Contains(outbox.BodyText!, "Weekly purchase");
            StringAssert.Contains(outbox.BodyText!, "Weekly additional copy");
            StringAssert.Contains(outbox.BodyText!, "stage=submitted");
            StringAssert.Contains(outbox.BodyText!, "stage=purchased_waiting_for_bib");
            StringAssert.Contains(outbox.BodyText!, "stage=additional_copies");
            Assert.AreEqual(1, await verify.AdministrativeAudits.CountAsync(item =>
                item.Action == "weekly_summary_force_queued" && item.TargetId == manualRunId));

            var second = await service.SendWeeklyStaffSummaryAsync(
                manualRunId,
                scope,
                CancellationToken.None,
                new StaffIdentityEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId));
            Assert.AreEqual("completed", second.Code);
            Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(item => item.BusinessKey == $"{businessPrefix}{actor.Id}"));
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;", ("@prefix", businessPrefix + "%"));
            await ExecuteNonQueryAsync("DELETE FROM [asap].[AdministrativeAudit] WHERE [TargetId] = @id;", ("@id", manualRunId));
            await ExecuteNonQueryAsync("DELETE FROM [asap].[AdditionalCopyRequest] WHERE [Id] IN (SELECT [Id] FROM [asap].[AdditionalCopyRequest] WHERE [Id] = @id);", ("@id", copyIds[0]));
            await ExecuteNonQueryAsync("DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@first, @second);", ("@first", requestIds[0]), ("@second", requestIds[1]));
            await using var restore = await contextFactory.CreateDbContextAsync();
            var system = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = oldStaffUrl;
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    private sealed class MutatingWeeklyRecipientEmailSender(long staffUserId, string currentAddress) : IEmailSender
    {
        private int mutationComplete;

        public int MutationCount { get; private set; }

        public async Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref mutationComplete, 1) == 0)
            {
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEmail] = @address WHERE [Id] = @id;",
                    ("@address", currentAddress), ("@id", staffUserId));
                MutationCount++;
            }
            return EmailTransportReadiness.Configured;
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Intent creation must not perform delivery.");
    }

    private sealed class MutatingWeeklyActorEmailSender(long staffUserId, string mutation) : IEmailSender
    {
        private int mutationComplete;

        public int MutationCount { get; private set; }

        public async Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref mutationComplete, 1) == 0)
            {
                await MutateManualActorAsync(staffUserId, mutation);
                MutationCount++;
            }
            return EmailTransportReadiness.Configured;
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Intent creation must not perform delivery.");
    }

    private static Job ReadEnqueuedHangfireJob(JobStorage storage, string jobId)
    {
        using var connection = storage.GetConnection();
        var data = connection.GetJobData(jobId);
        Assert.IsNotNull(data);
        data!.EnsureLoaded();
        Assert.IsNotNull(data.Job);
        return data.Job!;
    }

    private static void DeleteHangfireJobs(IBackgroundJobClient jobs, IEnumerable<string> jobIds)
    {
        foreach (var jobId in jobIds)
        {
            jobs.ChangeState(jobId, new DeletedState());
        }
    }
}
