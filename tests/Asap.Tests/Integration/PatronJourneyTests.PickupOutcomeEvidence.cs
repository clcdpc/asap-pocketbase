using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PickupSuccessfulProviderResponseIsJournaledWhenCallerCancels()
    {
        const int organizationId = 3498;
        var provider = new PickupJournalProvider(organizationId);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        using var cancellation = new CancellationTokenSource();
        var service = scoped.Services.GetRequiredService<StaffPickupService>();
        provider.AfterEffect = token =>
        {
            Assert.IsFalse(token.IsCancellationRequested);
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var input = new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
                provider.FirstBranch, true);
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.UpdateAsync(
                actor, request.Id, input, cancellation.Token));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(provider.SecondBranch, provider.Current);
            Assert.AreEqual(1, provider.Writes);
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var unchanged = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(provider.FirstBranch, unchanged.PreferredPickupBranchId);
                CollectionAssert.AreEqual(request.RowVersion, unchanged.RowVersion);
                Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [State], [ProviderConfirmedUtc], [DispatchFinishedUtc], [CompletedUtc]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                    """, connection);
                command.Parameters.AddWithValue("@barcode", provider.Barcode);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.IsFalse(reader.IsDBNull(1), "The definitive provider success must be durable.");
                Assert.IsFalse(reader.IsDBNull(2), "State 2 must retain dispatch-finished evidence.");
                Assert.IsTrue(reader.IsDBNull(3), "Caller cancellation must leave request acceptance for recovery.");
                Assert.IsFalse(await reader.ReadAsync(), "The patron must have one pending pickup operation.");
            }

            provider.AfterEffect = null;
            var loaded = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
            Assert.AreEqual("loaded", loaded.Code);
            var options = loaded.Options ?? throw new AssertFailedException("Pickup recovery options were not returned.");
            Assert.AreEqual(provider.SecondBranch, options.CurrentPreferredPickupBranchId);
            var recovered = await service.UpdateAsync(actor, request.Id,
                new(options.Version, provider.SecondBranch, options.CurrentPreferredPickupBranchId, true),
                CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, provider.Writes, "Recovery must accept the known provider result without a second PUT.");

            await using var verifyRecovery = await contexts.CreateDbContextAsync();
            var accepted = await verifyRecovery.TitleRequests.SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual(provider.SecondBranch, accepted.PreferredPickupBranchId);
            Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "pickup_preference_changed"));
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 3));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow(3499, 0)]
    [DataRow(3500, -3000)]
    [DataRow(3501, -3622)]
    public async Task PinnedPolarisPickupResultsAreRecordedWhenCallerCancelsAfterResponse(
        int organizationId, int papiErrorCode)
    {
        var journal = new PickupJournalProvider(organizationId);
        var handler = new ClassifiedPickupResponseHandler(papiErrorCode);
        var polaris = await CreatePolarisProviderAsync(handler,
            $"pickup-result-cancel-{organizationId}-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        var provider = new CancellationAfterPolarisResultProvider(journal, polaris, cancellation);
        await using var scoped = CreatePickupPolarisFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, journal);

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var input = new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), journal.SecondBranch,
                journal.FirstBranch, true);
            if (papiErrorCode == 0)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.UpdateAsync(
                    actor, request.Id, input, cancellation.Token));
                Assert.AreEqual(journal.SecondBranch, journal.Current);
                Assert.AreEqual(1, await PickupJournalCountAsync(journal.Barcode, state: 2));
            }
            else
            {
                var rejected = await service.UpdateAsync(actor, request.Id, input, cancellation.Token);
                Assert.AreEqual("pickup_provider_error", rejected.Code,
                    "A classified Polaris rejection remains a provider result when cancellation coincides.");
                Assert.AreEqual(journal.FirstBranch, journal.Current);
                Assert.AreEqual(1, await PickupJournalCountAsync(journal.Barcode, state: 4));
            }

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, provider.UpdateCalls);
            Assert.AreEqual(1, handler.UpdateRequestCount,
                "The production Polaris client must dispatch exactly one serialized pickup mutation.");
            Assert.IsTrue(handler.UpdateRequestPath.Contains("patron", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(string.IsNullOrWhiteSpace(handler.UpdateRequestBody));

            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var unchanged = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(journal.FirstBranch, unchanged.PreferredPickupBranchId);
                CollectionAssert.AreEqual(request.RowVersion, unchanged.RowVersion);
                Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [State], [ProviderConfirmedUtc], [DispatchFinishedUtc], [CompletedUtc],
                           [FailureCode], [ConfirmedByRead]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                    """, connection);
                command.Parameters.AddWithValue("@barcode", journal.Barcode);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                if (papiErrorCode == 0)
                {
                    Assert.AreEqual(2, reader.GetInt32(0));
                    Assert.IsFalse(reader.IsDBNull(1));
                    Assert.IsFalse(reader.IsDBNull(2));
                    Assert.AreEqual(reader.GetDateTime(1), reader.GetDateTime(2),
                        "Known success records confirmation and dispatch finish atomically at the same instant.");
                    Assert.IsTrue(reader.IsDBNull(3));
                    Assert.IsTrue(reader.IsDBNull(4));
                    Assert.IsFalse(reader.GetBoolean(5));
                }
                else
                {
                    Assert.AreEqual(4, reader.GetInt32(0));
                    Assert.IsTrue(reader.IsDBNull(1), "A definitive no-effect result cannot fabricate provider success.");
                    Assert.IsFalse(reader.IsDBNull(2));
                    Assert.IsFalse(reader.IsDBNull(3));
                    Assert.AreEqual(reader.GetDateTime(2), reader.GetDateTime(3),
                        "A definitive rejection records completion and dispatch finish together.");
                    Assert.AreEqual($"documented_rejection_{papiErrorCode}", reader.GetString(4));
                    Assert.IsFalse(reader.GetBoolean(5));
                }
                Assert.IsFalse(await reader.ReadAsync(), "Only one pickup operation is created for this request.");
            }

            if (papiErrorCode == 0)
            {
                var options = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
                Assert.AreEqual("loaded", options.Code);
                var loaded = options.Options ?? throw new AssertFailedException("Known pickup result did not recover.");
                Assert.AreEqual(journal.SecondBranch, loaded.CurrentPreferredPickupBranchId);
                var recovered = await service.UpdateAsync(actor, request.Id,
                    new(loaded.Version, journal.SecondBranch, loaded.CurrentPreferredPickupBranchId, true),
                    CancellationToken.None);
                Assert.AreEqual("updated", recovered.Code);
                Assert.AreEqual(1, provider.UpdateCalls);
                Assert.AreEqual(1, handler.UpdateRequestCount,
                    "Accepting the known provider result must not issue another PUT.");
                Assert.AreEqual(1, await PickupJournalCountAsync(journal.Barcode, state: 3));
                await using var verifyRecovery = await contexts.CreateDbContextAsync();
                Assert.AreEqual(journal.SecondBranch,
                    (await verifyRecovery.TitleRequests.SingleAsync(item => item.Id == request.Id)).PreferredPickupBranchId);
                Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == request.Id && item.EventType == "pickup_preference_changed"));
            }
            else
            {
                var options = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
                Assert.AreEqual("loaded", options.Code);
                Assert.AreEqual(journal.FirstBranch, options.Options!.CurrentPreferredPickupBranchId);
                Assert.AreEqual(1, provider.UpdateCalls,
                    "Reading a recorded no-effect result must not replay the rejected PUT.");
                Assert.AreEqual(1, handler.UpdateRequestCount);
            }
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task LostPickupSuccessFenceRetainsRecoverableOperationWithoutRepeatingWrite()
    {
        const int organizationId = 3504;
        var provider = new PickupJournalProvider(organizationId);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var simulatedFenceLossUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        provider.AfterEffect = _ => ExecuteNonQueryAsync("""
            UPDATE [asap].[PickupPreferenceOperation]
            SET [DispatchFinishedUtc] = @finished
            WHERE [Barcode] = @barcode AND [State] = 1 AND [DispatchFinishedUtc] IS NULL;
            """, ("@finished", simulatedFenceLossUtc), ("@barcode", provider.Barcode));

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var result = await service.UpdateAsync(actor, request.Id,
                new(StaffVersion.Encode(request.RowVersion), provider.SecondBranch, provider.FirstBranch, true),
                CancellationToken.None);
            Assert.AreEqual("pickup_reconciliation_required", result.Code);
            Assert.IsTrue(result.PickupChanged);
            Assert.IsNotNull(result.OperationId);
            Assert.AreEqual(provider.SecondBranch, provider.Current);
            Assert.AreEqual(1, provider.Writes);

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [State], [ProviderConfirmedUtc], [DispatchFinishedUtc], [CompletedUtc]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                    """, connection);
                command.Parameters.AddWithValue("@id", result.OperationId.Value);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(1, reader.GetInt32(0), "The lost result fence must remain recoverable, not look confirmed.");
                Assert.IsTrue(reader.IsDBNull(1));
                Assert.AreEqual(simulatedFenceLossUtc, reader.GetDateTime(2));
                Assert.IsTrue(reader.IsDBNull(3));
            }

            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var unchanged = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual(provider.FirstBranch, unchanged.PreferredPickupBranchId);
                CollectionAssert.AreEqual(request.RowVersion, unchanged.RowVersion);
                Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }

            provider.AfterEffect = null;
            var options = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
            Assert.AreEqual("loaded", options.Code);
            var loaded = options.Options ?? throw new AssertFailedException("Lost-fence operation did not remain readable.");
            Assert.AreEqual(provider.SecondBranch, loaded.CurrentPreferredPickupBranchId);
            var recovered = await service.UpdateAsync(actor, request.Id,
                new(loaded.Version, provider.SecondBranch, loaded.CurrentPreferredPickupBranchId, true), CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, provider.Writes, "Live-read recovery must not repeat the provider mutation.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 3));
            await using var verifyRecovery = await contexts.CreateDbContextAsync();
            Assert.AreEqual(provider.SecondBranch,
                (await verifyRecovery.TitleRequests.SingleAsync(item => item.Id == request.Id)).PreferredPickupBranchId);
            Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "pickup_preference_reconciled"));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    private WebApplicationFactory<Program> CreatePickupPolarisFactory(IPatronProvider provider) =>
        factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton(provider);
        }));

    private sealed class CancellationAfterPolarisResultProvider(
        PickupJournalProvider journal, PolarisPatronProvider polaris, CancellationTokenSource cancellation)
        : IPatronProvider
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
            try
            {
                await polaris.UpdatePreferredPickupBranchAsync(barcode, pickupBranchId, organizationId, token);
                journal.Current = pickupBranchId;
            }
            finally
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class ClassifiedPickupResponseHandler(int papiErrorCode) : HttpMessageHandler
    {
        private const string ProtectedTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"protected-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";

        public int UpdateRequestCount { get; private set; }
        public string UpdateRequestPath { get; private set; } = string.Empty;
        public string UpdateRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/authenticator/staff", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ProtectedTokenContent),
                    RequestMessage = request
                };
            }

            UpdateRequestCount++;
            UpdateRequestPath = path;
            UpdateRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { PAPIErrorCode = papiErrorCode })),
                RequestMessage = request
            };
        }
    }
}
