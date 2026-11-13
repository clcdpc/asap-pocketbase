using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("requested")]
    [DataRow("not_requested")]
    [DataRow("domain_suppressed")]
    public async Task StaffConfirmationActivityReflectsCommittedIntentAndSuppression(string scenario)
    {
        var patron = AddPostCommitCancellationPatron();
        if (scenario == "domain_suppressed")
        {
            patron.Provider.AddPatron(new PatronSnapshot(860_000_000, patron.Barcode, "patron@blocked.invalid", "Post", "Commit",
                1, "Adult", 101, 2, "Test Library", 101), [new PickupBranch(101, "Main Library")], 2);
        }
        var title = $"Email Activity confirmation {Guid.NewGuid():N}";
        var actor = await ReadConfiguredSuperAdminAsync();
        var dispatch = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(patron.Provider, dispatch);
        try
        {
            var accepted = await service.CreateForStaffAsync(actor, 2,
                ActivitySuggestion(patron.Barcode, title, scenario != "not_requested"), CancellationToken.None);
            Assert.AreEqual(scenario == "not_requested" ? "not_requested" : scenario == "requested" ? "queued" : "suppressed", accepted.NotificationStatus);
            var detail = await ReadActivityDetailAsync(actor, accepted.Id);
            Assert.IsTrue(detail.Activity.Any(item => item.Source == "request_event" && item.EventType == "created"));
            var mail = detail.Activity.Where(item => item.Email is not null).ToArray();
            var confirmationMail = mail.Where(item => item.Email!.MessageKind == "confirmation").ToArray();
            if (scenario == "not_requested")
            {
                Assert.AreEqual(0, confirmationMail.Length,
                    "The optional patron confirmation stays unrequested while staff notifications are still recorded.");
                return;
            }
            Assert.AreEqual(1, confirmationMail.Length);
            var evidence = confirmationMail.Single().Email!;
            Assert.AreEqual("confirmation", evidence.MessageKind);
            Assert.AreEqual(scenario == "requested" ? "queued" : "suppressed", evidence.Outcome);
            Assert.IsFalse(evidence.IsRecordedSummary);
            Assert.AreEqual(LibraryScope.ForLibrary(2).ToTransportValue(), evidence.OperationsScope);
            Assert.AreEqual(scenario == "requested" ? null : "Recipient domain not allowed", evidence.Reason);
            await using var context = await ActivityContexts().CreateDbContextAsync();
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item =>
                item.TitleRequestId == accepted.Id && item.MessageKind == "confirmation");
            Assert.AreEqual(outbox.Id.ToString(CultureInfo.InvariantCulture), evidence.OperationReference);
            Assert.AreEqual("confirmation", outbox.MessageKind);
            Assert.AreEqual(1, await context.EmailDeliveryEvents.CountAsync(item => item.EmailOutboxId == outbox.Id));
            Assert.AreEqual(scenario == "requested", dispatch.EnqueuedIds.Contains(outbox.Id));
            Assert.AreEqual("suggestion", detail.Status, "Suppression must not change successful request creation.");
            CollectionAssert.AreEqual(mail.Select(ActivityIdentity).ToArray(),
                (await ReadActivityDetailAsync(actor, accepted.Id)).Activity.Where(item => item.Email is not null).Select(ActivityIdentity).ToArray());
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, null);
        }
    }

    [TestMethod]
    public async Task NewPatronRequestNotificationsUseEligibleLibraryAndSystemRecipients()
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var originalSuperPreference = super.NewPatronRequestEmailEnabled;
        var foreignLibraryId = await ReserveSettingsCourseLibraryIdAsync();
        await UpsertTestOrganizationAsync(foreignLibraryId, "New request foreign library", "NRF");

        var submitter = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, 2);
        var subscribed = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        var optedOut = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        var inactive = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        var foreign = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, foreignLibraryId);
        var blockedDomain = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        var missingAddress = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        var invalidAddress = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        await SetNewRequestRecipientAsync(super.Id, enabled: true);
        await SetNewRequestRecipientAsync(submitter.Id, enabled: true,
            notificationEmail: $"new-request-submitter-{submitter.Id}@example.org", setNotificationEmail: true);
        await SetNewRequestRecipientAsync(subscribed.Id, enabled: true,
            notificationEmail: $"new-request-primary-{subscribed.Id}@example.org",
            setNotificationEmail: true, weeklyEmail: $"new-request-weekly-{subscribed.Id}@example.org");
        await SetNewRequestRecipientAsync(optedOut.Id, enabled: false);
        await SetNewRequestRecipientAsync(inactive.Id, enabled: true, active: false);
        await SetNewRequestRecipientAsync(foreign.Id, enabled: true);
        await SetNewRequestRecipientAsync(blockedDomain.Id, enabled: true,
            notificationEmail: $"new-request-{blockedDomain.Id}@blocked.invalid", setNotificationEmail: true);
        await SetNewRequestRecipientAsync(missingAddress.Id, enabled: true, setNotificationEmail: true);
        await SetNewRequestRecipientAsync(invalidAddress.Id, enabled: true,
            notificationEmail: "invalid-address", setNotificationEmail: true);

        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var eligibility = factory!.Services.GetRequiredService<StaffEligibilityService>();
        var submitterIdentity = await eligibility.EvaluateAsync(
            new StaffIdentityEvidence(submitter.Id, submitter.NormalizedUserPrincipalName!, tenantId),
            2,
            StaffRoleRequirement.Admin,
            requireParticipation: true,
            CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.Allowed, submitterIdentity.Outcome);
        var actor = submitterIdentity.Staff!;
        var patron = AddPostCommitCancellationPatron();
        var title = $"Scoped new request {Guid.NewGuid():N}";
        var dispatch = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(patron.Provider, dispatch);
        try
        {
            var accepted = await service.CreateForStaffAsync(actor, 2,
                ActivitySuggestion(patron.Barcode, title, confirmation: false), CancellationToken.None);
            Assert.AreEqual("not_requested", accepted.NotificationStatus,
                "Staff notifications do not change the optional patron-confirmation result.");

            await using var context = await ActivityContexts().CreateDbContextAsync();
            var rows = await context.EmailOutbox.AsNoTracking()
                .Where(item => item.TitleRequestId == accepted.Id && item.MessageKind == "new_patron_request")
                .ToDictionaryAsync(item => item.RecipientStaffUserId!.Value);
            var persistedTitle = await context.TitleRequests.AsNoTracking()
                .Where(item => item.Id == accepted.Id).Select(item => item.Title).SingleAsync();
            foreach (var recipient in new[] { super.Id, submitter.Id, subscribed.Id })
            {
                Assert.IsTrue(rows.ContainsKey(recipient), $"Eligible recipient {recipient} must receive a request-specific row.");
                var row = rows[recipient];
                Assert.AreEqual("new_patron_request", row.MessageKind);
                Assert.AreEqual("new_request", row.RecipientAddressKind);
                Assert.AreEqual("staff_authorization_sensitive", row.DeliveryClass);
                Assert.AreEqual(2, row.AuthorizationOrganizationId);
                Assert.AreEqual($"new-patron-request:{accepted.Id}:{recipient}", row.BusinessKey);
                Assert.AreEqual("pending", row.Status);
                Assert.AreEqual(1, await context.EmailDeliveryEvents.CountAsync(item => item.EmailOutboxId == row.Id));
                Assert.IsTrue(dispatch.EnqueuedIds.Contains(row.Id));
                StringAssert.Contains(row.BodyText!, persistedTitle);
                StringAssert.Contains(row.BodyText!, "Activity Author");
                StringAssert.Contains(row.BodyText!, "Test Library");
                Assert.IsFalse(row.BodyText!.Contains("Post Commit", StringComparison.Ordinal));
                Assert.IsFalse(row.BodyText.Contains(patron.Barcode, StringComparison.Ordinal));
                Assert.IsFalse(row.BodyText.Contains("patron@example.org", StringComparison.Ordinal));
            }

            Assert.AreEqual($"new-request-primary-{subscribed.Id}@example.org", rows[subscribed.Id].ToAddress,
                "New-request mail uses the primary address even when a weekly address exists.");
            Assert.IsFalse(rows.ContainsKey(optedOut.Id));
            Assert.IsFalse(rows.ContainsKey(inactive.Id));
            Assert.IsFalse(rows.ContainsKey(foreign.Id));

            foreach (var suppressedRecipient in new[] { blockedDomain.Id, missingAddress.Id, invalidAddress.Id })
            {
                Assert.IsTrue(rows.ContainsKey(suppressedRecipient), "Eligible but undeliverable recipients keep suppression evidence.");
                Assert.AreEqual("suppressed", rows[suppressedRecipient].Status);
                Assert.IsFalse(dispatch.EnqueuedIds.Contains(rows[suppressedRecipient].Id));
                Assert.AreEqual(1, await context.EmailDeliveryEvents.CountAsync(item => item.EmailOutboxId == rows[suppressedRecipient].Id));
            }
            Assert.AreEqual("recipient_domain_not_allowed", rows[blockedDomain.Id].SuppressionReason);
            Assert.AreEqual("recipient_missing", rows[missingAddress.Id].SuppressionReason);
            Assert.AreEqual("recipient_invalid", rows[invalidAddress.Id].SuppressionReason);
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, null);
            foreach (var row in new[] { submitter, subscribed, optedOut, inactive, foreign, blockedDomain, missingAddress, invalidAddress })
            {
                await SetNewRequestRecipientAsync(row.Id, active: false);
            }
            await SetNewRequestRecipientAsync(super.Id, enabled: originalSuperPreference);
        }
    }

    [TestMethod]
    [DataRow("public")]
    [DataRow("staff")]
    public async Task NewPatronRequestNotificationEvidenceRollsBackWithEitherSubmissionPath(string caller)
    {
        var patron = AddPostCommitCancellationPatron();
        var session = caller == "public" ? await IssueTestPatronSessionAsync(patron.Barcode) : null;
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = $"New request evidence rollback {caller} {Guid.NewGuid():N}";
        var trigger = $"NewRequestEvidenceFailure_{Guid.NewGuid():N}";
        var dispatch = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(patron.Provider, dispatch);
        int requestCountBefore;
        int outboxCountBefore;
        int evidenceCountBefore;
        await using (var context = await ActivityContexts().CreateDbContextAsync())
        {
            requestCountBefore = await context.TitleRequests.CountAsync();
            outboxCountBefore = await context.EmailOutbox.CountAsync();
            evidenceCountBefore = await context.EmailDeliveryEvents.CountAsync();
        }

        try
        {
            await ExecuteNonQueryAsync($"""
                CREATE TRIGGER [asap].[{trigger}] ON [asap].[EmailDeliveryEvent] AFTER INSERT AS
                IF EXISTS
                (
                    SELECT 1 FROM inserted AS event
                    INNER JOIN [asap].[EmailOutbox] AS outbox ON outbox.[Id] = event.[EmailOutboxId]
                    WHERE outbox.[MessageKind] = N'new_patron_request' AND event.[LocalEventKey] = N'initial'
                )
                    THROW 51247, 'Injected new-request outbox evidence failure.', 1;
                """);

            if (caller == "public")
            {
                await Assert.ThrowsAsync<SqlException>(() => service.CreateAsync(session!, Suggestion(title), CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<SqlException>(() => service.CreateForStaffAsync(actor, 2,
                    ActivitySuggestion(patron.Barcode, title, confirmation: true), CancellationToken.None));
            }

            await using var verify = await ActivityContexts().CreateDbContextAsync();
            Assert.IsFalse(await verify.TitleRequests.AnyAsync(item => item.Title == title));
            Assert.AreEqual(requestCountBefore, await verify.TitleRequests.CountAsync());
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.CountAsync());
            Assert.AreEqual(evidenceCountBefore, await verify.EmailDeliveryEvents.CountAsync());
            Assert.AreEqual(0, dispatch.EnqueuedIds.Count,
                "No provider dispatch may happen before the failed submission transaction commits.");
        }
        finally
        {
            await ExecuteNonQueryAsync($"DROP TRIGGER IF EXISTS [asap].[{trigger}];");
            await DeletePostCommitSuggestionAsync(title, session?.Id);
        }
    }

    [TestMethod]
    public async Task ConcurrentPublicSubmissionsCreateOneRequestAndOneNotificationPerRecipient()
    {
        var patron = AddPostCommitCancellationPatron();
        var session = await IssueTestPatronSessionAsync(patron.Barcode);
        var title = $"Concurrent new request {Guid.NewGuid():N}";
        var dispatch = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(patron.Provider, dispatch);

        async Task<(PatronSuggestionResult? Result, PatronFlowException? Failure)> SubmitAsync()
        {
            try
            {
                return (await service.CreateAsync(session, Suggestion(title), CancellationToken.None), null);
            }
            catch (PatronFlowException exception)
            {
                return (null, exception);
            }
        }

        try
        {
            var attempts = await Task.WhenAll(Task.Run(SubmitAsync), Task.Run(SubmitAsync));
            Assert.AreEqual(1, attempts.Count(item => item.Result is not null));
            var rejected = attempts.Single(item => item.Failure is not null).Failure!;
            Assert.AreEqual(409, rejected.StatusCode);

            await using var context = await ActivityContexts().CreateDbContextAsync();
            var requests = await context.TitleRequests.AsNoTracking().Where(item => item.Title == title).ToArrayAsync();
            Assert.AreEqual(1, requests.Length);
            var notifications = await context.EmailOutbox.AsNoTracking().Where(item =>
                item.TitleRequestId == requests[0].Id && item.MessageKind == "new_patron_request").ToArrayAsync();
            Assert.IsGreaterThanOrEqualTo(1, notifications.Length);
            Assert.AreEqual(notifications.Length, notifications.Select(item => item.RecipientStaffUserId).Distinct().Count());
            Assert.AreEqual(notifications.Length, notifications.Select(item => item.BusinessKey).Distinct().Count());
            Assert.IsTrue(notifications.All(item => item.BusinessKey ==
                $"new-patron-request:{requests[0].Id}:{item.RecipientStaffUserId}"));
            Assert.AreEqual(1, await context.EmailOutbox.CountAsync(item =>
                item.TitleRequestId == requests[0].Id && item.MessageKind == "confirmation"));
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, session.Id);
        }
    }

    [TestMethod]
    public async Task NewPatronRequestSnapshotsLibraryTemplateAndLeavesUnavailableLinkEmpty()
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var originalSuperPreference = super.NewPatronRequestEmailEnabled;
        await SetNewRequestRecipientAsync(super.Id, enabled: true);
        super = await ReadConfiguredSuperAdminAsync();
        var submitterRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, 2);
        await using (var renameSubmitter = await ActivityContexts().CreateDbContextAsync())
        {
            var user = await renameSubmitter.StaffUsers.SingleAsync(item => item.Id == submitterRecord.Id);
            user.DisplayName = "Request Recipient";
            await renameSubmitter.SaveChangesAsync();
        }
        var submitter = await ReadCorrectiveStaffAsync(submitterRecord);
        var patron = AddPostCommitCancellationPatron();
        var templateKey = StaffEmailTemplateCatalog.NewPatronRequest;
        const string testSubject = "Request {{title}} for {{recipientName}}";
        const string testBody = "{{recipientName}}: {{title}} by {{author}} in {{format}} at {{libraryName}} #{{requestId}} {{requestUrl}}";
        var titles = new List<string>();
        long? libraryTemplateId;
        string? originalStaffUrl;
        (string? Subject, string? Body, bool Hidden, bool Custom, string? DisplayName, int SortOrder)? originalLibraryTemplate = null;
        await using (var setup = await ActivityContexts().CreateDbContextAsync())
        {
            var systemTemplate = await setup.EmailTemplates.SingleAsync(item =>
                item.OrganizationId == 1 && item.TemplateKey == templateKey);
            var libraryTemplate = await setup.EmailTemplates.SingleOrDefaultAsync(item =>
                item.OrganizationId == 2 && item.SourceTemplateId == systemTemplate.Id);
            if (libraryTemplate is not null)
            {
                originalLibraryTemplate = (libraryTemplate.SubjectTemplate, libraryTemplate.BodyTemplate,
                    libraryTemplate.IsHidden, libraryTemplate.IsCustom, libraryTemplate.DisplayName, libraryTemplate.SortOrder);
                libraryTemplate.SubjectTemplate = testSubject;
                libraryTemplate.BodyTemplate = testBody;
                libraryTemplate.IsHidden = false;
            }
            else
            {
                libraryTemplate = new EmailTemplate
                {
                    OrganizationId = 2,
                    SourceTemplateId = systemTemplate.Id,
                    TemplateKey = templateKey,
                    SubjectTemplate = testSubject,
                    BodyTemplate = testBody,
                    IsHidden = false,
                    IsCustom = false,
                    SortOrder = systemTemplate.SortOrder
                };
                setup.EmailTemplates.Add(libraryTemplate);
            }
            await setup.SaveChangesAsync();
            libraryTemplateId = libraryTemplate.Id;
            originalStaffUrl = await setup.SystemSettings.Where(item => item.OrganizationId == 1)
                .Select(item => item.StaffApplicationUrl).SingleAsync();
        }

        try
        {
            foreach (var (staffUrl, hasRequestLink) in new (string? StaffUrl, bool HasRequestLink)[]
                     {
                         (null, false),
                         ("javascript:alert(1)", false),
                         ("https://trusted.example.org/staff/", true)
                     })
            {
                await using (var updateSettings = await ActivityContexts().CreateDbContextAsync())
                {
                    var settings = await updateSettings.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
                    settings.StaffApplicationUrl = staffUrl;
                    await updateSettings.SaveChangesAsync();
                }

                var title = $"Request <b>& snapshot {Guid.NewGuid():N}";
                titles.Add(title);
                var dispatch = new RecordingOutboxDispatcher();
                var accepted = await CreatePostCommitSuggestionService(patron.Provider, dispatch).CreateForStaffAsync(
                    submitter, 2, ActivitySuggestion(patron.Barcode, title, confirmation: false), CancellationToken.None);
                Assert.AreEqual("not_requested", accepted.NotificationStatus);
                await using var verify = await ActivityContexts().CreateDbContextAsync();
                var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
                    item.TitleRequestId == accepted.Id && item.MessageKind == "new_patron_request" &&
                    item.RecipientStaffUserId == submitter.Id);
                var persistedTitle = await verify.TitleRequests.AsNoTracking()
                    .Where(item => item.Id == accepted.Id).Select(item => item.Title).SingleAsync();
                var bodyText = outbox.BodyText!;
                var bodyHtml = outbox.BodyHtml!;
                var requestUrl = $"https://trusted.example.org/staff/?request={accepted.Id.ToString(CultureInfo.InvariantCulture)}&stage=suggestion&scope=2";
                Assert.AreEqual("pending", outbox.Status,
                    "An unavailable absolute staff link cannot suppress an otherwise deliverable request message.");
                Assert.AreEqual("new_request", outbox.RecipientAddressKind);
                Assert.AreEqual($"new-patron-request:{accepted.Id}:{submitter.Id}", outbox.BusinessKey);
                Assert.AreEqual($"Request {persistedTitle} for {submitter.DisplayName}", outbox.Subject);
                StringAssert.Contains(bodyText, persistedTitle);
                StringAssert.Contains(bodyText, "Activity Author");
                StringAssert.Contains(bodyText, "Test Library");
                StringAssert.Contains(bodyText, accepted.Id.ToString(CultureInfo.InvariantCulture));
                if (hasRequestLink)
                {
                    StringAssert.Contains(bodyText, requestUrl);
                    StringAssert.Contains(bodyHtml, "request=" + accepted.Id.ToString(CultureInfo.InvariantCulture));
                    StringAssert.Contains(bodyHtml, "scope=2");
                }
                else
                {
                    Assert.IsFalse(bodyText.Contains("https://", StringComparison.OrdinalIgnoreCase));
                }
                Assert.IsFalse(bodyText.Contains("{{requestUrl}}", StringComparison.Ordinal));
                StringAssert.Contains(bodyHtml, System.Text.Encodings.Web.HtmlEncoder.Default.Encode(persistedTitle));
                Assert.IsFalse(bodyHtml.Contains(persistedTitle, StringComparison.Ordinal));
                Assert.IsFalse(bodyHtml.Contains("<b>& snapshot", StringComparison.Ordinal));
                Assert.IsFalse(bodyText.Contains("Post Commit", StringComparison.Ordinal));
                Assert.IsFalse(bodyText.Contains(patron.Barcode, StringComparison.Ordinal));
                Assert.IsTrue(dispatch.EnqueuedIds.Contains(outbox.Id));
                var superOutbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item =>
                    item.TitleRequestId == accepted.Id && item.MessageKind == "new_patron_request" &&
                    item.RecipientStaffUserId == super.Id);
                Assert.AreEqual("pending", superOutbox.Status);
                var superRecipientName = string.IsNullOrWhiteSpace(super.DisplayName) ? "Staff member" : super.DisplayName;
                Assert.AreEqual($"Request {persistedTitle} for {superRecipientName}", superOutbox.Subject,
                    "System super-admin notifications use the request-owning library's effective template.");
                StringAssert.Contains(superOutbox.BodyText!, super.DisplayName!);

                await using (var edit = await ActivityContexts().CreateDbContextAsync())
                {
                    var template = await edit.EmailTemplates.SingleAsync(item => item.Id == libraryTemplateId);
                    template.SubjectTemplate = "Edited after request";
                    template.BodyTemplate = "Edited after request";
                    await edit.SaveChangesAsync();
                }
                var snapshot = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outbox.Id);
                Assert.AreEqual(outbox.Subject, snapshot.Subject);
                Assert.AreEqual(outbox.BodyText, snapshot.BodyText);
                Assert.AreEqual(outbox.BodyHtml, snapshot.BodyHtml,
                    "Template edits cannot rewrite already accepted outbox content.");

                await using (var restoreTestTemplate = await ActivityContexts().CreateDbContextAsync())
                {
                    var template = await restoreTestTemplate.EmailTemplates.SingleAsync(item => item.Id == libraryTemplateId);
                    template.SubjectTemplate = testSubject;
                    template.BodyTemplate = testBody;
                    await restoreTestTemplate.SaveChangesAsync();
                }
            }
        }
        finally
        {
            foreach (var title in titles)
            {
                await DeletePostCommitSuggestionAsync(title, null);
            }
            await using var restore = await ActivityContexts().CreateDbContextAsync();
            var settings = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            settings.StaffApplicationUrl = originalStaffUrl;
            if (originalLibraryTemplate is { } prior)
            {
                var template = await restore.EmailTemplates.SingleAsync(item => item.Id == libraryTemplateId);
                template.SubjectTemplate = prior.Subject;
                template.BodyTemplate = prior.Body;
                template.IsHidden = prior.Hidden;
                template.IsCustom = prior.Custom;
                template.DisplayName = prior.DisplayName;
                template.SortOrder = prior.SortOrder;
            }
            else if (libraryTemplateId.HasValue)
            {
                var template = await restore.EmailTemplates.SingleAsync(item => item.Id == libraryTemplateId);
                restore.EmailTemplates.Remove(template);
            }
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(submitterRecord.Id);
            await SetNewRequestRecipientAsync(super.Id, enabled: originalSuperPreference);
        }
    }

    [TestMethod]
    [DataRow("optout")]
    [DataRow("identity")]
    [DataRow("inactive")]
    [DataRow("access")]
    public async Task NewPatronRequestDeliveryRechecksCurrentRecipientAuthority(string change)
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var recipientRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
        await SetNewRequestRecipientAsync(recipientRecord.Id, enabled: true,
            notificationEmail: $"new-request-delivery-{recipientRecord.Id}@example.org", setNotificationEmail: true);
        var recipient = await ReadCorrectiveStaffAsync(recipientRecord);
        var foreignLibraryId = 0;
        if (change == "access")
        {
            foreignLibraryId = await ReserveSettingsCourseLibraryIdAsync();
            await UpsertTestOrganizationAsync(foreignLibraryId, "New request revoked library", "NRV");
        }

        var patron = AddPostCommitCancellationPatron();
        var title = $"New request delivery authority {change} {Guid.NewGuid():N}";
        var dispatch = new RecordingOutboxDispatcher();
        var actorRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, 2);
        var actor = await ReadCorrectiveStaffAsync(actorRecord);
        SeededSensitiveOutbox? unrelatedNotification = null;
        try
        {
            var accepted = await CreatePostCommitSuggestionService(patron.Provider, dispatch).CreateForStaffAsync(
                actor, 2, ActivitySuggestion(patron.Barcode, title, confirmation: false), CancellationToken.None);
            await using var context = await ActivityContexts().CreateDbContextAsync();
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item =>
                item.TitleRequestId == accepted.Id && item.RecipientStaffUserId == recipient.Id &&
                item.MessageKind == "new_patron_request");
            switch (change)
            {
                case "optout":
                    await SetNewRequestRecipientAsync(recipient.Id, enabled: false);
                    break;
                case "identity":
                    var changedEmail = $"changed-{recipient.Id}@example.org";
                    await ExecuteNonQueryAsync(
                        "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = @email, [NormalizedUserPrincipalName] = UPPER(@email) WHERE [Id] = @id;",
                        ("@email", changedEmail), ("@id", recipient.Id));
                    break;
                case "inactive":
                    await SetNewRequestRecipientAsync(recipient.Id, active: false);
                    break;
                case "access":
                    await ExecuteNonQueryAsync(
                        "UPDATE [asap].[StaffUser] SET [OrganizationId] = @organizationId WHERE [Id] = @id;",
                        ("@organizationId", foreignLibraryId), ("@id", recipient.Id));
                    break;
            }

            var sender = new ActivityEmailSender();
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(outbox.Id, CancellationToken.None);
            Assert.AreEqual(0, sender.SendCount);
            var state = await ReadEmailOutcomeDispatchSnapshotAsync(outbox.Id);
            Assert.AreEqual("suppressed", state.Status);
            await using (var verify = await ActivityContexts().CreateDbContextAsync())
            {
                Assert.AreEqual("recipient_authorization_changed", await verify.EmailOutbox.AsNoTracking()
                    .Where(item => item.Id == outbox.Id).Select(item => item.SuppressionReason).SingleAsync());
            }
            var events = await ActivityEventsAsync(outbox.Id);
            Assert.AreEqual(1, events.Count(item => item.EventType == "suppressed"));
            Assert.IsFalse(events.Any(item => item.EventType is "send_intent" or "provider_accepted"));

            if (change == "optout")
            {
                unrelatedNotification = await SeedSensitiveOutboxAsync($"new-request-independent-{Guid.NewGuid():N}",
                    addressKind: "notification_email");
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[StaffUser] SET [NewPatronRequestEmailEnabled] = 0 WHERE [Id] = @id;",
                    ("@id", unrelatedNotification.StaffUserId));
                var unrelatedSender = new ActivityEmailSender();
                await CreateEmailOutboxJobs(unrelatedSender, EmailOutboxRuntimeOptions.Default)
                    .DeliverAsync(unrelatedNotification.OutboxId, CancellationToken.None);
                Assert.AreEqual(1, unrelatedSender.SendCount,
                    "The new-request preference cannot suppress existing notification-email kinds.");
                Assert.AreEqual("sent", (await ReadEmailOutcomeDispatchSnapshotAsync(unrelatedNotification.OutboxId)).Status);
            }
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, null);
            await SetNewRequestRecipientAsync(recipient.Id, active: false);
            await DeactivateCorrectiveStaffAsync(actorRecord.Id);
            if (unrelatedNotification is not null)
            {
                await DeleteEmailOutcomeFixtureAsync(unrelatedNotification);
            }
        }
    }

    [TestMethod]
    public async Task StaffRequestReadinessFailureSuppressesOnlyNewNoticeAndKeepsAcceptance()
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var actorRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, 2);
        var actor = await ReadCorrectiveStaffAsync(actorRecord);
        var patron = AddPostCommitCancellationPatron();
        var title = $"New request readiness failure {Guid.NewGuid():N}";
        var sender = new ActivityEmailSender
        {
            OnReadiness = _ => Task.FromException<EmailTransportReadiness>(
                new InvalidOperationException("Deterministic readiness probe failure."))
        };
        try
        {
            var result = await CreatePostCommitSuggestionService(patron.Provider, new RecordingOutboxDispatcher(), sender)
                .CreateForStaffAsync(actor, 2, ActivitySuggestion(patron.Barcode, title, confirmation: false), CancellationToken.None);
            Assert.AreEqual("not_requested", result.NotificationStatus,
                "The existing staff-assisted no-confirmation result stays unchanged.");
            await using var context = await ActivityContexts().CreateDbContextAsync();
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item =>
                item.TitleRequestId == result.Id && item.RecipientStaffUserId == actor.Id &&
                item.MessageKind == "new_patron_request");
            Assert.AreEqual("suppressed", outbox.Status);
            Assert.AreEqual("mail_readiness_unavailable", outbox.SuppressionReason);
            Assert.AreEqual(1, await context.EmailDeliveryEvents.CountAsync(item => item.EmailOutboxId == outbox.Id));
            Assert.AreEqual(0, sender.SendCount);
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, null);
            await DeactivateCorrectiveStaffAsync(actorRecord.Id);
        }
    }

    [TestMethod]
    public async Task NewPatronRequestOutboxRetriesReadinessFailureThroughExistingDeliveryPolicy()
    {
        var super = await ReadConfiguredSuperAdminAsync();
        var originalPreference = super.NewPatronRequestEmailEnabled;
        await SetNewRequestRecipientAsync(super.Id, enabled: true);
        super = await ReadConfiguredSuperAdminAsync();
        var patron = AddPostCommitCancellationPatron();
        var title = $"New request retry {Guid.NewGuid():N}";
        var sender = new ActivityEmailSender();
        try
        {
            var accepted = await CreatePostCommitSuggestionService(patron.Provider,
                new RecordingOutboxDispatcher(), sender).CreateForStaffAsync(
                super, 2, ActivitySuggestion(patron.Barcode, title, confirmation: false), CancellationToken.None);
            Assert.AreEqual("not_requested", accepted.NotificationStatus);
            await using var context = await ActivityContexts().CreateDbContextAsync();
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item =>
                item.TitleRequestId == accepted.Id && item.RecipientStaffUserId == super.Id &&
                item.MessageKind == "new_patron_request");
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            sender.Readiness = EmailTransportReadiness.NotConfigured;
            await jobs.DeliverAsync(outbox.Id, CancellationToken.None);
            Assert.AreEqual("failed", (await ReadEmailOutcomeDispatchSnapshotAsync(outbox.Id)).Status);
            Assert.AreEqual(0, sender.SendCount);

            var failed = await ReadEmailOutcomeDispatchSnapshotAsync(outbox.Id);
            var retry = await factory!.Services.GetRequiredService<EmailOperationsService>().RetryAsync(
                super, outbox.Id, StaffVersion.Encode(failed.RowVersion), CancellationToken.None);
            Assert.AreEqual("queued", retry.Code);
            sender.Readiness = EmailTransportReadiness.Configured;
            await jobs.DeliverAsync(outbox.Id, CancellationToken.None);
            Assert.AreEqual(1, sender.SendCount);
            Assert.AreEqual("sent", (await ReadEmailOutcomeDispatchSnapshotAsync(outbox.Id)).Status);
            var events = await ActivityEventsAsync(outbox.Id);
            Assert.AreEqual(1, events.Count(item => item.EventType == "send_intent"));
            Assert.AreEqual(1, events.Count(item => item.EventType == "provider_accepted"));
            Assert.AreEqual(events.Count, events.Select(item => item.LocalEventKey).Distinct().Count());
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, null);
            await SetNewRequestRecipientAsync(super.Id, enabled: originalPreference);
        }
    }

    private async Task SetNewRequestRecipientAsync(
        long staffUserId,
        bool? enabled = null,
        bool? active = null,
        string? notificationEmail = null,
        bool setNotificationEmail = false,
        string? weeklyEmail = null)
    {
        await using var context = await ActivityContexts().CreateDbContextAsync();
        var row = await context.StaffUsers.SingleAsync(item => item.Id == staffUserId);
        if (enabled.HasValue)
        {
            row.NewPatronRequestEmailEnabled = enabled.Value;
        }
        if (active.HasValue)
        {
            row.IsActive = active.Value;
        }
        if (setNotificationEmail)
        {
            row.NotificationEmail = notificationEmail;
        }
        if (weeklyEmail is not null)
        {
            row.WeeklyActionSummaryEnabled = true;
            row.WeeklyActionSummaryEmail = weeklyEmail;
        }
        await context.SaveChangesAsync();
    }

    [TestMethod]
    [DataRow("postmark")]
    [DataRow("file")]
    public async Task RequestEmailActivityPreservesFailureRetryAndSuccessAcrossWorkers(string mode)
    {
        var confirmation = await CreateActivityConfirmationAsync();
        var actor = await ReadConfiguredSuperAdminAsync();
        var sender = new ActivityEmailSender { Readiness = EmailTransportReadiness.NotConfigured };
        try
        {
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(confirmation.OutboxId, CancellationToken.None);
            Assert.AreEqual(0, sender.SendCount);
            var failed = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
            Assert.AreEqual("failed", failed.Status);
            var before = await ActivityEventsAsync(confirmation.OutboxId);
            CollectionAssert.AreEqual(new[] { "queued", "claimed", "failed_no_send" }, before.Select(item => item.EventType).ToArray());
            var operations = factory!.Services.GetRequiredService<EmailOperationsService>();
            var retryTasks = Enumerable.Range(0, 2).Select(_ => operations.RetryAsync(actor,
                confirmation.OutboxId, StaffVersion.Encode(failed.RowVersion), CancellationToken.None)).ToArray();
            await Task.WhenAll(retryTasks.Cast<Task>().Append(CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).SweepAsync(CancellationToken.None)));
            var retries = retryTasks.Select(task => task.Result).ToArray();
            Assert.AreEqual(1, retries.Count(item => item.Code == "queued"));
            Assert.IsTrue(retries.Any(item => item.Code is "stale_version" or "email_not_retryable"));
            sender.Readiness = mode == "file" ? EmailTransportReadiness.LocalCapture : EmailTransportReadiness.Configured;
            sender.Result = new EmailSendResult(mode == "file" ? "file:activity-capture" : "activity-accepted") { DeliveryMode = mode };
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(confirmation.OutboxId, CancellationToken.None)));
            Assert.AreEqual(1, sender.SendCount);
            var events = await ActivityEventsAsync(confirmation.OutboxId);
            CollectionAssert.AreEqual(new[] { "queued", "claimed", "failed_no_send", "retry_queued", "claimed", "send_intent", mode == "file" ? "captured" : "provider_accepted" },
                events.Select(item => item.EventType).ToArray());
            Assert.IsTrue(before.All(item => events.Any(entry => entry.Id == item.Id && entry.ReceivedUtc == item.ReceivedUtc && entry.EventType == item.EventType)));
            Assert.AreEqual(events.Count, events.Select(item => item.LocalEventKey).Distinct().Count());
            Assert.IsTrue(events.All(item => item.ProviderEventId is null));
            var detail = await ReadActivityDetailAsync(actor, confirmation.RequestId);
            var activity = detail.Activity.Where(item => item.Email?.OperationReference == confirmation.OutboxId.ToString(CultureInfo.InvariantCulture)).ToArray();
            Assert.AreEqual(events.Count, activity.Length);
            Assert.IsFalse(activity.Any(item => item.Email!.IsRecordedSummary));
            Assert.IsTrue(activity.Any(item => item.Email!.Outcome == "failed_no_send"));
            Assert.IsFalse(activity.Any(item => item.Email!.OutcomeLabel.Contains("delivered", StringComparison.OrdinalIgnoreCase)));
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).SweepAsync(CancellationToken.None);
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(confirmation.OutboxId, CancellationToken.None);
            Assert.AreEqual(1, sender.SendCount);
            CollectionAssert.AreEqual(events.Select(item => item.Id).ToArray(), (await ActivityEventsAsync(confirmation.OutboxId)).Select(item => item.Id).ToArray());
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
        }
    }

    [TestMethod]
    [DataRow("timeout")]
    [DataRow("lost_response")]
    [DataRow("accepted_sql_failure")]
    [DataRow("accepted_cancelled")]
    public async Task RequestEmailActivityPreservesProviderUncertaintyAndKnownAcceptance(string boundary)
    {
        var confirmation = await CreateActivityConfirmationAsync();
        using var cancellation = new CancellationTokenSource();
        var trigger = $"ActivityEvidenceFailure_{Guid.NewGuid():N}";
        var sender = new ActivityEmailSender();
        sender.OnSend = async (_, _) =>
        {
            if (boundary == "accepted_cancelled")
            {
                cancellation.Cancel();
            }
            if (boundary == "accepted_sql_failure")
            {
                await ExecuteNonQueryAsync($"""
                    CREATE TRIGGER [asap].[{trigger}] ON [asap].[EmailDeliveryEvent] AFTER INSERT AS
                    IF EXISTS (SELECT 1 FROM inserted WHERE [EmailOutboxId] = {confirmation.OutboxId} AND [EventType] = N'provider_accepted')
                        THROW 51249, 'Injected accepted-history bookkeeping failure.', 1;
                    """);
            }
            return boundary switch
            {
                "timeout" => throw new OperationCanceledException("Deterministic provider timeout."),
                "lost_response" => throw new EmailOperationalException("Deterministic lost accepted response."),
                _ => new EmailSendResult("activity-provider-accepted")
            };
        };
        try
        {
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            if (boundary == "accepted_sql_failure")
            {
                await Assert.ThrowsAsync<SqlException>(() => jobs.DeliverAsync(confirmation.OutboxId, cancellation.Token));
                await ExecuteNonQueryAsync($"DROP TRIGGER IF EXISTS [asap].[{trigger}];");
            }
            else
            {
                await jobs.DeliverAsync(confirmation.OutboxId, cancellation.Token);
            }
            var state = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
            Assert.AreEqual(boundary == "accepted_cancelled" ? "sent" : "sending", state.Status);
            var events = await ActivityEventsAsync(confirmation.OutboxId);
            Assert.AreEqual(1, events.Count(item => item.EventType == "send_intent"));
            Assert.AreEqual(boundary == "accepted_cancelled" ? 1 : 0, events.Count(item => item.EventType == "provider_accepted"));
            if (boundary != "accepted_cancelled")
            {
                Assert.IsNull(state.ProviderMessageId);
                await SetLeaseExpiryAsync(confirmation.OutboxId, expired: true);
                await Task.WhenAll(jobs.SweepAsync(CancellationToken.None), jobs.SweepAsync(CancellationToken.None));
                var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
                Assert.AreEqual("failed", quarantined.Status);
                Assert.IsNull(quarantined.NextAttemptUtc);
                Assert.AreEqual("email_not_retryable", (await factory!.Services.GetRequiredService<EmailOperationsService>().RetryAsync(
                    await ReadConfiguredSuperAdminAsync(), confirmation.OutboxId, StaffVersion.Encode(quarantined.RowVersion), CancellationToken.None)).Code);
                Assert.AreEqual(1, (await ActivityEventsAsync(confirmation.OutboxId)).Count(item => item.EventType == "lease_expired_ambiguous"));
            }
            await jobs.DeliverAsync(confirmation.OutboxId, CancellationToken.None);
            await jobs.SweepAsync(CancellationToken.None);
            Assert.AreEqual(1, sender.SendCount, "Unknown or recorded acceptance must never trigger another provider send.");
            var detail = await ReadActivityDetailAsync(await ReadConfiguredSuperAdminAsync(), confirmation.RequestId);
            Assert.IsFalse(detail.Activity.Any(item => item.Email?.Outcome == "delivered"));
        }
        finally
        {
            await ExecuteNonQueryAsync($"DROP TRIGGER IF EXISTS [asap].[{trigger}];");
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
        }
    }

    [TestMethod]
    public async Task RequestEmailActivityKeysSurviveReusedAttemptCountsAndRejectStaleWorkerSuccess()
    {
        // SQL datetime would round this business clock forward. Fixture due
        // times must retain the worker's datetime2 clock and be truly eligible.
        timeProvider!.SetUtcNow(new DateTimeOffset(2030, 3, 17, 6, 30, 0, TimeSpan.Zero).AddMilliseconds(2));
        var confirmation = await CreateActivityConfirmationAsync();
        var sender = new ActivityEmailSender { ReadinessFailure = true };
        try
        {
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            for (var index = 0; index < 2; index++)
            {
                await jobs.DeliverAsync(confirmation.OutboxId, CancellationToken.None);
                var released = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
                Assert.AreEqual(0, released.AttemptCount);
                Assert.AreEqual("pending", released.Status);
                Assert.AreEqual("mail_readiness_unavailable", released.LastErrorCode);
                var releasedEvents = await ActivityEventsAsync(confirmation.OutboxId);
                Assert.AreEqual(index + 1, releasedEvents.Count(item => item.EventType == "claimed"));
                Assert.AreEqual(index + 1, releasedEvents.Count(item => item.EventType == "pre_send_released"));
                await MakeOutboxDueAsync(confirmation.OutboxId);
                var due = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
                Assert.IsNotNull(due.NextAttemptUtc);
                Assert.IsTrue(due.NextAttemptUtc.Value <= timeProvider.GetUtcNow().UtcDateTime,
                    $"Outbox due time {due.NextAttemptUtc:O} must precede the worker clock {timeProvider.GetUtcNow():O}.");
            }
            sender.ReadinessFailure = false;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sender.OnSend = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new EmailSendResult("accepted-by-stale-worker");
            };
            var sending = jobs.DeliverAsync(confirmation.OutboxId, CancellationToken.None);
            try
            {
                var reached = await Task.WhenAny(entered.Task, sending).WaitAsync(TimeSpan.FromSeconds(15));
                if (reached == sending && !entered.Task.IsCompleted)
                {
                    var state = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
                    var history = string.Join(", ", (await ActivityEventsAsync(confirmation.OutboxId)).Select(item => item.EventType));
                    Assert.Fail($"Delivery completed before the provider barrier: task={sending.Status}; " +
                        $"status={state.Status}; attempts={state.AttemptCount}; error={state.LastErrorCode}; " +
                        $"nextAttempt={state.NextAttemptUtc:O}; clock={timeProvider.GetUtcNow():O}; " +
                        $"events=[{history}]; fault={sending.Exception}");
                }
                await entered.Task;
                await SetLeaseExpiryAsync(confirmation.OutboxId, expired: true);
                await jobs.SweepAsync(CancellationToken.None);
            }
            finally
            {
                release.TrySetResult();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => sending);
            var events = await ActivityEventsAsync(confirmation.OutboxId);
            Assert.AreEqual(3, events.Count(item => item.EventType == "claimed"));
            Assert.AreEqual(2, events.Count(item => item.EventType == "pre_send_released"));
            Assert.AreEqual(1, events.Count(item => item.EventType == "send_intent"));
            Assert.AreEqual(0, events.Count(item => item.EventType == "provider_accepted"));
            Assert.AreEqual(events.Count, events.Select(item => item.LocalEventKey).Distinct().Count());
            Assert.AreEqual(1, sender.SendCount);
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
        }
    }

    [TestMethod]
    public async Task RequestEmailInitialEvidenceFailureRollsBackRequestAndOutboxTogether()
    {
        var patron = AddPostCommitCancellationPatron();
        var title = $"Email Activity rollback {Guid.NewGuid():N}";
        var trigger = $"ActivityInitialFailure_{Guid.NewGuid():N}";
        var dispatch = new RecordingOutboxDispatcher();
        try
        {
            await ExecuteNonQueryAsync($"""
                CREATE TRIGGER [asap].[{trigger}] ON [asap].[EmailDeliveryEvent] AFTER INSERT AS
                IF EXISTS (SELECT 1 FROM inserted WHERE [LocalEventKey] = N'initial')
                    THROW 51248, 'Injected initial evidence failure.', 1;
                """);
            Exception? failure = null;
            try
            {
                await CreatePostCommitSuggestionService(patron.Provider, dispatch).CreateForStaffAsync(await ReadConfiguredSuperAdminAsync(), 2,
                    ActivitySuggestion(patron.Barcode, title, true), CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            Assert.IsNotNull(failure);
            await using var context = await ActivityContexts().CreateDbContextAsync();
            Assert.IsFalse(await context.TitleRequests.AnyAsync(item => item.Title == title));
            Assert.IsFalse(await context.EmailOutbox.AnyAsync(item => item.ToAddress == "patron@example.org" && item.Subject != null && item.Subject.Contains(title)));
            Assert.AreEqual(0, dispatch.EnqueuedIds.Count);
        }
        finally
        {
            await ExecuteNonQueryAsync($"DROP TRIGGER IF EXISTS [asap].[{trigger}];");
            await DeletePostCommitSuggestionAsync(title, null);
        }
    }

    [TestMethod]
    [DataRow("caller_cancelled")]
    [DataRow("expired_readiness")]
    public async Task RequestEmailPreSendCancellationAndExpiredReadinessReleaseWithoutFabricatingSend(string boundary)
    {
        var confirmation = await CreateActivityConfirmationAsync();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new ActivityEmailSender
        {
            OnReadiness = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return EmailTransportReadiness.Configured;
            }
        };
        try
        {
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            var delivering = jobs.DeliverAsync(confirmation.OutboxId, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (boundary == "caller_cancelled")
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => delivering);
            }
            else
            {
                await SetLeaseExpiryAsync(confirmation.OutboxId, expired: true);
                await Task.WhenAll(jobs.SweepAsync(CancellationToken.None), jobs.SweepAsync(CancellationToken.None));
                release.TrySetResult();
                await delivering;
            }
            var released = await ReadEmailOutcomeDispatchSnapshotAsync(confirmation.OutboxId);
            Assert.AreEqual("pending", released.Status);
            Assert.AreEqual(0, released.AttemptCount);
            Assert.AreEqual(0, sender.SendCount);
            CollectionAssert.AreEqual(new[] { "queued", "claimed", "pre_send_released" },
                (await ActivityEventsAsync(confirmation.OutboxId)).Select(item => item.EventType).ToArray());
            sender.OnReadiness = null;
            await MakeOutboxDueAsync(confirmation.OutboxId);
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(confirmation.OutboxId, CancellationToken.None);
            Assert.AreEqual(1, sender.SendCount);
            var final = await ActivityEventsAsync(confirmation.OutboxId);
            Assert.AreEqual(2, final.Count(item => item.EventType == "claimed"));
            Assert.AreEqual(1, final.Count(item => item.EventType == "send_intent"));
        }
        finally
        {
            release.TrySetResult();
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
        }
    }

    [TestMethod]
    [DataRow("claim")]
    [DataRow("intent")]
    [DataRow("domain")]
    public async Task RequestEmailSuppressionEvidenceUsesCurrentRecipientAuthorityAtRealBoundaries(string boundary)
    {
        var confirmation = await CreateActivityConfirmationAsync();
        var sensitive = await SeedSensitiveOutboxAsync($"activity-recipient-{Guid.NewGuid():N}");
        var sender = new EmailRecipientAuthorityBarrierSender(pauseReadiness: boundary == "intent", pauseSend: false, "activity-sensitive");
        try
        {
            await using (var context = await ActivityContexts().CreateDbContextAsync())
            {
                var outbox = await context.EmailOutbox.SingleAsync(item => item.Id == sensitive.OutboxId);
                outbox.TitleRequestId = confirmation.RequestId;
                outbox.MessageKind = "assignment";
                await context.SaveChangesAsync();
            }
            if (boundary == "claim")
            {
                await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;", ("@id", sensitive.StaffUserId));
            }
            var delivery = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default,
                boundary == "domain" ? ["other.invalid"] : null).DeliverAsync(sensitive.OutboxId, CancellationToken.None);
            if (boundary == "intent")
            {
                await sender.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;", ("@id", sensitive.StaffUserId));
                sender.ReleaseReadiness();
            }
            await delivery;
            Assert.AreEqual(0, sender.SendCount);
            var events = await ActivityEventsAsync(sensitive.OutboxId);
            Assert.AreEqual(1, events.Count(item => item.EventType == "suppressed"));
            Assert.IsFalse(events.Any(item => item.EventType is "send_intent" or "provider_accepted"));
            var last = events.Single(item => item.EventType == "suppressed");
            Assert.AreEqual(boundary == "domain" ? "recipient_domain_not_allowed" : "recipient_authorization_changed", last.ReasonCode);
            var state = await ReadEmailOutcomeDispatchSnapshotAsync(sensitive.OutboxId);
            Assert.AreEqual("suppressed", state.Status);
            var detail = await ReadActivityDetailAsync(await ReadConfiguredSuperAdminAsync(), confirmation.RequestId);
            Assert.IsTrue(detail.Activity.Any(item => item.Email?.OperationReference == sensitive.OutboxId.ToString(CultureInfo.InvariantCulture) && item.Email.Outcome == "suppressed"));
        }
        finally
        {
            sender.ReleaseAll();
            await using (var context = await ActivityContexts().CreateDbContextAsync())
            {
                await DeleteOwnedEmailFixtureAsync(context, context.EmailOutbox.Where(item => item.Id == sensitive.OutboxId));
            }
            await DeleteEmailOutcomeFixtureAsync(sensitive);
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
        }
    }

    private IDbContextFactory<AsapDbContext> ActivityContexts() => factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();

    private async Task AssertRequestMailEvidenceAsync(EmailOutbox outbox, long requestId, string kind)
    {
        Assert.AreEqual(requestId, outbox.TitleRequestId);
        Assert.AreEqual(kind, outbox.MessageKind);
        var events = await ActivityEventsAsync(outbox.Id);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("initial", events[0].LocalEventKey);
        Assert.IsNull(events[0].ProviderEventId);
        Assert.AreEqual(outbox.Status == "suppressed" ? "suppressed" : "queued", events[0].EventType);
        Assert.AreEqual(outbox.SuppressedUtc ?? outbox.CreatedUtc, events[0].ReceivedUtc);
        var detail = await ReadActivityDetailAsync(await ReadConfiguredSuperAdminAsync(), requestId);
        Assert.IsTrue(detail.Activity.Any(item => item.Email?.OperationReference == outbox.Id.ToString(CultureInfo.InvariantCulture) &&
            item.Email.MessageKind == kind && !item.Email.IsRecordedSummary));
    }

    // Disposable fixtures explicitly remove their owned immutable events before
    // deleting an outbox. Production retains the original evidence-delete fence.
    private static async Task DeleteOwnedEmailFixtureAsync(AsapDbContext context, IQueryable<EmailOutbox> rows)
    {
        var ids = await rows.Select(item => item.Id).ToArrayAsync();
        await context.EmailDeliveryEvents.Where(item => item.EmailOutboxId.HasValue && ids.Contains(item.EmailOutboxId.Value)).ExecuteDeleteAsync();
        await rows.ExecuteDeleteAsync();
    }

    private async Task<TitleRequestDto> ReadActivityDetailAsync(CurrentStaff actor, long requestId)
    {
        var detail = await factory!.Services.GetRequiredService<TitleRequestViewService>()
            .GetAsync(actor, requestId.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
        Assert.IsNotNull(detail);
        return detail;
    }

    private static string ActivityIdentity(TitleRequestActivity activity) => $"{activity.Source}:{activity.Id}";

    private static StaffSuggestionInput ActivitySuggestion(string barcode, string title, bool confirmation) =>
        new(2, barcode, "book", title, "Activity Author", null, "Coming soon", null, null, 101, 101, true, confirmation, new Dictionary<string, string?>());

    private async Task<(long RequestId, long OutboxId, string Title)> CreateActivityConfirmationAsync()
    {
        var patron = AddPostCommitCancellationPatron();
        var title = $"Email Activity lifecycle {Guid.NewGuid():N}";
        var accepted = await CreatePostCommitSuggestionService(patron.Provider, new RecordingOutboxDispatcher()).CreateForStaffAsync(
            await ReadConfiguredSuperAdminAsync(), 2, ActivitySuggestion(patron.Barcode, title, true), CancellationToken.None);
        await using var context = await ActivityContexts().CreateDbContextAsync();
        var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item =>
            item.TitleRequestId == accepted.Id && item.MessageKind == "confirmation");
        Assert.AreEqual("pending", outbox.Status);
        return (accepted.Id, outbox.Id, title);
    }

    private async Task<List<EmailDeliveryEvent>> ActivityEventsAsync(long id)
    {
        await using var context = await ActivityContexts().CreateDbContextAsync();
        return await context.EmailDeliveryEvents.AsNoTracking().Where(item => item.EmailOutboxId == id).OrderBy(item => item.Id).ToListAsync();
    }

    private sealed class ActivityEmailSender : IEmailSender
    {
        private int sendCount;
        public int SendCount => Volatile.Read(ref sendCount);
        public bool ReadinessFailure { get; set; }
        public EmailTransportReadiness Readiness { get; set; } = EmailTransportReadiness.Configured;
        public EmailSendResult Result { get; set; } = new("activity-accepted");
        public Func<EmailEnvelope, CancellationToken, Task<EmailSendResult>>? OnSend { get; set; }
        public Func<CancellationToken, Task<EmailTransportReadiness>>? OnReadiness { get; set; }

        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OnReadiness is not null)
            {
                return OnReadiness(cancellationToken);
            }
            return ReadinessFailure ? Task.FromException<EmailTransportReadiness>(new EmailOperationalException("Deterministic readiness failure.")) : Task.FromResult(Readiness);
        }

        public async Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount);
            // A second connection must acquire the business row at the external
            // call boundary: delivery never holds its bookkeeping transaction here.
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var probe = new SqlCommand("SET LOCK_TIMEOUT 1000; SELECT [Id] FROM [asap].[EmailOutbox] WITH (UPDLOCK) WHERE [Id] = @id;", connection);
            probe.Parameters.AddWithValue("@id", envelope.OutboxId);
            await probe.ExecuteScalarAsync(cancellationToken);
            return OnSend is null ? Result : await OnSend(envelope, cancellationToken);
        }
    }
}
