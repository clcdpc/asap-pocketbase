using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task RequestEmailActivityAssociatesOnlyWholeKnownKeysVerifiedScopeAndExplicitCopySource()
    {
        // A datetime parameter would round this forward beyond the business
        // clock and make the later assignment violate UpdatedUtc >= CreatedUtc.
        timeProvider!.SetUtcNow(new DateTimeOffset(2030, 3, 17, 6, 30, 0, TimeSpan.Zero).AddMilliseconds(2));
        var source = await SeedActivityCollisionSourceAsync();
        var other = await SeedBibOwnershipRequestAsync($"activity-unrelated-{Guid.NewGuid():N}", 9001, true);
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var expected = new List<EmailOutbox>();
        var all = new List<EmailOutbox>();
        var copyIds = new List<long>();
        await using var context = await ActivityContexts().CreateDbContextAsync();
        try
        {
            // A direct copy deliberately has the SAME numeric ID as the title
            // request. Its absent explicit source must defeat that coincidence.
            await context.Database.ExecuteSqlRawAsync("""
                IF EXISTS (SELECT 1 FROM [asap].[AdditionalCopyRequest] WHERE [Id] = @id)
                    THROW 51247, 'Activity copy-collision fixture ID is in use.', 1;
                SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] ON;
                INSERT INTO [asap].[AdditionalCopyRequest] ([Id], [LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc])
                VALUES (@id, 2, 9001, N'Coincidental copy', N'open', @now, @now);
                SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] OFF;
                """, new SqlParameter("@id", source.Id), new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            copyIds.Add(source.Id);
            var relatedCopy = new AdditionalCopyRequest { SourceTitleRequestId = source.Id, LibraryOrganizationId = 2,
                BibId = 9001, Title = "Explicitly related copy", Status = "open", CreatedUtc = now, UpdatedUtc = now };
            var wrongScopeCopy = new AdditionalCopyRequest { SourceTitleRequestId = source.Id, LibraryOrganizationId = 1,
                BibId = 9001, Title = "Wrong scope copy", Status = "open", CreatedUtc = now, UpdatedUtc = now };
            context.AdditionalCopyRequests.AddRange(relatedCopy, wrongScopeCopy);
            await context.SaveChangesAsync();
            copyIds.AddRange([relatedCopy.Id, wrongScopeCopy.Id]);
            const string version = "0000000000000001";
            foreach (var key in new[]
            {
                $"patron-submission:{source.Id}", $"title-assignment:{source.Id}:{version}:1",
                $"purchase-reminder:{source.Id}:{version}", $"staff-patron-action:purchase_approved:{source.Id}:{version}",
                $"staff-patron-action:already_owned:{source.Id}:{version}", $"rejection:{source.Id}:{version}",
                $"title-hold-placed:{source.Id}:1", $"timeout:OutstandingTimeout:{source.Id}",
                $"additional-copy-reminder:{relatedCopy.Id}", $"additional-copy-assignment:{relatedCopy.Id}:{version}:1"
            })
            {
                expected.Add(LegacyActivityMail(key, now));
            }
            all.AddRange(expected);
            foreach (var key in new[]
            {
                $"xpatron-submission:{source.Id}", $"patron-submission:{source.Id}:extra",
                $"purchase-reminder:{source.Id}:{version}:extra", $"title-assignment:{source.Id}:{version}:+1",
                $"rejection:{source.Id}:000000000000000a", $"title-hold-placed:{source.Id}:0",
                $"timeout:OutstandingTimeout:{source.Id}:extra", $"staff-patron-action:other:{source.Id}:{version}",
                $"purchase-reminder:0{source.Id}:{version}", $"patron-submission:{other.Id}",
                $"additional-copy-reminder:{source.Id}", $"additional-copy-reminder:{wrongScopeCopy.Id}",
                $"weekly-summary:2:{source.Id}", $"weekly-summary-force:{Guid.NewGuid():N}:{source.Id}"
            })
            {
                all.Add(LegacyActivityMail(key, now));
            }
            var wrongScope = LegacyActivityMail($"purchase-reminder:{source.Id}:0000000000000002", now);
            wrongScope.OrganizationId = 1;
            all.Add(wrongScope);
            var typedWrongScope = LegacyActivityMail($"typed-wrong-library:{Guid.NewGuid():N}", now);
            typedWrongScope.OrganizationId = 1;
            typedWrongScope.TitleRequestId = source.Id;
            typedWrongScope.MessageKind = "confirmation";
            all.Add(typedWrongScope);
            var explicitOther = LegacyActivityMail($"rejection:{source.Id}:0000000000000002", now);
            explicitOther.TitleRequestId = other.Id;
            explicitOther.MessageKind = "rejection";
            all.Add(explicitOther);
            var operationalTest = LegacyActivityMail($"rejection:{source.Id}:0000000000000003", now);
            operationalTest.DeliveryClass = "operational_test";
            all.Add(operationalTest);
            context.EmailOutbox.AddRange(all);
            await context.SaveChangesAsync();
            var actor = await ReadConfiguredSuperAdminAsync();
            var directCopy = await context.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == source.Id);
            var assigned = await factory!.Services.GetRequiredService<AdditionalCopyService>().AssignAsync(actor, directCopy.Id,
                new AssignAdditionalCopyInput(StaffVersion.Encode(directCopy.RowVersion), actor.Id), CancellationToken.None);
            Assert.AreEqual("updated", assigned.Code);
            var unlinkedNotification = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == assigned.DispatchOutboxId);
            all.Add(unlinkedNotification);
            Assert.IsNull(unlinkedNotification.TitleRequestId);
            Assert.IsNull(unlinkedNotification.MessageKind);

            var detail = await ReadActivityDetailAsync(actor, source.Id);
            var mail = detail.Activity.Where(item => item.Email is not null).ToArray();
            CollectionAssert.AreEquivalent(expected.Select(item => item.Id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                mail.Select(item => item.Email!.OperationReference).ToArray());
            Assert.IsTrue(mail.All(item => item.Source == "email_summary" && item.Email!.IsRecordedSummary));
            Assert.IsTrue(mail.All(item => item.Email!.TimestampKind == "last_attempt" && item.Created == DateTime.SpecifyKind(now.AddMinutes(-2), DateTimeKind.Utc)));
            Assert.AreEqual(expected.Count, mail.Length, "AttemptCount 9 is one saved summary, never nine invented attempts.");
            var json = JsonSerializer.Serialize(detail);
            Assert.IsFalse(json.Contains("PRIVATE-PAYLOAD", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("PRIVATE-ERROR", StringComparison.Ordinal));
            Assert.IsTrue(mail.All(item => item.Email!.Reason == "Recorded detail unavailable"));
            Assert.IsTrue(mail.Where(item => item.Email!.MessageKind is "assignment" or "purchase_reminder" or "additional_copy_reminder" or "additional_copy_assignment")
                .All(item => item.Email!.IntendedRecipient is null), "Unverifiable historical staff addresses stay private.");
        }
        finally
        {
            var ids = all.Select(item => item.Id).ToArray();
            await DeleteOwnedEmailFixtureAsync(context, context.EmailOutbox.Where(item => ids.Contains(item.Id)));
            await context.AdditionalCopyRequests.Where(item => copyIds.Contains(item.Id)).ExecuteDeleteAsync();
            await DeleteRequestAsync(source.Id);
            await DeleteRequestAsync(other.Id);
            await ExecuteNonQueryAsync("DBCC CHECKIDENT ('asap.AdditionalCopyRequest', RESEED);");
        }
    }

    [TestMethod]
    public async Task LegacyRequestEmailSummaryIsFrozenBeforeRetryAndNeverReconstructsAttempts()
    {
        var source = await SeedBibOwnershipRequestAsync($"activity-summary-{Guid.NewGuid():N}", null, false);
        var actor = await ReadConfiguredSuperAdminAsync();
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        await using var context = await ActivityContexts().CreateDbContextAsync();
        var outbox = LegacyActivityMail($"patron-submission:{source.Id}", now);
        outbox.LastErrorCode = "mail_not_configured";
        context.EmailOutbox.Add(outbox);
        await context.SaveChangesAsync();
        try
        {
            var before = (await ReadActivityDetailAsync(actor, source.Id)).Activity.Single(item => item.Email is not null);
            Assert.AreEqual("email_summary", before.Source);
            Assert.AreEqual("recorded_failed", before.Email!.Outcome);
            Assert.AreEqual("last_attempt", before.Email.TimestampKind);
            Assert.AreEqual(0, (await ActivityEventsAsync(outbox.Id)).Count);
            Assert.AreEqual("queued", (await factory!.Services.GetRequiredService<EmailOperationsService>()
                .RetryAsync(actor, outbox.Id, StaffVersion.Encode(outbox.RowVersion), CancellationToken.None)).Code);
            var sender = new ActivityEmailSender();
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(outbox.Id, CancellationToken.None);
            var events = await ActivityEventsAsync(outbox.Id);
            CollectionAssert.AreEqual(new[] { "recorded_failed", "retry_queued", "claimed", "send_intent", "provider_accepted" }, events.Select(item => item.EventType).ToArray());
            Assert.AreEqual(before.Created, DateTime.SpecifyKind(events[0].ReceivedUtc, DateTimeKind.Utc));
            var after = (await ReadActivityDetailAsync(actor, source.Id)).Activity.Where(item => item.Email is not null).ToArray();
            Assert.AreEqual(5, after.Length);
            Assert.AreEqual(1, after.Count(item => item.Email!.IsRecordedSummary));
            Assert.IsFalse(after.Any(item => item.Source == "email_summary"));
            Assert.AreEqual(1, sender.SendCount);
        }
        finally
        {
            await DeleteOwnedEmailFixtureAsync(context, context.EmailOutbox.Where(item => item.Id == outbox.Id));
            await DeleteRequestAsync(source.Id);
        }
    }

    [TestMethod]
    public async Task RequestEmailActivityPreservesRecipientPrivacyAndRevalidatesCurrentRequestAuthority()
    {
        var confirmation = await CreateActivityConfirmationAsync();
        var ordinary = await SeedSensitiveOutboxAsync($"activity-own-{Guid.NewGuid():N}");
        var admin = await SeedSensitiveOutboxAsync($"activity-admin-{Guid.NewGuid():N}", role: "admin");
        var global = await SeedSensitiveOutboxAsync($"activity-global-{Guid.NewGuid():N}", role: "super_admin", staffOrganizationId: 1);
        var weeklyPrimary = await SeedSensitiveOutboxAsync($"activity-weekly-primary-{Guid.NewGuid():N}", addressKind: "weekly_summary_primary", weeklyEnabled: true);
        var weeklyAdditional = await SeedSensitiveOutboxAsync($"activity-weekly-additional-{Guid.NewGuid():N}", addressKind: "weekly_summary_additional", weeklyEnabled: true);
        var fixtures = new[] { ordinary, admin, global, weeklyPrimary, weeklyAdditional };
        const int foreignLibrary = 992429;
        await using (var context = await ActivityContexts().CreateDbContextAsync())
        {
            context.Organizations.Add(new Organization { Id = foreignLibrary, DisplayName = "Activity unauthorized library",
                OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true });
            await context.SaveChangesAsync();
        }
        var foreign = await SeedBibOwnershipRequestAsync($"activity-foreign-{Guid.NewGuid():N}", null, false, libraryOrganizationId: foreignLibrary);
        try
        {
            string[] expectedEmailReferences;
            EmailOutbox[] newRequestOutboxRows;
            Dictionary<long, (string Role, int OrganizationId)> newRequestRecipients;
            await using (var context = await ActivityContexts().CreateDbContextAsync())
            {
                var ids = fixtures.Select(item => item.OutboxId).ToArray();
                var rows = await context.EmailOutbox.Where(item => ids.Contains(item.Id)).ToArrayAsync();
                foreach (var row in rows)
                {
                    row.TitleRequestId = confirmation.RequestId;
                    row.MessageKind = "assignment";
                }
                await context.SaveChangesAsync();
                newRequestOutboxRows = await context.EmailOutbox.AsNoTracking()
                    .Where(item => item.TitleRequestId == confirmation.RequestId && item.MessageKind == "new_patron_request")
                    .ToArrayAsync();
                Assert.IsGreaterThanOrEqualTo(1, newRequestOutboxRows.Length);
                Assert.IsTrue(newRequestOutboxRows.All(item => item.RecipientStaffUserId.HasValue));
                var newRequestStaffIds = newRequestOutboxRows.Select(item => item.RecipientStaffUserId!.Value).Distinct().ToArray();
                var recipientRows = await context.StaffUsers.AsNoTracking()
                    .Where(item => newRequestStaffIds.Contains(item.Id))
                    .Select(item => new { item.Id, item.Role, item.OrganizationId })
                    .ToArrayAsync();
                newRequestRecipients = recipientRows.ToDictionary(item => item.Id,
                    item => (item.Role, item.OrganizationId));
                Assert.AreEqual(newRequestStaffIds.Length, newRequestRecipients.Count);
                expectedEmailReferences = new[]
                    { confirmation.OutboxId, ordinary.OutboxId, admin.OutboxId, global.OutboxId }
                    .Concat(newRequestOutboxRows.Select(item => item.Id))
                    .Select(item => item.ToString(CultureInfo.InvariantCulture))
                    .ToArray();
            }
            var staffActor = await ReadCorrectiveStaffAsync(await ReadActivityStaffAsync(ordinary.StaffUserId));
            var adminActor = await ReadCorrectiveStaffAsync(await ReadActivityStaffAsync(admin.StaffUserId));
            var systemActor = await ReadConfiguredSuperAdminAsync();
            var ordinaryDetail = await ReadActivityDetailAsync(staffActor, confirmation.RequestId);
            var adminDetail = await ReadActivityDetailAsync(adminActor, confirmation.RequestId);
            var globalDetail = await ReadActivityDetailAsync(systemActor, confirmation.RequestId);
            foreach (var detail in new[] { ordinaryDetail, adminDetail, globalDetail })
            {
                var emailReferences = detail.Activity.Where(item => item.Email is not null)
                    .Select(item => item.Email!.OperationReference).ToArray();
                Assert.AreEqual(expectedEmailReferences.Length, emailReferences.Length);
                CollectionAssert.AreEquivalent(expectedEmailReferences, emailReferences,
                    "Only the confirmation, intended association fixtures, and this request's staff notifications appear.");
                Assert.IsFalse(emailReferences.Contains(weeklyPrimary.OutboxId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
                    "Weekly primary remains excluded even with a forged typed association.");
                Assert.IsFalse(emailReferences.Contains(weeklyAdditional.OutboxId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal),
                    "Weekly additional remains excluded even with a forged typed association.");
            }
            RequestEmailActivity Mail(TitleRequestDto detail, long id) => detail.Activity.Single(item => item.Email?.OperationReference == id.ToString(CultureInfo.InvariantCulture)).Email!;
            Assert.AreEqual(ordinary.ToAddress, Mail(ordinaryDetail, ordinary.OutboxId).IntendedRecipient);
            Assert.IsNull(Mail(ordinaryDetail, admin.OutboxId).IntendedRecipient);
            Assert.IsNull(Mail(ordinaryDetail, global.OutboxId).IntendedRecipient);
            Assert.IsTrue(ordinaryDetail.Activity.Where(item => item.Email is not null).All(item => item.Email!.OperationsScope is null));
            Assert.AreEqual(ordinary.ToAddress, Mail(adminDetail, ordinary.OutboxId).IntendedRecipient);
            Assert.IsNull(Mail(adminDetail, global.OutboxId).IntendedRecipient);
            Assert.AreEqual(global.ToAddress, Mail(globalDetail, global.OutboxId).IntendedRecipient);
            foreach (var (detail, viewer) in new[]
                     { (ordinaryDetail, staffActor), (adminDetail, adminActor), (globalDetail, systemActor) })
            {
                foreach (var row in newRequestOutboxRows)
                {
                    var recipientId = row.RecipientStaffUserId!.Value;
                    var recipient = newRequestRecipients[recipientId];
                    var canSeeRecipient = recipientId == viewer.Id || viewer.Role == StaffRole.SuperAdmin ||
                        viewer.Role == StaffRole.Admin && recipient.Role != StaffRole.SuperAdmin &&
                        recipient.OrganizationId == viewer.OrganizationId;
                    Assert.AreEqual(canSeeRecipient ? row.ToAddress : null,
                        Mail(detail, row.Id).IntendedRecipient,
                        $"Recipient address visibility follows the existing policy for viewer {viewer.Id} and recipient {recipientId}.");
                }
            }
            Assert.AreEqual("staff_scope_forbidden", (await factory!.Services.GetRequiredService<EmailOperationsService>()
                .ListAsync(staffActor, new EmailOperationsQuery(LibraryScope.ForLibrary(2), Reference: confirmation.OutboxId.ToString(CultureInfo.InvariantCulture)), CancellationToken.None)).Code);
            // Revocation after capturing an identity removes recipient/outcome/reference access.
            await ExecuteNonQueryAsync("UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;", ("@id", admin.StaffUserId));
            Assert.IsNull(await factory.Services.GetRequiredService<TitleRequestViewService>().GetAsync(adminActor,
                confirmation.RequestId.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
            using var client = await CreateAuthenticatedStaffClientAsync(factory, staffActor);
            using var wrongScope = await client.GetAsync($"/api/asap/staff/title-requests/{foreign.Id}");
            Assert.AreEqual(HttpStatusCode.NotFound, wrongScope.StatusCode);
            Assert.IsNull(await factory.Services.GetRequiredService<TitleRequestViewService>().GetAsync(staffActor,
                foreign.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
        }
        finally
        {
            foreach (var fixture in fixtures)
            {
                await DeleteEmailOutcomeFixtureAsync(fixture);
            }
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
            await DeleteRequestAsync(foreign.Id);
            await using var cleanup = await ActivityContexts().CreateDbContextAsync();
            await cleanup.Organizations.Where(item => item.Id == foreignLibrary).ExecuteDeleteAsync();
        }
    }

    private async Task<StaffUser> ReadActivityStaffAsync(long id)
    {
        await using var context = await ActivityContexts().CreateDbContextAsync();
        return await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private async Task<(long Id, byte[] RowVersion)> SeedActivityCollisionSourceAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DECLARE @requestMax bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[TitleRequest]);
            DECLARE @copyMax bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[AdditionalCopyRequest]);
            DECLARE @id bigint = CASE WHEN @requestMax > @copyMax THEN @requestMax ELSE @copyMax END + 1;
            DECLARE @created TABLE ([Id] bigint, [Version] binary(8));
            SET IDENTITY_INSERT [asap].[TitleRequest] ON;
            INSERT INTO [asap].[TitleRequest] ([Id], [LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            OUTPUT inserted.[Id], inserted.[RowVersion] INTO @created
            SELECT @id, 2, @barcode, N'Legacy association source', 0, [Id], N'suggestion', @now, @now
            FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book';
            SET IDENTITY_INSERT [asap].[TitleRequest] OFF;
            SELECT [Id], [Version] FROM @created;
            """, connection);
        command.Parameters.AddWithValue("@barcode", $"activity-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@now", timeProvider!.GetUtcNow().UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return (reader.GetInt64(0), (byte[])reader[1]);
    }

    [TestMethod]
    public async Task RequestEmailActivityKeepsExactBigintReferencesAndSqlPrecisionSourceOrdering()
    {
        const long operationId = 9007199254740997;
        const long eventId = 9007199254740999;
        var source = await SeedBibOwnershipRequestAsync($"activity-precision-{Guid.NewGuid():N}", null, false);
        try
        {
            await ExecuteNonQueryAsync("""
                SET IDENTITY_INSERT [asap].[EmailOutbox] ON;
                INSERT INTO [asap].[EmailOutbox] ([Id], [OrganizationId], [BusinessKey], [TitleRequestId], [MessageKind], [DeliveryClass],
                    [ToAddress], [FromAddress], [Subject], [BodyText], [Status], [CreatedUtc])
                VALUES (@operation, 2, CONCAT(N'patron-submission:', @request), @request, N'confirmation', N'business_event',
                    N'precision@example.org', N'library@example.org', N'PRIVATE-PAYLOAD', N'PRIVATE-PAYLOAD', N'pending', '2026-01-01T00:00:00.0000000');
                SET IDENTITY_INSERT [asap].[EmailOutbox] OFF;
                SET IDENTITY_INSERT [asap].[TitleRequestEvent] ON;
                INSERT INTO [asap].[TitleRequestEvent] ([Id], [TitleRequestId], [EventType], [ActorType], [Message], [CreatedUtc])
                VALUES (@event, @request, N'precision', N'system', N'Equal timestamp', '2026-01-01T00:00:00.0000001'),
                    (@event + 1, @request, N'precision', N'system', N'Earlier submillisecond', '2026-01-01T00:00:00.0000000');
                SET IDENTITY_INSERT [asap].[TitleRequestEvent] OFF;
                SET IDENTITY_INSERT [asap].[EmailDeliveryEvent] ON;
                INSERT INTO [asap].[EmailDeliveryEvent] ([Id], [EmailOutboxId], [LocalEventKey], [EventType], [ReceivedUtc])
                VALUES (@event, @operation, N'initial', N'queued', '2026-01-01T00:00:00.0000001'),
                    (@event + 1, @operation, N'lease:precision:accepted', N'provider_accepted', '2026-01-01T00:00:00.0000002');
                SET IDENTITY_INSERT [asap].[EmailDeliveryEvent] OFF;
                """, ("@operation", operationId), ("@event", eventId), ("@request", source.Id));
            var detail = await ReadActivityDetailAsync(await ReadConfiguredSuperAdminAsync(), source.Id);
            CollectionAssert.AreEqual(new[] { $"request_event:{eventId + 1}", $"request_event:{eventId}", $"email_event:{eventId}", $"email_event:{eventId + 1}" },
                detail.Activity.Select(ActivityIdentity).ToArray(), "The API remains chronological with exact inverse source/ID ties for newest-first display.");
            Assert.AreEqual(4, detail.Activity.Select(ActivityIdentity).Distinct().Count());
            Assert.AreEqual(0L, detail.Activity[0].Created.Ticks % TimeSpan.TicksPerSecond);
            Assert.AreEqual(2L, detail.Activity[^1].Created.Ticks % TimeSpan.TicksPerSecond);
            Assert.AreEqual(operationId.ToString(CultureInfo.InvariantCulture), detail.Activity[^1].Email!.OperationReference);
            var json = JsonSerializer.Serialize(detail);
            StringAssert.Contains(json, "\"OperationReference\":\"9007199254740997\"");
            StringAssert.Contains(json, "2026-01-01T00:00:00.0000002Z");
            Assert.IsFalse(json.Contains("PRIVATE-PAYLOAD", StringComparison.Ordinal));
            var page = await factory!.Services.GetRequiredService<EmailOperationsService>().ListAsync(await ReadConfiguredSuperAdminAsync(),
                new EmailOperationsQuery(LibraryScope.ForLibrary(2), Reference: operationId.ToString(CultureInfo.InvariantCulture)), CancellationToken.None);
            Assert.AreEqual("ok", page.Code);
            Assert.AreEqual(operationId.ToString(CultureInfo.InvariantCulture), ((EmailOperationsPage)page.Data!).Items.Single().Id);
        }
        finally
        {
            await using var context = await ActivityContexts().CreateDbContextAsync();
            await DeleteOwnedEmailFixtureAsync(context, context.EmailOutbox.Where(item => item.Id == operationId));
            await DeleteRequestAsync(source.Id);
            await ExecuteNonQueryAsync("DBCC CHECKIDENT ('asap.EmailOutbox', RESEED); DBCC CHECKIDENT ('asap.TitleRequestEvent', RESEED); DBCC CHECKIDENT ('asap.EmailDeliveryEvent', RESEED);");
        }
    }

    [TestMethod]
    [DataRow("inactive")]
    [DataRow("authentication")]
    [DataRow("role")]
    [DataRow("binding")]
    public async Task RequestEmailActivityRejectsRevokedIdentityRoleAndLibraryAfterContextAcquisition(string mutation)
    {
        var confirmation = await CreateActivityConfirmationAsync();
        var staff = await SeedSensitiveOutboxAsync($"activity-actor-{Guid.NewGuid():N}",
            role: mutation == "role" ? "super_admin" : "admin", staffOrganizationId: mutation == "role" ? 1 : 2);
        const int reboundLibrary = 992430;
        try
        {
            var actor = await ReadCorrectiveStaffAsync(await ReadActivityStaffAsync(staff.StaffUserId));
            await ReadActivityDetailAsync(actor, confirmation.RequestId);
            if (mutation == "binding")
            {
                await using var context = await ActivityContexts().CreateDbContextAsync();
                context.Organizations.Add(new Organization { Id = reboundLibrary, DisplayName = "Rebound Activity library",
                    OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true });
                await context.SaveChangesAsync();
            }
            var sql = mutation switch
            {
                "inactive" => "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
                "authentication" => "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = N'changed-activity@example.org', [NormalizedUserPrincipalName] = N'CHANGED-ACTIVITY@EXAMPLE.ORG' WHERE [Id] = @id;",
                "role" => "UPDATE [asap].[StaffUser] SET [Role] = N'admin', [OrganizationId] = 2 WHERE [Id] = @id;",
                _ => "UPDATE [asap].[StaffUser] SET [OrganizationId] = 992430 WHERE [Id] = @id;"
            };
            await ExecuteNonQueryAsync(sql, ("@id", staff.StaffUserId));
            Assert.IsNull(await factory!.Services.GetRequiredService<TitleRequestViewService>().GetAsync(actor,
                confirmation.RequestId.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
            var events = await ActivityEventsAsync(confirmation.OutboxId);
            CollectionAssert.AreEqual(new[] { "queued" }, events.Select(item => item.EventType).ToArray(), "Unauthorized reads create no sending or history side effects.");
        }
        finally
        {
            await DeleteEmailOutcomeFixtureAsync(staff);
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
            if (mutation == "binding")
            {
                await using var context = await ActivityContexts().CreateDbContextAsync();
                await context.Organizations.Where(item => item.Id == reboundLibrary).ExecuteDeleteAsync();
            }
        }
    }

    [TestMethod]
    public async Task DeletingClosedRequestDetachesLinkAndRetainsExactEmailEvidence()
    {
        var confirmation = await CreateActivityConfirmationAsync();
        var actor = await ReadConfiguredSuperAdminAsync();
        try
        {
            byte[] requestVersion;
            await using (var context = await ActivityContexts().CreateDbContextAsync())
            {
                var request = await context.TitleRequests.SingleAsync(item => item.Id == confirmation.RequestId);
                request.Status = "closed";
                request.CloseReason = "rejected";
                await context.SaveChangesAsync();
                requestVersion = request.RowVersion;
            }
            var before = await ActivityEventsAsync(confirmation.OutboxId);
            var deleted = await factory!.Services.GetRequiredService<TitleRequestMutationService>().DeleteClosedAsync(actor,
                confirmation.RequestId, new VersionInput(StaffVersion.Encode(requestVersion), StaffVersion.Encode(actor.RowVersion)), CancellationToken.None);
            Assert.AreEqual("deleted", deleted.Code);
            await using var verify = await ActivityContexts().CreateDbContextAsync();
            var mail = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == confirmation.OutboxId);
            Assert.IsNull(mail.TitleRequestId);
            Assert.AreEqual("confirmation", mail.MessageKind);
            CollectionAssert.AreEqual(before.Select(item => item.Id).ToArray(), (await ActivityEventsAsync(confirmation.OutboxId)).Select(item => item.Id).ToArray());
            var failure = await Assert.ThrowsAsync<SqlException>(() => ExecuteNonQueryAsync("DELETE FROM [asap].[EmailOutbox] WHERE [Id] = @id;", ("@id", confirmation.OutboxId)));
            Assert.AreEqual(547, failure.Number);
            Assert.IsNull(await factory.Services.GetRequiredService<TitleRequestViewService>().GetAsync(actor,
                confirmation.RequestId.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
        }
        finally
        {
            await using var cleanup = await ActivityContexts().CreateDbContextAsync();
            await DeleteOwnedEmailFixtureAsync(cleanup, cleanup.EmailOutbox.Where(item => item.Id == confirmation.OutboxId));
            await DeletePostCommitSuggestionAsync(confirmation.Title, null);
        }
    }

    private static EmailOutbox LegacyActivityMail(string key, DateTime now) => new()
    {
        OrganizationId = 2, BusinessKey = key, DeliveryClass = "business_event", ToAddress = "historical@example.org",
        FromAddress = "sender@example.org", Subject = "PRIVATE-PAYLOAD subject", BodyText = "PRIVATE-PAYLOAD body",
        Status = "failed", AttemptCount = 9, LastErrorCode = "PRIVATE-ERROR", LastErrorDetail = "PRIVATE-ERROR detail",
        CreatedUtc = now.AddDays(-2), LastAttemptUtc = now.AddMinutes(-2)
    };
}
