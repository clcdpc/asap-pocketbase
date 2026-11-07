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
            if (scenario == "not_requested")
            {
                Assert.AreEqual(0, mail.Length);
                Assert.AreEqual(0, dispatch.EnqueuedIds.Count);
                return;
            }
            Assert.AreEqual(1, mail.Length);
            var evidence = mail.Single().Email!;
            Assert.AreEqual("confirmation", evidence.MessageKind);
            Assert.AreEqual(scenario == "requested" ? "queued" : "suppressed", evidence.Outcome);
            Assert.IsFalse(evidence.IsRecordedSummary);
            Assert.AreEqual(LibraryScope.ForLibrary(2).ToTransportValue(), evidence.OperationsScope);
            Assert.AreEqual(scenario == "requested" ? null : "Recipient domain not allowed", evidence.Reason);
            await using var context = await ActivityContexts().CreateDbContextAsync();
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.TitleRequestId == accepted.Id);
            Assert.AreEqual(outbox.Id.ToString(CultureInfo.InvariantCulture), evidence.OperationReference);
            Assert.AreEqual("confirmation", outbox.MessageKind);
            Assert.AreEqual(1, await context.EmailDeliveryEvents.CountAsync(item => item.EmailOutboxId == outbox.Id));
            Assert.AreEqual(scenario == "requested" ? 1 : 0, dispatch.EnqueuedIds.Count);
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
        var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.TitleRequestId == accepted.Id);
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
