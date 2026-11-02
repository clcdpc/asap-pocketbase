using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    // Incomplete-body, true send/read cancellation, and pre-dispatch programming controls remain covered by
    // PolarisHoldBoundaryRetainsUncertaintyAfterPossibleDispatch,
    // PolarisMutationCallerCancellationDuringSendPropagates, and
    // PolarisMutationBoundaryPropagatesProgrammingDefectsAndCallerCancellation.
    [TestMethod]
    public async Task PolarisParsedPickupSuccessIsJournaledBeforeCanceledRequestRecovery()
    {
        const int organizationId = 3505;
        var journal = new PickupJournalProvider(organizationId);
        using var cancellation = new CancellationTokenSource();
        var cleanupFault = new HttpRequestException(
            "Cleanup fault after parsed pickup success. cleanup-secret=https://polaris.invalid/provider-body");
        var cleanupLogger = new CapturingProviderCleanupLogger();
        var handler = new DisposeBoundaryPickupHandler(cancellation,
            onMutationResponseCreated: () => journal.Current = journal.SecondBranch,
            throwOnResponseDispose: cleanupFault);
        var polaris = await CreatePolarisProviderAsync(
            handler, "pickup-return-sql-" + Guid.NewGuid().ToString("N"), cleanupLogger);
        var provider = new ReturnedPickupBoundaryProvider(journal, polaris);
        await using var scoped = CreatePickupPolarisFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, journal);

        try
        {
            var outboxBefore = await ReadPickupOutboxCountAsync(contexts);
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.UpdateAsync(
                actor, request.Id,
                new(StaffVersion.Encode(request.RowVersion), journal.SecondBranch, journal.FirstBranch, true),
                cancellation.Token));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(journal.SecondBranch, journal.Current,
                "The simulated Polaris write is committed before the parsed response is disposed.");
            Assert.AreEqual(1, provider.UpdateCalls);
            Assert.AreEqual(1, handler.UpdateRequestCount,
                "The caller cancellation must not cause a second external pickup PUT.");
            Assert.AreEqual(HttpMethod.Put, handler.UpdateRequestMethod);
            Assert.IsTrue(handler.MutationResponseContentDisposed);
            Assert.AreEqual(1, handler.MutationResponseContent?.DisposeCount ?? 0,
                "The response wrapper must dispose the original content exactly once despite its cleanup fault.");
            AssertResponseCleanupDiagnostic(cleanupLogger, cleanupFault);

            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var unchanged = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(journal.FirstBranch, unchanged.PreferredPickupBranchId);
                CollectionAssert.AreEqual(request.RowVersion, unchanged.RowVersion,
                    "Caller cancellation stops request acceptance after the provider outcome is journaled.");
                Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }
            Assert.AreEqual(outboxBefore, await ReadPickupOutboxCountAsync(contexts),
                "Recording the known provider result before local acceptance creates no premature outbox work.");

            Guid operationId;
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [Id], [State], [ProviderConfirmedUtc], [DispatchFinishedUtc], [CompletedUtc],
                           [ObservedPickupBranchId], [ConfirmedByRead]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                    """, connection);
                command.Parameters.AddWithValue("@barcode", journal.Barcode);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                operationId = reader.GetGuid(0);
                Assert.AreEqual(2, reader.GetInt32(1),
                    "The parsed success must be stored atomically despite the canceled request token.");
                Assert.IsFalse(reader.IsDBNull(2));
                Assert.IsFalse(reader.IsDBNull(3));
                Assert.AreEqual(reader.GetDateTime(2), reader.GetDateTime(3));
                Assert.IsTrue(reader.IsDBNull(4), "Request acceptance remains a separate recoverable step.");
                Assert.AreEqual(journal.SecondBranch, reader.GetInt32(5));
                Assert.IsFalse(reader.GetBoolean(6), "This is provider confirmation, not a live-read inference.");
                Assert.IsFalse(await reader.ReadAsync(), "Exactly one durable operation represents the external PUT.");
            }

            var loaded = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
            Assert.AreEqual("loaded", loaded.Code);
            var options = loaded.Options ?? throw new AssertFailedException("Pickup recovery options were not returned.");
            Assert.AreEqual(journal.SecondBranch, options.CurrentPreferredPickupBranchId);
            var recovered = await service.UpdateAsync(actor, request.Id,
                new(options.Version, journal.SecondBranch, options.CurrentPreferredPickupBranchId, true),
                CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(operationId, recovered.OperationId);
            Assert.AreEqual(1, provider.UpdateCalls);
            Assert.AreEqual(1, handler.UpdateRequestCount,
                "Recovery accepts the known journal result without issuing a duplicate provider write.");
            Assert.AreEqual(1, await PickupJournalCountAsync(journal.Barcode, state: 3));

            await using var verifyRecovery = await contexts.CreateDbContextAsync();
            var accepted = await verifyRecovery.TitleRequests.SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual(journal.SecondBranch, accepted.PreferredPickupBranchId);
            Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "pickup_preference_changed"));
            Assert.AreEqual(outboxBefore, await ReadPickupOutboxCountAsync(contexts));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow(3506, -3000)]
    [DataRow(3507, -3622)]
    public async Task PolarisParsedPickupRejectionIsJournaledBeforeCanceledRequestReturns(
        int organizationId, int papiErrorCode)
    {
        var journal = new PickupJournalProvider(organizationId);
        using var cancellation = new CancellationTokenSource();
        var cleanupFault = new HttpRequestException(
            "Cleanup fault after parsed pickup rejection. cleanup-secret=https://polaris.invalid/provider-body");
        var cleanupLogger = new CapturingProviderCleanupLogger();
        var handler = new DisposeBoundaryPickupHandler(
            cancellation,
            mutationResponseContent: JsonSerializer.Serialize(new { PAPIErrorCode = papiErrorCode }),
            throwOnResponseDispose: cleanupFault);
        var polaris = await CreatePolarisProviderAsync(
            handler, $"pickup-rejection-return-sql-{papiErrorCode}-" + Guid.NewGuid().ToString("N"), cleanupLogger);
        var provider = new ReturnedPickupBoundaryProvider(journal, polaris);
        await using var scoped = CreatePickupPolarisFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, journal);

        try
        {
            var outboxBefore = await ReadPickupOutboxCountAsync(contexts);
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var result = await service.UpdateAsync(
                actor, request.Id,
                new(StaffVersion.Encode(request.RowVersion), journal.SecondBranch, journal.FirstBranch, true),
                cancellation.Token);

            Assert.AreEqual("pickup_provider_error", result.Code,
                "The parsed PAPI no-effect classification remains authoritative after response-content cleanup faults.");
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(journal.FirstBranch, journal.Current,
                "A definitive PAPI rejection does not change the provider's current branch.");
            Assert.AreEqual(1, provider.UpdateCalls);
            Assert.AreEqual(1, handler.UpdateRequestCount,
                "Known no-effect is journaled without retrying the one external PUT.");
            Assert.AreEqual(HttpMethod.Put, handler.UpdateRequestMethod);
            Assert.IsTrue(handler.MutationResponseContentDisposed);
            Assert.AreEqual(1, handler.MutationResponseContent?.DisposeCount ?? 0);
            AssertResponseCleanupDiagnostic(cleanupLogger, cleanupFault);

            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var unchanged = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(journal.FirstBranch, unchanged.PreferredPickupBranchId);
                CollectionAssert.AreEqual(request.RowVersion, unchanged.RowVersion);
                Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }
            Assert.AreEqual(outboxBefore, await ReadPickupOutboxCountAsync(contexts),
                "A rejected provider mutation creates no request acceptance or notification work.");

            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT [Id], [State], [FailureCode], [DispatchStartedUtc], [DispatchFinishedUtc],
                       [ProviderConfirmedUtc], [CompletedUtc], [ObservedPickupBranchId], [ConfirmedByRead],
                       [FromPickupBranchId], [ToPickupBranchId], [TitleRequestId]
                FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                """, connection);
            command.Parameters.AddWithValue("@barcode", journal.Barcode);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.IsTrue(reader.GetGuid(0) != Guid.Empty);
            Assert.AreEqual(4, reader.GetInt32(1), "The provider's known no-effect result is durably terminal.");
            Assert.AreEqual($"documented_rejection_{papiErrorCode}", reader.GetString(2));
            Assert.IsFalse(reader.IsDBNull(3), "The durable row records when dispatch began.");
            Assert.IsFalse(reader.IsDBNull(4), "The durable row records when the known no-effect dispatch ended.");
            Assert.IsTrue(reader.IsDBNull(5), "A rejection must not be represented as provider-confirmed success.");
            Assert.IsFalse(reader.IsDBNull(6));
            Assert.AreEqual(reader.GetDateTime(4), reader.GetDateTime(6));
            Assert.IsTrue(reader.GetDateTime(3) <= reader.GetDateTime(4));
            Assert.IsTrue(reader.IsDBNull(7));
            Assert.IsFalse(reader.GetBoolean(8));
            Assert.AreEqual(journal.FirstBranch, reader.GetInt32(9));
            Assert.AreEqual(journal.SecondBranch, reader.GetInt32(10));
            Assert.AreEqual(request.Id, reader.GetInt64(11));
            Assert.IsFalse(await reader.ReadAsync(), "The rejected provider call must have one durable journal row.");
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task PolarisParsedReplyRequiredHoldIsJournaledBeforeCanceledServiceReturns()
    {
        var rootFactory = factory ?? throw new AssertFailedException("The test web host was not initialized.");
        var seeded = await SeedBibOwnershipRequestAsync(
            $"hold-reply-required-return-sql-{Guid.NewGuid():N}",
            9001,
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "pending_hold",
            autoHold: true);
        string barcode;
        await using (var setup = await rootFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
                         .CreateDbContextAsync())
        {
            barcode = (await setup.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id)).Barcode;
        }
        var patronProvider = rootFactory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cleanupFault = new HttpRequestException(
            "Cleanup fault after parsed hold reply-required result. cleanup-secret=https://polaris.invalid/provider-body");
        var cleanupLogger = new CapturingProviderCleanupLogger();
        var handler = new DisposeBoundaryHoldHandler(
            HttpMethod.Post,
            HoldOutcomeResponse("reply-required"),
            cancellation,
            cleanupFault,
            requestStarted,
            responseRelease,
            barcode);
        var polaris = await CreatePolarisProviderAsync(
            handler, "hold-reply-required-return-sql-" + Guid.NewGuid().ToString("N"), cleanupLogger);
        await using var scopedFactory = rootFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(patronProvider);
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(polaris);
            }));
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        Task<HoldPlacementResult>? placing = null;
        long operationId = 0;
        Guid ownerToken = Guid.Empty;
        long executionEpoch = 0;

        try
        {
            var service = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placingTask = service.PlaceBackgroundAsync(seeded.Id, seeded.RowVersion, cancellation.Token);
            placing = placingTask;
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using (var before = await contexts.CreateDbContextAsync())
            {
                var operation = await before.HoldPlacementOperations.AsNoTracking()
                    .SingleAsync(item => item.TitleRequestId == seeded.Id);
                operationId = operation.Id;
                Assert.AreEqual(HoldOperationPhase.CreateStarted, operation.Phase);
                Assert.AreEqual(HoldOperationState.InProgress, operation.State);
                Assert.IsNotNull(operation.CreateStartedUtc);
                Assert.IsNotNull(operation.OwnerToken);
                ownerToken = operation.OwnerToken.Value;
                executionEpoch = operation.ExecutionEpoch;
                Assert.IsNotNull(operation.LeaseExpiresUtc);
                Assert.IsNull(operation.CreateResponseObservedUtc);
                Assert.IsNull(operation.ReplyStartedUtc);
                Assert.AreEqual(0, await before.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.Id));
                Assert.AreEqual(0, await before.EmailOutbox.CountAsync(item =>
                    item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));
            }

            responseRelease.TrySetResult(true);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => placingTask);

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, handler.MutationRequestCount,
                "The native PAPI create response is one POST; cancellation must not replay it or begin its reply PUT.");
            Assert.AreEqual(1, handler.HoldReadRequestCount,
                "The service's one preflight hold-list read returns the pinned empty-list shape.");
            Assert.AreEqual(HttpMethod.Post, handler.MutationRequestMethod);
            Assert.IsFalse(string.IsNullOrWhiteSpace(handler.MutationRequestBody));
            Assert.IsTrue(handler.MutationResponseContentDisposed);
            Assert.AreEqual(1, handler.MutationResponseContent?.DisposeCount ?? 0,
                "The adapter must dispose the original parsed response content exactly once.");
            AssertResponseCleanupDiagnostic(cleanupLogger, cleanupFault);

            await using var verify = await contexts.CreateDbContextAsync();
            var request = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == seeded.Id);
            var operationAfter = await verify.HoldPlacementOperations.AsNoTracking()
                .SingleAsync(item => item.Id == operationId);
            Assert.AreEqual("pending_hold", request.Status,
                "The canceled caller cannot accept a local hold before the required reply is dispatched.");
            CollectionAssert.AreEqual(seeded.RowVersion, request.RowVersion);
            Assert.AreEqual(HoldOperationPhase.ReplyReady, operationAfter.Phase);
            Assert.AreEqual(HoldOperationState.InProgress, operationAfter.State);
            Assert.AreEqual(ownerToken, operationAfter.OwnerToken);
            Assert.AreEqual(executionEpoch, operationAfter.ExecutionEpoch);
            Assert.AreEqual(1, operationAfter.AttemptNumber);
            Assert.AreEqual(7001, operationAfter.PatronIdSnapshot);
            Assert.AreEqual(9001, operationAfter.BibIdSnapshot);
            Assert.AreEqual(101, operationAfter.PickupBranchIdSnapshot);
            Assert.AreEqual(101, operationAfter.RequestingOrganizationIdSnapshot);
            Assert.IsNotNull(operationAfter.CreateStartedUtc);
            Assert.IsNotNull(operationAfter.CreateResponseObservedUtc);
            Assert.IsTrue(operationAfter.CreateResponseObservedUtc.Value >= operationAfter.CreateStartedUtc.Value);
            Assert.IsNotNull(operationAfter.LeaseExpiresUtc);
            Assert.IsNull(operationAfter.ReplyStartedUtc,
                "The canceled create result records reply readiness without starting a second provider call.");
            Assert.IsNull(operationAfter.ReplyResponseObservedUtc);
            Assert.AreEqual(BoundaryReply().RequestGuid, operationAfter.PolarisRequestGuid);
            Assert.AreEqual("group", operationAfter.TxnGroupQualifier);
            Assert.AreEqual("qualifier", operationAfter.TxnQualifier);
            Assert.AreEqual(3, operationAfter.ProviderStatusType);
            Assert.AreEqual(5, operationAfter.ProviderStatusValue);
            Assert.AreEqual("reply_required", operationAfter.ResultCode);
            Assert.AreEqual("create_status_5_reply_required", operationAfter.OutcomeEvidenceKind);
            Assert.IsNull(operationAfter.CompletedUtc);
            Assert.IsNull(operationAfter.LastErrorCode);
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == seeded.Id));
            Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(item =>
                item.BusinessKey == $"title-hold-placed:{seeded.Id}:1"));

            await using var leaseConnection = new SqlConnection(databaseConnectionString);
            await leaseConnection.OpenAsync();
            await using var liveLease = new SqlCommand(
                "SELECT CASE WHEN [LeaseExpiresUtc] > SYSUTCDATETIME() AND [OwnerToken] = @ownerToken " +
                "AND [ExecutionEpoch] = @epoch AND [Phase] = N'reply_ready' " +
                "AND [CreateResponseObservedUtc] IS NOT NULL THEN 1 ELSE 0 END " +
                "FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;",
                leaseConnection);
            liveLease.Parameters.AddWithValue("@id", operationId);
            liveLease.Parameters.AddWithValue("@ownerToken", ownerToken);
            liveLease.Parameters.AddWithValue("@epoch", executionEpoch);
            Assert.AreEqual(1, Convert.ToInt32(await liveLease.ExecuteScalarAsync()),
                "The known response remains protected by the original unexpired SQL owner fence.");
        }
        finally
        {
            responseRelease.TrySetResult(true);
            if (placing is not null)
            {
                try
                {
                    await placing;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // The parsed provider response cancels only after the test releases the response gate.
                }
            }
            await DeleteEmailOutboxByBusinessKeyAsync($"title-hold-placed:{seeded.Id}:1");
            await DeleteRequestAsync(seeded.Id);
        }
    }

    private static async Task<int> ReadPickupOutboxCountAsync(IDbContextFactory<AsapDbContext> contexts)
    {
        await using var context = await contexts.CreateDbContextAsync();
        return await context.EmailOutbox.CountAsync();
    }

    private sealed class ReturnedPickupBoundaryProvider(
        PickupJournalProvider journal, PolarisPatronProvider polaris) : IPatronProvider
    {
        public int UpdateCalls { get; private set; }

        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken token) =>
            journal.AuthenticateAsync(barcode, pin, token);

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken token) =>
            journal.RefreshAsync(barcode, organizationId, token);

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron, int organizationId, CancellationToken token) =>
            journal.GetPickupBranchesAsync(patron, organizationId, token);

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier, int organizationId, CancellationToken token) =>
            journal.LookupIdentifierAsync(identifier, organizationId, token);

        public async Task UpdatePreferredPickupBranchAsync(
            string barcode, int pickupBranchId, int organizationId, CancellationToken token)
        {
            UpdateCalls++;
            await polaris.UpdatePreferredPickupBranchAsync(barcode, pickupBranchId, organizationId, token);
        }
    }


}
