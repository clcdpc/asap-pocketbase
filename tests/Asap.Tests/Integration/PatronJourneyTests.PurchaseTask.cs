using System.Net;
using System.Net.Http.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task PurchaseWithVerifiedBibAndAutomaticHoldOffCreatesLinkedTask(
        bool reminderDefault, bool reminderChoice)
    {
        await using var fixture = await SeedPolarisQueueAsync(reminderDefault: reminderDefault);
        await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        await using (var setup = await fixture.Contexts.CreateDbContextAsync())
        {
            var editable = await setup.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
            editable.ClaimedByStaffUserId = fixture.Actor.Id;
            editable.ClaimedByDisplayName = fixture.Actor.DisplayName ?? fixture.Actor.UserPrincipalName ?? "Purchase task actor";
            editable.ClaimedAtUtc = timeProvider!.GetUtcNow().UtcDateTime;
            editable.ClaimType = ClaimType.Manual;
            await setup.SaveChangesAsync();
            fixture.Source.RowVersion = editable.RowVersion;
        }

        var command = PurchaseTaskCommand(fixture.Source, reminderChoice);
        var result = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        Assert.AreEqual("closed", result.FinalStatus);
        Assert.AreEqual(command.OperationId, result.OperationId);
        Assert.IsFalse(result.Replayed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.AdditionalCopyRequestId));
        Assert.AreEqual(reminderChoice ? "queued" : "not_requested", result.NotificationStatus);
        Assert.IsNull(result.PatronNotificationStatus, "A purchasing task must not send the Purchase approved patron email.");
        Assert.AreEqual(0, fixture.Provider.CreateCommands.Count);
        Assert.AreEqual(0, fixture.Provider.ReplyCommands.Count);
        Assert.AreEqual(0, fixture.Provider.Calls.Count(call => call.Operation is
            TestingPolarisOperation.BibHoldings or TestingPolarisOperation.PatronHolds));

        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var source = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == fixture.Source.Id);
        var task = await verify.AdditionalCopyRequests.AsNoTracking()
            .SingleAsync(item => item.SourceTitleRequestId == source.Id);
        Assert.AreEqual("closed", source.Status);
        Assert.AreEqual("purchased_no_hold", source.CloseReason);
        Assert.IsFalse(source.AutoHold);
        Assert.AreEqual(fixture.Source.Title, source.Title, "Purchase closes the existing request without replacing its title.");
        Assert.AreEqual(9001, source.BibId);
        Assert.IsTrue(source.BibIdStaffVerified);
        Assert.AreEqual("SAVED-BIB", source.Identifier, "Purchase preserves the saved request identifier.");
        Assert.AreEqual("open", task.Status);
        Assert.AreEqual(source.LibraryOrganizationId, task.LibraryOrganizationId);
        Assert.AreEqual(source.BibId, task.BibId);
        Assert.AreEqual("Verified purchase title", task.Title,
            "The purchasing task uses the authoritative verified Polaris BIB title.");
        Assert.AreEqual(source.MaterialFormatId, task.MaterialFormatId);
        Assert.AreEqual(source.Identifier, task.Identifier);
        Assert.AreEqual(fixture.Actor.Id, task.CreatedByStaffUserId);
        Assert.AreEqual(fixture.Actor.Id, task.ClaimedByStaffUserId);
        Assert.AreEqual(task.Id.ToString(), result.AdditionalCopyRequestId,
            "The bigint task identity is exposed as a string.");
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == source.Id));
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == source.Id &&
            item.EventType == "additional_copy_created"));
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == source.Id &&
            item.EventType == "polaris_queue_completed"));
        Assert.IsFalse(await verify.TitleRequestEvents.AnyAsync(item => item.TitleRequestId == source.Id &&
            item.EventType == "hold_placement_requested"));
        Assert.IsFalse(await verify.HoldPlacementOperations.AnyAsync(item => item.TitleRequestId == source.Id));
        var link = await verify.TitleRequestEvents.AsNoTracking().SingleAsync(item => item.TitleRequestId == source.Id &&
            item.EventType == "additional_copy_created");
        StringAssert.Contains(link.Message!, task.Id.ToString());
        StringAssert.Contains(link.MetadataJson!, $"\"additionalCopyRequestId\":\"{task.Id}\"");

        var outbox = await verify.EmailOutbox.AsNoTracking().SingleOrDefaultAsync(item =>
            item.BusinessKey == $"additional-copy-reminder:{task.Id}");
        Assert.AreEqual(reminderChoice, outbox is not null);
        if (reminderChoice)
        {
            Assert.IsNotNull(outbox);
            Assert.AreEqual(fixture.Actor.Id, outbox.RecipientStaffUserId);
            Assert.AreEqual("additional_copy_reminder", outbox.MessageKind);
            Assert.AreEqual("staff_authorization_sensitive", outbox.DeliveryClass);
            Assert.AreEqual(2, outbox.AuthorizationOrganizationId);
            Assert.AreEqual("queued", result.NotificationStatus,
                "The requested reminder outcome is reported independently of task creation.");
        }
        Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item => item.BusinessKey != null &&
            item.BusinessKey.StartsWith($"staff-patron-action:purchase_approved:{source.Id}:")));
        Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item => item.BusinessKey != null &&
            item.BusinessKey.StartsWith($"purchase-reminder:{source.Id}:")));

        var copies = factory!.Services.GetRequiredService<AdditionalCopyService>();
        var authorizedTask = await copies.GetAsync(fixture.Actor, task.Id.ToString(), null, CancellationToken.None);
        Assert.IsNotNull(authorizedTask);
        Assert.AreEqual(source.Id.ToString(), authorizedTask.SourceTitleRequest);
        var view = await factory.Services.GetRequiredService<TitleRequestViewService>()
            .GetAsync(fixture.Actor, source.Id.ToString(), CancellationToken.None);
        Assert.IsNotNull(view);
        Assert.AreEqual(task.Id.ToString(), view.AdditionalCopyRequestId);
    }

    [TestMethod]
    public async Task PurchaseTaskReplayReturnsReceiptWithoutPolarisAndRejectsChangedPayload()
    {
        await using var fixture = await SeedPolarisQueueAsync(reminderDefault: true);
        await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        var command = PurchaseTaskCommand(fixture.Source, true);
        var first = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", first.Code);
        fixture.Provider.SetFailure(new(TestingPolarisOperation.BibValidation, 2, NativeId: 9001),
            new PolarisOperationalException("test", "Offline after purchase task commit"));

        var replay = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", replay.Code);
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual(first.AdditionalCopyRequestId, replay.AdditionalCopyRequestId);
        Assert.AreEqual("queued", replay.NotificationStatus,
            "A replay returns the durable reminder intent, not a second dispatch result.");
        Assert.AreEqual("operation_conflict", (await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            command with { EmailPurchaseReminder = false }, CancellationToken.None)).Code);
        Assert.AreEqual(1, fixture.Provider.Calls.Count(call => call.Operation == TestingPolarisOperation.BibValidation));

        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id &&
            item.EventType == "additional_copy_created"));
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id &&
            item.EventType == "polaris_queue_completed"));
        Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(item => item.BusinessKey ==
            $"additional-copy-reminder:{first.AdditionalCopyRequestId}"));
    }

    [TestMethod]
    [DataRow("suppressed")]
    [DataRow("dispatch_failed")]
    public async Task PurchaseTaskReminderFailureDoesNotUndoTaskOrReceipt(string reminderOutcome)
    {
        await using var fixture = await SeedPolarisQueueAsync(reminderDefault: true);
        await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = reminderOutcome == "dispatch_failed" ? new QueueDispatchFailure(cancellation, false) : null;
        if (reminderOutcome == "suppressed")
        {
            await using var setup = await fixture.Contexts.CreateDbContextAsync();
            (await setup.StaffUsers.SingleAsync(item => item.Id == fixture.Actor.Id)).NotificationEmail = null;
            await setup.SaveChangesAsync();
        }
        var service = dispatcher is null ? QueueService()
            : CreatePostCommitTitleMutationService(dispatcher, staffPolarisProvider: fixture.Provider);
        var command = PurchaseTaskCommand(fixture.Source, true);
        var result = await service.ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", result.Code);
        Assert.AreEqual("closed", result.FinalStatus);
        Assert.AreEqual(reminderOutcome, result.NotificationStatus);
        Assert.AreEqual(reminderOutcome == "suppressed" ? "recipient_missing" : "queue_unavailable", result.NotificationReason);

        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        var task = await verify.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.SourceTitleRequestId == fixture.Source.Id);
        var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey ==
            $"additional-copy-reminder:{task.Id}");
        Assert.AreEqual(reminderOutcome == "suppressed" ? "suppressed" : "pending", outbox.Status);
        Assert.AreEqual(reminderOutcome == "suppressed" ? "recipient_missing" : null, outbox.SuppressionReason);
        Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id &&
            item.EventType == "polaris_queue_completed"));

        var callsBeforeReplay = fixture.Provider.Calls.Count;
        var replay = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, command, CancellationToken.None);
        Assert.AreEqual("updated", replay.Code);
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual(callsBeforeReplay, fixture.Provider.Calls.Count);
        Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.AreEqual(reminderOutcome == "suppressed" ? 0 : 1, dispatcher?.Calls ?? 0);
    }

    [TestMethod]
    public async Task PurchaseTaskRejectsStaleVersionAndForeignLibraryBeforeCreatingTask()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        var command = PurchaseTaskCommand(fixture.Source, false);
        await using (var change = await fixture.Contexts.CreateDbContextAsync())
        {
            var source = await change.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
            source.Notes = "Concurrent edit before Purchase";
            await change.SaveChangesAsync();
        }
        Assert.AreEqual("stale_version", (await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            command, CancellationToken.None)).Code);

        await using (var change = await fixture.Contexts.CreateDbContextAsync())
        {
            if (!await change.Organizations.AnyAsync(item => item.Id == 3))
            {
                change.Organizations.Add(new Organization
                {
                    Id = 3, DisplayName = "Other purchase library", OrganizationCodeId = 2, IsActive = true
                });
            }
            (await change.StaffUsers.SingleAsync(item => item.Id == fixture.Actor.Id)).OrganizationId = 3;
            var source = await change.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
            await change.SaveChangesAsync();
            command = command with { Version = StaffVersion.Encode(source.RowVersion) };
        }
        Assert.AreEqual("staff_scope_forbidden", (await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id,
            command, CancellationToken.None)).Code);
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.AreEqual("suggestion", (await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id)).Status);
        Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PurchaseTaskHttpCommitUncertaintyRecoversBySameOperation(bool afterCommit)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        var probe = new QueueCommitProbe(afterCommit) { Enabled = false };
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDbContextFactory<AsapDbContext>>();
            services.AddSingleton<IDbContextFactory<AsapDbContext>>(
                new QueueProbeContextFactory(databaseConnectionString, probe));
        }));
        using var client = await PolarisQueueClientAsync(fixture.Actor, scoped);
        var command = PurchaseTaskCommand(fixture.Source, false);
        probe.Enabled = true;
        using var uncertain = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{fixture.Source.Id}/action", PurchaseTaskPayload(command));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode,
            await uncertain.Content.ReadAsStringAsync());
        using var unknown = System.Text.Json.JsonDocument.Parse(await uncertain.Content.ReadAsStringAsync());
        Assert.AreEqual("request_outcome_unconfirmed", unknown.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(command.OperationId, unknown.RootElement.GetProperty("operationId").GetGuid());

        probe.Enabled = false;
        if (afterCommit)
        {
            fixture.Provider.SetFailure(new(TestingPolarisOperation.BibValidation, 2, NativeId: 9001),
                new PolarisOperationalException("test", "Polaris unavailable during recovery"));
        }
        using var recovery = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{fixture.Source.Id}/action", PurchaseTaskPayload(command));
        Assert.AreEqual(HttpStatusCode.OK, recovery.StatusCode, await recovery.Content.ReadAsStringAsync());
        using var recovered = System.Text.Json.JsonDocument.Parse(await recovery.Content.ReadAsStringAsync());
        Assert.IsTrue(recovered.RootElement.GetProperty("committed").GetBoolean());
        Assert.AreEqual(afterCommit, recovered.RootElement.GetProperty("replayed").GetBoolean());
        Assert.AreEqual("closed", recovered.RootElement.GetProperty("queueCommittedStatus").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(recovered.RootElement.GetProperty("additionalCopyRequestId").GetString()));
    }

    [TestMethod]
    public async Task PurchaseTaskReviewRejectsPreferenceChangedDuringBibVerification()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        var provider = new QueueReadProbe(fixture.Provider, async () =>
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync();
            var source = await context.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id);
            source.AutoHold = true;
            await context.SaveChangesAsync();
        });
        var service = CreatePostCommitTitleMutationService(staffPolarisProvider: provider);
        var result = await service.ActionAsync(fixture.Actor, fixture.Source.Id,
            PurchaseTaskCommand(fixture.Source, false), CancellationToken.None);
        Assert.AreEqual("stale_version", result.Code,
            "A preference change after the saved-state review must not switch the task into the hold-on branch.");
        await using var verify = await fixture.Contexts.CreateDbContextAsync();
        Assert.IsTrue((await verify.TitleRequests.SingleAsync(item => item.Id == fixture.Source.Id)).AutoHold);
        Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id));
    }

    private async Task SetSavedPurchaseBibAsync(PolarisQueueFixture fixture, bool autoHold)
    {
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        context.Attach(fixture.Source);
        fixture.Source.BibId = 9001;
        fixture.Source.BibIdStaffVerified = true;
        fixture.Source.AutoHold = autoHold;
        fixture.Source.IsbnCheckStatus = "found";
        fixture.Source.Identifier = "SAVED-BIB";
        await context.SaveChangesAsync();
        fixture.Provider.SetBib(9001, fixture.Source.LibraryOrganizationId,
            new(true, "Verified purchase title", "Verified author", "2026", "Book", "SAVED-BIB"),
            new(MyLibraryCount: 0, OtherLibraryCount: 0, ConsortiumCount: 0,
                IsHoldable: false, HasHoldableAtMyLibrary: false));
    }

    private static TitleRequestActionCommand PurchaseTaskCommand(TitleRequest source, bool reminder) =>
        new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(source.RowVersion),
            OperationId = Guid.NewGuid(),
            RequestType = "title_request",
            Action = "purchase",
            EmailPurchaseReminder = reminder
        }.ToCommand();

    private static object PurchaseTaskPayload(TitleRequestActionCommand command) => new
    {
        command.Version, command.OperationId, command.RequestType, command.Action, command.EmailPurchaseReminder
    };
}
