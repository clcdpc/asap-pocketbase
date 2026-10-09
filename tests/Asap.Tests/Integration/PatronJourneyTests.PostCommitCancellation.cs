using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task PublicSuggestionDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        var patron = AddPostCommitCancellationPatron();
        var session = await IssueTestPatronSessionAsync(patron.Barcode);
        var title = $"Public post-commit cancellation {Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var service = CreatePostCommitSuggestionService(patron.Provider, dispatcher);
        try
        {
            var submission = Task.Run(() => service.CreateAsync(
                session,
                Suggestion(title),
                cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var durableRequest = await AssertCommittedSuggestionAsync(title, dispatcher.EnqueuedIds);

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => submission);

            await AssertCommittedSuggestionStillDurableAsync(
                durableRequest.Request, dispatcher.EnqueuedIds.Single(), durableRequest.EventCount);
        }
        finally
        {
            dispatcher.Release();
            await DeletePostCommitSuggestionAsync(title, session.Id);
        }
    }

    [TestMethod]
    [DataRow("return")]
    [DataRow("canceled")]
    [DataRow("failure")]
    public async Task StaffSuggestionDispatchCancellationPropagatesAfterDurableCommit(string dispatchMode)
    {
        var patron = AddPostCommitCancellationPatron();
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = $"Staff post-commit cancellation {Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var dispatcher = new CancelAfterCommitOutboxDispatcher(dispatchMode);
        var service = CreatePostCommitSuggestionService(patron.Provider, dispatcher);
        var input = new StaffSuggestionInput(
            2,
            patron.Barcode,
            "book",
            title,
            "Cancellation Test Author",
            null,
            "Coming soon",
            null,
            null,
            101,
            101,
            true,
            true,
            new Dictionary<string, string?>());
        try
        {
            var submission = Task.Run(() => service.CreateForStaffAsync(
                actor,
                2,
                input,
                cancellation.Token));
            await dispatcher.EnqueueStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var durableRequest = await AssertCommittedSuggestionAsync(title, dispatcher.EnqueuedIds);

            cancellation.Cancel();
            dispatcher.Release();
            await Assert.ThrowsAsync<OperationCanceledException>(() => submission);

            await AssertCommittedSuggestionStillDurableAsync(
                durableRequest.Request, dispatcher.EnqueuedIds.Single(), durableRequest.EventCount);
        }
        finally
        {
            dispatcher.Release();
            await DeletePostCommitSuggestionAsync(title, sessionId: null);
        }
    }

    [TestMethod]
    [DataRow("public", "return")]
    [DataRow("public", "canceled")]
    [DataRow("public", "failure")]
    [DataRow("staff", "return")]
    [DataRow("staff", "canceled")]
    [DataRow("staff", "failure")]
    public async Task SuggestionIdentifierFollowupCancellationPropagatesAfterDurableCommit(
        string caller,
        string providerMode)
    {
        var patron = AddPostCommitCancellationPatron();
        var identifier = "9780000000001";
        var title = $"{caller} identifier cancellation {Guid.NewGuid():N}";
        var session = caller == "public" ? await IssueTestPatronSessionAsync(patron.Barcode) : null;
        var actor = caller == "staff" ? await ReadConfiguredSuperAdminAsync() : null;
        using var cancellation = new CancellationTokenSource();
        var provider = new CancelAfterIdentifierProvider(patron.Provider, cancellation, providerMode);
        var dispatcher = new RecordingOutboxDispatcher();
        var service = CreatePostCommitSuggestionService(provider, dispatcher);
        var suggestion = Suggestion(title) with { Isbn = identifier };
        try
        {
            Task<PatronSuggestionResult> submission;
            if (caller == "public")
            {
                submission = Task.Run(() => service.CreateAsync(session!, suggestion, cancellation.Token));
            }
            else
            {
                var input = new StaffSuggestionInput(
                    2,
                    patron.Barcode,
                    "book",
                    title,
                    "Cancellation Test Author",
                    identifier,
                    "Coming soon",
                    null,
                    null,
                    101,
                    101,
                    true,
                    true,
                    new Dictionary<string, string?>());
                submission = Task.Run(() => service.CreateForStaffAsync(actor!, 2, input, cancellation.Token));
            }

            await provider.IdentifierLookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var durableRequest = await ReadCommittedSuggestionAsync(title);
            Assert.IsTrue(durableRequest.Request.Id > 0);
            Assert.AreEqual("pending", await ReadSubmissionOutboxStatusAsync(durableRequest.Request.Id));
            Assert.AreEqual(1, dispatcher.EnqueuedIds.Count,
                "The accepted request's one durable outbox is dispatched before optional identifier processing.");

            provider.ReleaseIdentifierLookup();
            await Assert.ThrowsAsync<OperationCanceledException>(() => submission);

            await AssertCommittedSuggestionStillDurableAsync(
                durableRequest.Request, dispatcher.EnqueuedIds.Single(), durableRequest.EventCount);
        }
        finally
        {
            provider.ReleaseIdentifierLookup();
            await DeletePostCommitSuggestionAsync(title, session?.Id);
        }
    }

    private (string Barcode, DeterministicTestingPatronProvider Provider) AddPostCommitCancellationPatron()
    {
        var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var barcode = $"postcommit-{Guid.NewGuid():N}";
        provider.AddPatron(
            new PatronSnapshot(860_000_000, barcode, "patron@example.org", "Post", "Commit",
                1, "Adult", 101, 2, "Test Library", 101),
            [new PickupBranch(101, "Main Library")],
            2);
        return (barcode, provider);
    }

    private PatronSuggestionService CreatePostCommitSuggestionService(
        IPatronProvider patronProvider,
        IEmailOutboxDispatcher outboxDispatcher,
        IEmailSender? emailSender = null,
        IStaffPolarisProvider? staffPolarisProvider = null)
    {
        var services = factory!.Services;
        return new PatronSuggestionService(
            services.GetRequiredService<ExternalConfiguration>(),
            services.GetRequiredService<PatronConfigurationService>(),
            patronProvider,
            outboxDispatcher,
            emailSender ?? new RecordingEmailSender(),
            services.GetRequiredService<RecipientDomainPolicy>(),
            timeProvider!,
            NullLogger<PatronSuggestionService>.Instance,
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            services.GetRequiredService<StaffEligibilityService>(),
            staffPolarisProvider ?? services.GetRequiredService<IStaffPolarisProvider>(),
            services.GetRequiredService<PickupPreferenceMutationService>());
    }

    private async Task<(TitleRequest Request, int EventCount)> AssertCommittedSuggestionAsync(
        string title,
        IReadOnlyList<long> enqueuedIds)
    {
        Assert.HasCount(1, enqueuedIds, "Exactly one committed outbox should reach immediate dispatch.");
        var request = await ReadCommittedSuggestionAsync(title);
        await AssertCommittedSuggestionStillDurableAsync(request.Request, enqueuedIds.Single(), request.EventCount);
        return request;
    }

    private async Task<(TitleRequest Request, int EventCount)> ReadCommittedSuggestionAsync(string title)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Title == title);
        Assert.IsNotNull(request, "The caller must wait at post-commit dispatch only after SQL acceptance is durable.");
        Assert.AreEqual("suggestion", request.Status);
        var eventCount = await context.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id);
        Assert.IsGreaterThanOrEqualTo(1, eventCount, "The accepted request must have its creation event.");
        return (request, eventCount);
    }

    private async Task AssertCommittedSuggestionStillDurableAsync(
        TitleRequest request,
        long outboxId,
        int eventCount)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var persisted = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
        Assert.AreEqual(request.Status, persisted.Status);
        CollectionAssert.AreEqual(request.RowVersion, persisted.RowVersion);
        Assert.AreEqual(1, await context.TitleRequests.CountAsync(item => item.Title == request.Title));
        Assert.AreEqual(eventCount,
            await context.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id),
            "Cancellation after commit must not repeat request event writes.");
        var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId);
        Assert.AreEqual($"patron-submission:{request.Id}", outbox.BusinessKey);
        Assert.AreEqual("pending", outbox.Status);
        Assert.AreEqual(1, await context.EmailOutbox.CountAsync(item => item.BusinessKey == outbox.BusinessKey));
    }

    private async Task<string> ReadSubmissionOutboxStatusAsync(long requestId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        return await context.EmailOutbox.AsNoTracking()
            .Where(item => item.BusinessKey == $"patron-submission:{requestId}")
            .Select(item => item.Status)
            .SingleAsync();
    }

    private async Task DeletePostCommitSuggestionAsync(string title, long? sessionId)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var requestId = await context.TitleRequests.AsNoTracking()
            .Where(item => item.Title == title)
            .Select(item => (long?)item.Id)
            .SingleOrDefaultAsync();
        if (requestId.HasValue)
        {
            await context.EmailOutbox.Where(item => item.BusinessKey == $"patron-submission:{requestId.Value}")
                .ExecuteDeleteAsync();
            await context.TitleRequestEvents.Where(item => item.TitleRequestId == requestId.Value)
                .ExecuteDeleteAsync();
            await context.TitleRequests.Where(item => item.Id == requestId.Value).ExecuteDeleteAsync();
        }
        if (sessionId.HasValue)
        {
            await context.PatronSessions.Where(item => item.Id == sessionId.Value).ExecuteDeleteAsync();
        }
    }

    private sealed class CancelAfterCommitOutboxDispatcher(
        string dispatchMode) : IEmailOutboxDispatcher
    {
        private readonly TaskCompletionSource enqueueStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EnqueueStarted => enqueueStarted;
        public List<long> EnqueuedIds { get; } = [];

        public void Release() => released.TrySetResult();

        public void Enqueue(long outboxId)
        {
            EnqueuedIds.Add(outboxId);
            enqueueStarted.TrySetResult();
            released.Task.GetAwaiter().GetResult();
            switch (dispatchMode)
            {
                case "return":
                    return;
                case "canceled":
                    throw new OperationCanceledException("The committed dispatcher observed caller cancellation.");
                case "failure":
                    throw new InvalidOperationException("The committed dispatcher failed after caller cancellation.");
                default:
                    throw new AssertFailedException($"Unknown post-commit dispatch mode {dispatchMode}.");
            }
        }
    }

    private sealed class CancelAfterIdentifierProvider(
        DeterministicTestingPatronProvider inner,
        CancellationTokenSource cancellation,
        string providerMode) : IPatronProvider
    {
        private readonly TaskCompletionSource lookupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource IdentifierLookupStarted => lookupStarted;
        public void ReleaseIdentifierLookup() => released.TrySetResult();

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken cancellationToken) =>
            inner.AuthenticateAsync(barcode, pin, cancellationToken);

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
            inner.RefreshAsync(barcode, organizationId, cancellationToken);

        public Task<int?> GetPatronIdAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
            inner.GetPatronIdAsync(barcode, organizationId, cancellationToken);

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int organizationId,
            CancellationToken cancellationToken) =>
            inner.GetPickupBranchesAsync(patron, organizationId, cancellationToken);

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId,
            CancellationToken cancellationToken) =>
            inner.UpdatePreferredPickupBranchAsync(barcode, pickupBranchId, organizationId, cancellationToken);

        public async Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int organizationId,
            CancellationToken cancellationToken)
        {
            lookupStarted.TrySetResult();
            await released.Task;
            if (providerMode is "return" or "canceled" or "failure")
            {
                cancellation.Cancel();
            }

            return providerMode switch
            {
                "return" => new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound),
                "canceled" => throw new OperationCanceledException("Identifier provider observed caller cancellation."),
                "failure" => throw new InvalidOperationException("Identifier provider failed after caller cancellation."),
                _ => throw new AssertFailedException($"Unknown identifier provider mode {providerMode}.")
            };
        }
    }
}
