using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("action")]
    [DataRow("title_shared_refresh")]
    [DataRow("additional_copy_shared_refresh")]
    public async Task PostCommitRefreshFaultPreservesCommittedHttpOutcome(string mutation)
    {
        var rootFactory = factory!;
        var (staff, actor) = await CreatePostCommitAdminAsync();
        var emailSettings = await ConfigureCancellationEmailSettingsAsync();
        var serverRequestCancellation = new CancellationTokenSource();
        var clientCancellation = new CancellationTokenSource();
        var innerContexts = rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var faultingContexts = new PostCommitRefreshFaultingDbContextFactory(innerContexts);
        var dispatcher = new PostCommitRefreshCancelAndArmDispatcher(serverRequestCancellation, faultingContexts);
        var capturedLogs = new PostCommitRefreshLogProvider();
        long titleRequestId = 0;
        long copySourceRequestId = 0;
        long copyTaskId = 0;
        long copyOutboxId = 0;
        long copyCurrentClaimantId = 0;
        long copyHistoricalRuleId = 0;
        int titleBeforeEventCount = 0;
        byte[]? titleBeforeRowVersion = null;
        string? actionBarcode = null;
        string? version = null;
        byte[]? expectedCopyAssignmentVersion = null;
        var titleOutboxIds = new List<long>();
        DeterministicTestingPatronProvider? actionPatrons = null;
        HttpResponseMessage? response = null;

        try
        {
            if (mutation is "action" or "title_shared_refresh")
            {
                var seeded = await SeedBibOwnershipRequestAsync(
                    $"post-commit-refresh-{mutation}-{Guid.NewGuid():N}",
                    null,
                    staffVerified: false,
                    status: "suggestion");
                titleRequestId = seeded.Id;
                var before = await ReadCancellationTitleRequestSnapshotAsync(titleRequestId);
                Assert.AreEqual("suggestion", before.Status);
                titleBeforeEventCount = before.EventCount;
                titleBeforeRowVersion = before.RowVersion;
                version = StaffVersion.Encode(before.RowVersion);
                if (mutation == "action")
                {
                    await using var context = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                        .CreateDbContextAsync();
                    actionBarcode = await context.TitleRequests.AsNoTracking()
                        .Where(item => item.Id == titleRequestId)
                        .Select(item => item.Barcode)
                        .SingleAsync();
                }
            }
            else
            {
                var seeded = await SeedAdditionalCopyLegacySourceAsync(
                    staff.Id,
                    $"post-commit-refresh-copy-{Guid.NewGuid():N}");
                copySourceRequestId = seeded.SourceRequestId;
                copyCurrentClaimantId = seeded.CurrentClaimantId;
                copyHistoricalRuleId = seeded.HistoricalRuleId;
                var copies = rootFactory.Services.GetRequiredService<AdditionalCopyService>();
                var preview = await copies.PreviewAsync(actor, copySourceRequestId, CancellationToken.None);
                Assert.AreEqual("loaded", preview.Code);
                Assert.IsNotNull(preview.Preview);
                var taskService = CreatePostCommitAdditionalCopyService(
                    new RecordingOutboxDispatcher(),
                    new MutableReadinessEmailSender(isConfigured: true));
                var created = await taskService.CreateAsync(
                    actor,
                    copySourceRequestId,
                    new AdditionalCopyCreateInput(preview.Preview.Version, EmailPurchaseReminder: false),
                    CancellationToken.None);
                Assert.AreEqual("created", created.Code);
                copyTaskId = created.RequestId ??
                    throw new AssertFailedException("The setup additional-copy task was not created.");
                var current = await copies.GetAsync(actor, copyTaskId.ToString(), null, CancellationToken.None);
                Assert.IsNotNull(current);
                Assert.IsTrue(StaffVersion.TryDecode(current.Version, out expectedCopyAssignmentVersion));
                version = current.Version;
            }

            await using var scopedFactory = rootFactory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextFactory<AsapDbContext>>();
                    services.AddSingleton<IDbContextFactory<AsapDbContext>>(faultingContexts);
                    services.RemoveAll<IEmailOutboxDispatcher>();
                    services.AddSingleton<IEmailOutboxDispatcher>(dispatcher);
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(new MutableReadinessEmailSender(isConfigured: true));
                    services.AddSingleton<IStartupFilter>(new PostCommitRefreshRequestAbortedStartupFilter(serverRequestCancellation));
                    services.AddLogging(logging => logging.AddProvider(capturedLogs));
                }));

            if (mutation == "action")
            {
                actionPatrons = scopedFactory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
                actionPatrons.AddPatron(
                    new PatronSnapshot(7001, actionBarcode!, "ownership@example.org", "Ownership", "Patron",
                        1, "Adult", 101, 2, "Test Library", 101),
                    [new PickupBranch(101, "Main Library")],
                    2);
            }

            using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = true
            });
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            var path = mutation switch
            {
                "action" => $"/api/asap/staff/title-requests/{titleRequestId}/action",
                "title_shared_refresh" => $"/api/asap/staff/title-requests/{titleRequestId}/assign",
                _ => $"/api/asap/staff/additional-copies/{copyTaskId}/assign"
            };
            var request = mutation switch
            {
                "action" => client.PostAsJsonAsync(path, new
                {
                    version,
                    action = "purchase",
                    status = "outstanding_purchase",
                    emailPurchaseReminder = true
                }, clientCancellation.Token),
                _ when mutation == "title_shared_refresh" => client.PostAsJsonAsync(path, new
                {
                    version,
                    assigneeId = actor.Id
                }, clientCancellation.Token),
                _ => client.PostAsJsonAsync(path, new
                {
                    version,
                    assigneeId = actor.Id
                }, clientCancellation.Token)
            };

            Exception? requestFailure = null;
            try
            {
                response = await request;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                requestFailure = exception;
            }

            Assert.IsTrue(serverRequestCancellation.IsCancellationRequested,
                "The deterministic dispatcher canceled the server-side request token after committing its outbox row.");
            Assert.IsFalse(clientCancellation.IsCancellationRequested,
                "This witness keeps the HTTP client live; it does not claim that a disconnected client receives a response.");
            Assert.IsTrue(dispatcher.EnqueuedIds.Count > 0, "The post-commit dispatcher must be reached.");
            Assert.IsNotNull(faultingContexts.Failure,
                "The optional detail read must reach the injected database-context fault after commit.");
            Assert.IsTrue(faultingContexts.FailureWasThrown,
                "The wrapper must throw the recorded failure from the optional detail-read context creation.");
            if (actionPatrons is not null)
            {
                Assert.IsTrue(actionPatrons.Calls.Any(call =>
                    call.Operation == TestingPolarisOperation.Refresh &&
                    call.OrganizationId == 2 && call.Key == actionBarcode),
                    "The action fixture must refresh its exact SQL-seeded patron with current pickup branch 101.");
            }

            if (titleRequestId > 0)
            {
                var after = await ReadCancellationTitleRequestSnapshotAsync(titleRequestId);
                Assert.AreEqual(mutation == "action" ? "outstanding_purchase" : "suggestion", after.Status);
                Assert.IsGreaterThan(titleBeforeEventCount, after.EventCount);
                Assert.IsFalse(titleBeforeRowVersion!.SequenceEqual(after.RowVersion),
                    "The durable mutation must advance the title rowversion before optional refresh.");
                titleOutboxIds.AddRange(await ReadCommittedTitleMutationOutboxIdsAsync(
                    titleRequestId,
                    mutation == "action" ? "action" : "assign"));
                Assert.AreEqual(mutation == "action" ? 2 : 1, titleOutboxIds.Count);
                CollectionAssert.AllItemsAreUnique(titleOutboxIds);
                Assert.IsTrue(dispatcher.EnqueuedIds.All(titleOutboxIds.Contains));
                foreach (var outboxId in titleOutboxIds)
                {
                    AssertOutboxStillPending(await ReadCommittedOutboxByIdAsync(outboxId));
                }
                var unchanged = await ReadCancellationTitleRequestSnapshotAsync(titleRequestId);
                CollectionAssert.AreEqual(after.RowVersion, unchanged.RowVersion);
                Assert.AreEqual(after.EventCount, unchanged.EventCount,
                    "The failed optional refresh must not repeat the committed title event.");
            }
            else
            {
                copyOutboxId = dispatcher.EnqueuedIds.Single();
                var committed = await ReadCommittedAdditionalCopyAsync(
                    copyOutboxId,
                    "assign",
                    expectedCopyAssignmentVersion,
                    actor.Id);
                Assert.AreEqual(copyTaskId, committed.TaskId);
                await AssertCommittedAdditionalCopyStillDurableAsync(committed, copyOutboxId);
                await using var verify = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                    .CreateDbContextAsync();
                Assert.AreEqual(1, await verify.AdditionalCopyRequests.AsNoTracking()
                    .CountAsync(item => item.SourceTitleRequestId == copySourceRequestId));
            }

            var failure = faultingContexts.Failure;
            Assert.IsNotNull(failure);
            var expectedLogCategory = mutation == "additional_copy_shared_refresh"
                ? "Asap.Web.Features.Staff.AdditionalCopyEndpoints"
                : "Asap.Web.Features.Staff.TitleRequestEndpoints";
            var expectedLogText = mutation switch
            {
                "action" => "Request detail refresh failed after action",
                "title_shared_refresh" => "Request detail refresh failed after mutation",
                _ => "Additional-copy detail refresh failed after task"
            };
            Assert.IsTrue(capturedLogs.Entries.Any(entry =>
                entry.Category == expectedLogCategory &&
                entry.Message.Contains(expectedLogText, StringComparison.Ordinal) &&
                ReferenceEquals(entry.Exception, failure)),
                "The endpoint must record the original post-commit refresh failure rather than replacing it with cancellation.");

            if (response is not null)
            {
                Assert.IsNull(requestFailure, requestFailure?.ToString());
                string? responseBody = null;
                Exception? responseReadFailure = null;
                try
                {
                    responseBody = await response.Content.ReadAsStringAsync();
                }
                catch (Exception exception) when (
                    serverRequestCancellation.IsCancellationRequested &&
                    exception is OperationCanceledException or HttpRequestException or IOException)
                {
                    responseReadFailure = exception;
                }
                if (responseReadFailure is null)
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, responseBody);
                    using var body = JsonDocument.Parse(responseBody!);
                    var root = body.RootElement;
                    Assert.IsTrue(root.GetProperty("committed").GetBoolean());
                    Assert.IsTrue(root.GetProperty("refreshUnavailable").GetBoolean());
                    Assert.AreEqual(JsonValueKind.Null, root.GetProperty("request").ValueKind);
                    if (mutation == "action")
                    {
                        Assert.AreEqual("outstanding_purchase", root.GetProperty("finalStatus").GetString());
                    }
                }
                else
                {
                    Assert.IsTrue(serverRequestCancellation.IsCancellationRequested,
                        "An incomplete response is acceptable only after the recorded server-side cancellation.");
                }
            }
            else
            {
                Assert.IsNotNull(requestFailure,
                    "A missing response is acceptable only when the canceled server-side request ended the HTTP exchange.");
                Assert.IsTrue(serverRequestCancellation.IsCancellationRequested);
            }
        }
        finally
        {
            response?.Dispose();
            if (titleRequestId > 0)
            {
                if (titleOutboxIds.Count == 0)
                {
                    titleOutboxIds.AddRange(await ReadCommittedTitleMutationOutboxIdsAsync(
                        titleRequestId,
                        mutation == "action" ? "action" : "assign"));
                }
                await DeleteCommittedOutboxIdsAsync(titleOutboxIds);
                await DeleteBibOwnershipRequestsAsync([titleRequestId]);
            }
            if (copySourceRequestId > 0)
            {
                if (copyOutboxId == 0 && copyTaskId > 0)
                {
                    await using var context = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                        .CreateDbContextAsync();
                    copyOutboxId = await context.EmailOutbox.AsNoTracking()
                        .Where(item => item.BusinessKey != null && item.BusinessKey.StartsWith(
                            $"additional-copy-assignment:{copyTaskId}:"))
                        .Select(item => item.Id)
                        .SingleOrDefaultAsync();
                }
                if (copyOutboxId > 0)
                {
                    await DeleteCommittedOutboxIdsAsync([copyOutboxId]);
                }
                await DeleteAdditionalCopyTestDataAsync(copySourceRequestId, copyTaskId > 0 ? copyTaskId : null);
                await DeleteBibOwnershipRequestsAsync([copySourceRequestId]);
                await ExecuteNonQueryAsync(
                    "DECLARE @historicalStaffId bigint = (SELECT [StaffUserId] FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @ruleId); " +
                    "DELETE FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @ruleId; " +
                    "DELETE FROM [asap].[StaffUser] WHERE [Id] IN (@claimantId, @historicalStaffId);",
                    ("@ruleId", copyHistoricalRuleId),
                    ("@claimantId", copyCurrentClaimantId));
            }
            await RestoreCancellationEmailSettingsAsync(emailSettings);
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [NotificationEmail] = @email, " +
                "[AdditionalCopyReminderDefault] = @reminder WHERE [Id] = @staffId;",
                ("@email", (object?)staff.NotificationEmail ?? DBNull.Value),
                ("@reminder", staff.AdditionalCopyReminderDefault),
                ("@staffId", staff.Id));
            await DeactivateCorrectiveStaffAsync(staff.Id);
            serverRequestCancellation.Dispose();
            clientCancellation.Dispose();
        }
    }

    private sealed class PostCommitRefreshRequestAbortedStartupFilter(CancellationTokenSource cancellation) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Method == HttpMethods.Post &&
                    context.Request.Path.Value is { } path &&
                    (path.EndsWith("/action", StringComparison.Ordinal) || path.EndsWith("/assign", StringComparison.Ordinal)))
                {
                    context.RequestAborted = cancellation.Token;
                }
                await nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class PostCommitRefreshFaultingDbContextFactory(
        IDbContextFactory<AsapDbContext> inner) : IDbContextFactory<AsapDbContext>
    {
        private int armed;
        private int failureWasThrown;
        private InvalidOperationException? failure;

        public InvalidOperationException? Failure => Volatile.Read(ref failure);
        public bool FailureWasThrown => Volatile.Read(ref failureWasThrown) != 0;

        public void ArmFailure()
        {
            Interlocked.Exchange(ref failure,
                new InvalidOperationException("Injected optional detail refresh failure after commit."));
            Volatile.Write(ref armed, 1);
        }

        public AsapDbContext CreateDbContext()
        {
            ThrowIfArmed();
            return inner.CreateDbContext();
        }

        public Task<AsapDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfArmed();
            return inner.CreateDbContextAsync(cancellationToken);
        }

        private void ThrowIfArmed()
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
            {
                return;
            }
            Volatile.Write(ref failureWasThrown, 1);
            throw Failure ?? new InvalidOperationException("The optional detail refresh fault was not recorded.");
        }
    }

    private sealed class PostCommitRefreshCancelAndArmDispatcher(
        CancellationTokenSource serverRequestCancellation,
        PostCommitRefreshFaultingDbContextFactory contexts) : IEmailOutboxDispatcher
    {
        private int armed;
        private readonly ConcurrentQueue<long> enqueuedIds = new();

        public IReadOnlyList<long> EnqueuedIds => enqueuedIds.ToArray();

        public void Enqueue(long outboxId)
        {
            enqueuedIds.Enqueue(outboxId);
            if (Interlocked.Exchange(ref armed, 1) == 0)
            {
                contexts.ArmFailure();
                serverRequestCancellation.Cancel();
            }
        }
    }

    private sealed record PostCommitRefreshLogEntry(string Category, string Message, Exception? Exception);

    private sealed class PostCommitRefreshLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<PostCommitRefreshLogEntry> entries = new();

        public IReadOnlyList<PostCommitRefreshLogEntry> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new PostCommitRefreshLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class PostCommitRefreshLogScope : IDisposable
        {
            public static readonly PostCommitRefreshLogScope Instance = new();
            public void Dispose()
            {
            }
        }

        private sealed class PostCommitRefreshLogger(
            string category,
            ConcurrentQueue<PostCommitRefreshLogEntry> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => PostCommitRefreshLogScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(new PostCommitRefreshLogEntry(category, formatter(state, exception), exception));
            }
        }
    }
}
