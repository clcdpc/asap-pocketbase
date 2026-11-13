using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private const int WeeklyRecipientLibrary = 99441;

    [TestMethod]
    [DataRow("primary@example.org", null, true, 1, null, null)]
    [DataRow("primary@example.org", "  ", true, 1, null, null)]
    [DataRow("primary@example.org", "additional@example.org", true, 2, null, null)]
    [DataRow("primary@example.org", " PRIMARY@EXAMPLE.ORG ", true, 1, null, null)]
    [DataRow("primary@example.org", "additional@example.org", false, 0, null, null)]
    [DataRow("primary@example.org", "invalid-address", true, 2, null, "recipient_missing_or_invalid")]
    [DataRow("invalid-address", "additional@example.org", true, 2, "recipient_missing_or_invalid", null)]
    [DataRow("primary@outside.invalid", "additional@example.org", true, 2, "recipient_domain_not_allowed", null)]
    [DataRow("primary@example.org", "additional@outside.invalid", true, 2, null, "recipient_domain_not_allowed")]
    [DataRow("primary@outside.invalid", "additional@outside.invalid", true, 2, "recipient_domain_not_allowed", "recipient_domain_not_allowed")]
    public async Task WeeklyRecipientCombinationPersistsIndependentIdentityAndOutcome(
        string primary, string? additional, bool enabled, int count, string? primarySuppression, string? additionalSuppression)
    {
        await WithWeeklyRecipientsAsync(primary, additional, enabled, async id =>
        {
            await QueueWeeklyRecipientsAsync();
            var rows = await ReadWeeklyRecipientRowsAsync(id);
            Assert.HasCount(count, rows);
            var sender = new RecordingEmailSender();
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            foreach (var row in rows)
            {
                var isPrimary = row.RecipientAddressKind == "weekly_summary_primary";
                Assert.AreEqual(isPrimary ? "weekly_summary_primary" : "weekly_summary_additional", row.RecipientAddressKind);
                Assert.AreEqual(!isPrimary, row.BusinessKey!.EndsWith(":additional", StringComparison.Ordinal));
                Assert.AreEqual((long?)id, row.RecipientStaffUserId);
                Assert.AreEqual((int?)WeeklyRecipientLibrary, row.AuthorizationOrganizationId);
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.RecipientAuthenticationEmail));
                var suppression = isPrimary ? primarySuppression : additionalSuppression;
                Assert.AreEqual(suppression is null ? "pending" : "suppressed", row.Status);
                Assert.AreEqual(suppression, row.SuppressionReason);
                var source = isPrimary ? primary : additional;
                Assert.AreEqual(suppression == "recipient_missing_or_invalid" ? null : source!.Trim(), row.ToAddress);
                await jobs.DeliverAsync(row.Id, CancellationToken.None);
                var outcome = await ReadOutboxStateAsync(row.Id);
                Assert.AreEqual(suppression is null ? "sent" : "suppressed", outcome.Status);
                Assert.AreEqual(suppression is null ? "test-message" : null, outcome.ProviderMessageId);
            }
            if (rows.Length == 2 && primarySuppression is null && additionalSuppression is null)
            {
                Assert.AreEqual(rows[0].Subject, rows[1].Subject);
                Assert.AreEqual(rows[0].BodyText, rows[1].BodyText, "Both copies belong to the same scoped report.");
            }
            Assert.HasCount(rows.Count(row => row.Status == "pending"), sender.Envelopes);
            await QueueWeeklyRecipientsAsync();
            CollectionAssert.AreEquivalent(rows.Select(row => row.Id).ToArray(),
                (await ReadWeeklyRecipientRowsAsync(id)).Select(row => row.Id).ToArray(), "A repeat scheduled run cannot backfill copies.");
        });
    }

    [TestMethod]
    [DataRow("deactivate", false)]
    [DataRow("opt_out", false)]
    [DataRow("primary_change", false)]
    [DataRow("additional_change", false)]
    [DataRow("additional_remove", false)]
    [DataRow("move", false)]
    [DataRow("library_inactive", false)]
    [DataRow("authentication_change", false)]
    [DataRow("role_change", false)]
    [DataRow("deactivate", true)]
    [DataRow("opt_out", true)]
    [DataRow("primary_change", true)]
    [DataRow("additional_change", true)]
    [DataRow("additional_remove", true)]
    [DataRow("move", true)]
    [DataRow("library_inactive", true)]
    [DataRow("authentication_change", true)]
    [DataRow("role_change", true)]
    public async Task WeeklyRecipientAuthorityChangeSuppressesOriginalCopyWithoutReplacement(string mutation, bool atIntent)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            if (mutation == "role_change")
            {
                await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [Role] = N'super_admin', [OrganizationId] = 1 WHERE [Id] = @id;", ("@id", id));
            }
            await QueueWeeklyRecipientsAsync();
            var rows = await ReadWeeklyRecipientRowsAsync(id);
            Assert.HasCount(2, rows);
            foreach (var row in rows)
            {
                // Restore authority for each copy, then mutate either before the first
                // validation or while readiness is paused before the locked intent check.
                await RestoreWeeklyRecipientAuthorityAsync(id, row.RecipientAuthenticationEmail!, mutation == "role_change");
                var sender = new EmailRecipientAuthorityBarrierSender(atIntent, false, $"accepted-{row.Id}");
                var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
                Task? delivery = null;
                try
                {
                    if (atIntent)
                    {
                        delivery = jobs.DeliverAsync(row.Id, CancellationToken.None);
                        await sender.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        Assert.AreEqual("pre_send_check_pending", (await ReadPreSendStateAsync(row.Id)).LastErrorCode);
                    }
                    await MutateWeeklyRecipientAuthorityAsync(id, mutation);
                    sender.ReleaseReadiness();
                    await (delivery ?? jobs.DeliverAsync(row.Id, CancellationToken.None));
                }
                finally
                {
                    sender.ReleaseAll();
                    if (delivery is not null)
                    {
                        await delivery;
                    }
                }
                var isPrimary = row.RecipientAddressKind == "weekly_summary_primary";
                var suppressed = mutation switch
                {
                    "primary_change" => isPrimary,
                    "additional_change" or "additional_remove" => !isPrimary,
                    _ => true
                };
                var state = await ReadOutboxStateAsync(row.Id);
                Assert.AreEqual(suppressed ? "suppressed" : "sent", state.Status, $"{mutation}/{row.RecipientAddressKind}");
                Assert.AreEqual(suppressed ? "recipient_authorization_changed" : null, state.SuppressionReason);
                Assert.AreEqual(suppressed ? 0 : 1, sender.SendCount);
                await jobs.DeliverAsync(row.Id, CancellationToken.None);
                Assert.AreEqual(suppressed ? 0 : 1, sender.SendCount);
                var persisted = (await ReadWeeklyRecipientRowsAsync(id)).Single(item => item.Id == row.Id);
                Assert.AreEqual(row.ToAddress, persisted.ToAddress);
                Assert.AreEqual(row.BusinessKey, persisted.BusinessKey);
            }
            await RestoreWeeklyRecipientAuthorityAsync(id, rows[0].RecipientAuthenticationEmail!, mutation == "role_change");
            await QueueWeeklyRecipientsAsync();
            Assert.HasCount(2, await ReadWeeklyRecipientRowsAsync(id), "Authority changes never replace recipients inside an existing operation.");
        });
    }

    [TestMethod]
    [DataRow("primary")]
    [DataRow("additional")]
    public async Task WeeklyRecipientPartialSuccessRetriesOnlyTheUnacceptedCopyAfterRestart(string failingCopy)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            await QueueWeeklyRecipientsAsync();
            var rows = await ReadWeeklyRecipientRowsAsync(id);
            var failing = rows.Single(row => row.RecipientAddressKind == $"weekly_summary_{failingCopy}");
            var successful = rows.Single(row => row.Id != failing.Id);
            var accepted = new RecordingEmailSender();
            await CreateEmailOutboxJobs(accepted, EmailOutboxRuntimeOptions.Default).DeliverAsync(successful.Id, CancellationToken.None);
            var acceptedRow = (await ReadWeeklyRecipientRowsAsync(id)).Single(row => row.Id == successful.Id);
            var unavailable = new WeeklyReadinessFailureSender();
            await CreateEmailOutboxJobs(unavailable, EmailOutboxRuntimeOptions.Default).DeliverAsync(failing.Id, CancellationToken.None);
            var retryable = await ReadPreSendStateAsync(failing.Id);
            Assert.AreEqual("pending", retryable.Status);
            Assert.AreEqual("mail_readiness_unavailable", retryable.LastErrorCode);
            Assert.AreEqual(0, retryable.AttemptCount, "Readiness failed before external send intent.");
            Assert.IsNotNull(retryable.NextAttemptUtc);
            Assert.IsNull(retryable.LeaseId);

            // Construct a fresh worker with no in-memory recipient state, as after a restart.
            await MakeOutboxDueAsync(failing.Id);
            var restartedSender = new RecordingEmailSender();
            var restarted = CreateEmailOutboxJobs(restartedSender, EmailOutboxRuntimeOptions.Default);
            await restarted.SweepAsync(CancellationToken.None);
            Assert.IsTrue(dispatcher!.EnqueuedIds.Contains(failing.Id));
            foreach (var row in rows)
            {
                await restarted.DeliverAsync(row.Id, CancellationToken.None);
            }
            var completed = await ReadWeeklyRecipientRowsAsync(id);
            Assert.IsTrue(completed.All(row => row.Status == "sent" && row.AttemptCount == 1));
            var unchanged = completed.Single(row => row.Id == successful.Id);
            CollectionAssert.AreEqual(acceptedRow.RowVersion, unchanged.RowVersion);
            Assert.AreEqual(acceptedRow.ProviderMessageId, unchanged.ProviderMessageId);
            Assert.HasCount(1, restartedSender.Envelopes);
            Assert.AreEqual(failing.Id, restartedSender.Envelopes[0].OutboxId);
            await QueueWeeklyRecipientsAsync();
            Assert.HasCount(2, await ReadWeeklyRecipientRowsAsync(id));
        });
    }

    [TestMethod]
    [DataRow("primary", false)]
    [DataRow("additional", false)]
    [DataRow("primary", true)]
    [DataRow("additional", true)]
    public async Task WeeklyRecipientDefinitiveNoSendFailureRetriesOnlyTheFailedCopy(string failingCopy, bool forced)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            Guid? manualRunId = forced ? Guid.NewGuid() : null;
            await QueueWeeklyRecipientsAsync(manualRunId);
            var rows = await ReadWeeklyRecipientRowsAsync(id);
            var failing = rows.Single(row => row.RecipientAddressKind == $"weekly_summary_{failingCopy}");
            var successful = rows.Single(row => row.Id != failing.Id);
            await CreateEmailOutboxJobs(new RecordingEmailSender(), EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(successful.Id, CancellationToken.None);
            await CreateEmailOutboxJobs(new WeeklyNotConfiguredSender(), EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(failing.Id, CancellationToken.None);
            var partial = await ReadWeeklyRecipientRowsAsync(id);
            var failed = partial.Single(row => row.Id == failing.Id);
            var accepted = partial.Single(row => row.Id == successful.Id);
            Assert.AreEqual("failed", failed.Status);
            Assert.AreEqual("mail_not_configured", failed.LastErrorCode);
            Assert.IsNull(failed.ProviderMessageId);
            Assert.AreEqual("sent", accepted.Status);
            Assert.AreEqual("test-message", accepted.ProviderMessageId);
            var restartedSender = new RecordingEmailSender();
            var restarted = CreateEmailOutboxJobs(restartedSender, EmailOutboxRuntimeOptions.Default);
            await QueueWeeklyRecipientsAsync(manualRunId);
            var replayed = await ReadWeeklyRecipientRowsAsync(id);
            Assert.HasCount(2, replayed, "Retrying the same operation cannot create either recipient again after partial success.");
            foreach (var original in partial)
            {
                var replay = replayed.Single(row => row.Id == original.Id);
                Assert.AreEqual(original.BusinessKey, replay.BusinessKey);
                Assert.AreEqual(original.RecipientAddressKind, replay.RecipientAddressKind);
                Assert.AreEqual(original.ToAddress, replay.ToAddress);
                CollectionAssert.AreEqual(original.RowVersion, replay.RowVersion, "Queue replay preserves the independent partial outcomes.");
                if (forced)
                {
                    StringAssert.StartsWith(replay.BusinessKey!, $"weekly-summary-force:{manualRunId:N}:{id}");
                }
            }
            var actor = await ReadConfiguredSuperAdminAsync();
            var retry = await factory!.Services.GetRequiredService<EmailOperationsService>()
                .RetryAsync(actor, failed.Id, StaffVersion.Encode(failed.RowVersion), CancellationToken.None);
            Assert.AreEqual("queued", retry.Code);
            foreach (var row in rows)
            {
                await restarted.DeliverAsync(row.Id, CancellationToken.None);
            }
            var completed = await ReadWeeklyRecipientRowsAsync(id);
            Assert.IsTrue(completed.All(row => row.Status == "sent"));
            Assert.AreEqual(1, completed.Single(row => row.Id == successful.Id).AttemptCount);
            Assert.AreEqual(2, completed.Single(row => row.Id == failing.Id).AttemptCount);
            var acceptedAfterRetry = completed.Single(row => row.Id == successful.Id);
            Assert.AreEqual(accepted.ProviderMessageId, acceptedAfterRetry.ProviderMessageId);
            Assert.AreEqual(accepted.SentUtc, acceptedAfterRetry.SentUtc);
            CollectionAssert.AreEqual(accepted.RowVersion, acceptedAfterRetry.RowVersion,
                "Retrying the failed copy leaves the accepted sibling's durable state unchanged.");
            Assert.HasCount(1, restartedSender.Envelopes);
            Assert.AreEqual(failing.Id, restartedSender.Envelopes[0].OutboxId);
        });
    }

    [TestMethod]
    [DataRow("timeout")]
    [DataRow("exception")]
    public async Task WeeklyRecipientUncertainProviderOutcomeRetainsEvidenceAndPreventsAutomaticResend(string failure)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            await QueueWeeklyRecipientsAsync();
            var rows = await ReadWeeklyRecipientRowsAsync(id);
            var primary = rows.Single(row => row.RecipientAddressKind == "weekly_summary_primary");
            var additional = rows.Single(row => row.Id != primary.Id);
            await CreateEmailOutboxJobs(new RecordingEmailSender(), EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(primary.Id, CancellationToken.None);
            await CreateEmailOutboxJobs(new WeeklyUncertainSender(failure), EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(additional.Id, CancellationToken.None);
            var uncertain = await ReadPreSendStateAsync(additional.Id);
            Assert.AreEqual("sending", uncertain.Status);
            Assert.AreEqual(failure == "timeout" ? "provider_timeout" : "transport_failure", uncertain.LastErrorCode);
            Assert.AreEqual(1, uncertain.AttemptCount);
            Assert.IsNotNull(uncertain.LeaseId);
            await SetLeaseExpiryAsync(additional.Id, expired: true);
            var restartedSender = new RecordingEmailSender();
            var restarted = CreateEmailOutboxJobs(restartedSender, EmailOutboxRuntimeOptions.Default);
            await restarted.SweepAsync(CancellationToken.None);
            foreach (var row in rows)
            {
                await restarted.DeliverAsync(row.Id, CancellationToken.None);
            }
            var persisted = await ReadWeeklyRecipientRowsAsync(id);
            Assert.AreEqual("sent", persisted.Single(row => row.Id == primary.Id).Status);
            var failed = persisted.Single(row => row.Id == additional.Id);
            Assert.AreEqual("failed", failed.Status);
            Assert.AreEqual(uncertain.LastErrorCode, failed.LastErrorCode);
            Assert.IsNull(failed.ProviderMessageId, "An uncertain response does not establish provider acceptance.");
            Assert.IsNull(failed.NextAttemptUtc);
            var actor = await ReadConfiguredSuperAdminAsync();
            var retry = await factory!.Services.GetRequiredService<EmailOperationsService>()
                .RetryAsync(actor, failed.Id, StaffVersion.Encode(failed.RowVersion), CancellationToken.None);
            Assert.AreEqual("email_not_retryable", retry.Code);
            Assert.HasCount(0, restartedSender.Envelopes, "Recovery cannot safely repeat an uncertain external send.");
            await QueueWeeklyRecipientsAsync();
            Assert.HasCount(2, await ReadWeeklyRecipientRowsAsync(id));
        });
    }

    [TestMethod]
    public async Task WeeklyRecipientConcurrentWorkersKeepOneLiveLeasePerCopy()
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            await QueueWeeklyRecipientsAsync();
            var rows = await ReadWeeklyRecipientRowsAsync(id);
            foreach (var row in rows)
            {
                var sender = new EmailRecipientAuthorityBarrierSender(false, true, $"accepted-{row.Id}");
                var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
                var first = jobs.DeliverAsync(row.Id, CancellationToken.None);
                try
                {
                    await sender.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var leased = await ReadPreSendStateAsync(row.Id);
                    Assert.AreEqual("sending", leased.Status);
                    Assert.IsNotNull(leased.LeaseId);
                    var otherWorker = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
                    await otherWorker.DeliverAsync(row.Id, CancellationToken.None);
                    Assert.AreEqual(leased.LeaseId, (await ReadPreSendStateAsync(row.Id)).LeaseId);
                }
                finally
                {
                    sender.ReleaseAll();
                    await first;
                }
                Assert.AreEqual(1, sender.SendCount);
                var completed = (await ReadWeeklyRecipientRowsAsync(id)).Single(item => item.Id == row.Id);
                Assert.AreEqual("sent", completed.Status);
                Assert.AreEqual(1, completed.AttemptCount);
                Assert.AreEqual($"accepted-{row.Id}", completed.ProviderMessageId);
            }
        });
    }

    [TestMethod]
    public async Task WeeklyRecipientConcurrentQueueAttemptsAndForcedRunsKeepDurableOperationIdentity()
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            // Independent hosts contend through the durable distributed workflow guard.
            await using var otherHost = factory!.WithWebHostBuilder(_ => { });
            var firstService = factory.Services.GetRequiredService<WorkflowProcessingService>();
            var otherService = otherHost.Services.GetRequiredService<WorkflowProcessingService>();
            var results = await Task.WhenAll(
                firstService.SendWeeklyStaffSummaryAsync(null, WeeklyRecipientLibrary, CancellationToken.None),
                otherService.SendWeeklyStaffSummaryAsync(null, WeeklyRecipientLibrary, CancellationToken.None));
            Assert.IsTrue(results.All(result => result.Code is "completed" or "workflow_processing_busy"));
            Assert.AreEqual(1, results.Sum(result => result.Changed));
            var scheduled = await ReadWeeklyRecipientRowsAsync(id);
            Assert.HasCount(2, scheduled);
            Assert.AreEqual(scheduled.Single(row => row.RecipientAddressKind == "weekly_summary_primary").BusinessKey + ":additional",
                scheduled.Single(row => row.RecipientAddressKind == "weekly_summary_additional").BusinessKey);

            var forced = Guid.NewGuid();
            await QueueWeeklyRecipientsAsync(forced);
            var firstForced = (await ReadWeeklyRecipientRowsAsync(id)).Where(row => row.BusinessKey!.Contains(forced.ToString("N"))).ToArray();
            Assert.HasCount(2, firstForced);
            var sender = new RecordingEmailSender();
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            foreach (var row in firstForced)
            {
                await jobs.DeliverAsync(row.Id, CancellationToken.None);
            }
            await QueueWeeklyRecipientsAsync(forced);
            foreach (var row in firstForced)
            {
                await jobs.DeliverAsync(row.Id, CancellationToken.None);
            }
            Assert.HasCount(2, sender.Envelopes);
            Assert.HasCount(4, await ReadWeeklyRecipientRowsAsync(id));
            var nextForced = Guid.NewGuid();
            await QueueWeeklyRecipientsAsync(nextForced);
            var secondForced = (await ReadWeeklyRecipientRowsAsync(id)).Where(row => row.BusinessKey!.Contains(nextForced.ToString("N"))).ToArray();
            Assert.HasCount(2, secondForced);
            foreach (var row in secondForced)
            {
                await jobs.DeliverAsync(row.Id, CancellationToken.None);
            }
            Assert.HasCount(4, sender.Envelopes, "A second deliberate forced operation sends both copies again.");
            Assert.HasCount(6, await ReadWeeklyRecipientRowsAsync(id));
        });
    }

    [TestMethod]
    [DataRow("pending", false)]
    [DataRow("sent", false)]
    [DataRow("pending", true)]
    [DataRow("sent", true)]
    public async Task WeeklyRecipientLegacyOperationKeepsOriginalRecipientAndNeverBackfills(string status, bool forced)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", null, true, async id =>
        {
            Guid? legacyRun = forced ? Guid.NewGuid() : null;
            await QueueWeeklyRecipientsAsync(legacyRun);
            var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using (var seed = await contexts.CreateDbContextAsync())
            {
                var staff = await seed.StaffUsers.SingleAsync(row => row.Id == id);
                staff.WeeklyActionSummaryEmail = "historical-weekly@example.org";
                var legacy = await seed.EmailOutbox.SingleAsync(row => row.RecipientStaffUserId == id);
                legacy.RecipientAddressKind = "weekly_summary";
                legacy.ToAddress = staff.WeeklyActionSummaryEmail;
                legacy.Status = status;
                legacy.SentUtc = status == "sent" ? timeProvider!.GetUtcNow().UtcDateTime : null;
                legacy.ProviderMessageId = status == "sent" ? "historical-accepted" : null;
                await seed.SaveChangesAsync();
            }
            var before = (await ReadWeeklyRecipientRowsAsync(id)).Single();
            await QueueWeeklyRecipientsAsync(legacyRun);
            var after = (await ReadWeeklyRecipientRowsAsync(id)).Single();
            CollectionAssert.AreEqual(before.RowVersion, after.RowVersion, "Queue retry must preserve legacy history and pending state exactly.");
            var sender = new RecordingEmailSender();
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(before.Id, CancellationToken.None);
            var completed = (await ReadWeeklyRecipientRowsAsync(id)).Single();
            Assert.AreEqual("sent", completed.Status);
            Assert.AreEqual("weekly_summary", completed.RecipientAddressKind);
            Assert.AreEqual("historical-weekly@example.org", completed.ToAddress);
            Assert.AreEqual(status == "sent" ? "historical-accepted" : "test-message", completed.ProviderMessageId);
            Assert.HasCount(status == "sent" ? 0 : 1, sender.Envelopes);
            // Only a new operation adopts the new meaning of a stored weekly preference.
            await QueueWeeklyRecipientsAsync(Guid.NewGuid());
            var newCopies = (await ReadWeeklyRecipientRowsAsync(id)).Where(row => row.Id != before.Id).ToArray();
            CollectionAssert.AreEquivalent(new[] { "primary@example.org", "historical-weekly@example.org" },
                newCopies.Select(row => row.ToAddress).ToArray());
        });
    }

    [TestMethod]
    public async Task WeeklyRecipientPreferenceValidationPreservesPrimaryAndRowversion()
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "stored@example.org", true, async id =>
        {
            var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using var context = await contexts.CreateDbContextAsync();
            var row = await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == id);
            var current = await ReadCorrectiveStaffAsync(row);
            var service = factory.Services.GetRequiredService<StaffProfileService>();
            var input = new StaffProfileInput(StaffVersion.Encode(row.RowVersion), true, "invalid", false, false, false, false);
            Assert.AreEqual("invalid_weekly_email", (await service.UpdateAsync(current, input, CancellationToken.None)).Code);
            var saved = await service.UpdateAsync(current, input with { WeeklyActionSummaryEmail = " additional@example.org " }, CancellationToken.None);
            Assert.AreEqual("updated", saved.Code);
            Assert.AreEqual("additional@example.org", saved.Staff!.WeeklyActionSummaryEmail);
            Assert.AreEqual("primary@example.org", saved.Staff.NotificationEmail);
            Assert.AreEqual("stale_version", (await service.UpdateAsync(current, input with { WeeklyActionSummaryEmail = null }, CancellationToken.None)).Code);
            var cleared = await service.UpdateAsync(saved.Staff, input with
            {
                Version = StaffVersion.Encode(saved.Staff.RowVersion), WeeklyActionSummaryEmail = " "
            }, CancellationToken.None);
            Assert.AreEqual("updated", cleared.Code);
            Assert.IsNull(cleared.Staff!.WeeklyActionSummaryEmail);
            Assert.AreEqual("primary@example.org", cleared.Staff.NotificationEmail);
        });
    }

    private Task<WorkflowRunResult> QueueWeeklyRecipientsAsync(Guid? manualRunId = null) =>
        factory!.Services.GetRequiredService<WorkflowProcessingService>()
            .SendWeeklyStaffSummaryAsync(manualRunId, WeeklyRecipientLibrary, CancellationToken.None);

    private async Task<EmailOutbox[]> ReadWeeklyRecipientRowsAsync(long id)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        return await context.EmailOutbox.AsNoTracking().Where(row => row.RecipientStaffUserId == id)
            .OrderBy(row => row.Id).ToArrayAsync();
    }

    private static Task MutateWeeklyRecipientAuthorityAsync(long id, string mutation) =>
        ExecuteNonQueryAsync(mutation switch
        {
            "deactivate" => "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
            "opt_out" => "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEnabled] = 0 WHERE [Id] = @id;",
            "primary_change" => "UPDATE [asap].[StaffUser] SET [NotificationEmail] = N'new-primary@example.org' WHERE [Id] = @id;",
            "additional_change" => "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEmail] = N'new-additional@example.org' WHERE [Id] = @id;",
            "additional_remove" => "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEmail] = NULL WHERE [Id] = @id;",
            "move" => "UPDATE [asap].[StaffUser] SET [OrganizationId] = 2 WHERE [Id] = @id;",
            "library_inactive" => "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @scope;",
            "authentication_change" => "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = N'changed@example.org', [NormalizedUserPrincipalName] = N'CHANGED@EXAMPLE.ORG' WHERE [Id] = @id;",
            "role_change" => "UPDATE [asap].[StaffUser] SET [Role] = N'staff', [OrganizationId] = @scope WHERE [Id] = @id;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        }, ("@id", id), ("@scope", WeeklyRecipientLibrary));

    private static Task RestoreWeeklyRecipientAuthorityAsync(long id, string authenticationEmail, bool superAdmin) => ExecuteNonQueryAsync(
        """
        UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = @scope;
        UPDATE [asap].[StaffUser] SET [IsActive] = 1, [WeeklyActionSummaryEnabled] = 1,
            [NotificationEmail] = N'primary@example.org', [WeeklyActionSummaryEmail] = N'additional@example.org',
            [UserPrincipalName] = @authenticationEmail, [NormalizedUserPrincipalName] = @authenticationEmail,
            [OrganizationId] = CASE WHEN @superAdmin = 1 THEN 1 ELSE @scope END,
            [Role] = CASE WHEN @superAdmin = 1 THEN N'super_admin' ELSE N'staff' END
        WHERE [Id] = @id;
        """, ("@id", id), ("@scope", WeeklyRecipientLibrary), ("@authenticationEmail", authenticationEmail), ("@superAdmin", superAdmin));

    private async Task WithWeeklyRecipientsAsync(string primary, string? additional, bool enabled, Func<long, Task> exercise)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, WeeklyRecipientLibrary);
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var recipient = await CreateCorrectiveStaffAsync(superAdmin, "staff", WeeklyRecipientLibrary);
        string? oldUrl = null;
        long requestId = 0;
        var competingFlags = new Dictionary<long, bool>();
        try
        {
            await using (var seed = await contexts.CreateDbContextAsync())
            {
                var system = await seed.SystemSettings.SingleAsync(row => row.OrganizationId == 1);
                oldUrl = system.StaffApplicationUrl;
                system.StaffApplicationUrl = "https://staff.example.org/staff/";
                foreach (var competing in await seed.StaffUsers.Where(row => row.Id != recipient.Id && row.WeeklyActionSummaryEnabled &&
                             (row.OrganizationId == WeeklyRecipientLibrary || row.Role == StaffRole.SuperAdmin && row.OrganizationId == 1)).ToListAsync())
                {
                    competingFlags.Add(competing.Id, competing.WeeklyActionSummaryEnabled);
                    competing.WeeklyActionSummaryEnabled = false;
                }
                var staff = await seed.StaffUsers.SingleAsync(row => row.Id == recipient.Id);
                staff.NotificationEmail = primary;
                staff.WeeklyActionSummaryEmail = additional;
                staff.WeeklyActionSummaryEnabled = enabled;
                var format = await seed.MaterialFormats.SingleAsync(row => row.Code == "book");
                var now = timeProvider!.GetUtcNow().UtcDateTime;
                var request = new TitleRequest
                {
                    LibraryOrganizationId = WeeklyRecipientLibrary, Barcode = $"weekly-{recipient.Id}",
                    Title = "Weekly recipient authority report", MaterialFormatId = format.Id,
                    Status = "suggestion", CreatedUtc = now, UpdatedUtc = now
                };
                seed.TitleRequests.Add(request);
                await seed.SaveChangesAsync();
                requestId = request.Id;
            }
            await exercise(recipient.Id);
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[EmailOutbox] WHERE [RecipientStaffUserId] = @id;", ("@id", recipient.Id));
            await DeleteRequestIdsAsync(contexts, [requestId]);
            await using var restore = await contexts.CreateDbContextAsync();
            (await restore.Organizations.SingleAsync(row => row.Id == WeeklyRecipientLibrary)).IsActive = true;
            (await restore.SystemSettings.SingleAsync(row => row.OrganizationId == 1)).StaffApplicationUrl = oldUrl;
            foreach (var (id, flag) in competingFlags)
            {
                (await restore.StaffUsers.SingleAsync(row => row.Id == id)).WeeklyActionSummaryEnabled = flag;
            }
            await restore.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(recipient.Id);
        }
    }

    private sealed class WeeklyReadinessFailureSender : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken) =>
            throw new EmailOperationalException("Deterministic readiness outage before provider intent.");
        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Readiness failure must prevent provider dispatch.");
    }

    private sealed class WeeklyUncertainSender(string failure) : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(EmailTransportReadiness.Configured);
        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            failure == "timeout" ? throw new OperationCanceledException("Provider acknowledgement timed out.")
                : throw new EmailOperationalException("Provider acknowledgement was lost.");
    }

    private sealed class WeeklyNotConfiguredSender : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(EmailTransportReadiness.Configured);
        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            Task.FromResult(EmailSendResult.NotConfigured);
    }
}
