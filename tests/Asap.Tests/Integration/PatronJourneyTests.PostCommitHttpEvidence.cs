using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("assign", "canceled")]
    [DataRow("assign", "failure")]
    [DataRow("action", "canceled")]
    [DataRow("action", "failure")]
    [DataRow("additional_copy", "canceled")]
    [DataRow("additional_copy", "failure")]
    public async Task StaffPostCommitHttpSeparatesCanceledClientFromOptionalDispatchFailure(
        string mutation,
        string dispatchMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        long titleRequestId = 0;
        long copySourceRequestId = 0;
        long copyCurrentClaimantId = 0;
        long copyHistoricalRuleId = 0;
        long copyTaskId = 0;
        var titleOutboxIds = new List<long>();
        long copyOutboxId = 0;
        (byte[] RowVersion, int EventCount)? titleCommitted = null;
        (long TaskId, string Status, byte[] RowVersion, int SourceEventCount)? copyCommitted = null;

        try
        {
            await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailOutboxDispatcher>();
                    services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(new MutableReadinessEmailSender(isConfigured: true));
                }));
            using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = true
            });
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            string path;
            string? titleVersion = null;
            string? copyVersion = null;
            if (mutation is "assign" or "action")
            {
                var seeded = await SeedBibOwnershipRequestAsync(
                    $"http-post-commit-{mutation}-{Guid.NewGuid():N}",
                    null,
                    staffVerified: false,
                    status: "suggestion");
                titleRequestId = seeded.Id;
                if (mutation == "action")
                {
                    await SetRequestNativePatronIdAsync(titleRequestId, 7001);
                    await using var context = await factory.Services
                        .GetRequiredService<IDbContextFactory<AsapDbContext>>()
                        .CreateDbContextAsync();
                    var barcode = await context.TitleRequests.AsNoTracking()
                        .Where(item => item.Id == titleRequestId)
                        .Select(item => item.Barcode)
                        .SingleAsync();
                    scopedFactory.Services.GetRequiredService<DeterministicTestingPatronProvider>().AddPatron(
                        new PatronSnapshot(
                            7001,
                            barcode,
                            "ownership@example.org",
                            "Ownership",
                            "Patron",
                            1,
                            "Adult",
                            101,
                            2,
                            "Test Library",
                            101),
                        [new PickupBranch(101, "Main Library")],
                        2);
                }
                var initial = await ReadCancellationTitleRequestSnapshotAsync(titleRequestId);
                Assert.AreEqual("suggestion", initial.Status);
                titleVersion = StaffVersion.Encode(initial.RowVersion);
                path = $"/api/asap/staff/title-requests/{titleRequestId}/" +
                       (mutation == "assign" ? "assign" : "action");
            }
            else
            {
                var seeded = await SeedAdditionalCopyLegacySourceAsync(staff.Id, $"http-post-commit-{Guid.NewGuid():N}");
                copySourceRequestId = seeded.SourceRequestId;
                copyCurrentClaimantId = seeded.CurrentClaimantId;
                copyHistoricalRuleId = seeded.HistoricalRuleId;
                var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
                var preview = await copies.PreviewAsync(actor, copySourceRequestId, CancellationToken.None);
                Assert.AreEqual("loaded", preview.Code);
                copyVersion = preview.Preview?.Version ??
                    throw new AssertFailedException("Additional-copy preview did not return a version.");
                path = $"/api/asap/staff/title-requests/{copySourceRequestId}/additional-copy";
            }

            using var clientCancellation = new CancellationTokenSource();
            Task<HttpResponseMessage> request = mutation switch
            {
                "assign" => client.PostAsJsonAsync(path, new
                {
                    version = titleVersion,
                    assigneeId = actor.Id
                }, clientCancellation.Token),
                "action" => client.PostAsJsonAsync(path, new
                {
                    version = titleVersion,
                    action = "purchase",
                    status = "outstanding_purchase",
                    emailPurchaseReminder = true
                }, clientCancellation.Token),
                _ => client.PostAsJsonAsync(path, new
                {
                    version = copyVersion,
                    emailPurchaseReminder = true
                }, clientCancellation.Token)
            };

            var dispatchStarted = dispatcher.EnqueueStarted.Task;
            if (await Task.WhenAny(request, dispatchStarted) == request && !dispatchStarted.IsCompleted)
            {
                using var earlyResponse = await request;
                var earlyBody = await earlyResponse.Content.ReadAsStringAsync();
                Assert.Fail(
                    $"The {mutation} HTTP mutation completed with {earlyResponse.StatusCode} before the committed outbox enqueue began: {earlyBody}");
            }
            await dispatchStarted.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, dispatcher.EnqueuedIds.Count,
                "The dispatcher barrier is reached only after the HTTP mutation committed its durable outbox work.");

            if (mutation is "assign" or "action")
            {
                var expectedStatus = mutation == "action" ? "outstanding_purchase" : "suggestion";
                var snapshot = await ReadCancellationTitleRequestSnapshotAsync(titleRequestId);
                Assert.AreEqual(expectedStatus, snapshot.Status);
                Assert.IsGreaterThan(0, snapshot.EventCount);
                titleCommitted = (snapshot.RowVersion, snapshot.EventCount);
                titleOutboxIds.AddRange(await ReadCommittedTitleMutationOutboxIdsAsync(titleRequestId, mutation));
                Assert.AreEqual(mutation == "action" ? 2 : 1, titleOutboxIds.Count);
                foreach (var outboxId in titleOutboxIds)
                {
                    AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxId));
                }
                Assert.IsTrue(titleOutboxIds.Contains(dispatcher.EnqueuedIds[0]));
            }
            else
            {
                copyOutboxId = dispatcher.EnqueuedIds.Single();
                copyCommitted = await ReadCommittedAdditionalCopyAsync(
                    copyOutboxId,
                    "create",
                    expectedAssignmentVersion: null,
                    expectedAssigneeId: null);
                copyTaskId = copyCommitted.Value.TaskId;
            }

            if (dispatchMode == "canceled")
            {
                clientCancellation.Cancel();
            }
            dispatcher.Release();

            if (dispatchMode == "canceled")
            {
                try
                {
                    using var unexpectedResponse = await request;
                    Assert.Fail($"Canceled client unexpectedly received HTTP {unexpectedResponse.StatusCode}.");
                }
                catch (OperationCanceledException)
                {
                    Assert.IsTrue(clientCancellation.IsCancellationRequested);
                }
            }
            else
            {
                using var response = await request;
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.IsTrue(body.RootElement.GetProperty("committed").GetBoolean(),
                    "The live caller receives explicit metadata for the already committed mutation.");
                Assert.AreEqual("dispatch_failed", body.RootElement.GetProperty("notificationStatus").GetString());
                if (mutation == "action")
                {
                    Assert.AreEqual("outstanding_purchase", body.RootElement.GetProperty("finalStatus").GetString());
                }
                if (mutation == "additional_copy")
                {
                    Assert.IsFalse(body.RootElement.GetProperty("purchaseReminderEmail").GetProperty("queued").GetBoolean());
                }
            }

            if (titleRequestId > 0)
            {
                var final = await ReadCancellationTitleRequestSnapshotAsync(titleRequestId);
                Assert.AreEqual(titleCommitted!.Value.EventCount, final.EventCount,
                    "An HTTP abort or optional dispatch fault must not duplicate the accepted SQL event.");
                CollectionAssert.AreEqual(titleCommitted.Value.RowVersion, final.RowVersion);
                foreach (var outboxId in titleOutboxIds)
                {
                    AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxId));
                }
                Assert.IsTrue(dispatcher.EnqueuedIds.All(titleOutboxIds.Contains));
                CollectionAssert.AllItemsAreUnique(dispatcher.EnqueuedIds);
            }
            else if (copyCommitted is { } committedCopy)
            {
                await AssertCommittedAdditionalCopyStillDurableAsync(committedCopy, copyOutboxId);
                Assert.AreEqual(copyTaskId, committedCopy.TaskId);
                Assert.AreEqual(1, dispatcher.EnqueuedIds.Count);
            }
        }
        finally
        {
            dispatcher.Release();
            if (titleRequestId > 0)
            {
                if (titleOutboxIds.Count == 0)
                {
                    titleOutboxIds.AddRange(await ReadCommittedTitleMutationOutboxIdsAsync(titleRequestId, mutation));
                }
                await DeleteCommittedOutboxIdsAsync(titleOutboxIds);
                await DeleteBibOwnershipRequestsAsync([titleRequestId]);
            }
            if (copySourceRequestId > 0)
            {
                if (copyOutboxId > 0)
                {
                    await DeleteCommittedOutboxIdsAsync([copyOutboxId]);
                }
                await DeleteAdditionalCopyTestDataAsync(copySourceRequestId, copyTaskId > 0 ? copyTaskId : null);
                await DeleteBibOwnershipRequestsAsync([copySourceRequestId]);
                await ExecuteNonQueryAsync(
                    "DECLARE @historicalStaffId bigint = (SELECT [StaffUserId] FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @ruleId); " +
                    "DELETE FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @ruleId; " +
                    "DELETE FROM [asap].[StaffUser] WHERE [Id] IN (@claimantId, @historicalStaffId);",
                    ("@ruleId", copyHistoricalRuleId),
                    ("@claimantId", copyCurrentClaimantId));
            }
            await RestoreCancellationEmailSettingsAsync(emailSettings);
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [NotificationEmail] = @email, " +
                "[AdditionalCopyReminderDefault] = @reminder WHERE [Id] = @staffId;",
                ("@email", (object?)staff.NotificationEmail ?? DBNull.Value),
                ("@reminder", staff.AdditionalCopyReminderDefault),
                ("@staffId", staff.Id));
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }
}
