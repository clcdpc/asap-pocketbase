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
    public async Task CancelledHoldCreateDurablyRecordsDefinitiveSuccessBeforeRecovery()
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-create-known-cancel-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        var rootFactory = factory ?? throw new AssertFailedException("The test web host was not initialized.");
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.BlockCreate();
        var dispatcher = new RecordingOutboxDispatcher();
        using var cancellation = new CancellationTokenSource();
        long operationId = 0;
        Guid originalOwnerToken = Guid.Empty;
        long originalEpoch = 0;

        try
        {
            var responseJson = JsonSerializer.Serialize(new
            {
                PAPIErrorCode = 0,
                StatusType = 2,
                StatusValue = 1,
                RequestGUID = holdProvider.RequestGuid
            });
            var boundaryHandler = new ProviderBoundaryHandler((_, _) =>
                Task.FromResult(BoundaryResponse(responseJson)));
            var polarisProvider = await CreatePolarisProviderAsync(
                boundaryHandler,
                "hold-create-known-cancel-" + Guid.NewGuid().ToString("N"));
            var definiteSuccess = await polarisProvider.CreateHoldAsync(BoundaryCreate(), CancellationToken.None);
            Assert.AreEqual(HoldProviderOutcome.FinalSuccess, definiteSuccess.Outcome);
            Assert.AreEqual(holdProvider.RequestGuid, definiteSuccess.RequestGuid);
            Assert.AreEqual(2, definiteSuccess.StatusType);
            Assert.AreEqual(1, definiteSuccess.StatusValue);
            Assert.AreEqual("documented_create_success", definiteSuccess.EvidenceKind);
            Assert.AreEqual(1, boundaryHandler.OperationCalls);

            await using (var setup = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await setup.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
                request.PatronIdSnapshot = 7105;
                await setup.SaveChangesAsync();
            }
            var before = await ReadBibOwnershipRequestAsync(seeded.Id);
            await using var scopedFactory = rootFactory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(holdProvider);
                    services.RemoveAll<IStaffPolarisProvider>();
                    services.AddSingleton<IStaffPolarisProvider>(holdProvider);
                    services.RemoveAll<IEmailOutboxDispatcher>();
                    services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
                }));

            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placing = placement.PlaceBackgroundAsync(seeded.Id, before.RowVersion, cancellation.Token);
            await holdProvider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var marker = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var operation = await marker.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateStartedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                originalOwnerToken = operation.OwnerToken.Value;
                originalEpoch = operation.ExecutionEpoch;
                Assert.IsNull(operation.CreateResponseObservedUtc);
                Assert.AreEqual(0, await marker.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await marker.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }

            cancellation.Cancel();
            holdProvider.CompleteBlockedCreate(definiteSuccess);
            await Assert.ThrowsAsync<OperationCanceledException>(() => placing);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, holdProvider.CreateCount);
            Assert.AreEqual(0, holdProvider.ReplyCount);

            await using (var verify = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verify.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status,
                    "A known provider result must be journaled before caller cancellation stops local completion.");
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateStartedUtc);
                Assert.IsNotNull(operation.CreateResponseObservedUtc);
                Assert.AreEqual(originalOwnerToken, operation.OwnerToken);
                Assert.AreEqual(originalEpoch, operation.ExecutionEpoch);
                Assert.IsNotNull(operation.LeaseExpiresUtc);
                Assert.AreEqual(definiteSuccess.RequestGuid, operation.PolarisRequestGuid);
                Assert.IsNull(operation.PolarisHoldId,
                    "The documented final-success response establishes the request GUID, not a final hold ID.");
                Assert.AreEqual(2, operation.ProviderStatusType);
                Assert.AreEqual(1, operation.ProviderStatusValue);
                Assert.AreEqual("success", operation.ResultCode);
                Assert.AreEqual("documented_create_success", operation.OutcomeEvidenceKind);
                Assert.IsNull(operation.CompletedUtc);
                Assert.IsNull(operation.LastErrorCode);
                Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }
            await using (var leaseConnection = new SqlConnection(databaseConnectionString))
            {
                await leaseConnection.OpenAsync();
                await using var liveLease = new SqlCommand(
                    "SELECT CASE WHEN [LeaseExpiresUtc] > SYSUTCDATETIME() AND [OwnerToken] = @ownerToken " +
                    "AND [ExecutionEpoch] = @epoch AND [Phase] = N'result_recorded' THEN 1 ELSE 0 END " +
                    "FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;",
                    leaseConnection);
                liveLease.Parameters.AddWithValue("@id", operationId);
                liveLease.Parameters.AddWithValue("@ownerToken", originalOwnerToken);
                liveLease.Parameters.AddWithValue("@epoch", originalEpoch);
                Assert.AreEqual(1, Convert.ToInt32(await liveLease.ExecuteScalarAsync()),
                    "The known provider response must remain fenced by the original live SQL owner lease.");
            }
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);

            await ExecuteNonQueryAsync(
                "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) " +
                "WHERE [Id] = @id AND [OwnerToken] = @ownerToken AND [ExecutionEpoch] = @epoch " +
                "AND [Phase] = N'result_recorded';",
                ("@id", operationId),
                ("@ownerToken", originalOwnerToken),
                ("@epoch", originalEpoch));
            var recovered = await placement.RecoverBackgroundOperationAsync(
                operationId,
                scopeOrganizationId: null,
                CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, holdProvider.CreateCount, "Recovery must use the durable success without another create call.");
            Assert.AreEqual(0, holdProvider.ReplyCount);
            Assert.AreEqual(1, boundaryHandler.OperationCalls,
                "No recovery path may dispatch another real or simulated create operation.");

            await using (var verifyRecovery = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verifyRecovery.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verifyRecovery.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("hold_placed", request.Status);
                Assert.AreEqual(HoldOperationState.Succeeded, operation.State);
                Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                Assert.AreEqual(originalEpoch + 1, operation.ExecutionEpoch);
                Assert.AreEqual(definiteSuccess.RequestGuid, operation.PolarisRequestGuid);
                Assert.IsNull(operation.PolarisHoldId,
                    "A final-success create response does not establish a hold identity.");
                Assert.AreEqual("success", operation.ResultCode);
                Assert.AreEqual(2, operation.ProviderStatusType);
                Assert.AreEqual(1, operation.ProviderStatusValue);
                Assert.AreEqual("documented_create_success", operation.OutcomeEvidenceKind);
                Assert.IsNotNull(operation.CompletedUtc);
                Assert.AreEqual("hold_identity_unavailable", operation.LastErrorCode);
                Assert.IsNull(operation.OwnerToken);
                Assert.IsNull(operation.LeaseExpiresUtc);
                var placedEvent = await verifyRecovery.TitleRequestEvents.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed");
                var placedEventMetadata = placedEvent.MetadataJson ??
                    throw new AssertFailedException("The hold_placed event must include metadata.");
                using (var metadata = JsonDocument.Parse(placedEventMetadata))
                {
                    Assert.IsFalse(metadata.RootElement.GetProperty("holdIdentityAvailable").GetBoolean());
                }
                var outbox = await verifyRecovery.EmailOutbox.AsNoTracking()
                    .SingleAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1");
                CollectionAssert.AreEqual(new[] { outbox.Id }, dispatcher.EnqueuedIds);
            }
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
        }
    }

    [TestMethod]
    [DataRow("create_no_effect")]
    [DataRow("create_reply_required")]
    [DataRow("reply_success")]
    [DataRow("reply_no_effect")]
    public async Task CancelledHoldProviderReturnPersistsPinnedKnownOutcomes(string scenario)
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-known-outcome-{scenario}-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        var rootFactory = factory ?? throw new AssertFailedException("The test web host was not initialized.");
        var isReplyScenario = scenario is "reply_success" or "reply_no_effect";
        var requiresRecoveryReply = scenario == "create_reply_required";
        var hasPinnedReplyResponse = isReplyScenario || requiresRecoveryReply;
        var createRequiresReply = scenario != "create_no_effect";
        var holdProvider = isReplyScenario
            ? ScriptedHoldProvider.ReplyRequiredThenSuccess()
            : ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.BlockCreate();
        if (hasPinnedReplyResponse)
        {
            holdProvider.BlockReply();
        }
        var dispatcher = new RecordingOutboxDispatcher();
        using var cancellation = new CancellationTokenSource();
        long operationId = 0;
        Guid originalOwnerToken = Guid.Empty;
        long originalEpoch = 0;
        var requestingOrganizationIdSnapshot = 0;

        try
        {
            await using (var setup = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await setup.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
                request.PatronIdSnapshot = 7105;
                await setup.SaveChangesAsync();
            }

            var createResponseJson = scenario switch
            {
                "create_no_effect" => """{"PAPIErrorCode":0,"StatusType":1,"StatusValue":-4006}""",
                _ => JsonSerializer.Serialize(new
                {
                    PAPIErrorCode = 0,
                    StatusType = 3,
                    StatusValue = 5,
                    RequestGUID = holdProvider.RequestGuid,
                    TxnGroupQualifer = "group-qualifier",
                    TxnQualifier = "transaction-qualifier",
                    QueuePosition = 3
                })
            };
            var (createResult, createOperationCalls) = await ReadPinnedHoldOutcomeAsync(
                createResponseJson,
                isReply: false,
                holdProvider.RequestGuid,
                "hold-known-create-" + Guid.NewGuid().ToString("N"));
            Assert.AreEqual(createRequiresReply ? HoldProviderOutcome.ReplyRequired : HoldProviderOutcome.DefinitiveNoEffect,
                createResult.Outcome);
            Assert.AreEqual(1, createOperationCalls);
            Assert.AreEqual(createRequiresReply ? 3 : 1, createResult.StatusType);
            Assert.AreEqual(createRequiresReply ? 5 : -4006, createResult.StatusValue);

            HoldProviderResult? replyResult = null;
            var replyOperationCalls = 0;
            if (hasPinnedReplyResponse)
            {
                var replyStatusValue = scenario == "reply_no_effect" ? 0 : 1;
                var replyResponseJson = JsonSerializer.Serialize(new
                {
                    PAPIErrorCode = 0,
                    StatusType = 2,
                    StatusValue = replyStatusValue,
                    RequestGUID = holdProvider.RequestGuid
                });
                var (result, calls) = await ReadPinnedHoldOutcomeAsync(
                    replyResponseJson,
                    isReply: true,
                    holdProvider.RequestGuid,
                    "hold-known-reply-" + Guid.NewGuid().ToString("N"));
                replyResult = result;
                replyOperationCalls = calls;
                Assert.AreEqual(scenario == "reply_no_effect" ? HoldProviderOutcome.DefinitiveNoEffect : HoldProviderOutcome.FinalSuccess,
                    replyResult.Outcome);
                Assert.AreEqual(1, replyOperationCalls);
                Assert.AreEqual(2, replyResult.StatusType);
                Assert.AreEqual(replyStatusValue, replyResult.StatusValue);
                Assert.AreEqual(holdProvider.RequestGuid, replyResult.RequestGuid);
            }

            Assert.AreEqual(createRequiresReply ? holdProvider.RequestGuid : null, createResult.RequestGuid);
            Assert.AreEqual(createRequiresReply ? "group-qualifier" : null, createResult.TxnGroupQualifier);
            Assert.AreEqual(createRequiresReply ? "transaction-qualifier" : null, createResult.TxnQualifier);

            var before = await ReadBibOwnershipRequestAsync(seeded.Id);
            await using var scopedFactory = rootFactory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(holdProvider);
                    services.RemoveAll<IStaffPolarisProvider>();
                    services.AddSingleton<IStaffPolarisProvider>(holdProvider);
                    services.RemoveAll<IEmailOutboxDispatcher>();
                    services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
                }));

            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placing = placement.PlaceBackgroundAsync(seeded.Id, before.RowVersion, cancellation.Token);
            await holdProvider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (isReplyScenario)
            {
                holdProvider.CompleteBlockedCreate(createResult);
                await holdProvider.ReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await using (var marker = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var operation = await marker.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual(isReplyScenario ? HoldOperationPhase.ReplyStarted : HoldOperationPhase.CreateStarted,
                    operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.OwnerToken);
                originalOwnerToken = operation.OwnerToken.Value;
                originalEpoch = operation.ExecutionEpoch;
                Assert.IsNotNull(operation.LeaseExpiresUtc);
                Assert.AreEqual(isReplyScenario, operation.CreateResponseObservedUtc.HasValue);
                Assert.IsNull(operation.ReplyResponseObservedUtc);
                if (requiresRecoveryReply)
                {
                    Assert.IsNotNull(operation.RequestingOrganizationIdSnapshot);
                    requestingOrganizationIdSnapshot = operation.RequestingOrganizationIdSnapshot.Value;
                }
                Assert.AreEqual(0, await marker.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await marker.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }

            cancellation.Cancel();
            if (isReplyScenario)
            {
                holdProvider.CompleteBlockedReply(replyResult ??
                    throw new AssertFailedException("A reply scenario must carry its pinned HTTP response."));
            }
            else
            {
                holdProvider.CompleteBlockedCreate(createResult);
            }
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => placing);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, holdProvider.CreateCount);
            Assert.AreEqual(isReplyScenario ? 1 : 0, holdProvider.ReplyCount,
                "A canceled create response that requests a reply must stop before another provider call.");

            await using (var verify = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verify.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(originalOwnerToken, operation.OwnerToken);
                Assert.AreEqual(originalEpoch, operation.ExecutionEpoch);
                Assert.IsNotNull(operation.LeaseExpiresUtc);
                Assert.IsNull(operation.CompletedUtc);
                if (scenario == "create_no_effect")
                {
                    Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                    Assert.IsNotNull(operation.CreateResponseObservedUtc);
                    Assert.AreEqual("definitive_no_effect", operation.ResultCode);
                    Assert.AreEqual("documented_create_rejection", operation.OutcomeEvidenceKind);
                    Assert.AreEqual("provider_status_-4006", operation.LastErrorCode);
                    Assert.AreEqual(-4006, operation.ProviderStatusValue);
                    Assert.IsNull(operation.PolarisRequestGuid);
                    Assert.IsNull(operation.PolarisHoldId);
                }
                else if (scenario == "create_reply_required")
                {
                    Assert.AreEqual(HoldOperationPhase.ReplyReady, operation.Phase);
                    Assert.IsNotNull(operation.CreateResponseObservedUtc);
                    Assert.AreEqual("reply_required", operation.ResultCode);
                    Assert.AreEqual("create_status_5_reply_required", operation.OutcomeEvidenceKind);
                    Assert.AreEqual(holdProvider.RequestGuid, operation.PolarisRequestGuid);
                    Assert.AreEqual(3, operation.ProviderStatusType);
                    Assert.AreEqual(5, operation.ProviderStatusValue);
                    Assert.AreEqual("group-qualifier", operation.TxnGroupQualifier);
                    Assert.AreEqual("transaction-qualifier", operation.TxnQualifier);
                    Assert.IsNull(operation.ReplyStartedUtc);
                    Assert.IsNull(operation.PolarisHoldId);
                }
                else
                {
                    Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                    Assert.IsNotNull(operation.CreateResponseObservedUtc);
                    Assert.IsNotNull(operation.ReplyStartedUtc);
                    Assert.IsNotNull(operation.ReplyResponseObservedUtc);
                    Assert.AreEqual(holdProvider.RequestGuid, operation.PolarisRequestGuid);
                    Assert.IsNull(operation.PolarisHoldId);
                    Assert.AreEqual(scenario == "reply_success" ? "success" : "definitive_no_effect", operation.ResultCode);
                    Assert.AreEqual(scenario == "reply_success" ? "documented_reply_success" : "documented_reply_rejection",
                        operation.OutcomeEvidenceKind);
                    Assert.AreEqual(2, operation.ProviderStatusType);
                    Assert.AreEqual(scenario == "reply_success" ? 1 : 0, operation.ProviderStatusValue);
                    Assert.AreEqual(scenario == "reply_success" ? null : "provider_status_0", operation.LastErrorCode);
                }
                Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);

            if (scenario == "create_reply_required")
            {
                Assert.AreEqual(0, holdProvider.ReplyCount,
                    "Persisting reply-required evidence must not authorize a reply after caller cancellation.");
                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) " +
                    "WHERE [Id] = @id AND [OwnerToken] = @ownerToken AND [ExecutionEpoch] = @epoch " +
                    "AND [Phase] = N'reply_ready' AND [LeaseExpiresUtc] > SYSUTCDATETIME();",
                    ("@id", operationId),
                    ("@ownerToken", originalOwnerToken),
                    ("@epoch", originalEpoch));
                var recoveringReply = placement.RecoverBackgroundOperationAsync(
                    operationId,
                    scopeOrganizationId: null,
                    CancellationToken.None);
                await holdProvider.ReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1, holdProvider.CreateCount,
                    "A new worker must continue from the recorded reply-required result without replaying create.");
                Assert.AreEqual(1, holdProvider.ReplyCount,
                    "Only the new worker's recovery may dispatch the required reply.");
                Assert.AreEqual(1, replyOperationCalls,
                    "The reply result must be normalized through the pinned provider before the blocked call is released.");
                var lastReply = holdProvider.LastReplyCommand ??
                    throw new AssertFailedException("Recovery must dispatch the persisted reply conversation.");
                Assert.AreEqual(holdProvider.RequestGuid, lastReply.RequestGuid);
                Assert.AreEqual("group-qualifier", lastReply.TxnGroupQualifier);
                Assert.AreEqual("transaction-qualifier", lastReply.TxnQualifier);
                Assert.AreEqual(requestingOrganizationIdSnapshot, lastReply.RequestingOrganizationId);
                await using (var verifyReplyMarker = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                                 .CreateDbContextAsync())
                {
                    var request = await verifyReplyMarker.TitleRequests.AsNoTracking()
                        .SingleAsync(item => item.Id == seeded.Id);
                    var operation = await verifyReplyMarker.HoldPlacementOperations.AsNoTracking()
                        .SingleAsync(item => item.Id == operationId);
                    Assert.AreEqual("pending_hold", request.Status);
                    CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                    Assert.AreEqual(HoldOperationPhase.ReplyStarted, operation.Phase);
                    Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                    Assert.AreEqual(originalEpoch + 1, operation.ExecutionEpoch);
                    Assert.IsNotNull(operation.OwnerToken);
                    Assert.AreNotEqual(originalOwnerToken, operation.OwnerToken.Value,
                        "Recovery must acquire a new fenced worker token.");
                    Assert.IsNotNull(operation.LeaseExpiresUtc);
                    Assert.IsNotNull(operation.CreateResponseObservedUtc);
                    Assert.IsNotNull(operation.ReplyStartedUtc);
                    Assert.IsNull(operation.ReplyResponseObservedUtc);
                    Assert.AreEqual(holdProvider.RequestGuid, operation.PolarisRequestGuid);
                    Assert.AreEqual("group-qualifier", operation.TxnGroupQualifier);
                    Assert.AreEqual("transaction-qualifier", operation.TxnQualifier);
                    Assert.AreEqual(requestingOrganizationIdSnapshot, operation.RequestingOrganizationIdSnapshot);
                    Assert.AreEqual(0, await verifyReplyMarker.TitleRequestEvents.AsNoTracking()
                        .CountAsync(item => item.TitleRequestId == seeded.Id));
                    Assert.AreEqual(0, await verifyReplyMarker.EmailOutbox.AsNoTracking()
                        .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
                }
                Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
                holdProvider.CompleteBlockedReply(replyResult ??
                    throw new AssertFailedException("Recovery must use the actual pinned reply response."));
                var recoveredReply = await recoveringReply;
                Assert.AreEqual("updated", recoveredReply.Code);
                Assert.AreEqual(1, holdProvider.CreateCount);
                Assert.AreEqual(1, holdProvider.ReplyCount);
                Assert.AreEqual(1, createOperationCalls);
                Assert.AreEqual(1, replyOperationCalls);
                await using (var verifyReplyRecovery = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                                 .CreateDbContextAsync())
                {
                    var request = await verifyReplyRecovery.TitleRequests.AsNoTracking()
                        .SingleAsync(item => item.Id == seeded.Id);
                    var operation = await verifyReplyRecovery.HoldPlacementOperations.AsNoTracking()
                        .SingleAsync(item => item.Id == operationId);
                    Assert.AreEqual("hold_placed", request.Status);
                    Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                    Assert.AreEqual(HoldOperationState.Succeeded, operation.State);
                    Assert.AreEqual(originalEpoch + 1, operation.ExecutionEpoch);
                    Assert.AreEqual("success", operation.ResultCode);
                    Assert.AreEqual("documented_reply_success", operation.OutcomeEvidenceKind);
                    Assert.AreEqual(holdProvider.RequestGuid, operation.PolarisRequestGuid);
                    Assert.AreEqual("group-qualifier", operation.TxnGroupQualifier);
                    Assert.AreEqual("transaction-qualifier", operation.TxnQualifier);
                    Assert.AreEqual(requestingOrganizationIdSnapshot, operation.RequestingOrganizationIdSnapshot);
                    Assert.AreEqual(2, operation.ProviderStatusType);
                    Assert.AreEqual(1, operation.ProviderStatusValue);
                    Assert.IsNull(operation.PolarisHoldId);
                    Assert.IsNotNull(operation.CreateResponseObservedUtc);
                    Assert.IsNotNull(operation.ReplyStartedUtc);
                    Assert.IsNotNull(operation.ReplyResponseObservedUtc);
                    Assert.IsNotNull(operation.CompletedUtc);
                    Assert.AreEqual("hold_identity_unavailable", operation.LastErrorCode);
                    Assert.IsNull(operation.OwnerToken);
                    Assert.IsNull(operation.LeaseExpiresUtc);
                    Assert.AreEqual(1, await verifyReplyRecovery.TitleRequestEvents.AsNoTracking()
                        .CountAsync(item => item.TitleRequestId == seeded.Id),
                        "Recovery must emit exactly one hold_placed event.");
                    var placedEvent = await verifyReplyRecovery.TitleRequestEvents.AsNoTracking()
                        .SingleAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed");
                    var placedEventMetadata = placedEvent.MetadataJson ??
                        throw new AssertFailedException("The recovered hold_placed event must include metadata.");
                    using (var metadata = JsonDocument.Parse(placedEventMetadata))
                    {
                        Assert.IsFalse(metadata.RootElement.GetProperty("holdIdentityAvailable").GetBoolean());
                    }
                    var outbox = await verifyReplyRecovery.EmailOutbox.AsNoTracking()
                        .SingleAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1");
                    CollectionAssert.AreEqual(new[] { outbox.Id }, dispatcher.EnqueuedIds);
                }
                return;
            }

            await ExecuteNonQueryAsync(
                "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) " +
                "WHERE [Id] = @id AND [OwnerToken] = @ownerToken AND [ExecutionEpoch] = @epoch " +
                "AND [Phase] = N'result_recorded';",
                ("@id", operationId),
                ("@ownerToken", originalOwnerToken),
                ("@epoch", originalEpoch));
            var recovered = await placement.RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            var expectedNoEffect = scenario is "create_no_effect" or "reply_no_effect";
            Assert.AreEqual(expectedNoEffect ? "hold_not_placed" : "updated", recovered.Code);
            Assert.AreEqual(1, holdProvider.CreateCount, "Recovery must use the persisted provider outcome without replaying create.");
            Assert.AreEqual(isReplyScenario ? 1 : 0, holdProvider.ReplyCount,
                "Recovery must not repeat a reply whose outcome is already recorded.");
            Assert.AreEqual(1, createOperationCalls);
            Assert.AreEqual(isReplyScenario ? 1 : 0, replyOperationCalls);

            await using (var verifyRecovery = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verifyRecovery.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verifyRecovery.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual(originalEpoch + 1, operation.ExecutionEpoch);
                Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                Assert.AreEqual(expectedNoEffect ? HoldOperationState.NoHold : HoldOperationState.Succeeded, operation.State);
                Assert.AreEqual(expectedNoEffect ? "definitive_no_effect" : "success", operation.ResultCode);
                Assert.AreEqual(isReplyScenario ? holdProvider.RequestGuid : null, operation.PolarisRequestGuid);
                Assert.IsNull(operation.PolarisHoldId);
                Assert.AreEqual(scenario == "create_no_effect" ? 1 : 2, operation.ProviderStatusType);
                Assert.AreEqual(scenario switch
                {
                    "create_no_effect" => -4006,
                    "reply_success" => 1,
                    _ => 0
                }, operation.ProviderStatusValue);
                Assert.AreEqual(scenario switch
                {
                    "create_no_effect" => "documented_create_rejection",
                    "reply_success" => "documented_reply_success",
                    _ => "documented_reply_rejection"
                }, operation.OutcomeEvidenceKind);
                Assert.IsNotNull(operation.CompletedUtc);
                Assert.IsNull(operation.OwnerToken);
                Assert.IsNull(operation.LeaseExpiresUtc);
                if (expectedNoEffect)
                {
                    Assert.AreEqual("pending_hold", request.Status);
                    Assert.AreEqual(0, await verifyRecovery.TitleRequestEvents.AsNoTracking()
                        .CountAsync(item => item.TitleRequestId == seeded.Id));
                    Assert.AreEqual(0, await verifyRecovery.EmailOutbox.AsNoTracking()
                        .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
                    Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
                }
                else
                {
                    Assert.AreEqual("hold_placed", request.Status);
                    Assert.AreEqual("hold_identity_unavailable", operation.LastErrorCode);
                    Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.AsNoTracking()
                        .CountAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed"));
                    var outbox = await verifyRecovery.EmailOutbox.AsNoTracking()
                        .SingleAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1");
                    CollectionAssert.AreEqual(new[] { outbox.Id }, dispatcher.EnqueuedIds);
                }
            }
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
        }
    }

    [TestMethod]
    public async Task CancelledHoldCreateDoesNotRecordAcrossLostSqlLeaseFence()
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-known-outcome-lost-fence-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        var rootFactory = factory ?? throw new AssertFailedException("The test web host was not initialized.");
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.BlockCreate();
        using var cancellation = new CancellationTokenSource();
        long operationId = 0;
        Guid originalOwnerToken = Guid.Empty;
        Guid replacementOwnerToken = Guid.NewGuid();
        long originalEpoch = 0;

        try
        {
            await using (var setup = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await setup.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
                request.PatronIdSnapshot = 7105;
                await setup.SaveChangesAsync();
            }
            var responseJson = JsonSerializer.Serialize(new
            {
                PAPIErrorCode = 0,
                StatusType = 2,
                StatusValue = 1,
                RequestGUID = holdProvider.RequestGuid
            });
            var (definiteSuccess, operationCalls) = await ReadPinnedHoldOutcomeAsync(
                responseJson,
                isReply: false,
                holdProvider.RequestGuid,
                "hold-lost-fence-" + Guid.NewGuid().ToString("N"));
            Assert.AreEqual(HoldProviderOutcome.FinalSuccess, definiteSuccess.Outcome);
            Assert.AreEqual(holdProvider.RequestGuid, definiteSuccess.RequestGuid);
            Assert.IsNull(definiteSuccess.HoldRequestId);
            Assert.AreEqual(1, operationCalls);

            var before = await ReadBibOwnershipRequestAsync(seeded.Id);
            await using var scopedFactory = rootFactory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(holdProvider);
                    services.RemoveAll<IStaffPolarisProvider>();
                    services.AddSingleton<IStaffPolarisProvider>(holdProvider);
                }));
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placing = placement.PlaceBackgroundAsync(seeded.Id, before.RowVersion, cancellation.Token);
            await holdProvider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var marker = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var operation = await marker.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.IsNotNull(operation.OwnerToken);
                originalOwnerToken = operation.OwnerToken.Value;
                originalEpoch = operation.ExecutionEpoch;
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var expireLease = new SqlCommand(
                    "UPDATE [asap].[HoldPlacementOperation] SET [OwnerToken] = @replacementOwnerToken, " +
                    "[ExecutionEpoch] = [ExecutionEpoch] + 1, " +
                    "[LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) " +
                    "WHERE [Id] = @id AND [OwnerToken] = @ownerToken AND [ExecutionEpoch] = @epoch " +
                    "AND [Phase] = N'create_started' AND [CompletedUtc] IS NULL; SELECT @@ROWCOUNT;",
                    connection);
                expireLease.Parameters.AddWithValue("@id", operationId);
                expireLease.Parameters.AddWithValue("@ownerToken", originalOwnerToken);
                expireLease.Parameters.AddWithValue("@epoch", originalEpoch);
                expireLease.Parameters.AddWithValue("@replacementOwnerToken", replacementOwnerToken);
                Assert.AreEqual(1, Convert.ToInt32(await expireLease.ExecuteScalarAsync()),
                    "Lose the persisted response fence only after the provider dispatch marker is live.");
            }

            cancellation.Cancel();
            holdProvider.CompleteBlockedCreate(definiteSuccess);
            var placementResult = await placing;
            Assert.AreEqual("operation_ownership_lost", placementResult.Code);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, holdProvider.CreateCount);
            Assert.AreEqual(0, holdProvider.ReplyCount);
            Assert.AreEqual(1, operationCalls);

            await using (var verify = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verify.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.AreEqual(replacementOwnerToken, operation.OwnerToken);
                Assert.AreEqual(originalEpoch + 1, operation.ExecutionEpoch);
                Assert.IsNull(operation.CreateResponseObservedUtc);
                Assert.IsNull(operation.ResultCode);
                Assert.IsNull(operation.OutcomeEvidenceKind);
                Assert.IsNull(operation.CompletedUtc);
                var expiredLeaseUtc = operation.LeaseExpiresUtc ??
                    throw new AssertFailedException("The lost-fence witness must retain its expired lease timestamp.");
                Assert.IsTrue(expiredLeaseUtc <= DateTime.UtcNow);
                Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }

            var recovered = await placement.RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", recovered.Code);
            Assert.AreEqual(1, holdProvider.CreateCount, "A lost persistence fence must never replay the dispatched create.");
            await using (var verifyRecovery = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var operation = await verifyRecovery.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.OperatorRequired, operation.State);
                Assert.AreEqual(originalEpoch + 2, operation.ExecutionEpoch);
                Assert.IsNull(operation.CreateResponseObservedUtc);
                Assert.AreEqual(0, await verifyRecovery.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verifyRecovery.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
        }
    }

    private async Task<(HoldProviderResult Result, int OperationCalls)> ReadPinnedHoldOutcomeAsync(
        string responseJson,
        bool isReply,
        Guid requestGuid,
        string accessId)
    {
        var handler = new ProviderBoundaryHandler((_, _) => Task.FromResult(BoundaryResponse(responseJson)));
        var provider = await CreatePolarisProviderAsync(handler, accessId);
        var result = isReply
            ? await provider.ReplyToHoldAsync(new HoldReplyCommand(
                requestGuid, "group-qualifier", "transaction-qualifier", 2), CancellationToken.None)
            : await provider.CreateHoldAsync(BoundaryCreate(), CancellationToken.None);
        return (result, handler.OperationCalls);
    }
}
