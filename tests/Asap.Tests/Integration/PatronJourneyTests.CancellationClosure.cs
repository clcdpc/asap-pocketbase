using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task HoldAcquisitionDependencyCancellationPrecedesDurableIntent(string providerMode)
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-acquire-cancel-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        var before = await ReadBibOwnershipRequestAsync(seeded.Id);
        using var cancellation = new CancellationTokenSource();
        var inner = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var provider = new PreMutationStaffSuggestionProvider(inner, "refresh", providerMode, cancellation);
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        try
        {
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            if (providerMode == "failure_uncancelled")
            {
                Assert.AreEqual("hold_provider_error", (await placement.PlaceBackgroundAsync(
                    seeded.Id, seeded.RowVersion, cancellation.Token)).Code);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => placement.PlaceBackgroundAsync(
                    seeded.Id, seeded.RowVersion, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, provider.RefreshCount);
            Assert.AreEqual(0, provider.UpdateCount);
            var after = await ReadBibOwnershipRequestAsync(seeded.Id);
            Assert.AreEqual("pending_hold", after.Status);
            CollectionAssert.AreEqual(before.RowVersion, after.RowVersion);
            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            Assert.AreEqual(0, await context.HoldPlacementOperations.AsNoTracking()
                .CountAsync(item => item.TitleRequestId == seeded.Id));
            Assert.AreEqual(0, await context.TitleRequestEvents.AsNoTracking()
                .CountAsync(item => item.TitleRequestId == seeded.Id));
            Assert.AreEqual(0, await context.EmailOutbox.AsNoTracking()
                .CountAsync(item => item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{seeded.Id}:")));
        }
        finally
        {
            await DeleteRequestAsync(seeded.Id);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("generic_failure_cancelled")]
    public async Task HoldCreateCancellationAfterDurableMarkerRetainsRecoveryEvidence(string providerMode)
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-create-cancel-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        await using (var setup = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                         .CreateDbContextAsync())
        {
            var request = await setup.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            request.PatronIdSnapshot = 7105;
            await setup.SaveChangesAsync();
        }
        var before = await ReadBibOwnershipRequestAsync(seeded.Id);
        using var cancellation = new CancellationTokenSource();
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.BlockCreate();
        holdProvider.HoldAfterCreate = new PolarisHoldSnapshot(8124, 9001, 2, "Active", 101);
        var dispatcher = new RecordingOutboxDispatcher();
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
            }));

        long operationId;
        try
        {
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placing = placement.PlaceBackgroundAsync(seeded.Id, before.RowVersion, cancellation.Token);
            await holdProvider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var marker = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var operation = await marker.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateStartedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                Assert.AreEqual(0, await marker.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await marker.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }

            cancellation.Cancel();
            switch (providerMode)
            {
                case "return_cancelled":
                    holdProvider.CompleteBlockedCreate(new HoldProviderResult(
                        HoldProviderOutcome.FinalSuccess,
                        null,
                        8124,
                        null,
                        null,
                        2,
                        1,
                        "documented_create_success"));
                    break;
                case "operation_cancelled":
                    Assert.IsTrue(holdProvider.PendingCreate!.TrySetException(
                        new OperationCanceledException("Provider observed cancellation after the create marker.")));
                    break;
                case "failure_cancelled":
                    Assert.IsTrue(holdProvider.PendingCreate!.TrySetException(
                        new PolarisOperationalException("testing_hold_create_failure", "Create failed after caller cancellation.")));
                    break;
                case "generic_failure_cancelled":
                    Assert.IsTrue(holdProvider.PendingCreate!.TrySetException(
                        new InvalidOperationException("Create failed after caller cancellation.")));
                    break;
                default:
                    throw new AssertFailedException($"Unexpected provider mode: {providerMode}");
            }
            await Assert.ThrowsAsync<OperationCanceledException>(() => placing);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, holdProvider.CreateCount);

            await using (var verify = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verify.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateStartedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                Assert.IsNull(operation.CreateResponseObservedUtc);
                Assert.IsNull(operation.CompletedUtc);
                Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);

            await ExecuteNonQueryAsync(
                "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [Id] = @id;",
                ("@id", operationId));
            var recovered = await placement.RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", recovered.Code);
            Assert.AreEqual(1, holdProvider.CreateCount, "Recovery must not replay the acquired create call.");
            await using (var verifyRecovery = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verifyRecovery.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verifyRecovery.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.OperatorRequired, operation.State);
                Assert.IsNull(operation.OwnerToken);
                Assert.IsNotNull(operation.CreateStartedUtc);
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

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("generic_failure_cancelled")]
    public async Task HoldReplyCancellationAfterDurableMarkerRetainsRecoveryEvidence(string providerMode)
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-reply-cancel-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        await using (var setup = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                         .CreateDbContextAsync())
        {
            var request = await setup.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            request.PatronIdSnapshot = 7105;
            await setup.SaveChangesAsync();
        }
        var before = await ReadBibOwnershipRequestAsync(seeded.Id);
        using var cancellation = new CancellationTokenSource();
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        holdProvider.BlockCreate();
        holdProvider.BlockReply();
        holdProvider.HoldAfterCreate = new PolarisHoldSnapshot(8125, 9001, 2, "Active", 101);
        var dispatcher = new RecordingOutboxDispatcher();
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
            }));

        long operationId;
        try
        {
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placing = placement.PlaceBackgroundAsync(seeded.Id, before.RowVersion, cancellation.Token);
            await holdProvider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            holdProvider.CompleteBlockedCreateWithConfiguredResult();
            await holdProvider.ReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var marker = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var operation = await marker.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual(HoldOperationPhase.ReplyStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateResponseObservedUtc);
                Assert.IsNotNull(operation.ReplyStartedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                Assert.AreEqual(0, await marker.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await marker.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }

            cancellation.Cancel();
            switch (providerMode)
            {
                case "return_cancelled":
                    holdProvider.CompleteBlockedReply(new HoldProviderResult(
                        HoldProviderOutcome.FinalSuccess,
                        holdProvider.RequestGuid,
                        8125,
                        "group-qualifier",
                        "transaction-qualifier",
                        2,
                        1,
                        "documented_reply_success"));
                    break;
                case "operation_cancelled":
                    Assert.IsTrue(holdProvider.PendingReply!.TrySetException(
                        new OperationCanceledException("Provider observed cancellation after the reply marker.")));
                    break;
                case "failure_cancelled":
                    Assert.IsTrue(holdProvider.PendingReply!.TrySetException(
                        new PolarisOperationalException("testing_hold_reply_failure", "Reply failed after caller cancellation.")));
                    break;
                case "generic_failure_cancelled":
                    Assert.IsTrue(holdProvider.PendingReply!.TrySetException(
                        new InvalidOperationException("Reply failed after caller cancellation.")));
                    break;
                default:
                    throw new AssertFailedException($"Unexpected provider mode: {providerMode}");
            }
            await Assert.ThrowsAsync<OperationCanceledException>(() => placing);
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, holdProvider.CreateCount);
            Assert.AreEqual(1, holdProvider.ReplyCount);

            await using (var verify = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verify.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.ReplyStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateResponseObservedUtc);
                Assert.IsNotNull(operation.ReplyStartedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                Assert.IsNull(operation.ReplyResponseObservedUtc);
                Assert.IsNull(operation.CompletedUtc);
                Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);

            await ExecuteNonQueryAsync(
                "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [Id] = @id;",
                ("@id", operationId));
            var recovered = await placement.RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", recovered.Code);
            Assert.AreEqual(1, holdProvider.CreateCount, "Recovery must not replay the acquired create call.");
            Assert.AreEqual(1, holdProvider.ReplyCount, "Recovery must not replay the acquired reply call.");
            await using (var verifyRecovery = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verifyRecovery.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verifyRecovery.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(before.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.ReplyStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.OperatorRequired, operation.State);
                Assert.IsNull(operation.OwnerToken);
                Assert.IsNotNull(operation.ReplyStartedUtc);
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

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task HoldRecordedResultReadinessCancellationRetainsRecoveryEvidence(string readinessMode)
    {
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-result-cancel-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        var seededPatronProvider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var operationId = 0L;
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        await using (var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                         .CreateDbContextAsync())
        {
            var request = await context.TitleRequests.SingleAsync(item => item.Id == seeded.Id);
            var operation = new HoldPlacementOperation
            {
                TitleRequestId = request.Id,
                PatronBarcodeSnapshot = request.Barcode,
                PatronIdSnapshot = 7001,
                BibIdSnapshot = 9001,
                PickupBranchIdSnapshot = 101,
                RequestingOrganizationIdSnapshot = 1,
                WorkstationIdSnapshot = 1,
                PolarisUserIdSnapshot = 1,
                AttemptNumber = 1,
                State = HoldOperationState.InProgress,
                Phase = HoldOperationPhase.ResultRecorded,
                ExecutionEpoch = 1,
                RequestStartedUtc = now.AddMinutes(-5),
                CreateStartedUtc = now.AddMinutes(-4),
                CreateResponseObservedUtc = now.AddMinutes(-3),
                PolarisRequestGuid = Guid.NewGuid(),
                PolarisHoldId = 8123,
                ResultCode = "success",
                OutcomeEvidenceKind = "documented_reply_success"
            };
            context.HoldPlacementOperations.Add(operation);
            await context.SaveChangesAsync();
            operationId = operation.Id;
        }

        using var cancellation = new CancellationTokenSource();
        var sender = new PreMutationCancellationEmailSender(true, readinessMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DeterministicTestingPatronProvider>();
                services.AddSingleton(seededPatronProvider);
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(seededPatronProvider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(seededPatronProvider);
                services.RemoveAll<IPolarisReferenceProvider>();
                services.AddSingleton<IPolarisReferenceProvider>(seededPatronProvider);
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
            }));

        try
        {
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            if (readinessMode == "failure_uncancelled")
            {
                Assert.AreEqual("hold_resolution_dependency_unavailable", (await placement.RecoverBackgroundOperationAsync(
                    operationId, null, cancellation.Token)).Code);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => placement.RecoverBackgroundOperationAsync(
                    operationId, null, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, sender.ReadinessCount);
            await using (var verify = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
                var operation = await verify.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual("pending_hold", request.Status);
                CollectionAssert.AreEqual(seeded.RowVersion, request.RowVersion);
                Assert.AreEqual(HoldOperationPhase.ResultRecorded, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.AreEqual("success", operation.ResultCode);
                Assert.IsNull(operation.CompletedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);

            await ExecuteNonQueryAsync(
                "UPDATE [asap].[HoldPlacementOperation] SET [OwnerToken] = NULL, [LeaseExpiresUtc] = NULL WHERE [Id] = @id;",
                ("@id", operationId));
            var recoveryDispatcher = new RecordingOutboxDispatcher();
            await using var recoveryFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DeterministicTestingPatronProvider>();
                    services.AddSingleton(seededPatronProvider);
                    services.RemoveAll<IPatronProvider>();
                    services.AddSingleton<IPatronProvider>(seededPatronProvider);
                    services.RemoveAll<IStaffPolarisProvider>();
                    services.AddSingleton<IStaffPolarisProvider>(seededPatronProvider);
                    services.RemoveAll<IPolarisReferenceProvider>();
                    services.AddSingleton<IPolarisReferenceProvider>(seededPatronProvider);
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(new MutableReadinessEmailSender(isConfigured: true));
                    services.RemoveAll<IEmailOutboxDispatcher>();
                    services.AddSingleton<IEmailOutboxDispatcher>(recoveryDispatcher);
                }));
            var recovered = await recoveryFactory.Services.GetRequiredService<HoldPlacementService>()
                .RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            await using (var verifyRecovery = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                Assert.AreEqual("hold_placed", await verifyRecovery.TitleRequests.AsNoTracking()
                    .Where(item => item.Id == seeded.Id).Select(item => item.Status).SingleAsync());
                var completed = await verifyRecovery.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.Id == operationId);
                Assert.AreEqual(HoldOperationState.Succeeded, completed.State);
                Assert.AreEqual(HoldOperationPhase.ResultRecorded, completed.Phase);
                Assert.IsNotNull(completed.CompletedUtc);
                Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed"));
                var outboxRows = await verifyRecovery.EmailOutbox.AsNoTracking()
                    .Where(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1")
                    .ToListAsync();
                var recoveredOutbox = outboxRows.Single();
                Assert.AreEqual(1, outboxRows.Count);
                Assert.AreEqual("pending", recoveredOutbox.Status);
                CollectionAssert.AreEqual(new[] { recoveredOutbox.Id }, recoveryDispatcher.EnqueuedIds);
            }

            Assert.AreEqual(0, seededPatronProvider.CreateCommands.Count);
            Assert.AreEqual(0, seededPatronProvider.ReplyCommands.Count);
            var callsAfterRecovery = seededPatronProvider.Calls.Count;
            var secondRecovery = await recoveryFactory.Services.GetRequiredService<HoldPlacementService>()
                .RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("not_found", secondRecovery.Code);
            Assert.AreEqual(callsAfterRecovery, seededPatronProvider.Calls.Count);
            Assert.AreEqual(1, recoveryDispatcher.EnqueuedIds.Count);
            await using (var verifyNoReplay = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                Assert.AreEqual(1, await verifyNoReplay.EmailOutbox.AsNoTracking()
                    .CountAsync(item => item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
                Assert.AreEqual(1, await verifyNoReplay.TitleRequestEvents.AsNoTracking()
                    .CountAsync(item => item.TitleRequestId == seeded.Id && item.EventType == "hold_placed"));
            }
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
            await RestoreCancellationEmailSettingsAsync(emailSettings);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task WeeklySummaryReadinessCancellationStopsBeforeOutbox(string readinessMode)
    {
        var (staff, _) = await CreatePostCommitAdminAsync();
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var title = $"Weekly readiness {Guid.NewGuid():N}";
        byte[] requestVersion = [];
        var systemStaffUrl = await contexts.CreateDbContextAsync();
        string? originalStaffUrl;
        await using (systemStaffUrl)
        {
            var system = await systemStaffUrl.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            originalStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var recipient = await systemStaffUrl.StaffUsers.SingleAsync(item => item.Id == staff.Id);
            recipient.WeeklyActionSummaryEnabled = true;
            recipient.WeeklyActionSummaryEmail = "weekly-readiness@example.org";
            var formatId = await systemStaffUrl.MaterialFormats.Where(item => item.Code == "book")
                .Select(item => item.Id).SingleAsync();
            var request = new TitleRequest
            {
                LibraryOrganizationId = 2,
                Barcode = $"weekly-ready-{Guid.NewGuid():N}"[..35],
                Title = title,
                MaterialFormatId = formatId,
                Status = "suggestion",
                CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            systemStaffUrl.TitleRequests.Add(request);
            await systemStaffUrl.SaveChangesAsync();
            requestVersion = request.RowVersion.ToArray();
        }

        var businessKeyPrefix = $"weekly-summary:{staff.Id}:";
        using var cancellation = new CancellationTokenSource();
        var sender = new PreMutationCancellationEmailSender(true, readinessMode, cancellation);
        try
        {
            await using var scopedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(sender);
                }));
            var workflow = scopedFactory.Services.GetRequiredService<WorkflowProcessingService>();
            if (readinessMode == "failure_uncancelled")
            {
                Assert.AreEqual("operational_failure", (await workflow.SendWeeklyStaffSummaryAsync(
                    null, 2, cancellation.Token)).Code);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.SendWeeklyStaffSummaryAsync(
                    null, 2, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, sender.ReadinessCount);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking().CountAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith(businessKeyPrefix)));
            var unchanged = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Title == title);
            Assert.AreEqual("suggestion", unchanged.Status);
            CollectionAssert.AreEqual(requestVersion, unchanged.RowVersion);
        }
        finally
        {
            await using (var cleanup = await contexts.CreateDbContextAsync())
            {
                await cleanup.EmailOutbox.Where(item => item.BusinessKey != null &&
                        item.BusinessKey.StartsWith(businessKeyPrefix))
                    .ExecuteDeleteAsync();
                var system = await cleanup.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
                system.StaffApplicationUrl = originalStaffUrl;
                var recipient = await cleanup.StaffUsers.SingleAsync(item => item.Id == staff.Id);
                recipient.WeeklyActionSummaryEnabled = false;
                recipient.WeeklyActionSummaryEmail = null;
                var ids = await cleanup.TitleRequests.AsNoTracking()
                    .Where(item => item.Title == title)
                    .Select(item => item.Id).ToArrayAsync();
                cleanup.TitleRequestEvents.RemoveRange(cleanup.TitleRequestEvents.Where(item => ids.Contains(item.TitleRequestId)));
                cleanup.TitleRequests.RemoveRange(cleanup.TitleRequests.Where(item => ids.Contains(item.Id)));
                await cleanup.SaveChangesAsync();
            }
            await RestoreCancellationEmailSettingsAsync(emailSettings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task TimeoutReadinessCancellationPrecedesTitleAndOutboxWrites(string readinessMode)
    {
        const int scope = 99025;
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, scope);
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        WorkflowSettings? originalSettings = null;
        var createdSettings = false;
        var requestId = 0L;
        byte[] requestVersion = [];
        var barcode = $"timeout-ready-{Guid.NewGuid():N}"[..35];
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        try
        {
            await using (var seed = await contexts.CreateDbContextAsync())
            {
                var settings = await seed.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
                createdSettings = settings is null;
                if (settings is null)
                {
                    settings = new WorkflowSettings { OrganizationId = scope };
                    seed.WorkflowSettings.Add(settings);
                }
                else
                {
                    originalSettings = new WorkflowSettings
                    {
                        OrganizationId = scope,
                        OutstandingTimeoutEnabled = settings.OutstandingTimeoutEnabled,
                        OutstandingTimeoutDays = settings.OutstandingTimeoutDays,
                        OutstandingTimeoutSendEmail = settings.OutstandingTimeoutSendEmail
                    };
                }
                settings.OutstandingTimeoutEnabled = true;
                settings.OutstandingTimeoutDays = 1;
                settings.OutstandingTimeoutSendEmail = true;
                var formatId = await seed.MaterialFormats.Where(item => item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                var request = NewRequest(scope, barcode, "suggestion", now.AddDays(-5), now.AddDays(-5));
                request.MaterialFormatId = formatId;
                request.Email = "timeout-readiness@example.org";
                seed.TitleRequests.Add(request);
                await seed.SaveChangesAsync();
                requestId = request.Id;
                requestVersion = request.RowVersion.ToArray();
            }
            await PrepareTimeoutCycleAsync(contexts, QueueNames.OutstandingTimeout, requestId, scope);

            using var cancellation = new CancellationTokenSource();
            var sender = new PreMutationCancellationEmailSender(true, readinessMode, cancellation);
            await using var scopedFactory = factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(sender);
                }));
            var workflow = scopedFactory.Services.GetRequiredService<WorkflowProcessingService>();
            if (readinessMode == "failure_uncancelled")
            {
                Assert.AreEqual("operational_failure", (await workflow.ProcessWorkflowAsync(
                    scope, cancellation.Token)).Code);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.ProcessWorkflowAsync(
                    scope, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, sender.ReadinessCount);
            await using var verify = await contexts.CreateDbContextAsync();
            var requestAfter = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
            Assert.AreEqual("suggestion", requestAfter.Status);
            CollectionAssert.AreEqual(requestVersion, requestAfter.RowVersion);
            Assert.AreEqual(0, await verify.TitleRequestEvents.AsNoTracking()
                .CountAsync(item => item.TitleRequestId == requestId));
            Assert.AreEqual(0, await verify.EmailOutbox.AsNoTracking()
                .CountAsync(item => item.BusinessKey == $"timeout:OutstandingTimeout:{requestId}"));
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync($"timeout:OutstandingTimeout:{requestId}");
            if (requestId > 0)
            {
                await DeleteRequestIdsAsync(contexts, [requestId]);
            }
            await using (var restore = await contexts.CreateDbContextAsync())
            {
                var settings = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
                if (createdSettings)
                {
                    if (settings is not null)
                    {
                        restore.WorkflowSettings.Remove(settings);
                    }
                }
                else if (settings is not null && originalSettings is not null)
                {
                    settings.OutstandingTimeoutEnabled = originalSettings.OutstandingTimeoutEnabled;
                    settings.OutstandingTimeoutDays = originalSettings.OutstandingTimeoutDays;
                    settings.OutstandingTimeoutSendEmail = originalSettings.OutstandingTimeoutSendEmail;
                }
                await restore.QueueProgress.Where(item => item.ScopeOrganizationId == scope).ExecuteDeleteAsync();
                await restore.SaveChangesAsync();
            }
            await RestoreCancellationEmailSettingsAsync(emailSettings);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    public async Task IdentifierQueueProviderCancellationDoesNotCommitRowOutcome(string providerMode)
    {
        var seeded = await SeedBibOwnershipRequestAsync(
            "9780000000001",
            bibId: null,
            staffVerified: false,
            isbnCheckStatus: "pending",
            status: "suggestion");
        using var cancellation = new CancellationTokenSource();
        var inner = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var provider = new PreMutationStaffSuggestionProvider(inner, "identifier", providerMode, cancellation);
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        await PrepareSingleItemCycleAsync(QueueNames.IdentifierProcessing, 2, seeded.Id);
        var before = await ReadBibOwnershipRequestAsync(seeded.Id);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => scopedFactory.Services
                .GetRequiredService<WorkflowProcessingService>()
                .ProcessIdentifierAsync(2, cancellation.Token));
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, provider.IdentifierLookupCount);
            var after = await ReadBibOwnershipRequestAsync(seeded.Id);
            Assert.AreEqual("pending", after.IsbnCheckStatus);
            Assert.AreEqual(0, after.IsbnCheckRetryCount);
            Assert.IsNull(after.BibId);
            CollectionAssert.AreEqual(before.RowVersion, after.RowVersion);
            await using var context = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            Assert.AreEqual(0, await context.TitleRequestEvents.AsNoTracking()
                .CountAsync(item => item.TitleRequestId == seeded.Id));
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task ExplicitBibValidationCancellationPrecedesRequestMutation(string providerMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"bib-preflight-cancel-{Guid.NewGuid():N}", null, false, status: "suggestion");
        var before = await ReadCancellationTitleRequestSnapshotAsync(seeded.Id);
        using var cancellation = new CancellationTokenSource();
        var inner = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var provider = new PreMutationStaffSuggestionProvider(inner, "bib", providerMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitTitleMutationService(
            dispatcher,
            staffPolarisProvider: provider,
            patronProvider: provider);
        var input = new TitleRequestActionInput
        {
            Version = StaffVersion.Encode(seeded.RowVersion),
            Action = "edit",
            Bibid = 9001
        }.ToCommand();

        try
        {
            if (providerMode == "failure_uncancelled")
            {
                Assert.AreEqual("bib_validation_unavailable", (await service.ActionAsync(
                    actor, seeded.Id, input, cancellation.Token)).Code);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.ActionAsync(
                    actor, seeded.Id, input, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, provider.BibValidationCount);
            var after = await ReadCancellationTitleRequestSnapshotAsync(seeded.Id);
            Assert.AreEqual(before.Status, after.Status);
            CollectionAssert.AreEqual(before.RowVersion, after.RowVersion);
            Assert.AreEqual(before.EventCount, after.EventCount);
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("failure_cancelled")]
    public async Task SettingsPatronCodePreflightCancellationLeavesSettingsAndAuditUntouched(string providerMode)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var versionBefore = await ReadSettingsVersionAsync(actor);
        var auditBefore = await ReadAuditHighWatermarkAsync();
        using var cancellation = new CancellationTokenSource();
        var referenceProvider = new CancelAfterPatronCodeReadProvider(cancellation, providerMode);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPolarisReferenceProvider>();
                services.AddSingleton<IPolarisReferenceProvider>(referenceProvider);
            }));
        var administration = scopedFactory.Services.GetRequiredService<AdministrationService>();
        var command = AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new
        {
            orgId = "system",
            version = versionBefore,
            allowedPatronCodeIds = new[] { 2147483000 }
        }));

        await Assert.ThrowsAsync<OperationCanceledException>(() => administration.SaveSettingsAsync(
            actor, command, cancellation.Token));
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(1, referenceProvider.ReadCount);
        Assert.AreEqual(versionBefore, await ReadSettingsVersionAsync(actor));
        Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
    }

    private async Task<string> ReadSettingsVersionAsync(CurrentStaff actor)
    {
        var result = await factory!.Services.GetRequiredService<AdministrationService>()
            .GetSettingsAsync(actor, LibraryScope.System, CancellationToken.None);
        Assert.AreEqual("ok", result.Code);
        return JsonSerializer.SerializeToElement(result.Data).GetProperty("version").GetString()!;
    }

    private sealed class CancelAfterPatronCodeReadProvider(
        CancellationTokenSource cancellation,
        string mode) : IPolarisReferenceProvider
    {
        public int ReadCount { get; private set; }

        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(true, 1));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>([]);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            cancellation.Cancel();
            return mode switch
            {
                "return_cancelled" => Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>(
                    [new PolarisPatronCodeSnapshot(1, "Adult")]),
                "failure_cancelled" => Task.FromException<IReadOnlyList<PolarisPatronCodeSnapshot>>(
                    new PolarisOperationalException("testing_patron_codes_failure", "Provider failed after cancellation.")),
                _ => throw new AssertFailedException($"Unknown patron-code provider mode {mode}.")
            };
        }
    }
}
