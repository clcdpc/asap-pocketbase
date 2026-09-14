using System.Security.Cryptography;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task RunNowResolvesOmittedAdminScopeAndRejectsForgedScope()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var adminSeed = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var staff = await ReadCorrectiveStaffAsync(adminSeed);
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, staff.Id, staff.EntraTenantId, staff.EntraObjectId);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var omitted = await client.PostAsync("/api/asap/staff/workflow/run-now", content: null);
        Assert.AreEqual(System.Net.HttpStatusCode.Accepted, omitted.StatusCode);
        using var omittedBody = System.Text.Json.JsonDocument.Parse(await omitted.Content.ReadAsStringAsync());
        Assert.AreEqual(2, omittedBody.RootElement.GetProperty("organizationId").GetInt32());

        using var forged = await client.PostAsync(
            "/api/asap/staff/workflow/run-now?organizationId=3", content: null);
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, forged.StatusCode);
        using var forgedBody = System.Text.Json.JsonDocument.Parse(await forged.Content.ReadAsStringAsync());
        Assert.AreEqual("staff_scope_forbidden", forgedBody.RootElement.GetProperty("code").GetString());

        var superClient = factory.CreateClient();
        AddTestingStaffHeaders(superClient, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.EntraObjectId);
        superClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(superClient));
        using var global = await superClient.PostAsync("/api/asap/staff/workflow/run-now", content: null);
        Assert.AreEqual(System.Net.HttpStatusCode.Accepted, global.StatusCode);
        using var globalBody = System.Text.Json.JsonDocument.Parse(await global.Content.ReadAsStringAsync());
        Assert.AreEqual(1, globalBody.RootElement.GetProperty("organizationId").GetInt32());
        await DeactivateCorrectiveStaffAsync(adminSeed.Id);
    }

    [TestMethod]
    public async Task RunNowDeniesOrdinaryStaffBeforeEnqueue()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var staff = await CreateCorrectiveStaffAsync(superAdmin, "staff", 2);
        var actor = await ReadCorrectiveStaffAsync(staff);
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.EntraObjectId);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var response = await client.PostAsync(
            "/api/asap/staff/workflow/run-now?organizationId=2",
            content: null);
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
        await DeactivateCorrectiveStaffAsync(staff.Id);
    }

    [TestMethod]
    public async Task EmailOperationsRequireAdminAndMergeSparseSenderFields()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var staff = await CreateCorrectiveStaffAsync(superAdmin, "staff", 2);
        var ordinaryStaff = await ReadCorrectiveStaffAsync(staff);
        var service = factory!.Services.GetRequiredService<EmailOperationsService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var createdOutboxIds = new List<long>();

        await using var context = await contextFactory.CreateDbContextAsync();
        var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
        var library = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var createdLibrary = library is null;
        library ??= new EmailSettings { OrganizationId = 2 };
        if (createdLibrary) context.EmailSettings.Add(library);
        var oldSystemAddress = system.FromAddress;
        var oldSystemName = system.FromName;
        var oldLibraryAddress = library.FromAddress;
        var oldLibraryName = library.FromName;
        system.FromAddress = "system.sender@example.org";
        system.FromName = "System Notices";
        library.FromAddress = null;
        library.FromName = "Library Notices";
        await context.SaveChangesAsync();

        try
        {
            var queued = await service.QueueTestAsync(actor, null, CancellationToken.None);
            Assert.AreEqual("queued", queued.Code);
            var queuedId = Convert.ToInt64(queued.Data!.GetType().GetProperty("id")!.GetValue(queued.Data));
            createdOutboxIds.Add(queuedId);
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == queuedId);
            Assert.AreEqual("system.sender@example.org", outbox.FromAddress);
            Assert.AreEqual("Library Notices", outbox.FromName);

            Assert.AreEqual("staff_scope_forbidden",
                (await service.QueueTestAsync(ordinaryStaff, 2, CancellationToken.None)).Code);
            Assert.AreEqual(0, (await service.ListAsync(ordinaryStaff, 2, null, CancellationToken.None)).Count);
            Assert.AreEqual("staff_scope_forbidden",
                (await service.RetryAsync(ordinaryStaff, queuedId, StaffVersion.Encode(outbox.RowVersion), CancellationToken.None)).Code);
        }
        finally
        {
            if (createdOutboxIds.Count != 0)
            {
                var rows = await context.EmailOutbox.Where(item => createdOutboxIds.Contains(item.Id)).ToListAsync();
                context.EmailOutbox.RemoveRange(rows);
            }
            system.FromAddress = oldSystemAddress;
            system.FromName = oldSystemName;
            if (createdLibrary)
            {
                context.EmailSettings.Remove(library);
            }
            else
            {
                library.FromAddress = oldLibraryAddress;
                library.FromName = oldLibraryName;
            }
            await context.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(admin.Id);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    public async Task EmailRetryRevalidatesActorAndReturnsConflictForSameRowRace()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var service = factory!.Services.GetRequiredService<EmailOperationsService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long outboxId;
        byte[] version;
        var businessKey = $"slice5-retry-test:{Guid.NewGuid():N}";
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var row = new EmailOutbox
            {
                OrganizationId = 2,
                DeliveryClass = "operational_test",
                BusinessKey = businessKey,
                Status = "failed",
                AttemptCount = 3,
                LastErrorCode = "provider_failed",
                ToAddress = "retry@example.org",
                FromAddress = "system@example.org",
                Subject = "Retry test",
                BodyText = "Retry test body",
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            seed.EmailOutbox.Add(row);
            await seed.SaveChangesAsync();
            outboxId = row.Id;
            version = row.RowVersion;
        }

        try
        {
            var first = await service.RetryAsync(actor, outboxId, StaffVersion.Encode(version), CancellationToken.None);
            Assert.AreEqual("queued", first.Code);
            var second = await service.RetryAsync(actor, outboxId, StaffVersion.Encode(version), CancellationToken.None);
            StringAssert.Contains(second.Code, "stale_version");

            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
                ("@id", admin.Id));
            var revoked = await service.RetryAsync(actor, outboxId, StaffVersion.Encode(version), CancellationToken.None);
            StringAssert.Contains(revoked.Code, "staff_session_invalid");
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[EmailOutbox] WHERE [Id] = @id;", ("@id", outboxId));
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
                ("@id", admin.Id));
        }
    }

    [TestMethod]
    public async Task EmailListCanReachRetainedFailureBehindNewerTerminalRows()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var service = factory!.Services.GetRequiredService<EmailOperationsService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var ids = new List<long>();
        var businessKeyPrefix = $"slice5-retained:{Guid.NewGuid():N}";
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var oldFailure = new EmailOutbox
            {
                OrganizationId = 2, DeliveryClass = "operational_test", BusinessKey = $"{businessKeyPrefix}:failed", Status = "failed",
                LastErrorCode = "retained_failure", ToAddress = "retained@example.org", FromAddress = "system@example.org",
                Subject = "Retained failure", BodyText = "Retained failure body", CreatedUtc = DateTime.UtcNow.AddDays(-30)
            };
            seed.EmailOutbox.Add(oldFailure);
            seed.EmailOutbox.AddRange(Enumerable.Range(0, 500).Select(index => new EmailOutbox
            {
                OrganizationId = 2, DeliveryClass = "operational_test", BusinessKey = $"{businessKeyPrefix}:terminal:{index}", Status = index % 2 == 0 ? "sent" : "suppressed",
                ToAddress = "terminal@example.org", FromAddress = "system@example.org", Subject = "Terminal", BodyText = "Terminal body",
                SentUtc = index % 2 == 0 ? DateTime.UtcNow.AddMinutes(index) : null,
                SuppressedUtc = index % 2 == 0 ? null : DateTime.UtcNow.AddMinutes(index),
                SuppressionReason = index % 2 == 0 ? null : "test_terminal",
                CreatedUtc = DateTime.UtcNow.AddMinutes(index)
            }));
            await seed.SaveChangesAsync();
            ids.Add(oldFailure.Id);
        }

        try
        {
            var result = await service.ListAsync(actor, 2, null, CancellationToken.None);
            Assert.IsTrue(result.Any(item => item.Id == ids[0] && item.Status == "failed"));
            var failedOnly = await service.ListAsync(actor, 2, "failed", CancellationToken.None);
            Assert.IsTrue(failedOnly.Any(item => item.Id == ids[0]));
            Assert.IsTrue(failedOnly.All(item => item.Status == "failed"));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix;",
                ("@prefix", $"{businessKeyPrefix}:%"));
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    [TestMethod]
    public async Task HangfireStoresExactlySevenSchedulesWithConfiguredBusinessTimezone()
    {
        var worker = ActivatorUtilities.CreateInstance<HangfireWorkerHostedService>(factory!.Services);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var storage = factory.Services.GetRequiredService<JobStorage>();
            using var connection = storage.GetConnection();
            var ids = connection.GetAllItemsFromSet("recurring-jobs");
            Assert.HasCount(7, ids);
            var expected = TestConfigurationFactory.Create().Hangfire.Schedules!;
            var expectedTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
                TestConfigurationFactory.Create().Application.BusinessTimeZone!).Id;
            var expectedIds = new Dictionary<string, string>
            {
                ["asap-workflow-processing"] = "WorkflowProcessing",
                ["asap-identifier-processing"] = "IdentifierProcessing",
                ["asap-organization-refresh"] = "OrganizationRefresh",
                ["asap-weekly-staff-summary"] = "WeeklyStaffSummary",
                ["asap-email-outbox-sweep"] = "EmailOutboxSweep",
                ["asap-patron-session-cleanup"] = "PatronSessionCleanup",
                ["asap-email-payload-cleanup"] = "EmailPayloadCleanup"
            };
            foreach (var id in ids)
            {
                var fields = connection.GetAllEntriesFromHash($"recurring-job:{id}");
                Assert.IsTrue(expectedIds.TryGetValue(id, out var key));
                Assert.AreEqual(expected[key], fields["Cron"]);
                Assert.AreEqual(expectedTimeZone, fields["TimeZoneId"]);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [TestMethod]
    public async Task SessionCleanupRemovesExpiredAndOldRevokedSessionsButRetainsActiveAndRecentlyRevoked()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var now = DateTime.UtcNow;
        var marker = Guid.NewGuid().ToString("N");
        var sessionIds = new List<long>();
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var sessions = new[]
            {
                new PatronSession
                {
                    TokenHash = RandomNumberGenerator.GetBytes(32), Barcode = $"2000000000{marker[..10]}",
                    EffectiveOrganizationId = 2, CreatedUtc = now.AddHours(-2), ExpiresUtc = now.AddHours(-1)
                },
                new PatronSession
                {
                    TokenHash = RandomNumberGenerator.GetBytes(32), Barcode = $"2000000001{marker[..10]}",
                    EffectiveOrganizationId = 2, CreatedUtc = now.AddDays(-3), ExpiresUtc = now.AddDays(1),
                    RevokedUtc = now.AddDays(-2)
                },
                new PatronSession
                {
                    TokenHash = RandomNumberGenerator.GetBytes(32), Barcode = $"2000000002{marker[..10]}",
                    EffectiveOrganizationId = 2, CreatedUtc = now.AddDays(-1), ExpiresUtc = now.AddDays(1),
                    RevokedUtc = now.AddHours(-12)
                },
                new PatronSession
                {
                    TokenHash = RandomNumberGenerator.GetBytes(32), Barcode = $"2000000003{marker[..10]}",
                    EffectiveOrganizationId = 2, CreatedUtc = now.AddHours(-1), ExpiresUtc = now.AddHours(1)
                }
            };
            seed.PatronSessions.AddRange(sessions);
            await seed.SaveChangesAsync();
            sessionIds.AddRange(sessions.Select(item => item.Id));
        }

        var worker = factory.Services.GetRequiredService<WorkflowProcessingService>();
        Assert.AreEqual(2, await worker.CleanupSessionsAsync());

        await using var verify = await contextFactory.CreateDbContextAsync();
        var remaining = await verify.PatronSessions
            .Where(item => sessionIds.Contains(item.Id))
            .ToListAsync();
        Assert.HasCount(2, remaining);
        Assert.IsTrue(remaining.Any(item => item.ExpiresUtc > now && item.RevokedUtc is null));
        Assert.IsTrue(remaining.Any(item => item.RevokedUtc.HasValue && item.RevokedUtc > now.AddDays(-1)));
        verify.PatronSessions.RemoveRange(remaining);
        await verify.SaveChangesAsync();
    }

    [TestMethod]
    public async Task EmailPayloadCleanupPurgesOnlyOldTerminalPayloadsAcrossInactiveLibraries()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var now = DateTime.UtcNow;
        var prefix = $"slice5-payload-cleanup:{Guid.NewGuid():N}";
        var old = now.AddDays(-100);
        var recent = now.AddDays(-30);
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var organization = await seed.Organizations.SingleAsync(item => item.Id == 2);
            var wasActive = organization.IsActive;
            organization.IsActive = false;
            seed.EmailOutbox.AddRange(
                NewCleanupOutbox(prefix, "pending", old),
                NewCleanupOutbox(prefix, "sending", old, sending: true),
                NewCleanupOutbox(prefix, "failed", old, errorCode: "provider_failed"),
                NewCleanupOutbox(prefix, "sent-old", old, status: "sent", sentUtc: old),
                NewCleanupOutbox(prefix, "suppressed-old", old, status: "suppressed", suppressedUtc: old,
                    suppressionReason: "mail_not_configured"),
                NewCleanupOutbox(prefix, "sent-recent", recent, status: "sent", sentUtc: recent),
                NewCleanupOutbox(prefix, "suppressed-recent", recent, status: "suppressed", suppressedUtc: recent,
                    suppressionReason: "test_terminal"));
            await seed.SaveChangesAsync();
            organization.IsActive = wasActive;
            await seed.SaveChangesAsync();
        }

        var worker = factory.Services.GetRequiredService<WorkflowProcessingService>();
        Assert.AreEqual(2, await worker.CleanupEmailPayloadsAsync());

        await using var verify = await contextFactory.CreateDbContextAsync();
        var rows = await verify.EmailOutbox
            .Where(item => item.BusinessKey != null && item.BusinessKey.StartsWith(prefix))
            .ToListAsync();
        try
        {
            Assert.HasCount(7, rows);
            foreach (var row in rows.Where(item => item.Status is "pending" or "sending" or "failed" ||
                                                    rowIsRecentTerminal(item, recent)))
            {
                Assert.AreEqual("Subject", row.Subject);
                Assert.AreEqual("Body", row.BodyText);
                Assert.AreEqual("<p>Body</p>", row.BodyHtml);
            }

            foreach (var row in rows.Where(item =>
                         item.Status == "sent" && item.SentUtc < now.AddDays(-90) ||
                         item.Status == "suppressed" && item.SuppressedUtc < now.AddDays(-90)))
            {
                Assert.IsNull(row.Subject);
                Assert.IsNull(row.BodyText);
                Assert.IsNull(row.BodyHtml);
            }

            var suppressed = rows.Single(item => item.Status == "suppressed" && item.SuppressionReason == "mail_not_configured");
            Assert.AreEqual("mail_not_configured", suppressed.SuppressionReason);
        }
        finally
        {
            verify.EmailOutbox.RemoveRange(rows);
            await verify.SaveChangesAsync();
        }

        static bool rowIsRecentTerminal(EmailOutbox row, DateTime recentCutoff) =>
            row.Status is "sent" or "suppressed" &&
            (row.SentUtc ?? row.SuppressedUtc) >= recentCutoff;
    }

    private static EmailOutbox NewCleanupOutbox(
        string prefix,
        string key,
        DateTime createdUtc,
        string status = "pending",
        bool sending = false,
        string? errorCode = null,
        DateTime? sentUtc = null,
        DateTime? suppressedUtc = null,
        string? suppressionReason = null) => new()
        {
            OrganizationId = 2,
            BusinessKey = $"{prefix}:{key}",
            DeliveryClass = "operational_test",
            ToAddress = "cleanup@example.org",
            FromAddress = "system@example.org",
            Subject = "Subject",
            BodyText = "Body",
            BodyHtml = "<p>Body</p>",
            Status = sending ? "sending" : status,
            AttemptCount = sending ? 1 : 0,
            SendingStartedUtc = sending ? createdUtc : null,
            LeaseId = sending ? Guid.NewGuid() : null,
            LeaseExpiresUtc = sending ? createdUtc.AddMinutes(2) : null,
            LastErrorCode = errorCode,
            SentUtc = sentUtc,
            SuppressedUtc = suppressedUtc,
            SuppressionReason = suppressionReason,
            CreatedUtc = createdUtc
        };

    private async Task DeactivateCorrectiveStaffAsync(long staffId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var row = await context.StaffUsers.SingleOrDefaultAsync(item => item.Id == staffId);
        if (row is null || !row.IsActive) return;
        row.IsActive = false;
        await context.SaveChangesAsync();
    }
}
