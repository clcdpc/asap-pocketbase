using System.Text.Json;
using System.Data;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Features.Patron;
using Microsoft.Data.SqlClient;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Asap.Web.Infrastructure.Jobs;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task IdentifierRetryUsesTheSamePickupRecoveryGateAsCapabilities()
    {
        var provider = new PickupJournalProvider(3482) { FailBeforeEffect = true };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var row = await setup.TitleRequests.SingleAsync(item => item.Id == request.Id);
                row.Status = RequestStatus.Suggestion;
                row.Identifier = "9781234567890";
                row.IsbnCheckStatus = IdentifierCheckState.RetryExhausted;
                await setup.SaveChangesAsync();
                request.RowVersion = row.RowVersion;
            }
            var actor = await ReadConfiguredSuperAdminAsync();
            var pickup = await scoped.Services.GetRequiredService<StaffPickupService>().UpdateAsync(actor, request.Id,
                new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
                    provider.FirstBranch, true), CancellationToken.None);
            Assert.AreEqual("pickup_outcome_unconfirmed", pickup.Code);
            var view = await scoped.Services.GetRequiredService<TitleRequestViewService>()
                .GetAsync(actor, request.Id.ToString(), CancellationToken.None);
            Assert.IsNotNull(view);
            Assert.IsFalse(view.Capabilities.CanRetryIdentifierCheck);
            var result = await scoped.Services.GetRequiredService<TitleRequestMutationService>()
                .RetryIdentifierAsync(actor, request.Id, new VersionInput(StaffVersion.Encode(request.RowVersion)),
                    CancellationToken.None);
            Assert.AreEqual(view.Capabilities.BlockingReason, result.Code);
            Assert.AreEqual("pickup_reconciliation_required", result.Code);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(IdentifierCheckState.RetryExhausted,
                (await verify.TitleRequests.SingleAsync(item => item.Id == request.Id)).IsbnCheckStatus);
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task WorkflowEmailReadinessSeparatesOperationalFailuresFromProgrammingDefects(
        bool weekly, bool programmingDefect)
    {
        var scope = await CreateFulfillmentLibraryAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var sender = new MutableReadinessEmailSender(isConfigured: true)
        {
            ReadinessException = programmingDefect
                ? new InvalidOperationException("Synthetic programming defect")
                : new EmailOperationalException("Synthetic normalized transport failure")
        };
        await using var scoped = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
        }));
        long requestId = 0;
        long staffId = 0;
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var email = $"349-workflow-{Guid.NewGuid():N}@example.org";
                var staff = new StaffUser
                {
                    UserPrincipalName = email, NormalizedUserPrincipalName = email.ToUpperInvariant(),
                    Role = StaffRole.Staff, OrganizationId = scope, IsActive = true,
                    WeeklyActionSummaryEnabled = true, NotificationEmail = email
                };
                setup.StaffUsers.Add(staff);
                setup.WorkflowSettings.Add(new WorkflowSettings
                {
                    OrganizationId = scope, OutstandingTimeoutEnabled = true,
                    OutstandingTimeoutDays = 1, OutstandingTimeoutSendEmail = true
                });
                var now = timeProvider!.GetUtcNow().UtcDateTime;
                var request = NewRequest(scope, $"349-readiness-{Guid.NewGuid():N}", RequestStatus.Suggestion,
                    now.AddDays(-7), now.AddDays(-7));
                request.MaterialFormatId = await setup.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                setup.TitleRequests.Add(request);
                await setup.SaveChangesAsync();
                requestId = request.Id;
                staffId = staff.Id;
            }
            await PrepareTimeoutCycleAsync(contexts, QueueNames.OutstandingTimeout, requestId, scope);
            var jobs = scoped.Services.GetRequiredService<WorkflowProcessingService>();
            Func<Task<WorkflowRunResult>> run = weekly
                ? () => jobs.SendWeeklyStaffSummaryAsync(scopeOrganizationId: scope)
                : () => jobs.ProcessWorkflowAsync(scope);
            if (programmingDefect)
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await run());
            }
            else
            {
                Assert.AreEqual("operational_failure", (await run()).Code);
            }
            Assert.AreEqual(RequestStatus.Suggestion, await ReadRequestStatusAsync(contexts, requestId));
            Assert.AreEqual(0, sender.SendCount);
        }
        finally
        {
            if (requestId > 0)
            {
                await DeleteRequestAsync(requestId);
            }
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.StaffUsers.Where(item => item.Id == staffId).ExecuteDeleteAsync();
            await cleanup.WorkflowSettings.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
            await DeleteFulfillmentLibraryAsync(scope);
        }
    }

    [TestMethod]
    public async Task EmailBusinessTimeUsesTheApplicationClockWhileLeaseFencingUsesSqlTime()
    {
        var now = DateTimeOffset.UtcNow.AddDays(1);
        timeProvider!.SetUtcNow(now);
        var seeded = await SeedSensitiveOutboxAsync("349-application-clock");
        try
        {
            var sender = new MutableReadinessEmailSender(isConfigured: true);
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(seeded.OutboxId, CancellationToken.None);

            Assert.AreEqual(1, sender.SendCount);
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var read = new SqlCommand(
                "SELECT [Status], [CreatedUtc], [LastAttemptUtc], [SentUtc] FROM [asap].[EmailOutbox] WHERE [Id]=@id;",
                connection);
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = seeded.OutboxId;
            await using var reader = await read.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("sent", reader.GetString(0));
            for (var index = 1; index <= 3; index++)
            {
                Assert.AreEqual(now.UtcDateTime, reader.GetDateTime(index));
            }
        }
        finally
        {
            await DeletePolicyOutboxAsync(seeded);
        }
    }

    [TestMethod]
    public async Task EmailReadinessProgrammingDefectsPropagateWithRecoverablePreSendEvidence()
    {
        var seeded = await SeedSensitiveOutboxAsync("349-readiness-defect");
        try
        {
            var sender = new MutableReadinessEmailSender(isConfigured: true)
            {
                ReadinessException = new InvalidOperationException("Synthetic programming defect")
            };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default)
                    .DeliverAsync(seeded.OutboxId, CancellationToken.None));
            var state = await ReadPreSendStateAsync(seeded.OutboxId);
            Assert.AreEqual("sending", state.Status);
            Assert.AreEqual("pre_send_check_pending", state.LastErrorCode);
            Assert.AreEqual(0, sender.SendCount);
        }
        finally
        {
            await DeletePolicyOutboxAsync(seeded);
        }
    }

    [TestMethod]
    public async Task EmailDeliveryReusesCanonicalAuthenticationEmailPolicy()
    {
        var seeded = await SeedSensitiveOutboxAsync("349-email-owner");
        try
        {
            var sender = new MutableReadinessEmailSender(isConfigured: true);
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var change = new SqlCommand("""
                UPDATE [asap].[StaffUser]
                SET [UserPrincipalName]=N'not-an-email', [NormalizedUserPrincipalName]=N'NOT-AN-EMAIL'
                WHERE [Id]=@staffId;
                UPDATE [asap].[EmailOutbox] SET [RecipientAuthenticationEmail]=N'NOT-AN-EMAIL' WHERE [Id]=@id;
                """, connection);
            change.Parameters.Add("@staffId", SqlDbType.BigInt).Value = seeded.StaffUserId;
            change.Parameters.Add("@id", SqlDbType.BigInt).Value = seeded.OutboxId;
            await change.ExecuteNonQueryAsync();
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default)
                .DeliverAsync(seeded.OutboxId, CancellationToken.None);
            Assert.AreEqual("suppressed", (await ReadOutboxStateAsync(seeded.OutboxId)).Status);
            Assert.AreEqual(0, sender.SendCount);
        }
        finally
        {
            await DeletePolicyOutboxAsync(seeded);
        }
    }

    [TestMethod]
    public async Task AutomaticClaimRejectsAnInvalidAuthenticationEmailThroughTheCanonicalPolicy()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var seed = await SeedLifecycleAutoClaimRaceAsync("349-invalid-email");
        const string barcode = "349-invalid-claim";
        try
        {
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var change = new SqlCommand(
                    "UPDATE [asap].[StaffUser] SET [UserPrincipalName]=N'not-an-email', [NormalizedUserPrincipalName]=N'NOT-AN-EMAIL' WHERE [Id]=@id;",
                    connection);
                change.Parameters.AddWithValue("@id", seed.StaffId);
                await change.ExecuteNonQueryAsync();
            }
            var result = await factory.Services.GetRequiredService<PatronSuggestionService>().CreateAsync(
                new PatronSessionContext(0, barcode, 2, 2, 2, timeProvider!.GetUtcNow().UtcDateTime.AddHours(1)),
                new PatronSuggestionInput("book", "Invalid claimant", "Policy author", null, "Coming soon", 101, true,
                    new Dictionary<string, string?>()), CancellationToken.None);
            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == result.Id);
            Assert.IsNull(request.ClaimedByStaffUserId);
            Assert.AreEqual(timeProvider.GetUtcNow().UtcDateTime, request.CreatedUtc);
            Assert.IsTrue(await context.TitleRequestEvents.AnyAsync(item =>
                item.TitleRequestId == result.Id && item.EventType == "claim_auto_skipped"));
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand(
                "DELETE FROM [asap].[TitleRequest] WHERE [Barcode]=@barcode; DELETE FROM [asap].[FormatAutoClaimRule] WHERE [StaffUserId]=@id; DELETE FROM [asap].[StaffUser] WHERE [Id]=@id;",
                connection);
            cleanup.Parameters.AddWithValue("@barcode", barcode);
            cleanup.Parameters.AddWithValue("@id", seed.StaffId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task StructuredLegacyHoldAuthorityBlocksReopenDeletionAndNewPlacementWithoutJsonAuthority()
    {
        var actor = await GetOwnershipTestActorAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long closedId;
        long pendingId;
        await using (var setup = await contexts.CreateDbContextAsync())
        {
            var format = await setup.MaterialFormats.SingleAsync(item => item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime;
            var closed = new TitleRequest
            {
                LibraryOrganizationId = 2, Barcode = "349-authority-closed", Title = "Protected closed",
                MaterialFormatId = format.Id, Status = RequestStatus.Closed, CloseReason = "manual",
                BibId = 34901, BibIdStaffVerified = true, AutoHold = true, LegacyHoldProtected = true,
                CreatedUtc = now, UpdatedUtc = now
            };
            var pending = new TitleRequest
            {
                LibraryOrganizationId = 2, Barcode = "349-authority-pending", Title = "Protected pending",
                MaterialFormatId = format.Id, Status = RequestStatus.PendingHold,
                BibId = 34902, BibIdStaffVerified = true, AutoHold = true, LegacyHoldProtected = true,
                CreatedUtc = now, UpdatedUtc = now
            };
            setup.TitleRequests.AddRange(closed, pending);
            await setup.SaveChangesAsync();
            setup.TitleRequestEvents.Add(new TitleRequestEvent
            {
                TitleRequestId = closed.Id, EventType = "legacy", ActorType = "system",
                MetadataJson = JsonSerializer.Serialize(new { legacyBibProtection = false }), CreatedUtc = now
            });
            await setup.SaveChangesAsync();
            closedId = closed.Id;
            pendingId = pending.Id;
        }
        try
        {
            await using var read = await contexts.CreateDbContextAsync();
            var closed = await read.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == closedId);
            var mutations = factory.Services.GetRequiredService<TitleRequestMutationService>();
            var reopen = await mutations.ActionAsync(actor, closedId, new TitleRequestActionInput
            {
                Version = StaffVersion.Encode(closed.RowVersion), Action = "reopen"
            }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("hold_history_retained", reopen.Code);
            var delete = await mutations.DeleteClosedAsync(actor, closedId,
                new VersionInput(StaffVersion.Encode(closed.RowVersion), StaffVersion.Encode(actor.RowVersion)), CancellationToken.None);
            Assert.AreEqual("hold_history_retained", delete.Code);
            var pending = await read.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == pendingId);
            var place = await factory.Services.GetRequiredService<HoldPlacementService>()
                .PlaceAsync(actor, pendingId, new VersionInput(StaffVersion.Encode(pending.RowVersion)), CancellationToken.None);
            Assert.AreEqual("hold_history_retained", place.Code);
            Assert.IsFalse(await read.HoldPlacementOperations.AnyAsync(item => item.TitleRequestId == pendingId));
            Assert.IsTrue(await read.TitleRequests.AnyAsync(item => item.Id == closedId && item.LegacyHoldProtected));
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([closedId, pendingId]);
        }
    }
    private async Task DeletePolicyOutboxAsync(SeededSensitiveOutbox seeded)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        await context.EmailOutbox.Where(item => item.Id == seeded.OutboxId).ExecuteDeleteAsync();
        await context.StaffUsers.Where(item => item.Id == seeded.StaffUserId).ExecuteDeleteAsync();
    }

}
