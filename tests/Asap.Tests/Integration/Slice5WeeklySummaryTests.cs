using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Features.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task NormalWeeklySummaryUsesBusinessPeriodAndQueuesOnlyOnceForCurrentRecipient()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestId = 0L;
        string? oldStaffUrl;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            oldStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var staff = await seed.StaffUsers.SingleAsync(item => item.Id == admin.Id);
            staff.WeeklyActionSummaryEnabled = true;
            staff.WeeklyActionSummaryEmail = "weekly-normal@example.org";
            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-1);
            var submission = new TitleRequest
            {
                LibraryOrganizationId = 2,
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
            var first = await service.SendWeeklyStaffSummaryAsync(null, 2, CancellationToken.None);
            var second = await service.SendWeeklyStaffSummaryAsync(null, 2, CancellationToken.None);
            Assert.AreEqual("completed", first.Code);
            Assert.AreEqual("completed", second.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var queued = await verify.EmailOutbox.AsNoTracking()
                .Where(item => item.BusinessKey.StartsWith(businessKeyPrefix))
                .ToListAsync();
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("pending", queued[0].Status);
            Assert.AreEqual("weekly_summary", queued[0].RecipientAddressKind);
            StringAssert.Contains(queued[0].Subject!, "1 new");
            StringAssert.Contains(queued[0].BodyText!, "stage=submitted");
        }
        finally
        {
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
    public async Task ForcedWeeklySummaryUsesOpenActionCountsSamplesLinksAndDurableManualId()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        const string manualRunId = "slice5-weekly-manual-01";
        var businessPrefix = $"weekly-summary-force:{manualRunId}:";
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
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
                LibraryOrganizationId = 2, Barcode = "slice5-weekly-submission", Title = "Weekly submission",
                Author = "Summary Author", MaterialFormatId = format.Id, Status = "suggestion",
                AutoHold = false, CreatedUtc = now, UpdatedUtc = now
            };
            var purchase = new TitleRequest
            {
                LibraryOrganizationId = 2, Barcode = "slice5-weekly-purchase", Title = "Weekly purchase",
                Author = "Purchase Author", MaterialFormatId = format.Id, Status = "outstanding_purchase",
                AutoHold = false, CreatedUtc = now, UpdatedUtc = now
            };
            seed.TitleRequests.AddRange(submission, purchase);
            var copy = new AdditionalCopyRequest
            {
                LibraryOrganizationId = 2, BibId = "9001", Title = "Weekly additional copy",
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
                2,
                CancellationToken.None,
                new StaffIdentityEvidence(actor.Id, actor.EntraTenantId, actor.EntraObjectId));
            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual(manualRunId, result.ManualRunId);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var outbox = await verify.EmailOutbox.AsNoTracking()
                .SingleAsync(item => item.BusinessKey == $"{businessPrefix}{actor.Id}");
            var expectedNew = await verify.TitleRequests.CountAsync(item =>
                item.LibraryOrganizationId == 2 && item.Status == "suggestion");
            var expectedPurchases = await verify.TitleRequests.CountAsync(item =>
                item.LibraryOrganizationId == 2 && item.Status == "outstanding_purchase" && string.IsNullOrWhiteSpace(item.BibId));
            var expectedCopies = await verify.AdditionalCopyRequests.CountAsync(item =>
                item.LibraryOrganizationId == 2 && item.Status == "open");
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
                2,
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
}
