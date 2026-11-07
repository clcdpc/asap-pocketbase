using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task RequestEmailActivityBrowserPreservesDurableHistoryReferencesDraftsAndAuthorizedLayouts()
    {
        const int otherLibrary = 992431;
        const string marker = "i429-browser-owned";
        var originalTime = timeProvider!.GetUtcNow();
        var oldTime = DateTimeOffset.Parse("2026-09-15T16:00:00.1234561Z", CultureInfo.InvariantCulture);
        var requestIds = new List<long>();
        var outboxIds = new List<long>();
        var staffRows = new List<StaffUser>();
        var browserSender = new EmailActivityBrowserSender();
        var transport = new ActivityEmailSender();
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var configuredArtifacts = Environment.GetEnvironmentVariable("ASAP_EMAIL_ACTIVITY_BROWSER_ARTIFACTS");
        var artifacts = string.IsNullOrWhiteSpace(configuredArtifacts)
            ? Path.Combine(root, ".artifacts", "browser", $"request-email-activity-{Guid.NewGuid():N}")
            : Path.GetFullPath(configuredArtifacts);
        Directory.CreateDirectory(artifacts);
        TestContext.WriteLine($"Request email Activity browser evidence: {artifacts}");
        await using var application = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(browserSender);
        }));
        application.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = application.CreateClient();
        var contexts = ActivityContexts();
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                setup.Organizations.Add(new Organization { Id = otherLibrary, DisplayName = "Activity other library",
                    OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true });
                await setup.SaveChangesAsync();
            }
            var bootstrap = await ReadConfiguredSuperAdminAsync();
            var superRecord = await CreateCorrectiveStaffAsync(bootstrap, StaffRole.SuperAdmin, 1);
            staffRows.Add(superRecord);
            var super = await ReadCorrectiveStaffAsync(superRecord);
            var adminRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, 2);
            staffRows.Add(adminRecord);
            var admin = await ReadCorrectiveStaffAsync(adminRecord);
            var staffRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Staff, 2);
            staffRows.Add(staffRecord);
            var staff = await ReadCorrectiveStaffAsync(staffRecord);
            var foreignRecord = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, otherLibrary);
            staffRows.Add(foreignRecord);
            var foreign = await ReadCorrectiveStaffAsync(foreignRecord);

            // Seed identity counters before the REAL producer runs. Its created
            // workflow and initial suppression events therefore collide across
            // source tables without manufacturing a production email transition.
            await ExecuteNonQueryAsync("""
                DECLARE @mailMaximum bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[EmailOutbox]);
                DECLARE @mailSeed bigint = CASE WHEN @mailMaximum > 9007199254749000 THEN @mailMaximum + 1000 ELSE 9007199254749000 END;
                DECLARE @workflowMaximum bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[TitleRequestEvent]);
                DECLARE @deliveryMaximum bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[EmailDeliveryEvent]);
                DECLARE @eventMaximum bigint = CASE WHEN @workflowMaximum > @deliveryMaximum THEN @workflowMaximum ELSE @deliveryMaximum END;
                DECLARE @eventSeed bigint = CASE WHEN @eventMaximum > 9007199254750000 THEN @eventMaximum + 1000 ELSE 9007199254750000 END;
                DBCC CHECKIDENT ('asap.EmailOutbox', RESEED, @mailSeed) WITH NO_INFOMSGS;
                DBCC CHECKIDENT ('asap.TitleRequestEvent', RESEED, @eventSeed) WITH NO_INFOMSGS;
                DBCC CHECKIDENT ('asap.EmailDeliveryEvent', RESEED, @eventSeed) WITH NO_INFOMSGS;
                """);
            timeProvider.SetUtcNow(oldTime);
            var patron = AddPostCommitCancellationPatron();
            var patronName = "<img src=x onerror=alert('patron')> " + new string('P', 130);
            patron.Provider.AddPatron(new PatronSnapshot(860_000_000, patron.Barcode, "confirmation@blocked.invalid", patronName, "Recorded & Patron",
                1, "Adult", 101, 2, "Test Library", 101), [new PickupBranch(101, "Main Library")], 2);
            var title = "Activity <b>request</b> " + string.Join(' ', Enumerable.Repeat("Long bibliographic title", 13));
            var created = await CreatePostCommitSuggestionService(patron.Provider, dispatcher!).CreateForStaffAsync(super, 2,
                ActivitySuggestion(patron.Barcode, title, true), CancellationToken.None);
            requestIds.Add(created.Id);
            Assert.AreEqual("suppressed", created.NotificationStatus);
            long confirmationId;
            long collisionId;
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var confirmation = await setup.EmailOutbox.AsNoTracking().SingleAsync(item => item.TitleRequestId == created.Id && item.MessageKind == "confirmation");
                confirmationId = confirmation.Id;
                outboxIds.Add(confirmationId);
                Assert.IsTrue(confirmationId > 9007199254740991L);
                Assert.AreEqual("recipient_domain_not_allowed", confirmation.SuppressionReason);
                var workflow = await setup.TitleRequestEvents.AsNoTracking().SingleAsync(item => item.TitleRequestId == created.Id && item.EventType == "created");
                var delivery = await setup.EmailDeliveryEvents.AsNoTracking().SingleAsync(item => item.EmailOutboxId == confirmationId);
                collisionId = delivery.Id;
                Assert.AreEqual(workflow.CreatedUtc, delivery.ReceivedUtc);
                if (workflow.Id != delivery.Id)
                {
                    var matchingWorkflow = await setup.TitleRequestEvents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == delivery.Id);
                    if (matchingWorkflow is null)
                    {
                        // Fresh and previously-used identity tables can choose
                        // different first values after DBCC. Fill only the free
                        // workflow identity; the email remains real producer evidence.
                        await ExecuteNonQueryAsync("""
                            SET IDENTITY_INSERT [asap].[TitleRequestEvent] ON;
                            INSERT INTO [asap].[TitleRequestEvent] ([Id], [TitleRequestId], [EventType], [ActorType], [Message], [CreatedUtc])
                            VALUES (@id, @request, N'fixture_precision', N'system', N'Equal SQL time and source identity', @created);
                            SET IDENTITY_INSERT [asap].[TitleRequestEvent] OFF;
                            """, ("@id", delivery.Id), ("@request", created.Id), ("@created", delivery.ReceivedUtc));
                    }
                    else
                    {
                        Assert.AreEqual(created.Id, matchingWorkflow.TitleRequestId);
                        Assert.AreEqual(delivery.ReceivedUtc, matchingWorkflow.CreatedUtc);
                    }
                }
                setup.TitleRequestEvents.AddRange(
                    new TitleRequestEvent { TitleRequestId = created.Id, EventType = "fixture_precision", ActorType = "system",
                        Message = "<img src=x onerror=alert('activity')> & " + new string('W', 800), CreatedUtc = oldTime.UtcDateTime.AddTicks(1) },
                    new TitleRequestEvent { TitleRequestId = created.Id, EventType = "fixture_precision", ActorType = "system",
                        Message = "Earlier SQL tick", CreatedUtc = oldTime.UtcDateTime.AddTicks(-1) });
                var row = await setup.TitleRequests.SingleAsync(item => item.Id == created.Id);
                row.Notes = "Saved note <b>text</b>. Email evidence belongs in Activity.";
                await setup.SaveChangesAsync();
            }

            async Task<long> AssignAndDeliverAsync(CurrentStaff recipient, EmailTransportReadiness readiness, string messageId, DateTimeOffset clock)
            {
                timeProvider.SetUtcNow(clock);
                await using var setup = await contexts.CreateDbContextAsync();
                var row = await setup.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == created.Id);
                var assigned = await factory.Services.GetRequiredService<TitleRequestMutationService>().AssignAsync(super, row.Id,
                    new AssignTitleRequestInput(StaffVersion.Encode(row.RowVersion), recipient.Id), CancellationToken.None);
                Assert.AreEqual("updated", assigned.Code);
                var id = assigned.DispatchOutboxIds!.Single();
                outboxIds.Add(id);
                if (readiness.DeliveryMode == "file")
                {
                    transport.Readiness = EmailTransportReadiness.NotConfigured;
                    await CreateEmailOutboxJobs(transport, EmailOutboxRuntimeOptions.Default).DeliverAsync(id, CancellationToken.None);
                    var failed = await ReadEmailOutcomeDispatchSnapshotAsync(id);
                    Assert.AreEqual("failed", failed.Status);
                    Assert.AreEqual("queued", (await factory.Services.GetRequiredService<EmailOperationsService>().RetryAsync(super,
                        id, StaffVersion.Encode(failed.RowVersion), CancellationToken.None)).Code);
                }
                transport.Readiness = readiness;
                transport.Result = new EmailSendResult(messageId) { DeliveryMode = readiness.DeliveryMode };
                await CreateEmailOutboxJobs(transport, EmailOutboxRuntimeOptions.Default).DeliverAsync(id, CancellationToken.None);
                return id;
            }
            var captureId = await AssignAndDeliverAsync(super, EmailTransportReadiness.LocalCapture, "file:i429-browser-capture", oldTime.AddMinutes(1));
            var acceptedId = await AssignAndDeliverAsync(staff, EmailTransportReadiness.Configured, "i429-browser-provider-accepted", oldTime.AddMinutes(2));
            timeProvider.SetUtcNow(originalTime);

            long nullRecipientId;
            long recordedFailureId;
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var missing = new EmailOutbox { OrganizationId = 2, BusinessKey = $"purchase-reminder:{created.Id}:0000000000000001",
                    DeliveryClass = "business_event", Status = "suppressed", SuppressionReason = "recipient_missing",
                    CreatedUtc = oldTime.AddMinutes(3).UtcDateTime, SuppressedUtc = oldTime.AddMinutes(3).UtcDateTime };
                var recorded = LegacyActivityMail($"rejection:{created.Id}:0000000000000001", oldTime.AddMinutes(6).UtcDateTime);
                recorded.LastErrorCode = "<img src=x onerror=alert('reason')>";
                recorded.LastErrorDetail = "PRIVATE-ACTIVITY-ERROR";
                recorded.Subject = "PRIVATE-ACTIVITY-SUBJECT";
                recorded.BodyText = "PRIVATE-ACTIVITY-BODY";
                setup.EmailOutbox.AddRange(missing, recorded);
                setup.EmailOutbox.AddRange(Enumerable.Range(0, 520).Select(index => new EmailOutbox
                {
                    OrganizationId = 2, BusinessKey = $"{marker}:newer:{index}", DeliveryClass = "business_event",
                    ToAddress = "unrelated@example.org", FromAddress = "library@example.org", Status = "sent",
                    CreatedUtc = originalTime.AddMinutes(index / 2).UtcDateTime, SentUtc = originalTime.AddMinutes(index / 2).UtcDateTime,
                    ProviderMessageId = $"unrelated-record-{index}"
                }));
                await setup.SaveChangesAsync();
                nullRecipientId = missing.Id;
                recordedFailureId = recorded.Id;
                outboxIds.AddRange([missing.Id, recorded.Id]);
                outboxIds.AddRange(await setup.EmailOutbox.Where(item => item.BusinessKey != null && item.BusinessKey.StartsWith(marker))
                    .Select(item => item.Id).ToArrayAsync());
            }
            var other = await SeedBibOwnershipRequestAsync($"{marker}-{Guid.NewGuid():N}", null, false);
            requestIds.Add(other.Id);
            var foreignRequest = await SeedBibOwnershipRequestAsync($"{marker}-foreign-{Guid.NewGuid():N}", null, false, libraryOrganizationId: otherLibrary);
            requestIds.Add(foreignRequest.Id);
            var operationId = Guid.NewGuid();
            var queuedTest = await factory.Services.GetRequiredService<EmailOperationsService>().QueueTestAsync(super, otherLibrary,
                CancellationToken.None, operationId);
            Assert.AreEqual("queued", queuedTest.Code);
            EmailOutbox savedTest;
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                savedTest = await setup.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey == $"operational-test:{otherLibrary}:{super.Id}:{operationId:N}");
                outboxIds.Add(savedTest.Id);
            }

            var superDetail = await ReadActivityDetailAsync(super, created.Id);
            var adminDetail = await ReadActivityDetailAsync(admin, created.Id);
            var staffDetail = await ReadActivityDetailAsync(staff, created.Id);
            Assert.IsNull(await factory.Services.GetRequiredService<TitleRequestViewService>().GetAsync(foreign,
                created.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None));
            Assert.IsTrue(superDetail.Activity.Any(item => item.Email?.Outcome == "captured"));
            Assert.IsTrue(superDetail.Activity.Any(item => item.Email?.Outcome == "provider_accepted"));
            Assert.IsTrue(superDetail.Activity.Any(item => item.Email?.Outcome == "failed_no_send"));
            Assert.IsTrue(superDetail.Activity.Any(item => item.Email?.Outcome == "retry_queued"));
            Assert.IsTrue(superDetail.Activity.Any(item => item.Email?.IsRecordedSummary == true));
            Assert.IsTrue(staffDetail.Activity.Where(item => item.Email is not null).All(item => item.Email!.OperationsScope is null));
            Assert.IsTrue(superDetail.Activity.Where(item => item.Email is not null).All(item => item.Email!.OperationReference != savedTest.Id.ToString(CultureInfo.InvariantCulture)));
            var actorKey = string.Join('|', super.EntraTenantId.ToString(), super.Id.ToString(CultureInfo.InvariantCulture),
                super.AuthenticationEmail, super.Role, super.OrganizationId.ToString(CultureInfo.InvariantCulture));
            var recoveryKey = $"asap.staff.email-test.{Uri.EscapeDataString(actorKey)}.{otherLibrary}";
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            await File.WriteAllTextAsync(Path.Combine(artifacts, "fixture.json"), JsonSerializer.Serialize(new
            {
                library = "2", otherLibrary = otherLibrary.ToString(CultureInfo.InvariantCulture),
                requests = new { primary = created.Id.ToString(CultureInfo.InvariantCulture), other = other.Id.ToString(CultureInfo.InvariantCulture),
                    foreign = foreignRequest.Id.ToString(CultureInfo.InvariantCulture) },
                references = new { confirmation = confirmationId.ToString(CultureInfo.InvariantCulture), capture = captureId.ToString(CultureInfo.InvariantCulture),
                    accepted = acceptedId.ToString(CultureInfo.InvariantCulture), nullRecipient = nullRecipientId.ToString(CultureInfo.InvariantCulture),
                    recordedFailure = recordedFailureId.ToString(CultureInfo.InvariantCulture) },
                collisionId = collisionId.ToString(CultureInfo.InvariantCulture), collisionCreatedUtc = oldTime.ToString("O", CultureInfo.InvariantCulture),
                profiles = new { superAdmin = EmailActivityActor(super), libraryAdmin = EmailActivityActor(admin), ordinaryStaff = EmailActivityActor(staff), foreignAdmin = EmailActivityActor(foreign) },
                expectedActivity = new { superAdmin = superDetail.Activity, libraryAdmin = adminDetail.Activity, ordinaryStaff = staffDetail.Activity },
                expectedOrdering = superDetail.Activity.Select(ActivityIdentity).ToArray(),
                savedNotes = superDetail.Notes, patronName, title, newerUnrelatedOperationCount = 520,
                forbiddenText = new[] { "PRIVATE-ACTIVITY-ERROR", "PRIVATE-ACTIVITY-SUBJECT", "PRIVATE-ACTIVITY-BODY", "<img src=x onerror=alert('reason')>" },
                recovery = new { organizationId = otherLibrary, storageKey = recoveryKey,
                    record = new { storageKey = recoveryKey, operationId, id = savedTest.Id.ToString(CultureInfo.InvariantCulture), pending = true,
                        status = savedTest.Status, version = StaffVersion.Encode(savedTest.RowVersion) } }
            }, options));
            var before = await EmailActivityBrowserSnapshotAsync(requestIds, outboxIds);
            var dispatchedBefore = dispatcher!.EnqueuedIds.ToArray();
            var providerCallsBefore = transport.SendCount;
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "request-email-activity.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            var result = report.RootElement;
            Assert.AreEqual(0, result.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, result.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(0, result.GetProperty("writes").GetInt32());
            foreach (var scenario in new[] { "actualSuppression", "sourceAndTickOrdering", "safeText", "activityRetention", "loadingErrorReload",
                "exactOlderReference", "clearReference", "referenceOnlyStaff", "unauthorizedReferenceDenied", "dirtyCancelKeepsDraft",
                "dirtyDiscardNavigates", "testRecoveryUnchanged", "staleDetailRetired", "sessionEpochRetired", "focusMovementPreserved", "noNotesContamination" })
            {
                Assert.IsTrue(result.GetProperty("scenarios").GetProperty(scenario).GetBoolean(), scenario);
            }
            var states = result.GetProperty("states").EnumerateArray().ToArray();
            foreach (var name in new[] { "light-1280", "light-390", "dark-1280", "dark-390", "request-email-native-200" })
            {
                Assert.IsTrue(states.Any(item => item.GetProperty("name").GetString() == name), name);
            }
            foreach (var state in states)
            {
                Assert.AreEqual(0, state.GetProperty("violations").GetArrayLength());
                var layout = state.GetProperty("layout");
                Assert.IsTrue(layout.GetProperty("scrollWidth").GetInt32() <= layout.GetProperty("width").GetInt32());
                Assert.IsTrue(layout.GetProperty("imagesLoaded").GetBoolean());
            }
            var capture = states.Single(item => item.GetProperty("name").GetString() == "request-email-native-200").GetProperty("capture");
            Assert.AreEqual("cdp-viewport", capture.GetProperty("method").GetString());
            Assert.IsTrue(capture.GetProperty("width").GetInt32() >= capture.GetProperty("cssWidth").GetInt32() * 1.9);
            Assert.AreEqual(before, await EmailActivityBrowserSnapshotAsync(requestIds, outboxIds), "Browsing and discarding a draft cannot change request/mail/event state or rowversions.");
            CollectionAssert.AreEqual(dispatchedBefore, dispatcher.EnqueuedIds.ToArray());
            Assert.AreEqual(providerCallsBefore, transport.SendCount);
            Assert.AreEqual(0, browserSender.SendCount);
            Assert.HasCount(0, ((RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>()).Envelopes);
            Assert.IsTrue(browserSender.ReadinessOrganizations.All(id => id is 1 or 2 || id == otherLibrary));
            Assert.IsTrue(browserSender.ReadinessOrganizations.Contains(otherLibrary), "The browser must preserve and inspect the original saved test scope.");
            await File.WriteAllTextAsync(Path.Combine(artifacts, "native-state-evidence.json"), JsonSerializer.Serialize(new
            {
                unchangedRequestMailEventsAndVersions = true, unchangedDispatchIds = dispatchedBefore.Select(id => id.ToString(CultureInfo.InvariantCulture)),
                deterministicSeedProviderCalls = providerCallsBefore, browserProviderCalls = browserSender.SendCount,
                browserReadinessOrganizations = browserSender.ReadinessOrganizations.ToArray()
            }, options));
        }
        finally
        {
            timeProvider.SetUtcNow(originalTime);
            await using var cleanup = await contexts.CreateDbContextAsync();
            var staffIds = staffRows.Select(item => item.Id).ToArray();
            var staffKeys = staffIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
            await DeleteOwnedEmailFixtureAsync(cleanup, cleanup.EmailOutbox.Where(item => outboxIds.Contains(item.Id) ||
                item.TitleRequestId.HasValue && requestIds.Contains(item.TitleRequestId.Value) || item.TestRequestedByStaffUserId.HasValue && staffIds.Contains(item.TestRequestedByStaffUserId.Value) ||
                item.BusinessKey != null && item.BusinessKey.StartsWith(marker)));
            foreach (var requestId in requestIds)
            {
                await DeleteRequestAsync(requestId);
            }
            await cleanup.AdministrativeAudits.Where(item => item.TargetType == "StaffUser" && item.TargetId != null && staffKeys.Contains(item.TargetId)).ExecuteDeleteAsync();
            await cleanup.StaffUsers.Where(item => staffIds.Contains(item.Id)).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == otherLibrary).ExecuteDeleteAsync();
            await ExecuteNonQueryAsync("""
                DECLARE @mailMaximum bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[EmailOutbox]);
                DECLARE @workflowMaximum bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[TitleRequestEvent]);
                DECLARE @deliveryMaximum bigint = (SELECT COALESCE(MAX([Id]), 0) FROM [asap].[EmailDeliveryEvent]);
                DBCC CHECKIDENT ('asap.EmailOutbox', RESEED, @mailMaximum) WITH NO_INFOMSGS;
                DBCC CHECKIDENT ('asap.TitleRequestEvent', RESEED, @workflowMaximum) WITH NO_INFOMSGS;
                DBCC CHECKIDENT ('asap.EmailDeliveryEvent', RESEED, @deliveryMaximum) WITH NO_INFOMSGS;
                """);
        }
    }

    private async Task<string> EmailActivityBrowserSnapshotAsync(IReadOnlyList<long> requestIds, IReadOnlyList<long> outboxIds)
    {
        await using var context = await ActivityContexts().CreateDbContextAsync();
        var requests = await context.TitleRequests.AsNoTracking().Where(item => requestIds.Contains(item.Id)).OrderBy(item => item.Id).ToArrayAsync();
        var mail = await context.EmailOutbox.AsNoTracking().Where(item => outboxIds.Contains(item.Id)).OrderBy(item => item.Id).ToArrayAsync();
        var events = await context.EmailDeliveryEvents.AsNoTracking().Where(item => item.EmailOutboxId.HasValue && outboxIds.Contains(item.EmailOutboxId.Value)).OrderBy(item => item.Id).ToArrayAsync();
        var workflow = await context.TitleRequestEvents.AsNoTracking().Where(item => requestIds.Contains(item.TitleRequestId)).OrderBy(item => item.Id).ToArrayAsync();
        return JsonSerializer.Serialize(new
        {
            requests = requests.Select(item => new { item.Id, item.Status, item.CloseReason, item.Notes, item.Title, item.ClaimedByStaffUserId,
                item.CreatedUtc, item.UpdatedUtc, version = StaffVersion.Encode(item.RowVersion) }),
            mail = mail.Select(item => new { item.Id, item.OrganizationId, item.TitleRequestId, item.MessageKind, item.Status, item.AttemptCount,
                item.NextAttemptUtc, item.LastAttemptUtc, item.SendingStartedUtc, item.LeaseId, item.LeaseExpiresUtc, item.ProviderMessageId,
                item.SuppressionReason, item.LastErrorCode, item.LastErrorDetail, item.CreatedUtc, item.SentUtc, item.SuppressedUtc,
                item.DeliveryClass, item.RecipientStaffUserId, item.RecipientAuthenticationEmail, item.AuthorizationOrganizationId, item.RecipientAddressKind,
                item.ToAddress, item.FromAddress, item.FromName, item.Subject, item.BodyText, item.BodyHtml,
                version = StaffVersion.Encode(item.RowVersion) }),
            events = events.Select(item => new { item.Id, item.EmailOutboxId, item.LocalEventKey, item.ProviderEventId, item.ProviderMessageId,
                item.EventType, item.ReceivedUtc, item.ReasonCode, item.DeliveryMode, item.TimestampKind, item.MetadataJson }),
            workflow = workflow.Select(item => new { item.Id, item.TitleRequestId, item.EventType, item.Message, item.CreatedUtc })
        });
    }

    private static object EmailActivityActor(CurrentStaff actor) => new
    {
        id = actor.Id.ToString(CultureInfo.InvariantCulture), tenantId = actor.EntraTenantId.ToString(), email = actor.AuthenticationEmail,
        role = actor.Role, organizationId = actor.OrganizationId.ToString(CultureInfo.InvariantCulture)
    };

    private sealed class EmailActivityBrowserSender : IEmailSender
    {
        private int sendCount;
        public int SendCount => Volatile.Read(ref sendCount);
        public ConcurrentQueue<int> ReadinessOrganizations { get; } = new();
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadinessOrganizations.Enqueue(organizationId);
            return Task.FromResult(EmailTransportReadiness.Configured);
        }
        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref sendCount);
            return Task.FromException<EmailSendResult>(new InvalidOperationException("Read-only Activity browser journey must never send email."));
        }
    }
}
