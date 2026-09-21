using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task TimeoutQueuesUseStatusSpecificAgesAndNeverExpireOutstandingPurchases()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var old = now.AddDays(-31);
        var requestIds = new List<long>();
        long copyId;
        WorkflowSettings? originalSettings;
        EmailSettings? originalLibraryEmail;
        string? originalSystemEmailAddress;
        string? originalSystemEmailName;
        var createdLibraryEmail = false;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var settings = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            originalSettings = settings is null ? null : new WorkflowSettings
            {
                OrganizationId = scope,
                SuggestionLimit = settings.SuggestionLimit,
                SuggestionLimitMessage = settings.SuggestionLimitMessage,
                OutstandingTimeoutEnabled = settings.OutstandingTimeoutEnabled,
                OutstandingTimeoutDays = settings.OutstandingTimeoutDays,
                OutstandingTimeoutSendEmail = settings.OutstandingTimeoutSendEmail,
                OutstandingTimeoutRejectionTemplateId = settings.OutstandingTimeoutRejectionTemplateId,
                PendingHoldTimeoutEnabled = settings.PendingHoldTimeoutEnabled,
                PendingHoldTimeoutDays = settings.PendingHoldTimeoutDays,
                HoldPickupTimeoutEnabled = settings.HoldPickupTimeoutEnabled,
                HoldPickupTimeoutDays = settings.HoldPickupTimeoutDays,
                AdditionalCopyTimeoutEnabled = settings.AdditionalCopyTimeoutEnabled,
                AdditionalCopyTimeoutDays = settings.AdditionalCopyTimeoutDays,
                AutoPromote = settings.AutoPromote,
                CommonAuthorsEnabled = settings.CommonAuthorsEnabled,
                CommonAuthorsLabel = settings.CommonAuthorsLabel,
                CommonAuthorsHelp = settings.CommonAuthorsHelp,
                CommonAuthorsMessage = settings.CommonAuthorsMessage,
                AllowPatronAutoholdOptOut = settings.AllowPatronAutoholdOptOut,
                AllowAnyRegisteredCardLogin = settings.AllowAnyRegisteredCardLogin,
                PatronCodeEligibilityEnabled = settings.PatronCodeEligibilityEnabled,
                PatronCodeEligibilityMessage = settings.PatronCodeEligibilityMessage,
                UpdatedUtc = settings.UpdatedUtc
            };
            if (settings is null)
            {
                settings = new WorkflowSettings { OrganizationId = scope };
                seed.WorkflowSettings.Add(settings);
            }
            settings.OutstandingTimeoutEnabled = true;
            settings.OutstandingTimeoutDays = 30;
            settings.OutstandingTimeoutSendEmail = true;
            settings.PendingHoldTimeoutEnabled = true;
            settings.PendingHoldTimeoutDays = 30;
            settings.HoldPickupTimeoutEnabled = true;
            settings.HoldPickupTimeoutDays = 30;
            settings.AdditionalCopyTimeoutEnabled = true;
            settings.AdditionalCopyTimeoutDays = 30;
            settings.AutoPromote = false;

            var systemEmail = await seed.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
            originalSystemEmailAddress = systemEmail.FromAddress;
            originalSystemEmailName = systemEmail.FromName;
            var libraryEmail = await seed.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            originalLibraryEmail = libraryEmail is null ? null : new EmailSettings
            {
                OrganizationId = scope,
                FromAddress = libraryEmail.FromAddress,
                FromName = libraryEmail.FromName,
                UpdatedUtc = libraryEmail.UpdatedUtc
            };
            if (libraryEmail is null)
            {
                libraryEmail = new EmailSettings { OrganizationId = scope };
                seed.EmailSettings.Add(libraryEmail);
                createdLibraryEmail = true;
            }
            systemEmail.FromAddress = "system.sender@example.org";
            systemEmail.FromName = "System Sender";
            libraryEmail.ProtectedServerToken = null;
            libraryEmail.FromAddress = null;
            libraryEmail.FromName = "Library Sender";

            var format = await seed.MaterialFormats.SingleAsync(item => item.Code == "book");
            var suggestion = NewRequest(scope, "timeout-suggestion", "suggestion", old, now);
            suggestion.Email = "timeout-recipient@example.org";
            var purchase = NewRequest(scope, "timeout-purchase", "outstanding_purchase", old, old);
            purchase.BibId = null;
            var pendingHold = NewRequest(scope, "timeout-pending", "pending_hold", old, old);
            pendingHold.AutoHold = true;
            var placed = NewRequest(scope, "timeout-placed", "hold_placed", old, old);
            suggestion.MaterialFormatId = format.Id;
            purchase.MaterialFormatId = format.Id;
            pendingHold.MaterialFormatId = format.Id;
            placed.MaterialFormatId = format.Id;
            seed.TitleRequests.AddRange(suggestion, purchase, pendingHold, placed);
            var copy = new AdditionalCopyRequest
            {
                LibraryOrganizationId = scope,
                BibId = "timeout-copy-bib",
                Title = "Timeout copy",
                Status = "open",
                CreatedUtc = old,
                UpdatedUtc = old
            };
            seed.AdditionalCopyRequests.Add(copy);
            await seed.SaveChangesAsync();
            requestIds.AddRange([suggestion.Id, purchase.Id, pendingHold.Id, placed.Id]);
            copyId = copy.Id;

            foreach (var (queue, id, created) in new[]
                     {
                         (QueueNames.OutstandingTimeout, suggestion.Id, suggestion.CreatedUtc),
                         (QueueNames.PendingHoldTimeout, pendingHold.Id, pendingHold.CreatedUtc),
                         (QueueNames.HoldPickupTimeout, placed.Id, placed.CreatedUtc),
                         (QueueNames.AdditionalCopyTimeout, copy.Id, copy.CreatedUtc)
                     })
            {
                var progress = await seed.QueueProgress.SingleOrDefaultAsync(item =>
                    item.QueueName == queue && item.ScopeOrganizationId == scope);
                progress ??= new QueueProgress { QueueName = queue, ScopeOrganizationId = scope };
                if (progress.RowVersion.Length == 0) seed.QueueProgress.Add(progress);
                progress.CycleMaxId = id;
                progress.LastCreatedUtc = created.AddTicks(-1);
                progress.LastItemId = 0;
                progress.LastOutcomeCode = "test_cycle_started";
                progress.UpdatedUtc = now;
            }
            await seed.SaveChangesAsync();
        }

        try
        {
            var result = await factory.Services.GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", result.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            var requests = await verify.TitleRequests.AsNoTracking()
                .Where(item => requestIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Barcode!);
            Assert.AreEqual("closed", requests["timeout-suggestion"].Status);
            Assert.AreEqual("rejected", requests["timeout-suggestion"].CloseReason);
            Assert.AreEqual("outstanding_purchase", requests["timeout-purchase"].Status);
            Assert.AreEqual("closed", requests["timeout-pending"].Status);
            Assert.AreEqual("closed", requests["timeout-placed"].Status);
            Assert.AreEqual("closed", (await verify.AdditionalCopyRequests.AsNoTracking()
                .SingleAsync(item => item.Id == copyId)).Status);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == requests["timeout-suggestion"].Id && item.EventType == "timeout_closed"));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == requests["timeout-pending"].Id && item.EventType == "timeout_closed"));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == requests["timeout-placed"].Id && item.EventType == "timeout_closed"));
            var timeoutMail = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
                item.BusinessKey == $"timeout:OutstandingTimeout:{requests["timeout-suggestion"].Id}");
            Assert.AreEqual("pending", timeoutMail.Status);
            Assert.AreEqual("system.sender@example.org", timeoutMail.FromAddress);
            Assert.AreEqual("Library Sender", timeoutMail.FromName);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] = CONCAT(N'timeout:OutstandingTimeout:', @a); DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN (@a,@b,@c,@d); DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@a,@b,@c,@d); DELETE FROM [asap].[AdditionalCopyRequest] WHERE [Id] = @copy;",
                ("@a", requestIds[0]), ("@b", requestIds[1]), ("@c", requestIds[2]), ("@d", requestIds[3]), ("@copy", copyId));
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
                settings.PendingHoldTimeoutEnabled = originalSettings.PendingHoldTimeoutEnabled;
                settings.PendingHoldTimeoutDays = originalSettings.PendingHoldTimeoutDays;
                settings.HoldPickupTimeoutEnabled = originalSettings.HoldPickupTimeoutEnabled;
                settings.HoldPickupTimeoutDays = originalSettings.HoldPickupTimeoutDays;
                settings.AdditionalCopyTimeoutEnabled = originalSettings.AdditionalCopyTimeoutEnabled;
                settings.AdditionalCopyTimeoutDays = originalSettings.AdditionalCopyTimeoutDays;
                settings.AutoPromote = originalSettings.AutoPromote;
            }
            var systemEmail = await restore.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
            systemEmail.FromAddress = originalSystemEmailAddress;
            systemEmail.FromName = originalSystemEmailName;
            var libraryEmail = await restore.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            if (createdLibraryEmail)
            {
                if (libraryEmail is not null) restore.EmailSettings.Remove(libraryEmail);
            }
            else if (libraryEmail is not null && originalLibraryEmail is not null)
            {
                libraryEmail.FromAddress = originalLibraryEmail.FromAddress;
                libraryEmail.FromName = originalLibraryEmail.FromName;
            }
            await restore.SaveChangesAsync();
        }
    }

    private static TitleRequest NewRequest(int organizationId, string barcode, string status, DateTime createdUtc, DateTime updatedUtc) =>
        new()
        {
            LibraryOrganizationId = organizationId,
            Barcode = barcode,
            Title = barcode,
            Author = "Slice Five",
            Status = status,
            IsbnCheckStatus = "not_found",
            AutoHold = false,
            CreatedUtc = createdUtc,
            UpdatedUtc = updatedUtc
        };
}
