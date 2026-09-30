using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task RequestActionsRevalidateTemplatesAndReportAuthoritativeActivityAndNotifications()
    {
        using var client = factory!.CreateClient();
        using var initialSession = await client.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, initialSession.StatusCode);
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                         .CreateDbContextAsync())
        {
            actorId = await context.StaffUsers.Where(item => item.NormalizedUserPrincipalName == "ADMIN@EXAMPLE.ORG")
                .Select(item => item.Id).SingleAsync();
        }
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery",
            sessionJson.RootElement.GetProperty("antiforgeryToken").GetString());

        var requestIds = new List<long>();
        var templateIds = new List<long>();
        var formatIds = new List<long>();
        try
        {
            long goodId;
            long hiddenId;
            long foreignId;
            long deletedId;
            string collidingCode;
            await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var suffix = Guid.NewGuid().ToString("N");
                collidingCode = $"action_format_{suffix}";
                var systemFormat = new MaterialFormat
                {
                    OwnerOrganizationId = 1, Code = collidingCode, Label = "System action format",
                    IsEnabled = true, CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
                };
                var libraryFormat = new MaterialFormat
                {
                    OwnerOrganizationId = 2, Code = collidingCode, Label = "Library action format",
                    IsEnabled = true, CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
                };
                context.MaterialFormats.AddRange(systemFormat, libraryFormat);
                await context.SaveChangesAsync();
                formatIds.AddRange([systemFormat.Id, libraryFormat.Id]);
                var templates = new[]
                {
                    new EmailTemplate { OrganizationId = 2, TemplateKey = $"rejection:good_{suffix}",
                        DisplayName = "Good <img src=x onerror=alert(1)>", SubjectTemplate = "Update: {{title}}",
                        BodyTemplate = "Hello {{name}}", IsCustom = true },
                    new EmailTemplate { OrganizationId = 2, TemplateKey = $"rejection:hidden_{suffix}",
                        DisplayName = "Hidden", SubjectTemplate = "Hidden", BodyTemplate = "Hidden",
                        IsCustom = true, IsHidden = true },
                    new EmailTemplate { OrganizationId = 101, TemplateKey = $"rejection:foreign_{suffix}",
                        DisplayName = "Foreign", SubjectTemplate = "Foreign", BodyTemplate = "Foreign", IsCustom = true },
                    new EmailTemplate { OrganizationId = 2, TemplateKey = $"rejection:deleted_{suffix}",
                        DisplayName = "Deleted", SubjectTemplate = "Deleted", BodyTemplate = "Deleted", IsCustom = true }
                };
                context.EmailTemplates.AddRange(templates);
                var formatId = await context.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id).FirstAsync();
                for (var index = 0; index < 7; index++)
                {
                    context.TitleRequests.Add(new TitleRequest
                    {
                        LibraryOrganizationId = 2,
                        Barcode = $"20000000203{index:00}",
                        Email = "patron@example.org",
                        NameFirst = "Test",
                        Title = $"Action title {index}",
                        ExactPublicationDate = index == 5 ? new DateOnly(2026, 1, 2) : null,
                        CustomFieldsJson = index == 5
                            ? "{\"shelf\":{\"value\":\"Reference\",\"type\":\"text\",\"label\":\"Shelf\"}}"
                            : null,
                        MaterialFormatId = index == 5 ? systemFormat.Id : formatId,
                        BibId = index >= 5 ? 9001 : null,
                        BibIdStaffVerified = index == 6,
                        AutoHold = index == 6,
                        Status = "suggestion",
                        IsbnCheckStatus = "skipped_no_isbn",
                        CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                        UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
                    });
                }
                await context.SaveChangesAsync();
                goodId = templates[0].Id;
                hiddenId = templates[1].Id;
                foreignId = templates[2].Id;
                deletedId = templates[3].Id;
                templateIds.AddRange(templates.Select(item => item.Id));
                requestIds.AddRange(await context.TitleRequests.Where(item => item.Title.StartsWith("Action title "))
                    .OrderByDescending(item => item.Id).Take(7).Select(item => item.Id).ToListAsync());
            }
            requestIds.Sort();
            await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var timestamp = timeProvider!.GetUtcNow().UtcDateTime;
                context.TitleRequestEvents.AddRange(
                    new TitleRequestEvent { TitleRequestId = requestIds[0], EventType = "staff_note",
                        ActorType = "staff", Message = "<svg onload=alert(1)>", CreatedUtc = timestamp },
                    new TitleRequestEvent { TitleRequestId = requestIds[0], EventType = "status_changed",
                        ActorType = "system", Message = "Second event", CreatedUtc = timestamp });
                context.EmailTemplates.Remove(await context.EmailTemplates.SingleAsync(item => item.Id == deletedId));
                await context.SaveChangesAsync();
            }

            async Task<JsonDocument> Detail(long id)
            {
                using var response = await client.GetAsync($"/api/asap/staff/title-requests/{id}");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }

            using var first = await Detail(requestIds[0]);
            var version = first.RootElement.GetProperty("version").GetString();
            var activity = first.RootElement.GetProperty("activity");
            Assert.AreEqual(2, activity.GetArrayLength());
            Assert.IsTrue(long.Parse(activity[0].GetProperty("id").GetString()!) <
                          long.Parse(activity[1].GetProperty("id").GetString()!));
            Assert.AreEqual(JsonValueKind.Null, activity[0].GetProperty("actorName").ValueKind);
            Assert.AreEqual("<svg onload=alert(1)>", activity[0].GetProperty("message").GetString());
            using var repeated = await Detail(requestIds[0]);
            Assert.AreEqual(2, repeated.RootElement.GetProperty("activity").GetArrayLength());

            using var choicesResponse = await client.GetAsync(
                $"/api/asap/staff/title-requests/{requestIds[0]}/rejection-templates");
            Assert.AreEqual(HttpStatusCode.OK, choicesResponse.StatusCode);
            using var choices = JsonDocument.Parse(await choicesResponse.Content.ReadAsStringAsync());
            Assert.IsTrue(choices.RootElement.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == goodId.ToString()));
            Assert.IsFalse(choices.RootElement.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == hiddenId.ToString()));

            await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var selected = await context.EmailTemplates.SingleAsync(item => item.Id == goodId);
                selected.IsHidden = true;
                await context.SaveChangesAsync();
            }
            using (var disabled = await client.PostAsJsonAsync(
                       $"/api/asap/staff/title-requests/{requestIds[0]}/action",
                       new { version, action = "reject", rejectionTemplateId = goodId.ToString() }))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, disabled.StatusCode);
                using var error = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
                Assert.AreEqual("invalid_rejection_template", error.RootElement.GetProperty("code").GetString());
            }
            await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var selected = await context.EmailTemplates.SingleAsync(item => item.Id == goodId);
                selected.IsHidden = false;
                await context.SaveChangesAsync();
            }

            foreach (var selected in new[] { hiddenId, foreignId, deletedId })
            {
                using var rejected = await client.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[0]}/action",
                    new { version, action = "reject", rejectionTemplateId = selected.ToString() });
                Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode,
                    await rejected.Content.ReadAsStringAsync());
                using var error = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Assert.AreEqual("invalid_rejection_template", error.RootElement.GetProperty("code").GetString());
            }
            using var unchanged = await Detail(requestIds[0]);
            Assert.AreEqual("suggestion", unchanged.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(2, unchanged.RootElement.GetProperty("activity").GetArrayLength());
            using var paddedReject = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[0]}/action",
                new { version, action = " reject ", rejectionTemplateId = goodId.ToString() });
            Assert.AreEqual(HttpStatusCode.BadRequest, paddedReject.StatusCode);
            using var paddedRejectBody = JsonDocument.Parse(await paddedReject.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_action", paddedRejectBody.RootElement.GetProperty("code").GetString());

            using var validReject = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[0]}/action",
                new { version, action = "reject", rejectionTemplateId = goodId.ToString() });
            Assert.AreEqual(HttpStatusCode.OK, validReject.StatusCode,
                await validReject.Content.ReadAsStringAsync());
            using var committed = JsonDocument.Parse(await validReject.Content.ReadAsStringAsync());
            Assert.IsTrue(committed.RootElement.GetProperty("committed").GetBoolean());
            Assert.AreEqual("closed", committed.RootElement.GetProperty("finalStatus").GetString());
            Assert.AreEqual("closed", committed.RootElement.GetProperty("status").GetString());
            Assert.IsTrue(committed.RootElement.GetProperty("activity")
                .EnumerateArray().Any(item => item.GetProperty("eventType").GetString() == "rejection_template_selected"));

            using var staleReject = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[0]}/action",
                new { version, action = "reject", rejectionTemplateId = goodId.ToString() });
            Assert.AreEqual(HttpStatusCode.Conflict, staleReject.StatusCode);
            using var staleBody = JsonDocument.Parse(await staleReject.Content.ReadAsStringAsync());
            Assert.AreEqual("stale_version", staleBody.RootElement.GetProperty("code").GetString());

            for (var index = 1; index <= 3; index++)
            {
                using var detail = await Detail(requestIds[index]);
                var currentVersion = detail.RootElement.GetProperty("version").GetString();
                object purchaseInput = index == 3
                    ? new { version = currentVersion, action = "purchase" }
                    : new { version = currentVersion, action = "purchase",
                        emailPurchaseReminder = index == 2 };
                using var purchase = await client.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[index]}/action",
                    purchaseInput);
                Assert.AreEqual(HttpStatusCode.OK, purchase.StatusCode,
                    await purchase.Content.ReadAsStringAsync());
                using var result = JsonDocument.Parse(await purchase.Content.ReadAsStringAsync());
                Assert.AreEqual("outstanding_purchase", result.RootElement.GetProperty("finalStatus").GetString());
                Assert.AreEqual(index == 2 ? "queued" : "not_requested",
                    result.RootElement.GetProperty("notificationStatus").GetString());
            }
            using var stalePurchaseDetail = await Detail(requestIds[4]);
            var stalePurchaseVersion = stalePurchaseDetail.RootElement.GetProperty("version").GetString();
            using var paddedPurchase = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[4]}/action",
                new { version = stalePurchaseVersion, action = " purchase ", emailPurchaseReminder = true });
            Assert.AreEqual(HttpStatusCode.BadRequest, paddedPurchase.StatusCode);
            using var paddedPurchaseBody = JsonDocument.Parse(await paddedPurchase.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_action", paddedPurchaseBody.RootElement.GetProperty("code").GetString());
            await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await context.TitleRequests.SingleAsync(item => item.Id == requestIds[4]);
                request.Notes = "Concurrent edit";
                await context.SaveChangesAsync();
            }
            using var stalePurchase = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[4]}/action",
                new { version = stalePurchaseVersion, action = "purchase", emailPurchaseReminder = true });
            Assert.AreEqual(HttpStatusCode.Conflict, stalePurchase.StatusCode);
            using var stalePurchaseBody = JsonDocument.Parse(await stalePurchase.Content.ReadAsStringAsync());
            Assert.AreEqual("stale_version", stalePurchaseBody.RootElement.GetProperty("code").GetString());

            using var timeoutDetail = await Detail(requestIds[4]);
            await using (var timeoutFactory = factory.WithWebHostBuilder(builder =>
                             builder.ConfigureServices(services =>
                             {
                                 services.RemoveAll<IEmailSender>();
                                 services.AddSingleton<IEmailSender, TimeoutReadinessEmailSender>();
                             })))
            {
                using var timeoutClient = timeoutFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { HandleCookies = true });
                timeoutClient.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
                timeoutClient.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
                using var timeoutSession = await timeoutClient.GetAsync("/api/asap/staff/session");
                using var timeoutSessionJson = JsonDocument.Parse(await timeoutSession.Content.ReadAsStringAsync());
                timeoutClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery",
                    timeoutSessionJson.RootElement.GetProperty("antiforgeryToken").GetString());
                using var timeout = await timeoutClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[4]}/action",
                    new { version = timeoutDetail.RootElement.GetProperty("version").GetString(),
                        action = "purchase", emailPurchaseReminder = true });
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timeout.StatusCode,
                    await timeout.Content.ReadAsStringAsync());
                using var timeoutResult = JsonDocument.Parse(await timeout.Content.ReadAsStringAsync());
                Assert.AreEqual("notification_dependency_unavailable",
                    timeoutResult.RootElement.GetProperty("code").GetString());

                using var assignTimeout = await timeoutClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[4]}/assign",
                    new { version = timeoutDetail.RootElement.GetProperty("version").GetString(),
                        assigneeId = actorId });
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, assignTimeout.StatusCode,
                    await assignTimeout.Content.ReadAsStringAsync());
                using var assignTimeoutResult = JsonDocument.Parse(await assignTimeout.Content.ReadAsStringAsync());
                Assert.AreEqual("notification_dependency_unavailable",
                    assignTimeoutResult.RootElement.GetProperty("code").GetString());

                using var pendingDetail = await Detail(requestIds[6]);
                using var pendingPurchase = await timeoutClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[6]}/action",
                    new { version = pendingDetail.RootElement.GetProperty("version").GetString(),
                        action = "purchase", emailPurchaseReminder = true });
                Assert.AreEqual(HttpStatusCode.OK, pendingPurchase.StatusCode,
                    await pendingPurchase.Content.ReadAsStringAsync());
                using var pendingResult = JsonDocument.Parse(await pendingPurchase.Content.ReadAsStringAsync());
                Assert.AreEqual("pending_hold", pendingResult.RootElement.GetProperty("finalStatus").GetString());
                Assert.AreEqual("not_applicable", pendingResult.RootElement.GetProperty("notificationStatus").GetString());
            }
            using var afterTimeout = await Detail(requestIds[4]);
            Assert.AreEqual("suggestion", afterTimeout.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(timeoutDetail.RootElement.GetProperty("version").GetString(),
                afterTimeout.RootElement.GetProperty("version").GetString());

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var actor = new CurrentStaff(actorId, "admin@example.org", Guid.Parse(identity.TenantId!),
                identity.UserPrincipalName, identity.DisplayName, identity.NotificationEmail,
                "super_admin", 1, "System", true, false, null, false, false, false, []);
            var cancelled = false;
            try
            {
                await factory.Services.GetRequiredService<TitleRequestMutationService>().ActionAsync(
                    actor, requestIds[4], new TitleRequestActionInput
                    {
                        Version = afterTimeout.RootElement.GetProperty("version").GetString(),
                        Action = "purchase",
                        EmailPurchaseReminder = true
                    }.ToCommand(), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Assert.IsTrue(cancelled, "Caller cancellation must propagate through the action boundary.");

            await using (var dispatchFactory = factory.WithWebHostBuilder(builder =>
                             builder.ConfigureServices(services =>
                             {
                                 services.RemoveAll<IEmailOutboxDispatcher>();
                                 services.AddSingleton<IEmailOutboxDispatcher, CanceledOutboxDispatcher>();
                             })))
            {
                using var dispatchClient = dispatchFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { HandleCookies = true });
                dispatchClient.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
                dispatchClient.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
                using var dispatchSession = await dispatchClient.GetAsync("/api/asap/staff/session");
                using var dispatchSessionJson = JsonDocument.Parse(await dispatchSession.Content.ReadAsStringAsync());
                dispatchClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery",
                    dispatchSessionJson.RootElement.GetProperty("antiforgeryToken").GetString());
                using var dispatch = await dispatchClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[4]}/action",
                    new { version = afterTimeout.RootElement.GetProperty("version").GetString(),
                        action = "purchase", emailPurchaseReminder = true });
                Assert.AreEqual(HttpStatusCode.OK, dispatch.StatusCode,
                    await dispatch.Content.ReadAsStringAsync());
                using var dispatchResult = JsonDocument.Parse(await dispatch.Content.ReadAsStringAsync());
                Assert.IsTrue(dispatchResult.RootElement.GetProperty("committed").GetBoolean());
                Assert.AreEqual("outstanding_purchase",
                    dispatchResult.RootElement.GetProperty("finalStatus").GetString());
                Assert.AreEqual("dispatch_failed",
                    dispatchResult.RootElement.GetProperty("notificationStatus").GetString());
                using var assign = await dispatchClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestIds[4]}/assign",
                    new { version = dispatchResult.RootElement.GetProperty("version").GetString(),
                        assigneeId = actorId });
                Assert.AreEqual(HttpStatusCode.OK, assign.StatusCode,
                    await assign.Content.ReadAsStringAsync());
                using var assignResult = JsonDocument.Parse(await assign.Content.ReadAsStringAsync());
                Assert.IsTrue(assignResult.RootElement.GetProperty("committed").GetBoolean());
                Assert.AreEqual("dispatch_failed",
                    assignResult.RootElement.GetProperty("notificationStatus").GetString());
                Assert.AreEqual(actorId.ToString(),
                    assignResult.RootElement.GetProperty("claimedByStaffUserId").GetString());
            }
            using var afterDispatch = await Detail(requestIds[4]);
            Assert.AreEqual("outstanding_purchase", afterDispatch.RootElement.GetProperty("status").GetString());

            using var datedDetail = await Detail(requestIds[5]);
            using var dateOmitted = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[5]}/action",
                new { version = datedDetail.RootElement.GetProperty("version").GetString(), action = "edit",
                    status = "suggestion", title = "Action title 5", format = collidingCode });
            Assert.AreEqual(HttpStatusCode.OK, dateOmitted.StatusCode, await dateOmitted.Content.ReadAsStringAsync());
            using var dateOmittedBody = JsonDocument.Parse(await dateOmitted.Content.ReadAsStringAsync());
            Assert.AreEqual("2026-01-02", dateOmittedBody.RootElement.GetProperty("exactPublicationDate").GetString());
            Assert.IsFalse(dateOmittedBody.RootElement.GetProperty("activity").EnumerateArray()
                .Any(item => item.GetProperty("eventType").GetString() == "request_edited"));
            using var dateCleared = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[5]}/action",
                new { version = dateOmittedBody.RootElement.GetProperty("version").GetString(), action = "edit",
                    status = "suggestion", exactPublicationDate = (string?)null });
            Assert.AreEqual(HttpStatusCode.OK, dateCleared.StatusCode, await dateCleared.Content.ReadAsStringAsync());
            using var dateClearedBody = JsonDocument.Parse(await dateCleared.Content.ReadAsStringAsync());
            Assert.AreEqual(JsonValueKind.Null,
                dateClearedBody.RootElement.GetProperty("exactPublicationDate").ValueKind);
            Assert.IsTrue(dateClearedBody.RootElement.GetProperty("activity").EnumerateArray()
                .Any(item => item.GetProperty("eventType").GetString() == "request_edited" &&
                             item.GetProperty("message").GetString()!.Contains("exact publication date")));

            using var bibVerified = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[5]}/action",
                new { version = dateClearedBody.RootElement.GetProperty("version").GetString(), action = "edit",
                    status = "suggestion", bibid = "9001", staffSelectedBibId = "9001" });
            Assert.AreEqual(HttpStatusCode.OK, bibVerified.StatusCode, await bibVerified.Content.ReadAsStringAsync());
            using var bibVerifiedBody = JsonDocument.Parse(await bibVerified.Content.ReadAsStringAsync());
            Assert.IsTrue(bibVerifiedBody.RootElement.GetProperty("bibidStaffVerified").GetBoolean());
            Assert.IsTrue(bibVerifiedBody.RootElement.GetProperty("activity").EnumerateArray()
                .Any(item => item.GetProperty("eventType").GetString() == "request_edited" &&
                             item.GetProperty("message").GetString()!.Contains("BIB verification")));

            using var defaultRejectDetail = await Detail(requestIds[5]);
            using var defaultReject = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[5]}/action",
                new { version = defaultRejectDetail.RootElement.GetProperty("version").GetString(), action = "reject" });
            Assert.AreEqual(HttpStatusCode.OK, defaultReject.StatusCode,
                await defaultReject.Content.ReadAsStringAsync());
            using var defaultRejectResult = JsonDocument.Parse(await defaultReject.Content.ReadAsStringAsync());
            Assert.AreEqual("closed", defaultRejectResult.RootElement.GetProperty("finalStatus").GetString());
            Assert.AreEqual("queued", defaultRejectResult.RootElement.GetProperty("notificationStatus").GetString());
            Assert.IsTrue(defaultRejectResult.RootElement.GetProperty("activity")
                .EnumerateArray().Any(item => item.GetProperty("eventType").GetString() == "rejection_template_default"));
            using var closedEdit = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestIds[5]}/action",
                new { version = defaultRejectResult.RootElement.GetProperty("version").GetString(),
                    action = "edit", status = "closed", notes = "Note after rejection" });
            Assert.AreEqual(HttpStatusCode.OK, closedEdit.StatusCode, await closedEdit.Content.ReadAsStringAsync());
            using var closedEditBody = JsonDocument.Parse(await closedEdit.Content.ReadAsStringAsync());
            Assert.AreEqual("rejected", closedEditBody.RootElement.GetProperty("closeReason").GetString());
        }
        finally
        {
            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            foreach (var id in requestIds)
            {
                var rejectionKey = $"rejection:{id}:%";
                var reminderKey = $"purchase-reminder:{id}:%";
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE {rejectionKey} OR [BusinessKey] LIKE {reminderKey}");
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = {id}");
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [asap].[TitleRequest] WHERE [Id] = {id}");
            }
            foreach (var id in templateIds)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [asap].[EmailTemplate] WHERE [Id] = {id}");
            }
            foreach (var id in formatIds)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [asap].[MaterialFormat] WHERE [Id] = {id}");
            }
        }
    }

    private sealed class TimeoutReadinessEmailSender : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Notification dependency timed out without caller cancellation.");

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CanceledOutboxDispatcher : IEmailOutboxDispatcher
    {
        public void Enqueue(long outboxId) => throw new OperationCanceledException("Queue aborted after commit.");
    }
}
