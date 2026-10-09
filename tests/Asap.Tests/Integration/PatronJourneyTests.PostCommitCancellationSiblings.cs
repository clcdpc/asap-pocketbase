using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("assign", "return")]
    [DataRow("assign", "canceled")]
    [DataRow("assign", "failure")]
    [DataRow("action", "return")]
    [DataRow("action", "canceled")]
    [DataRow("action", "failure")]
    public async Task TitleRequestMutationDispatchCancellationPropagatesAfterDurableCommit(
        string mutation,
        string dispatchMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var settings = await ConfigureCancellationEmailSettingsAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"post-commit-{Guid.NewGuid():N}", null, false, status: "suggestion");
        var title = await ReadCancellationTitleRequestSnapshotAsync(seeded.Id);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var service = CreatePostCommitTitleMutationService(
            dispatcher, emailSender: new MutableReadinessEmailSender(isConfigured: true));
        var committedOutboxIds = new List<long>();
        try
        {
            var mutationTask = Task.Run(() => mutation == "assign"
                ? service.AssignAsync(
                    actor,
                    seeded.Id,
                    new AssignTitleRequestInput(StaffVersion.Encode(title.RowVersion), actor.Id),
                    cancellation.Token)
                : service.ActionAsync(
                    actor,
                    seeded.Id,
                    new TitleRequestActionInput
                    {
                        Version = StaffVersion.Encode(title.RowVersion),
                        Action = "purchase",
                        EmailPurchaseReminder = true
                    }.ToCommand(),
                    cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var beforeCancel = await AssertTitleMutationCommittedAsync(
                seeded.Id, mutation == "action" ? "outstanding_purchase" : "suggestion", dispatcher.EnqueuedIds);
            Assert.AreEqual(1, dispatcher.EnqueuedIds.Count);
            committedOutboxIds.AddRange(await ReadCommittedTitleMutationOutboxIdsAsync(seeded.Id, mutation));
            Assert.AreEqual(mutation == "action" ? 2 : 1, committedOutboxIds.Count);

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => mutationTask);

            await AssertTitleMutationStillCommittedAsync(seeded.Id, committedOutboxIds, beforeCancel);
        }
        finally
        {
            dispatcher.Release();
            await DeleteCommittedOutboxIdsAsync(committedOutboxIds);
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
            await RestoreCancellationEmailSettingsAsync(settings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return", true)]
    [DataRow("canceled", true)]
    [DataRow("failure", true)]
    [DataRow("canceled", false)]
    public async Task IdentifierRetryDispatchPreservesCallerCancellationAndCommittedRetry(
        string dispatchMode,
        bool cancelCaller)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"retry-cancellation-{Guid.NewGuid():N}",
            null,
            staffVerified: false,
            isbnCheckStatus: IdentifierCheckState.RetryExhausted,
            retryCount: 5);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterIdentifierRetryDispatcher(dispatchMode);
        var service = CreatePostCommitTitleMutationService(
            factory!.Services.GetRequiredService<IEmailOutboxDispatcher>(), dispatcher);
        try
        {
            var retry = Task.Run(() => service.RetryIdentifierAsync(
                actor,
                seeded.Id,
                new VersionInput(StaffVersion.Encode(seeded.RowVersion)),
                cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var beforeCancel = await ReadIdentifierRetrySnapshotAsync(seeded.Id);
            Assert.AreEqual(IdentifierCheckState.Pending, beforeCancel.Status);
            Assert.AreEqual(1, beforeCancel.RetryEvents);
            Assert.AreEqual(1, dispatcher.RequestIds.Count);

            if (cancelCaller)
            {
                cancellation.Cancel();
            }
            dispatcher.Release();
            if (cancelCaller)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => retry);
            }
            else
            {
                Assert.AreEqual("updated", (await retry).Code,
                    "An OCE from the dispatcher without caller cancellation remains recoverable by the recurring processor.");
            }

            var afterCancel = await ReadIdentifierRetrySnapshotAsync(seeded.Id);
            Assert.AreEqual(beforeCancel.Status, afterCancel.Status);
            CollectionAssert.AreEqual(beforeCancel.RowVersion, afterCancel.RowVersion);
            Assert.AreEqual(beforeCancel.RetryEvents, afterCancel.RetryEvents,
                "A canceled caller must not repeat the committed retry event.");
        }
        finally
        {
            dispatcher.Release();
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("create", "return")]
    [DataRow("create", "canceled")]
    [DataRow("create", "failure")]
    [DataRow("assign", "return")]
    [DataRow("assign", "canceled")]
    [DataRow("assign", "failure")]
    public async Task AdditionalCopyDispatchCancellationPropagatesAfterDurableCommit(
        string mutation,
        string dispatchMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var settings = await ConfigureCancellationEmailSettingsAsync();
        var seeded = await SeedAdditionalCopyLegacySourceAsync(staff.Id, $"cancel-{Guid.NewGuid():N}");
        var baseService = factory!.Services.GetRequiredService<AdditionalCopyService>();
        var preview = await baseService.PreviewAsync(actor, seeded.SourceRequestId, CancellationToken.None);
        Assert.AreEqual("loaded", preview.Code);
        Assert.IsNotNull(preview.Preview);

        var taskId = 0L;
        var createdOutboxIds = new List<long>();
        byte[]? expectedAssignmentVersion = null;
        long? expectedAssigneeId = null;
        if (mutation == "assign")
        {
            var created = await baseService.CreateAsync(
                actor,
                seeded.SourceRequestId,
                new AdditionalCopyCreateInput(preview.Preview!.Version, EmailPurchaseReminder: false),
                CancellationToken.None);
            Assert.AreEqual("created", created.Code);
            taskId = created.RequestId!.Value;
        }

        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var service = CreatePostCommitAdditionalCopyService(
            dispatcher, new MutableReadinessEmailSender(isConfigured: true));
        try
        {
            Task<AdditionalCopyMutationResult> mutationTask;
            if (mutation == "create")
            {
                mutationTask = Task.Run(() => service.CreateAsync(
                    actor,
                    seeded.SourceRequestId,
                    new AdditionalCopyCreateInput(preview.Preview!.Version, EmailPurchaseReminder: true),
                    cancellation.Token));
            }
            else
            {
                var current = await baseService.GetAsync(actor, taskId.ToString(), null, CancellationToken.None);
                Assert.IsNotNull(current);
                Assert.IsTrue(StaffVersion.TryDecode(current.Version, out expectedAssignmentVersion));
                expectedAssigneeId = actor.Id;
                mutationTask = Task.Run(() => service.AssignAsync(
                    actor,
                    taskId,
                    new AssignAdditionalCopyInput(current.Version, actor.Id),
                    cancellation.Token));
            }

            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var outboxId = dispatcher.EnqueuedIds.Single();
            createdOutboxIds.Add(outboxId);
            var committed = await ReadCommittedAdditionalCopyAsync(
                outboxId, mutation, expectedAssignmentVersion, expectedAssigneeId);
            taskId = committed.TaskId;

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => mutationTask);

            await AssertCommittedAdditionalCopyStillDurableAsync(committed, outboxId);
        }
        finally
        {
            dispatcher.Release();
            await DeleteCommittedOutboxIdsAsync(createdOutboxIds);
            if (taskId > 0)
            {
                await DeleteAdditionalCopyTestDataAsync(seeded.SourceRequestId, taskId);
            }
            else
            {
                await DeleteAdditionalCopyTestDataAsync(seeded.SourceRequestId, null);
            }
            await RestoreCancellationEmailSettingsAsync(settings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task EmailTestQueueDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var settings = await ConfigureCancellationEmailSettingsAsync();
        var operationId = Guid.NewGuid();
        var businessKey = $"operational-test:2:{actor.Id}:{operationId:N}";
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var service = CreatePostCommitEmailOperationsService(
            dispatcher, new MutableReadinessEmailSender(isConfigured: true));
        try
        {
            var queue = Task.Run(() => service.QueueTestAsync(actor, 2, cancellation.Token, operationId));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var outboxId = dispatcher.EnqueuedIds.Single();
            await ReadCommittedOutboxAsync(outboxId, businessKey);

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queue);

            AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxId));
        }
        finally
        {
            dispatcher.Release();
            await DeleteEmailOutboxByBusinessKeyAsync(businessKey);
            await RestoreCancellationEmailSettingsAsync(settings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task EmailRetryDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var businessKey = $"post-commit-retry:{Guid.NewGuid():N}";
        var outbox = await SeedFailedEmailOutboxAsync(businessKey);
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var service = CreatePostCommitEmailOperationsService(dispatcher);
        try
        {
            var retry = Task.Run(() => service.RetryAsync(
                actor,
                outbox.Id,
                StaffVersion.Encode(outbox.RowVersion),
                cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(outbox.Id, dispatcher.EnqueuedIds.Single());
            var committed = await ReadCommittedOutboxAsync(outbox.Id, businessKey);
            Assert.AreEqual("pending", committed.Status);

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => retry);

            AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outbox.Id), expectedAttempts: 3);
        }
        finally
        {
            dispatcher.Release();
            await DeleteEmailOutboxByBusinessKeyAsync(businessKey);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task HoldResolutionDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var settings = await ConfigureCancellationEmailSettingsAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-cancel-{Guid.NewGuid():N}", 9011, staffVerified: true,
            isbnCheckStatus: "found", status: "pending_hold", autoHold: true);
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
                BibIdSnapshot = 9011,
                PickupBranchIdSnapshot = 101,
                AttemptNumber = 1,
                State = "operator_required",
                Phase = "create_started",
                ExecutionEpoch = 2,
                RequestStartedUtc = now.AddMinutes(-5),
                CreateStartedUtc = now.AddMinutes(-5),
                ResultCode = "ambiguous",
                LastErrorCode = "provider_timeout"
            };
            context.HoldPlacementOperations.Add(operation);
            await context.SaveChangesAsync();
            operationId = operation.Id;
        }

        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        var emailSender = new MutableReadinessEmailSender(isConfigured: true);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(emailSender);
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
            }));

        try
        {
            byte[] operationVersion;
            await using (var context = await scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                             .CreateDbContextAsync())
            {
                operationVersion = await context.HoldPlacementOperations.AsNoTracking()
                    .Where(item => item.Id == operationId).Select(item => item.RowVersion).SingleAsync();
            }

            var input = new ResolveHoldOperationInput(
                StaffVersion.Encode(operationVersion),
                StaffVersion.Encode(seeded.RowVersion),
                "succeeded",
                "The final provider response accounts for this exact operation attempt.",
                "provider_final_success",
                "support-case-post-commit-cancellation",
                OperationSpecificProofAttested: true,
                ProofSource: "Polaris operations record",
                CausalConnection: "The response identifies this hold attempt and its final outcome.",
                ProvenFinalHoldId: null,
                OriginalExecutorExcluded: true,
                ExecutorExclusionAttested: true,
                ExecutorExclusionReference: "worker-stop-record-1",
                ExecutorExclusionExplanation: "The original worker was stopped before this resolution.");
            var service = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var resolution = Task.Run(() => service.ResolveAsync(actor, operationId, input, cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.HasCount(1, dispatcher.EnqueuedIds);
            var committed = await ReadHoldResolutionSnapshotAsync(seeded.Id, operationId, dispatcher.EnqueuedIds.Single());

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => resolution);

            var after = await ReadHoldResolutionSnapshotAsync(seeded.Id, operationId, dispatcher.EnqueuedIds.Single());
            Assert.AreEqual(committed.Status, after.Status);
            CollectionAssert.AreEqual(committed.RequestVersion, after.RequestVersion);
            Assert.AreEqual(committed.OperationState, after.OperationState);
            Assert.AreEqual(committed.EventCount, after.EventCount,
                "Cancellation after operator resolution must not repeat the accepted hold event.");
            AssertOutboxStillPending(after.Outbox);
        }
        finally
        {
            dispatcher.Release();
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[AdministrativeAudit] WHERE [TargetType] = N'hold_placement_operation' AND [TargetId] = @id;",
                ("@id", operationId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await DeleteRequestAsync(seeded.Id);
            await RestoreCancellationEmailSettingsAsync(settings);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task WeeklySummaryDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        var (staff, _) = await CreatePostCommitAdminAsync();
        var settings = await ConfigureCancellationEmailSettingsAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        string? originalStaffUrl = null;
        var barcode = $"weekly-cancel-{Guid.NewGuid():N}"[..35];
        var title = $"Weekly cancellation summary {Guid.NewGuid():N}";
        using (var setup = await contexts.CreateDbContextAsync())
        {
            var system = await setup.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            originalStaffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            var recipient = await setup.StaffUsers.SingleAsync(item => item.Id == staff.Id);
            recipient.WeeklyActionSummaryEnabled = true;
            recipient.WeeklyActionSummaryEmail = "weekly-cancel@example.org";
            var format = await setup.MaterialFormats.SingleAsync(item => item.Code == "book");
            setup.TitleRequests.Add(new TitleRequest
            {
                LibraryOrganizationId = 2,
                Barcode = barcode,
                Title = title,
                MaterialFormatId = format.Id,
                Status = "suggestion",
                CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
            });
            await setup.SaveChangesAsync();
        }

        var businessKeyPrefix = $"weekly-summary:{staff.Id}:";
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new MutableReadinessEmailSender(isConfigured: true));
            }));
        try
        {
            var workflow = scopedFactory.Services.GetRequiredService<WorkflowProcessingService>();
            var execution = Task.Run(() => workflow.SendWeeklyStaffSummaryAsync(
                null, 2, cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.HasCount(1, dispatcher.EnqueuedIds);
            var committed = await ReadCommittedOutboxByIdAsync(dispatcher.EnqueuedIds.Single());
            Assert.IsTrue(committed.BusinessKey!.StartsWith(businessKeyPrefix, StringComparison.Ordinal));
            AssertOutboxStillPending(committed);

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => execution);

            AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(dispatcher.EnqueuedIds.Single()));
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(item =>
                item.BusinessKey != null && item.BusinessKey.StartsWith(businessKeyPrefix)));
            Assert.AreEqual("suggestion", await verify.TitleRequests.Where(item => item.Barcode == barcode)
                .Select(item => item.Status).SingleAsync());
        }
        finally
        {
            dispatcher.Release();
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE @prefix; DELETE FROM [asap].[TitleRequest] WHERE [Barcode] = @barcode;",
                ("@prefix", businessKeyPrefix + "%"), ("@barcode", barcode));
            await using var restore = await contexts.CreateDbContextAsync();
            var system = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            system.StaffApplicationUrl = originalStaffUrl;
            var recipient = await restore.StaffUsers.SingleAsync(item => item.Id == staff.Id);
            recipient.WeeklyActionSummaryEnabled = false;
            recipient.WeeklyActionSummaryEmail = null;
            await restore.SaveChangesAsync();
            await RestoreCancellationEmailSettingsAsync(settings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task TimeoutEmailDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        const int scope = 99017;
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, scope);
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        WorkflowSettings? originalSettings = null;
        var createdSettings = false;
        var requestIds = new List<long>();
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var barcode = $"timeout-cancel-{Guid.NewGuid():N}"[..35];
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
                        OrganizationId = settings.OrganizationId,
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
                request.Email = "timeout-cancel@example.org";
                seed.TitleRequests.Add(request);
                await seed.SaveChangesAsync();
                requestIds.Add(request.Id);
            }

            await PrepareTimeoutCycleAsync(contexts, QueueNames.OutstandingTimeout, requestIds.Single(), scope);
            using var cancellation = new CancellationTokenSource();
            await using var scopedFactory = factory!.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailOutboxDispatcher>();
                    services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(new MutableReadinessEmailSender(isConfigured: true));
                }));
            var workflow = scopedFactory.Services.GetRequiredService<WorkflowProcessingService>();
            var execution = Task.Run(() => workflow.ProcessWorkflowAsync(scope, cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.HasCount(1, dispatcher.EnqueuedIds);
            var outboxId = dispatcher.EnqueuedIds.Single();
            var committed = await ReadCommittedOutboxByIdAsync(outboxId);
            Assert.AreEqual($"timeout:OutstandingTimeout:{requestIds.Single()}", committed.BusinessKey);
            AssertOutboxStillPending(committed);
            await using (var before = await contexts.CreateDbContextAsync())
            {
                var request = await before.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestIds.Single());
                Assert.AreEqual("closed", request.Status);
                Assert.AreEqual("rejected", request.CloseReason);
                Assert.AreEqual(1, await before.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == request.Id && item.EventType == "timeout_closed"));
            }

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => execution);

            AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxId));
            await using var after = await contexts.CreateDbContextAsync();
            Assert.AreEqual(1, await after.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == requestIds.Single() && item.EventType == "timeout_closed"));
            Assert.AreEqual("closed", await after.TitleRequests.Where(item => item.Id == requestIds.Single())
                .Select(item => item.Status).SingleAsync());
        }
        finally
        {
            dispatcher.Release();
            await DeleteEmailOutboxByBusinessKeyAsync($"timeout:OutstandingTimeout:{requestIds.FirstOrDefault()}");
            await DeleteRequestIdsAsync(contexts, requestIds);
            await using var restore = await contexts.CreateDbContextAsync();
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
            await RestoreCancellationEmailSettingsAsync(emailSettings);
        }
    }

    private async Task<(string Status, byte[] RequestVersion, string OperationState, int EventCount, EmailOutbox Outbox)>
        ReadHoldResolutionSnapshotAsync(long requestId, long operationId, long outboxId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var operation = await context.HoldPlacementOperations.AsNoTracking().SingleAsync(item => item.Id == operationId);
        var events = await context.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == requestId && item.EventType == "hold_placed");
        var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId);
        Assert.AreEqual("hold_placed", request.Status);
        Assert.AreEqual("succeeded", operation.State);
        Assert.AreEqual("pending", outbox.Status);
        return (request.Status, request.RowVersion, operation.State, events, outbox);
    }

    private async Task<(StaffUser Row, CurrentStaff Actor)> CreatePostCommitAdminAsync()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var staff = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
        await using (var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                         .CreateDbContextAsync())
        {
            var row = await context.StaffUsers.SingleAsync(item => item.Id == staff.Id);
            row.NotificationEmail = row.UserPrincipalName;
            await context.SaveChangesAsync();
            staff.NotificationEmail = row.NotificationEmail;
        }
        return (staff, await ReadCorrectiveStaffAsync(staff));
    }

    private TitleRequestMutationService CreatePostCommitTitleMutationService(
        IEmailOutboxDispatcher? outboxDispatcher = null,
        IIdentifierLookupDispatcher? identifierDispatcher = null,
        IEmailSender? emailSender = null,
        IStaffPolarisProvider? staffPolarisProvider = null,
        IPatronProvider? patronProvider = null)
    {
        var services = factory!.Services;
        return new TitleRequestMutationService(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            emailSender ?? services.GetRequiredService<IEmailSender>(),
            services.GetRequiredService<RecipientDomainPolicy>(),
            outboxDispatcher ?? services.GetRequiredService<IEmailOutboxDispatcher>(),
            staffPolarisProvider ?? services.GetRequiredService<IStaffPolarisProvider>(),
            patronProvider ?? services.GetRequiredService<IPatronProvider>(),
            services.GetRequiredService<PatronConfigurationService>(),
            identifierDispatcher ?? services.GetRequiredService<IIdentifierLookupDispatcher>(),
            NullLogger<TitleRequestMutationService>.Instance,
            services.GetRequiredService<StaffEligibilityService>(),
            timeProvider!);
    }

    private AdditionalCopyService CreatePostCommitAdditionalCopyService(
        IEmailOutboxDispatcher dispatcher,
        IEmailSender? emailSender = null)
    {
        var services = factory!.Services;
        return new AdditionalCopyService(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            emailSender ?? services.GetRequiredService<IEmailSender>(),
            services.GetRequiredService<RecipientDomainPolicy>(),
            dispatcher,
            timeProvider!,
            NullLogger<AdditionalCopyService>.Instance,
            services.GetRequiredService<StaffEligibilityService>());
    }

    private EmailOperationsService CreatePostCommitEmailOperationsService(
        IEmailOutboxDispatcher dispatcher,
        IEmailSender? emailSender = null)
    {
        var services = factory!.Services;
        return new EmailOperationsService(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            dispatcher,
            emailSender ?? services.GetRequiredService<IEmailSender>(),
            services.GetRequiredService<RecipientDomainPolicy>(),
            timeProvider!,
            services.GetRequiredService<StaffEligibilityService>(),
            NullLogger<EmailOperationsService>.Instance);
    }

    private async Task<(byte[] RowVersion, string Status, int EventCount)> ReadCancellationTitleRequestSnapshotAsync(long requestId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        return (request.RowVersion, request.Status,
            await context.TitleRequestEvents.CountAsync(item => item.TitleRequestId == requestId));
    }

    private async Task<(byte[] RowVersion, int EventCount)> AssertTitleMutationCommittedAsync(
        long requestId,
        string expectedStatus,
        IReadOnlyList<long> outboxIds)
    {
        Assert.HasCount(1, outboxIds);
        var snapshot = await ReadCancellationTitleRequestSnapshotAsync(requestId);
        Assert.AreEqual(expectedStatus, snapshot.Status);
        Assert.IsGreaterThanOrEqualTo(1, snapshot.EventCount);
        AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxIds.Single()));
        return (snapshot.RowVersion, snapshot.EventCount);
    }

    private async Task AssertTitleMutationStillCommittedAsync(
        long requestId,
        IReadOnlyList<long> outboxIds,
        (byte[] RowVersion, int EventCount) committed)
    {
        var snapshot = await ReadCancellationTitleRequestSnapshotAsync(requestId);
        CollectionAssert.AreEqual(committed.RowVersion, snapshot.RowVersion);
        Assert.AreEqual(committed.EventCount, snapshot.EventCount,
            "Cancellation after commit must not repeat the accepted title event.");
        foreach (var outboxId in outboxIds)
        {
            AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxId));
        }
    }

    private async Task<List<long>> ReadCommittedTitleMutationOutboxIdsAsync(long requestId, string mutation)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var assignmentPrefix = $"title-assignment:{requestId}:";
        var purchaseReminderPrefix = $"purchase-reminder:{requestId}:";
        var patronActionPrefix = $"staff-patron-action:purchase_approved:{requestId}:";
        var rows = await context.EmailOutbox.AsNoTracking()
            .Where(item => item.Status == "pending" && item.BusinessKey != null &&
                (item.BusinessKey.StartsWith(assignmentPrefix) ||
                 mutation == "action" && (item.BusinessKey.StartsWith(purchaseReminderPrefix) ||
                                           item.BusinessKey.StartsWith(patronActionPrefix))))
            .Select(item => item.Id)
            .ToListAsync();
        return rows;
    }

    private async Task<(long TaskId, string Status, byte[] RowVersion, int SourceEventCount)> ReadCommittedAdditionalCopyAsync(
        long outboxId,
        string mutation,
        byte[]? expectedAssignmentVersion,
        long? expectedAssigneeId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId);
        var prefix = mutation == "create" ? "additional-copy-reminder:" : "additional-copy-assignment:";
        Assert.IsTrue(outbox.BusinessKey?.StartsWith(prefix, StringComparison.Ordinal) == true);
        var keyParts = outbox.BusinessKey![prefix.Length..].Split(':');
        var taskId = long.Parse(keyParts[0], System.Globalization.CultureInfo.InvariantCulture);
        if (mutation == "create")
        {
            Assert.AreEqual(1, keyParts.Length);
            Assert.IsNull(expectedAssignmentVersion);
            Assert.IsNull(expectedAssigneeId);
        }
        else
        {
            Assert.AreEqual(3, keyParts.Length);
            var assignmentVersion = Convert.FromHexString(keyParts[1]);
            Assert.AreEqual(8, assignmentVersion.Length);
            Assert.IsNotNull(expectedAssignmentVersion);
            CollectionAssert.AreEqual(expectedAssignmentVersion, assignmentVersion);
            var assigneeId = long.Parse(keyParts[2], System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(expectedAssigneeId, assigneeId);
        }
        var task = await context.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == taskId);
        if (mutation == "assign")
        {
            Assert.AreEqual(expectedAssigneeId, task.ClaimedByStaffUserId);
        }
        Assert.AreEqual("open", task.Status);
        var sourceId = task.SourceTitleRequestId!.Value;
        var eventCount = await context.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == sourceId && item.EventType == "additional_copy_created");
        Assert.AreEqual(1, eventCount);
        Assert.AreEqual("pending", outbox.Status);
        return (taskId, task.Status, task.RowVersion, eventCount);
    }

    private async Task AssertCommittedAdditionalCopyStillDurableAsync(
        (long TaskId, string Status, byte[] RowVersion, int SourceEventCount) committed,
        long outboxId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var task = await context.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == committed.TaskId);
        Assert.AreEqual(committed.Status, task.Status);
        CollectionAssert.AreEqual(committed.RowVersion, task.RowVersion);
        var eventCount = await context.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == task.SourceTitleRequestId && item.EventType == "additional_copy_created");
        Assert.AreEqual(committed.SourceEventCount, eventCount);
        AssertOutboxStillPending(await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId));
    }

    private async Task<(byte[] RowVersion, string Status, int RetryEvents)> ReadIdentifierRetrySnapshotAsync(long requestId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var events = await context.TitleRequestEvents.CountAsync(item =>
            item.TitleRequestId == requestId && item.EventType == "identifier_retry_requested");
        return (request.RowVersion, request.IsbnCheckStatus!, events);
    }

    private async Task<EmailOutbox> SeedFailedEmailOutboxAsync(string businessKey)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var row = new EmailOutbox
        {
            OrganizationId = 2,
            DeliveryClass = "operational_test",
            BusinessKey = businessKey,
            Status = "failed",
            AttemptCount = 3,
            LastErrorCode = "provider_failed",
            ToAddress = "retry@example.org",
            FromAddress = "system@example.org",
            Subject = "Retry cancellation",
            BodyText = "Retry cancellation",
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-1)
        };
        context.EmailOutbox.Add(row);
        await context.SaveChangesAsync();
        return row;
    }

    private async Task<EmailOutbox> ReadCommittedOutboxAsync(long outboxId, string businessKey)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId);
        Assert.AreEqual(businessKey, outbox.BusinessKey);
        Assert.AreEqual("pending", outbox.Status);
        return outbox;
    }

    private async Task<EmailOutbox> ReadCommittedOutboxByIdAsync(long outboxId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        return await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId);
    }

    private static void AssertOutboxStillPending(EmailOutbox outbox, int expectedAttempts = 0)
    {
        Assert.AreEqual("pending", outbox.Status);
        Assert.AreEqual(expectedAttempts, outbox.AttemptCount);
    }

    private async Task DeleteCommittedOutboxIdsAsync(IEnumerable<long> outboxIds)
    {
        var ids = outboxIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        await context.EmailOutbox.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync();
    }

    private async Task DeleteAdditionalCopyTestDataAsync(long sourceRequestId, long? taskId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        if (taskId.HasValue)
        {
            await context.AdditionalCopyRequests.Where(item => item.Id == taskId.Value).ExecuteDeleteAsync();
        }
        await context.TitleRequestEvents.Where(item =>
            item.TitleRequestId == sourceRequestId && item.EventType == "additional_copy_created").ExecuteDeleteAsync();
    }

    private async Task<EmailSettingsSnapshot> ConfigureCancellationEmailSettingsAsync()
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
        var library = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var snapshot = new EmailSettingsSnapshot(
            system.FromAddress,
            system.FromName,
            library is not null,
            library?.FromAddress,
            library?.FromName);
        library ??= new EmailSettings { OrganizationId = 2 };
        if (!snapshot.LibraryExisted)
        {
            context.EmailSettings.Add(library);
        }
        system.FromAddress = "system.sender@example.org";
        system.FromName = "System Sender";
        library.FromAddress = null;
        library.FromName = null;
        await context.SaveChangesAsync();
        return snapshot;
    }

    private async Task RestoreCancellationEmailSettingsAsync(EmailSettingsSnapshot snapshot)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
        system.FromAddress = snapshot.SystemFromAddress;
        system.FromName = snapshot.SystemFromName;
        var library = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        if (snapshot.LibraryExisted)
        {
            library!.FromAddress = snapshot.LibraryFromAddress;
            library.FromName = snapshot.LibraryFromName;
        }
        else if (library is not null)
        {
            context.EmailSettings.Remove(library);
        }
        await context.SaveChangesAsync();
    }

    private async Task DeleteEmailOutboxByBusinessKeyAsync(string businessKey)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        await context.EmailOutbox.Where(item => item.BusinessKey == businessKey).ExecuteDeleteAsync();
    }

    private sealed record EmailSettingsSnapshot(
        string? SystemFromAddress,
        string? SystemFromName,
        bool LibraryExisted,
        string? LibraryFromAddress,
        string? LibraryFromName);

    private sealed class CancelAfterIdentifierRetryDispatcher(string dispatchMode) : IIdentifierLookupDispatcher
    {
        private readonly TaskCompletionSource enqueueStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EnqueueStarted => enqueueStarted;
        public List<long> RequestIds { get; } = [];
        public void Release() => released.TrySetResult();

        public void Enqueue(long requestId, string identifier, int organizationId, byte[] expectedRowVersion)
        {
            RequestIds.Add(requestId);
            enqueueStarted.TrySetResult();
            released.Task.GetAwaiter().GetResult();
            switch (dispatchMode)
            {
                case "return":
                    return;
                case "canceled":
                    throw new OperationCanceledException("Identifier retry dispatcher observed cancellation.");
                case "failure":
                    throw new InvalidOperationException("Identifier retry dispatcher failed after cancellation.");
                default:
                    throw new AssertFailedException($"Unknown identifier retry dispatch mode {dispatchMode}.");
            }
        }
    }
}
