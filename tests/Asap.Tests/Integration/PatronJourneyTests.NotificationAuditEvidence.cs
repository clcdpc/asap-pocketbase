using System.Net;
using System.Text;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("reassigned")]
    [DataRow("former_alias")]
    [DataRow("request_conflict")]
    [DataRow("operation_conflict")]
    [DataRow("legacy")]
    [DataRow("operation_only")]
    [DataRow("unverified_alias")]
    public async Task RecordedHoldSuccessCorrelatesEverySavedNativeIdentityBeforeSelectingRecipient(string scenario)
    {
        var seeded = await SeedBibOwnershipRequestAsync($"recipient-recovery-{Guid.NewGuid():N}", 9001,
            staffVerified: true, isbnCheckStatus: "found", status: "pending_hold", autoHold: true);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scripted = ScriptedHoldProvider.AmbiguousCreate();
        scripted.BlockCreate();
        var provider = new AuditNotificationProvider(scripted);
        var outboxDispatcher = new RecordingOutboxDispatcher();
        using var cancellation = new CancellationTokenSource();
        await using var scoped = AuditNotificationFactory(provider, outboxDispatcher);
        var service = scoped.Services.GetRequiredService<HoldPlacementService>();
        try
        {
            var version = await BindAuditNotificationRequestAsync(contexts, seeded.Id, 7105);
            var placing = service.PlaceBackgroundAsync(seeded.Id, version, cancellation.Token);
            await scripted.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            cancellation.Cancel();
            scripted.CompleteBlockedCreate(AuditFinalHoldSuccess(scripted.RequestGuid));
            await Assert.ThrowsAsync<OperationCanceledException>(() => placing);

            long operationId;
            await using (var context = await contexts.CreateDbContextAsync())
            {
                var operation = await context.HoldPlacementOperations.SingleAsync(item => item.TitleRequestId == seeded.Id);
                var request = await context.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual("success", operation.ResultCode);
                Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                Assert.AreEqual("pending_hold", request.Status);
                Assert.AreEqual(0, await context.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await context.EmailOutbox.CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
                if (scenario == "request_conflict")
                {
                    request.PatronIdSnapshot = 7106;
                }
                if (scenario == "operation_conflict")
                {
                    operation.PatronIdSnapshot = 7106;
                }
                if (scenario is "legacy" or "operation_only")
                {
                    request.PatronIdSnapshot = null;
                }
                if (scenario == "legacy")
                {
                    operation.PatronIdSnapshot = null;
                }
                await context.SaveChangesAsync();
            }
            provider.ApplyRecipientScenario(scenario);
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [Id] = @id;",
                ("@id", operationId));
            var recovered = await service.RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            var deliverable = scenario is "former_alias" or "operation_only";
            Assert.AreEqual(deliverable ? "queued" : "suppressed", recovered.NotificationStatus);
            Assert.AreEqual(deliverable ? null : scenario == "legacy" ? "native_patron_identity_unavailable" :
                scenario == "unverified_alias" ? "patron_identity_unverified" : "native_patron_identity_mismatch",
                recovered.NotificationReason);

            await using var verify = await contexts.CreateDbContextAsync();
            var completed = await verify.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.Id == operationId);
            var finalRequest = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
            Assert.AreEqual("hold_placed", finalRequest.Status);
            StringAssert.Contains(finalRequest.Notes!, "Hold placed in Polaris.");
            Assert.AreEqual("Historical pickup A", finalRequest.PreferredPickupBranchName);
            Assert.AreEqual(HoldOperationState.Succeeded, completed.State);
            Assert.AreEqual("success", completed.ResultCode);
            Assert.AreEqual("documented_create_success", completed.OutcomeEvidenceKind);
            Assert.AreEqual(8123, completed.PolarisHoldId);
            Assert.AreEqual(1, scripted.CreateCount);
            Assert.AreEqual(0, scripted.ReplyCount);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed"));
            var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1");
            Assert.AreEqual(deliverable ? "patron-x@example.org" : null, outbox.ToAddress);
            Assert.AreEqual(deliverable ? "pending" : "suppressed", outbox.Status);
            Assert.AreEqual(deliverable ? 1 : 0, outboxDispatcher.EnqueuedIds.Count);
            await service.RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual(1, scripted.CreateCount);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed"));
            Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
        }
    }

    [TestMethod]
    [DataRow("reassigned", false)]
    [DataRow("former_alias", false)]
    [DataRow("operation_conflict", false)]
    [DataRow("legacy", false)]
    [DataRow("former_alias", true)]
    [DataRow("unverified_alias", false)]
    public async Task OperatorConfirmedHoldSuccessSuppressesUncorrelatedRecipientWithoutChangingOutcome(string scenario, bool missingPickupName)
    {
        var seeded = await SeedBibOwnershipRequestAsync($"recipient-operator-{Guid.NewGuid():N}", 9001,
            staffVerified: true, isbnCheckStatus: "found", status: "pending_hold", autoHold: true);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scripted = ScriptedHoldProvider.AmbiguousCreate();
        var provider = new AuditNotificationProvider(scripted)
        {
            PickupBranchId = missingPickupName ? 99682 : 101,
            PickupBranchName = missingPickupName ? "" : "Actual provider pickup"
        };
        var outboxDispatcher = new RecordingOutboxDispatcher();
        await using var scoped = AuditNotificationFactory(provider, outboxDispatcher);
        var service = scoped.Services.GetRequiredService<HoldPlacementService>();
        long operationId = 0;
        try
        {
            var version = await BindAuditNotificationRequestAsync(contexts, seeded.Id, 7105);
            var placed = await service.PlaceBackgroundAsync(seeded.Id, version, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", placed.Code);
            await using var context = await contexts.CreateDbContextAsync();
            var operation = await context.HoldPlacementOperations.SingleAsync(item => item.TitleRequestId == seeded.Id);
            operationId = operation.Id;
            var request = await context.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            if (scenario == "operation_conflict")
            {
                operation.PatronIdSnapshot = 7106;
            }
            if (scenario == "legacy")
            {
                request.PatronIdSnapshot = null;
                operation.PatronIdSnapshot = null;
            }
            await context.SaveChangesAsync();
            provider.ApplyRecipientScenario(scenario);
            var result = await service.ResolveAsync(await ReadConfiguredSuperAdminAsync(), operation.Id,
                new ResolveHoldOperationInput(StaffVersion.Encode(operation.RowVersion), StaffVersion.Encode(request.RowVersion),
                    "succeeded", "Provider confirmed this attempt", "authoritative_correlated_hold", "fixture:provider-final-report",
                    true, "deterministic provider report", "Report identifies this exact dispatched attempt", 8123,
                    true, true, "fixture:executor-ended", "The fixture provider execution returned and has ended."),
                CancellationToken.None);
            Assert.AreEqual("resolved", result.Code);
            Assert.AreEqual("hold_placed", result.FinalStatus);
            var deliverable = scenario == "former_alias";
            Assert.AreEqual(deliverable ? "queued" : "suppressed", result.NotificationStatus);
            Assert.AreEqual(deliverable ? null : scenario == "legacy" ? "native_patron_identity_unavailable" :
                scenario == "unverified_alias" ? "patron_identity_unverified" : "native_patron_identity_mismatch", result.NotificationReason);
            await using var verify = await contexts.CreateDbContextAsync();
            var completed = await verify.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.Id == operation.Id);
            Assert.AreEqual("success", completed.ResultCode);
            Assert.AreEqual(HoldOperationState.Succeeded, completed.State);
            Assert.AreEqual(8123, completed.PolarisHoldId);
            var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1");
            Assert.AreEqual(deliverable ? "patron-x@example.org" : null, outbox.ToAddress);
            if (missingPickupName)
            {
                Assert.AreEqual($"A hold has been placed for {request.Title}.", outbox.BodyText);
            }
            else
            {
                StringAssert.Contains(outbox.BodyText!, "Actual provider pickup");
            }
            Assert.IsFalse(outbox.BodyText!.Contains("Historical pickup A", StringComparison.Ordinal));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed"));
            Assert.AreEqual(1, await verify.AdministrativeAudits.CountAsync(item => item.TargetType == "hold_placement_operation" && item.TargetId == operation.Id.ToString()));
            Assert.AreEqual(1, scripted.CreateCount);
            Assert.AreEqual(0, scripted.ReplyCount);
            Assert.AreEqual(deliverable ? 1 : 0, outboxDispatcher.EnqueuedIds.Count);
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.AdministrativeAudits.Where(item => item.TargetType == "hold_placement_operation" && item.TargetId == operationId.ToString()).ExecuteDeleteAsync();
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
        }
    }

    [TestMethod]
    [DataRow("reject", "reassigned")]
    [DataRow("purchase", "reassigned")]
    [DataRow("alreadyOwn", "reassigned")]
    [DataRow("reject", "former_alias")]
    [DataRow("purchase", "former_alias")]
    [DataRow("alreadyOwn", "former_alias")]
    [DataRow("reject", "legacy")]
    [DataRow("purchase", "legacy")]
    [DataRow("alreadyOwn", "legacy")]
    [DataRow("reject", "operation_conflict")]
    [DataRow("purchase", "operation_conflict")]
    [DataRow("alreadyOwn", "operation_conflict")]
    [DataRow("reject", "successful_operation_only")]
    [DataRow("purchase", "successful_operation_only")]
    [DataRow("alreadyOwn", "successful_operation_only")]
    [DataRow("reject", "unverified_alias")]
    [DataRow("purchase", "unverified_alias")]
    [DataRow("alreadyOwn", "unverified_alias")]
    [DataRow("reject", "operation_unverified_alias")]
    [DataRow("purchase", "operation_unverified_alias")]
    [DataRow("alreadyOwn", "operation_unverified_alias")]
    public async Task StaffBusinessActionCorrelatesSavedNativeIdentityAndVerifiedFormerAliases(string action, string scenario)
    {
        var seeded = await SeedBibOwnershipRequestAsync($"staff-recipient-{Guid.NewGuid():N}", action == "alreadyOwn" ? 9001 : null,
            staffVerified: action == "alreadyOwn", status: "suggestion", autoHold: action != "alreadyOwn");
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var provider = new AuditNotificationProvider(ScriptedHoldProvider.AmbiguousCreate());
        await using var scoped = AuditNotificationFactory(provider, new RecordingOutboxDispatcher());
        try
        {
            var version = await BindAuditNotificationRequestAsync(contexts, seeded.Id, scenario is "legacy" or "successful_operation_only" ? null : 7105);
            if (scenario is "operation_conflict" or "successful_operation_only" or "operation_unverified_alias")
            {
                await using var setup = await contexts.CreateDbContextAsync();
                var savedRequest = await setup.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                setup.HoldPlacementOperations.Add(new HoldPlacementOperation
                {
                    TitleRequestId = seeded.Id, PatronBarcodeSnapshot = scenario == "operation_unverified_alias" ? "unverified-operation-barcode" : savedRequest.Barcode,
                    PatronIdSnapshot = scenario == "operation_conflict" ? 7106 : 7105,
                    BibIdSnapshot = 9001, AttemptNumber = 1, ExecutionEpoch = 1, State = HoldOperationState.Succeeded,
                    Phase = HoldOperationPhase.ResultRecorded, ResultCode = "success", OutcomeEvidenceKind = "documented_create_success",
                    RequestStartedUtc = savedRequest.CreatedUtc, CompletedUtc = savedRequest.CreatedUtc
                });
                await setup.SaveChangesAsync();
            }
            provider.ApplyRecipientScenario(scenario);
            var result = await scoped.Services.GetRequiredService<TitleRequestMutationService>().ActionAsync(
                await ReadConfiguredSuperAdminAsync(), seeded.Id,
                new TitleRequestActionInput { Version = StaffVersion.Encode(version), Action = action }.ToCommand(), CancellationToken.None);
            Assert.AreEqual("updated", result.Code);
            var deliverable = scenario is "former_alias" or "successful_operation_only";
            await using var verify = await contexts.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
            Assert.AreEqual(action == "purchase" ? "outstanding_purchase" : "closed", request.Status);
            Assert.AreEqual(scenario is "legacy" or "successful_operation_only" ? null : (int?)7105, request.PatronIdSnapshot);
            var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey != null &&
                (item.BusinessKey.StartsWith($"rejection:{seeded.Id}:") || item.BusinessKey.Contains($":{seeded.Id}:")));
            Assert.AreEqual(deliverable ? "pending" : "suppressed", outbox.Status);
            Assert.AreEqual(deliverable ? "patron-x@example.org" : null, outbox.ToAddress);
            Assert.AreEqual(deliverable ? null : scenario == "legacy" ? "native_patron_identity_unavailable" :
                scenario is "unverified_alias" or "operation_unverified_alias" ? "patron_identity_unverified" : "native_patron_identity_mismatch", outbox.SuppressionReason);
            if (!deliverable)
            {
                Assert.IsFalse(outbox.BodyText!.Contains("Different patron", StringComparison.Ordinal));
            }
            Assert.IsTrue(await verify.TitleRequestEvents.AnyAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "status_changed"));
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.EmailOutbox.Where(item => item.BusinessKey != null &&
                (item.BusinessKey.StartsWith($"rejection:{seeded.Id}:") || item.BusinessKey.Contains($":{seeded.Id}:"))).ExecuteDeleteAsync();
            await DeleteRequestAsync(seeded.Id);
        }
    }

    [TestMethod]
    [DataRow("new", false)]
    [DataRow("new", true)]
    [DataRow("adoption", false)]
    [DataRow("adoption", true)]
    [DataRow("recovery", false)]
    [DataRow("recovery", true)]
    public async Task HoldNotificationUsesFrozenActualPickupOrGenericWording(string flow, bool missingName)
    {
        const int actualPickupId = 99681;
        var seeded = await SeedBibOwnershipRequestAsync($"pickup-body-{Guid.NewGuid():N}", 9001,
            staffVerified: true, isbnCheckStatus: "found", status: "pending_hold", autoHold: true);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scripted = ScriptedHoldProvider.AmbiguousCreate();
        scripted.BlockCreate();
        var provider = new AuditNotificationProvider(scripted)
        {
            PickupBranchId = actualPickupId,
            PickupBranchName = missingName ? "" : "Actual pickup B"
        };
        if (flow == "adoption")
        {
            scripted.Holds = [new PolarisHoldSnapshot(8123, 9001, 2, "Active", actualPickupId)];
        }
        await using var scoped = AuditNotificationFactory(provider, new RecordingOutboxDispatcher());
        var service = scoped.Services.GetRequiredService<HoldPlacementService>();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var version = await BindAuditNotificationRequestAsync(contexts, seeded.Id, 7105);
            if (!missingName)
            {
                await using var setup = await contexts.CreateDbContextAsync();
                setup.Organizations.Add(new Organization { Id = actualPickupId,
                    DisplayName = flow == "adoption" ? "Actual pickup B" : "Changed cached name", Abbreviation = "APB",
                    OrganizationCodeId = 3, ParentOrganizationId = 2, IsActive = true });
                await setup.SaveChangesAsync();
            }
            var placing = service.PlaceBackgroundAsync(seeded.Id, version, cancellation.Token);
            if (flow != "adoption")
            {
                await scripted.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(actualPickupId, provider.CreateCommand!.PickupBranchId);
                await using (var intent = await contexts.CreateDbContextAsync())
                {
                    var frozen = await intent.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.TitleRequestId == seeded.Id);
                    using var detail = System.Text.Json.JsonDocument.Parse(frozen.DetailJson!);
                    Assert.AreEqual(actualPickupId, detail.RootElement.GetProperty("pickupBranchId").GetInt32());
                    Assert.AreEqual(missingName ? null : "Actual pickup B", detail.RootElement.GetProperty("pickupBranchName").GetString());
                }
                if (flow == "recovery")
                {
                    cancellation.Cancel();
                }
                scripted.CompleteBlockedCreate(AuditFinalHoldSuccess(scripted.RequestGuid));
            }
            if (flow == "recovery")
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => placing);
                await using var journal = await contexts.CreateDbContextAsync();
                var operation = await journal.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.TitleRequestId == seeded.Id);
                await ExecuteNonQueryAsync("UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [Id] = @id;", ("@id", operation.Id));
                Assert.AreEqual("updated", (await service.RecoverBackgroundOperationAsync(operation.Id, null, CancellationToken.None)).Code);
            }
            else
            {
                Assert.AreEqual("updated", (await placing).Code);
            }
            await using var verify = await contexts.CreateDbContextAsync();
            var completed = await verify.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.TitleRequestId == seeded.Id);
            Assert.AreEqual(actualPickupId, completed.PickupBranchIdSnapshot);
            Assert.AreEqual("Historical pickup A", (await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id)).PreferredPickupBranchName);
            var outbox = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1");
            Assert.AreEqual("pending", outbox.Status);
            Assert.IsFalse(outbox.BodyText!.Contains("Historical pickup A", StringComparison.Ordinal));
            if (missingName)
            {
                var requestTitle = await verify.TitleRequests.Where(item => item.Id == seeded.Id).Select(item => item.Title).SingleAsync();
                Assert.AreEqual($"A hold has been placed for {requestTitle}.", outbox.BodyText);
            }
            else
            {
                StringAssert.Contains(outbox.BodyText!, "Actual pickup B");
                Assert.IsFalse(outbox.BodyText!.Contains("Changed cached name", StringComparison.Ordinal));
            }
            Assert.AreEqual(flow == "adoption" ? 0 : 1, scripted.CreateCount);
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.Organizations.Where(item => item.Id == actualPickupId).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    [DataRow("{\"ErrorCode\":0,\"MessageID\":\"accepted-id\"}", true)]
    [DataRow("{\"ErrorCode\":500,\"ErrorCode\":0,\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":0,\"ErrorCode\":0,\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":0,\"MessageID\":\"first-id\",\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"errorcode\":500,\"ErrorCode\":0,\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":0,\"messageid\":\"first-id\",\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":0,\"Message\\u0049D\":\"first-id\",\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":\"0\",\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":null,\"MessageID\":\"accepted-id\"}", false)]
    [DataRow("{\"ErrorCode\":0,\"MessageID\":42}", false)]
    [DataRow("{\"ErrorCode\":0,\"MessageID\":null}", false)]
    [DataRow("{\"ErrorCode\":0,\"MessageID\":\" \"}", false)]
    [DataRow("{\"ErrorCode\":0}", false)]
    [DataRow("[{\"ErrorCode\":0,\"MessageID\":\"accepted-id\"}]", false)]
    [DataRow("{\"ErrorCode\":0,", false)]
    public async Task PostmarkAcceptanceRequiresUniqueTypedEvidenceAndNeverReplaysUncertainDelivery(string responseBody, bool accepted)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var protector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        var postCount = 0;
        using var client = new HttpClient(new EmailOutcomePostmarkHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ID\":42,\"DeliveryType\":\"Live\"}", Encoding.UTF8, "application/json") });
            }
            Interlocked.Increment(ref postCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") });
        }));
        var seeded = await SeedSensitiveOutboxAsync($"postmark-evidence-{Guid.NewGuid():N}");
        EmailOutcomeSystemSettingsSnapshot? settings = null;
        try
        {
            settings = await ConfigureEmailOutcomePostmarkAsync(contexts, protector);
            var sender = new PostmarkEmailSender(contexts, protector, new EmailOutcomeHttpClientFactory(client), NullLogger<PostmarkEmailSender>.Instance);
            var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
            var before = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
            var dispatched = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
            Assert.AreEqual(1, dispatched.AttemptCount);
            Assert.AreEqual(accepted ? "sent" : "sending", dispatched.Status);
            Assert.AreEqual(accepted ? "accepted-id" : null, dispatched.ProviderMessageId);
            Assert.AreEqual(accepted ? null : before.NextAttemptUtc, dispatched.NextAttemptUtc,
                "An ambiguous dispatched row retains its original due timestamp until lease quarantine; it does not schedule a retry.");
            if (!accepted)
            {
                Assert.AreEqual("transport_failure", dispatched.LastErrorCode);
                Assert.IsNotNull(dispatched.SendingStartedUtc);
                Assert.IsNotNull(dispatched.LeaseId);
                await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
                Assert.AreEqual(1, Volatile.Read(ref postCount));
                await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
                await jobs.SweepAsync(CancellationToken.None);
                var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
                Assert.AreEqual("failed", quarantined.Status);
                Assert.AreEqual("transport_failure", quarantined.LastErrorCode,
                    "Lease quarantine preserves the recorded post-dispatch failure evidence.");
                Assert.IsNull(quarantined.ProviderMessageId);
                Assert.IsNull(quarantined.NextAttemptUtc);
                Assert.IsNull(quarantined.LeaseId);
                var operations = new EmailOperationsService(contexts, dispatcher!, sender,
                    factory.Services.GetRequiredService<RecipientDomainPolicy>(), timeProvider!, factory.Services.GetRequiredService<StaffEligibilityService>());
                Assert.AreEqual("email_not_retryable", (await operations.RetryAsync(await ReadConfiguredSuperAdminAsync(), seeded.OutboxId,
                    StaffVersion.Encode(quarantined.RowVersion), CancellationToken.None)).Code);
                AssertEmailOutcomeDispatchSnapshotEqual(quarantined, await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
            }
            await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
            Assert.AreEqual(1, Volatile.Read(ref postCount));
        }
        finally
        {
            await DeleteEmailOutcomeFixtureAsync(seeded);
            if (settings is not null)
            {
                await RestoreEmailOutcomePostmarkAsync(contexts, settings);
            }
        }
    }

    [TestMethod]
    [DataRow("{\"ID\":42,\"ID\":43,\"DeliveryType\":\"Live\"}")]
    [DataRow("{\"ID\":42,\"DeliveryType\":\"Sandbox\",\"DeliveryType\":\"Live\"}")]
    [DataRow("{\"ID\":42,\"deliverytype\":\"Sandbox\",\"DeliveryType\":\"Live\"}")]
    [DataRow("{\"ID\":\"42\",\"DeliveryType\":\"Live\"}")]
    public async Task PostmarkReadinessRejectsAmbiguousServerEvidenceBeforeAnyEmailPost(string responseBody)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var protector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        var postCount = 0;
        using var client = new HttpClient(new EmailOutcomePostmarkHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") });
            }
            Interlocked.Increment(ref postCount);
            throw new AssertFailedException("Invalid readiness evidence must prevent the email POST.");
        }));
        var seeded = await SeedSensitiveOutboxAsync($"postmark-readiness-evidence-{Guid.NewGuid():N}");
        EmailOutcomeSystemSettingsSnapshot? settings = null;
        try
        {
            settings = await ConfigureEmailOutcomePostmarkAsync(contexts, protector);
            var sender = new PostmarkEmailSender(contexts, protector, new EmailOutcomeHttpClientFactory(client), NullLogger<PostmarkEmailSender>.Instance);
            await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(seeded.OutboxId, CancellationToken.None);
            var state = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
            Assert.AreEqual("pending", state.Status);
            Assert.AreEqual("mail_readiness_unavailable", state.LastErrorCode);
            Assert.AreEqual(0, state.AttemptCount);
            Assert.IsNull(state.SendingStartedUtc);
            Assert.IsNull(state.ProviderMessageId);
            Assert.IsNull(state.LeaseId);
            Assert.AreEqual(0, Volatile.Read(ref postCount));
        }
        finally
        {
            await DeleteEmailOutcomeFixtureAsync(seeded);
            if (settings is not null)
            {
                await RestoreEmailOutcomePostmarkAsync(contexts, settings);
            }
        }
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> AuditNotificationFactory(AuditNotificationProvider provider, RecordingOutboxDispatcher outboxDispatcher) =>
        factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IStaffPolarisProvider>(provider);
            services.RemoveAll<IEmailOutboxDispatcher>();
            services.AddSingleton<IEmailOutboxDispatcher>(outboxDispatcher);
        }));

    private static async Task<byte[]> BindAuditNotificationRequestAsync(IDbContextFactory<AsapDbContext> contexts, long requestId, int? nativeId)
    {
        await using var setup = await contexts.CreateDbContextAsync();
        var request = await setup.TitleRequests.SingleAsync(item => item.Id == requestId);
        request.PatronIdSnapshot = nativeId;
        request.PreferredPickupBranchName = "Historical pickup A";
        await setup.SaveChangesAsync();
        return request.RowVersion;
    }

    private static HoldProviderResult AuditFinalHoldSuccess(Guid requestGuid) =>
        new(HoldProviderOutcome.FinalSuccess, requestGuid, 8123, null, null, 2, 1, "documented_create_success");

    private sealed class AuditNotificationProvider(ScriptedHoldProvider holds) : IPatronProvider, IStaffPolarisProvider
    {
        private int nativeId = 7105;
        private bool formerAlias;
        private bool unverifiedAlias;
        public int PickupBranchId { get; set; } = 101;
        public string PickupBranchName { get; set; } = "Actual provider pickup";
        public HoldCreateCommand? CreateCommand { get; private set; }

        public void ApplyRecipientScenario(string scenario)
        {
            nativeId = scenario == "reassigned" ? 7106 : 7105;
            formerAlias = scenario == "former_alias";
            unverifiedAlias = scenario == "unverified_alias";
        }

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PatronSnapshot(nativeId, unverifiedAlias ? "unrelated-provider-barcode" : formerAlias ? "current-rotated-barcode" : barcode,
                nativeId == 7105 ? "patron-x@example.org" : "patron-y@example.org", nativeId == 7105 ? "Original patron" : "Different patron",
                "Last", 1, "Adult", 101, organizationId, "Test Library", PickupBranchId, FormerBarcode: formerAlias ? barcode.ToUpperInvariant() : null));
        }

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken) => RefreshAsync(barcode, 2, cancellationToken);
        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron, int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickupBranch>>([new PickupBranch(PickupBranchId, PickupBranchName)]);
        public Task UpdatePreferredPickupBranchAsync(string barcode, int pickupBranchId, int organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, int organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken cancellationToken) => holds.ValidateBibAsync(bibId, organizationId, cancellationToken);
        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int organizationId, CancellationToken cancellationToken) => holds.GetPatronHoldsAsync(barcode, organizationId, cancellationToken);
        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken cancellationToken)
        {
            CreateCommand = command;
            return holds.CreateHoldAsync(command, cancellationToken);
        }
        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken cancellationToken) => holds.ReplyToHoldAsync(command, cancellationToken);
    }
}
