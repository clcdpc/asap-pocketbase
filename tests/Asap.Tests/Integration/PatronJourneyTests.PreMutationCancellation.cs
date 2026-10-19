using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("refresh", "return_cancelled")]
    [DataRow("refresh", "operation_cancelled")]
    [DataRow("refresh", "failure_cancelled")]
    [DataRow("refresh", "failure_uncancelled")]
    [DataRow("branches", "return_cancelled")]
    [DataRow("branches", "operation_cancelled")]
    [DataRow("branches", "failure_cancelled")]
    [DataRow("branches", "failure_uncancelled")]
    [DataRow("readiness", "return_cancelled")]
    [DataRow("readiness", "operation_cancelled")]
    [DataRow("readiness", "failure_cancelled")]
    [DataRow("readiness", "failure_uncancelled")]
    public async Task PublicSuggestionDependencyCancellationStopsBeforePickupOrRequest(
        string stage,
        string providerMode)
    {
        var (barcode, inner) = AddPostCommitCancellationPatron();
        var session = await IssueTestPatronSessionAsync(barcode);
        var title = $"Public dependency cancellation {stage} {Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var provider = new PreMutationStaffSuggestionProvider(inner, stage, providerMode, cancellation);
        var emailSender = new PreMutationCancellationEmailSender(stage == "readiness", providerMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(provider, dispatcher, emailSender, provider);

        try
        {
            if (providerMode is "failure_cancelled" or "failure_uncancelled")
            {
                var failure = await Assert.ThrowsExactlyAsync<PatronFlowException>(() =>
                    service.CreateAsync(session, Suggestion(title), cancellation.Token));
                Assert.AreEqual(502, failure.StatusCode);
                Assert.AreEqual(providerMode == "failure_cancelled", cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    service.CreateAsync(session, Suggestion(title), cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, provider.RefreshCount);
            Assert.AreEqual(stage == "refresh" ? 0 : 1, provider.BranchCount);
            Assert.AreEqual(stage == "readiness" ? 1 : 0, emailSender.ReadinessCount);
            Assert.AreEqual(0, provider.UpdateCount);
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
            await AssertNoStaffSuggestionAsync(title);
            Assert.AreEqual(0, await PickupJournalCountAsync(barcode, state: null));
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, session.Id);
        }
    }

    [TestMethod]
    [DataRow("refresh", "return_cancelled")]
    [DataRow("refresh", "operation_cancelled")]
    [DataRow("refresh", "failure_cancelled")]
    [DataRow("refresh", "failure_uncancelled")]
    [DataRow("branches", "return_cancelled")]
    [DataRow("branches", "operation_cancelled")]
    [DataRow("branches", "failure_cancelled")]
    [DataRow("branches", "failure_uncancelled")]
    [DataRow("bib", "return_cancelled")]
    [DataRow("bib", "operation_cancelled")]
    [DataRow("bib", "failure_cancelled")]
    [DataRow("bib", "failure_uncancelled")]
    [DataRow("readiness", "return_cancelled")]
    [DataRow("readiness", "operation_cancelled")]
    [DataRow("readiness", "failure_cancelled")]
    [DataRow("readiness", "failure_uncancelled")]
    public async Task DirectStaffSuggestionDependencyCancellationStopsBeforePickupOrRequest(
        string stage,
        string providerMode)
    {
        var (barcode, inner) = AddPostCommitCancellationPatron();
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = $"Staff dependency cancellation {stage} {Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var provider = new PreMutationStaffSuggestionProvider(inner, stage, providerMode, cancellation);
        var emailSender = new PreMutationCancellationEmailSender(stage == "readiness", providerMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(
            provider,
            dispatcher,
            emailSender,
            provider);
        var input = new StaffSuggestionInput(
            2,
            barcode,
            "book",
            title,
            "Cancellation Author",
            null,
            "Coming soon",
            null,
            null,
            101,
            101,
            false,
            stage == "readiness",
            new Dictionary<string, string?>(),
            9001,
            true);

        try
        {
            if (providerMode is "failure_cancelled" or "failure_uncancelled")
            {
                var failure = await Assert.ThrowsExactlyAsync<PatronFlowException>(() =>
                    service.CreateForStaffAsync(actor, 2, input, cancellation.Token));
                Assert.AreEqual(502, failure.StatusCode);
                Assert.AreEqual(providerMode == "failure_cancelled", cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    service.CreateForStaffAsync(actor, 2, input, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            Assert.AreEqual(1, provider.RefreshCount);
            Assert.AreEqual(stage is "refresh" ? 0 : 1, provider.BranchCount);
            Assert.AreEqual(stage is "bib" or "readiness" ? 1 : 0, provider.BibValidationCount);
            Assert.AreEqual(stage == "readiness" ? 1 : 0, emailSender.ReadinessCount);
            Assert.AreEqual(0, provider.UpdateCount,
                "No provider pickup PUT may run when caller cancellation or dependency failure occurs before the write intent.");
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
            await AssertNoStaffSuggestionAsync(title);
            Assert.AreEqual(0, await PickupJournalCountAsync(barcode, state: null));
        }
        finally
        {
            await DeletePostCommitSuggestionAsync(title, sessionId: null);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("operation_cancelled")]
    [DataRow("email_failure")]
    [DataRow("generic_failure")]
    public async Task OutboxReadinessCancellationRemainsVisibleAndReleasesPreSendClaim(string mode)
    {
        var seeded = await SeedSensitiveOutboxAsync($"readiness-caller-cancel-{mode}");
        using var cancellation = new CancellationTokenSource();
        var sender = new CancelAfterReadinessClaimEmailSender(cancellation, mode);
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
        var delivery = jobs.DeliverAsync(seeded.OutboxId, cancellation.Token);
        await sender.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var claimed = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("sending", claimed.Status, "Readiness is checked only after the SQL pre-send claim is acquired.");
        sender.ReleaseReadiness();
        switch (mode)
        {
            case "email_failure":
                await Assert.ThrowsExactlyAsync<EmailOperationalException>(() => delivery);
                break;
            case "generic_failure":
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => delivery);
                break;
            default:
                await Assert.ThrowsAsync<OperationCanceledException>(() => delivery);
                break;
        }

        var released = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("pending", released.Status);
        Assert.AreEqual("pre_send_cancelled", released.LastErrorCode);
        Assert.AreEqual(0, released.AttemptCount);
        Assert.IsNull(released.LeaseId);
        Assert.IsNull(released.SendingStartedUtc);
        Assert.IsNull(released.LeaseExpiresUtc);
        Assert.AreEqual(0, sender.SendCount);
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task TitleAssignmentReadinessCancellationPrecedesSqlMutation(string readinessMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var seeded = await SeedBibOwnershipRequestAsync(
            $"readiness-cancel-assign-{Guid.NewGuid():N}", null, false, status: "suggestion");
        var before = await ReadCancellationTitleRequestSnapshotAsync(seeded.Id);
        var assignmentBusinessKey = $"title-assignment:{seeded.Id}:{Convert.ToHexString(seeded.RowVersion)}:{actor.Id}";
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int outboxCountBefore;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            outboxCountBefore = await context.EmailOutbox.AsNoTracking()
                .CountAsync(item => item.BusinessKey == assignmentBusinessKey);
        }
        using var cancellation = new CancellationTokenSource();
        var sender = new PreMutationCancellationEmailSender(true, readinessMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitTitleMutationService(dispatcher, emailSender: sender);

        try
        {
            if (readinessMode is "failure_cancelled" or "failure_uncancelled")
            {
                Assert.AreEqual("notification_dependency_unavailable", (await service.AssignAsync(
                    actor,
                    seeded.Id,
                    new AssignTitleRequestInput(StaffVersion.Encode(seeded.RowVersion), actor.Id),
                    cancellation.Token)).Code);
                Assert.AreEqual(readinessMode == "failure_cancelled", cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.AssignAsync(
                    actor,
                    seeded.Id,
                    new AssignTitleRequestInput(StaffVersion.Encode(seeded.RowVersion), actor.Id),
                    cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            var after = await ReadCancellationTitleRequestSnapshotAsync(seeded.Id);
            Assert.AreEqual(before.Status, after.Status);
            CollectionAssert.AreEqual(before.RowVersion, after.RowVersion);
            Assert.AreEqual(before.EventCount, after.EventCount);
            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual(outboxCountBefore, await verify.EmailOutbox.AsNoTracking()
                .CountAsync(item => item.BusinessKey == assignmentBusinessKey));
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
        }
        finally
        {
            await DeleteCommittedOutboxIdsAsync(dispatcher.EnqueuedIds);
            await using (var cleanup = await contextFactory.CreateDbContextAsync())
            {
                await cleanup.EmailOutbox.Where(item => item.BusinessKey == assignmentBusinessKey)
                    .ExecuteDeleteAsync();
            }
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
            await RestoreCancellationEmailSettingsAsync(emailSettings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("create", "return_cancelled")]
    [DataRow("create", "operation_cancelled")]
    [DataRow("create", "failure_cancelled")]
    [DataRow("create", "failure_uncancelled")]
    [DataRow("assign", "return_cancelled")]
    [DataRow("assign", "operation_cancelled")]
    [DataRow("assign", "failure_cancelled")]
    [DataRow("assign", "failure_uncancelled")]
    public async Task AdditionalCopyReadinessCancellationPrecedesSqlMutation(string mutation, string readinessMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var seeded = await SeedAdditionalCopyLegacySourceAsync(staff.Id, $"readiness-cancel-{Guid.NewGuid():N}");
        var baseService = factory!.Services.GetRequiredService<AdditionalCopyService>();
        var preview = await baseService.PreviewAsync(actor, seeded.SourceRequestId, CancellationToken.None);
        Assert.AreEqual("loaded", preview.Code);
        Assert.IsNotNull(preview.Preview);
        long? taskId = null;
        AdditionalCopyDto? beforeTask = null;
        if (mutation == "assign")
        {
            var created = await baseService.CreateAsync(
                actor,
                seeded.SourceRequestId,
                new AdditionalCopyCreateInput(preview.Preview!.Version, EmailPurchaseReminder: false),
                CancellationToken.None);
            Assert.AreEqual("created", created.Code);
            var createdTaskId = created.RequestId
                ?? throw new AssertFailedException("The additional-copy task was not returned after creation.");
            taskId = createdTaskId;
            beforeTask = await baseService.GetAsync(actor, createdTaskId.ToString(), null, CancellationToken.None)
                ?? throw new AssertFailedException("The newly created additional-copy task was not readable.");
        }
        using var cancellation = new CancellationTokenSource();
        var sender = new PreMutationCancellationEmailSender(true, readinessMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitAdditionalCopyService(dispatcher, sender);
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        int outboxCountBefore;
        int copyEventCountBefore;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            outboxCountBefore = await context.EmailOutbox.AsNoTracking().CountAsync(item =>
                item.RecipientStaffUserId == actor.Id && item.BusinessKey != null &&
                (item.BusinessKey.StartsWith("additional-copy-reminder:") ||
                 item.BusinessKey.StartsWith("additional-copy-assignment:")));
            copyEventCountBefore = await context.TitleRequestEvents.AsNoTracking().CountAsync(item =>
                item.TitleRequestId == seeded.SourceRequestId && item.EventType == "additional_copy_created");
        }

        try
        {
            if (readinessMode is "failure_cancelled" or "failure_uncancelled")
            {
                Assert.AreEqual("notification_dependency_unavailable", (await RunAdditionalCopyReadinessMutationAsync(
                    mutation, service, actor, seeded.SourceRequestId, preview.Preview!.Version,
                    taskId, beforeTask, cancellation.Token)).Code);
                Assert.AreEqual(readinessMode == "failure_cancelled", cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => RunAdditionalCopyReadinessMutationAsync(
                    mutation, service, actor, seeded.SourceRequestId, preview.Preview!.Version,
                    taskId, beforeTask, cancellation.Token));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            await using var context = await contextFactory.CreateDbContextAsync();
            if (mutation == "create")
            {
                Assert.AreEqual(0, await context.AdditionalCopyRequests.AsNoTracking()
                    .CountAsync(item => item.SourceTitleRequestId == seeded.SourceRequestId));
            }
            else
            {
                var after = await context.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.Id == taskId!.Value);
                Assert.AreEqual(beforeTask!.ClaimedByDisplayName, after.ClaimedByDisplayName,
                    "Readiness cancellation must preserve the existing task owner.");
                Assert.AreEqual("open", after.Status);
                Assert.AreEqual(beforeTask!.Version, StaffVersion.Encode(after.RowVersion));
            }
            Assert.AreEqual(copyEventCountBefore, await context.TitleRequestEvents.AsNoTracking().CountAsync(item =>
                item.TitleRequestId == seeded.SourceRequestId && item.EventType == "additional_copy_created"));
            Assert.AreEqual(outboxCountBefore, await context.EmailOutbox.AsNoTracking().CountAsync(item =>
                item.RecipientStaffUserId == actor.Id && item.BusinessKey != null &&
                (item.BusinessKey.StartsWith("additional-copy-reminder:") ||
                 item.BusinessKey.StartsWith("additional-copy-assignment:"))));
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
        }
        finally
        {
            await DeleteCommittedOutboxIdsAsync(dispatcher.EnqueuedIds);
            await using (var cleanup = await contextFactory.CreateDbContextAsync())
            {
                await cleanup.EmailOutbox.Where(item =>
                        item.RecipientStaffUserId == actor.Id && item.BusinessKey != null &&
                        (item.BusinessKey.StartsWith("additional-copy-reminder:") ||
                         item.BusinessKey.StartsWith("additional-copy-assignment:")))
                    .ExecuteDeleteAsync();
                var taskIds = await cleanup.AdditionalCopyRequests.AsNoTracking()
                    .Where(item => item.SourceTitleRequestId == seeded.SourceRequestId)
                    .Select(item => item.Id).ToArrayAsync();
                if (taskIds.Length > 0)
                {
                    await cleanup.AdditionalCopyRequests.Where(item => taskIds.Contains(item.Id)).ExecuteDeleteAsync();
                }
                await cleanup.TitleRequestEvents.Where(item =>
                    item.TitleRequestId == seeded.SourceRequestId && item.EventType == "additional_copy_created")
                    .ExecuteDeleteAsync();
            }
            await DeleteAdditionalCopyTestDataAsync(seeded.SourceRequestId, taskId: null);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    private Task<AdditionalCopyMutationResult> RunAdditionalCopyReadinessMutationAsync(
        string mutation,
        AdditionalCopyService service,
        CurrentStaff actor,
        long sourceRequestId,
        string sourceVersion,
        long? taskId,
        AdditionalCopyDto? beforeTask,
        CancellationToken cancellationToken) =>
        mutation == "create"
            ? service.CreateAsync(actor, sourceRequestId,
                new AdditionalCopyCreateInput(sourceVersion, EmailPurchaseReminder: true), cancellationToken)
            : service.AssignAsync(actor, taskId!.Value,
                new AssignAdditionalCopyInput(beforeTask!.Version, actor.Id), cancellationToken);

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task EmailTestReadinessCancellationStopsBeforeDurableQueue(string readinessMode)
    {
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var operationId = Guid.NewGuid();
        var businessKey = $"operational-test:2:{actor.Id}:{operationId:N}";
        using var cancellation = new CancellationTokenSource();
        var sender = new PreMutationCancellationEmailSender(true, readinessMode, cancellation);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitEmailOperationsService(dispatcher, sender);

        try
        {
            if (readinessMode is "failure_cancelled" or "failure_uncancelled")
            {
                Assert.AreEqual("email_transport_unavailable", (await service.QueueTestAsync(
                    actor, 2, cancellation.Token, operationId)).Code);
                Assert.AreEqual(readinessMode == "failure_cancelled", cancellation.IsCancellationRequested);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    service.QueueTestAsync(actor, 2, cancellation.Token, operationId));
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                .CreateDbContextAsync();
            Assert.AreEqual(0, await context.EmailOutbox.AsNoTracking().CountAsync(item => item.BusinessKey == businessKey));
            Assert.AreEqual(0, dispatcher.EnqueuedIds.Count);
        }
        finally
        {
            await DeleteEmailOutboxByBusinessKeyAsync(businessKey);
            await RestoreCancellationEmailSettingsAsync(emailSettings);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    private async Task AssertNoStaffSuggestionAsync(string title)
    {
        await using var context = await factory!.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        Assert.AreEqual(0, await context.TitleRequests.AsNoTracking().CountAsync(item => item.Title == title));
    }

    private sealed class PreMutationStaffSuggestionProvider(
        IPatronProvider inner,
        string boundary,
        string mode,
        CancellationTokenSource cancellation) : IPatronProvider, IStaffPolarisProvider
    {
        public int RefreshCount { get; private set; }
        public int BranchCount { get; private set; }
        public int BibValidationCount { get; private set; }
        public int IdentifierLookupCount { get; private set; }
        public int UpdateCount { get; private set; }

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken token) =>
            inner.AuthenticateAsync(barcode, pin, token);

        public async Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken token)
        {
            RefreshCount++;
            var patron = await inner.RefreshAsync(barcode, organizationId, token);
            ApplyBoundary("refresh", token);
            return patron;
        }

        public async Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int organizationId,
            CancellationToken token)
        {
            BranchCount++;
            var branches = await inner.GetPickupBranchesAsync(patron, organizationId, token);
            ApplyBoundary("branches", token);
            return branches.Append(new PickupBranch(102, "Second Library")).ToArray();
        }

        public async Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId,
            CancellationToken token)
        {
            UpdateCount++;
            await inner.UpdatePreferredPickupBranchAsync(barcode, pickupBranchId, organizationId, token);
        }

        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, int organizationId, CancellationToken token)
        {
            IdentifierLookupCount++;
            ApplyBoundary("identifier", token);
            if (boundary == "identifier" && mode == "return_cancelled")
            {
                return Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound));
            }
            return inner.LookupIdentifierAsync(identifier, organizationId, token);
        }

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken token) =>
            Task.FromException<IReadOnlyList<PolarisOrganizationSnapshot>>(new NotSupportedException());

        public Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken token)
        {
            BibValidationCount++;
            ApplyBoundary("bib", token);
            return Task.FromResult(new BibValidationResult(true));
        }

        public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(string query, int organizationId, CancellationToken token) =>
            Task.FromException<IReadOnlyList<PatronSnapshot>>(new NotSupportedException());

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int organizationId, CancellationToken token) =>
            Task.FromException<IReadOnlyList<PolarisHoldSnapshot>>(new NotSupportedException());

        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken token) =>
            Task.FromException<HoldProviderResult>(new NotSupportedException());

        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken token) =>
            Task.FromException<HoldProviderResult>(new NotSupportedException());

        private void ApplyBoundary(string current, CancellationToken token)
        {
            if (boundary != current)
            {
                return;
            }
            switch (mode)
            {
                case "return_cancelled":
                    cancellation.Cancel();
                    return;
                case "operation_cancelled":
                    cancellation.Cancel();
                    throw new OperationCanceledException("The provider canceled after the caller token.", token);
                case "failure_cancelled":
                    cancellation.Cancel();
                    throw new PolarisOperationalException("testing_dependency_failure", "The provider failed after cancellation.");
                case "failure_uncancelled":
                    throw new PolarisOperationalException("testing_dependency_failure", "The provider failed without cancellation.");
                default:
                    throw new AssertFailedException($"Unknown provider mode {mode}.");
            }
        }
    }

    private sealed class PreMutationCancellationEmailSender(
        bool isBoundary,
        string mode,
        CancellationTokenSource cancellation) : IEmailSender
    {
        public int ReadinessCount { get; private set; }

        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken token)
        {
            ReadinessCount++;
            if (isBoundary)
            {
                switch (mode)
                {
                    case "return_cancelled":
                        cancellation.Cancel();
                        return Task.FromResult(EmailTransportReadiness.Configured);
                    case "operation_cancelled":
                        cancellation.Cancel();
                        return Task.FromException<EmailTransportReadiness>(new OperationCanceledException(
                            "Readiness canceled after the caller token.", token));
                    case "failure_cancelled":
                        cancellation.Cancel();
                        return Task.FromException<EmailTransportReadiness>(new EmailOperationalException(
                            "Readiness failed after cancellation."));
                    case "failure_uncancelled":
                        return Task.FromException<EmailTransportReadiness>(new EmailOperationalException(
                            "Readiness failed without cancellation."));
                    default:
                        throw new AssertFailedException($"Unknown readiness mode {mode}.");
                }
            }
            return Task.FromResult(EmailTransportReadiness.Configured);
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken token) =>
            Task.FromException<EmailSendResult>(new NotSupportedException());
    }

    private sealed class CancelAfterReadinessClaimEmailSender(
        CancellationTokenSource cancellation,
        string mode) : IEmailSender
    {
        private readonly TaskCompletionSource readinessStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource readinessReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadinessStarted => readinessStarted;
        public int SendCount { get; private set; }

        public void ReleaseReadiness() => readinessReleased.TrySetResult();

        public async Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken token)
        {
            readinessStarted.TrySetResult();
            await readinessReleased.Task;
            cancellation.Cancel();
            return mode switch
            {
                "return" => EmailTransportReadiness.Configured,
                "operation_cancelled" => throw new OperationCanceledException("Readiness observed caller cancellation.", token),
                "email_failure" => throw new EmailOperationalException("Readiness failed after caller cancellation."),
                "generic_failure" => throw new InvalidOperationException("Readiness failed after caller cancellation."),
                _ => throw new AssertFailedException($"Unknown readiness mode {mode}.")
            };
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken token)
        {
            SendCount++;
            return Task.FromResult(new EmailSendResult("unexpected-send"));
        }
    }
}
