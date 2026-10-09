using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("identity_changed")]
    [DataRow("provider_unavailable")]
    public async Task PublicPostPutRefreshFailureRetainsPickupReceiptAndRetryDoesNotRepeatPut(string failureKind)
    {
        var organizationId = failureKind == "identity_changed" ? 38130 : 38131;
        var nativePatronId = failureKind == "identity_changed" ? 739030 : 739031;
        var provider = new PickupJournalProvider(organizationId) { PatronId = nativePatronId };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider);
        var issued = await IssueNativePatronSessionAsync(
            scoped.Services, provider.Barcode, nativePatronId, organizationId);
        var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
        using var client = scoped.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        var title = "Post-PUT refresh partial truth " + Guid.NewGuid().ToString("N");

        try
        {
            provider.AfterRefresh = _ =>
            {
                if (provider.Reads == 2)
                {
                    if (failureKind == "identity_changed")
                    {
                        provider.PatronId = nativePatronId + 1;
                    }
                    else
                    {
                        throw new PolarisOperationalException(
                            "testing_post_put_refresh_unavailable", "The post-PUT refresh was unavailable.");
                    }
                }
                return Task.CompletedTask;
            };

            using (var first = await PostPatronSuggestionAsync(client, title, provider.SecondBranch))
            {
                var raw = await first.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.Conflict, first.StatusCode, raw);
                using var body = JsonDocument.Parse(raw);
                Assert.AreEqual("request_not_created_pickup_changed", body.RootElement.GetProperty("code").GetString());
                var operationId = body.RootElement.GetProperty("operationId").GetGuid();
                Assert.AreNotEqual(Guid.Empty, operationId);
                Assert.IsTrue(body.RootElement.GetProperty("pickupPreferenceChanged").GetBoolean());
                Assert.AreEqual(1, provider.Writes);
                Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 2));
                Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId),
                    "The accepted pickup effect must not create a request, event, or outbox entry after final refresh fails.");
                await using (var verify = await contexts.CreateDbContextAsync())
                {
                    Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.Title == title));
                }

                await using var connection = new SqlConnection(databaseConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    SELECT [PatronId], [State], [TitleRequestId], [DispatchFinishedUtc]
                    FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                    """, connection);
                command.Parameters.AddWithValue("@id", operationId);
                await using var journal = await command.ExecuteReaderAsync();
                Assert.IsTrue(await journal.ReadAsync());
                Assert.AreEqual(nativePatronId, journal.GetInt32(0));
                Assert.AreEqual(2, journal.GetInt32(1));
                Assert.IsTrue(journal.IsDBNull(2));
                Assert.IsFalse(journal.IsDBNull(3));
                Assert.IsFalse(await journal.ReadAsync());
            }

            // The exact accepted operation can be recovered once a current refresh
            // confirms the original native patron; its provider PUT is never repeated.
            provider.AfterRefresh = null;
            provider.PatronId = nativePatronId;
            using (var retry = await PostPatronSuggestionAsync(client, title, provider.SecondBranch))
            {
                Assert.AreEqual(HttpStatusCode.Created, retry.StatusCode, await retry.Content.ReadAsStringAsync());
            }
            Assert.AreEqual(1, provider.Writes);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 3));
            await using var final = await contexts.CreateDbContextAsync();
            var created = await final.TitleRequests.SingleAsync(item => item.Title == title);
            Assert.AreEqual(nativePatronId, created.PatronIdSnapshot);
        }
        finally
        {
            provider.AfterRefresh = null;
            provider.PatronId = nativePatronId;
            await DeleteTestPatronSessionAsync(issued.Context.Id);
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task PublicPostPutCallerCancellationPropagatesAndLeavesOnlyPickupJournal()
    {
        const int organizationId = 38132;
        const int nativePatronId = 739032;
        var provider = new PickupJournalProvider(organizationId) { PatronId = nativePatronId };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider);
        var issued = await IssueNativePatronSessionAsync(
            scoped.Services, provider.Barcode, nativePatronId, organizationId);
        var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
        var cancellation = new CancellationTokenSource();
        var title = "Post-PUT caller cancellation " + Guid.NewGuid().ToString("N");

        try
        {
            provider.AfterRefresh = token =>
            {
                if (provider.Reads == 2)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.CompletedTask;
            };
            var service = scoped.Services.GetRequiredService<PatronSuggestionService>();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.CreateAsync(
                issued.Context,
                Suggestion(title) with { PreferredPickupBranchId = provider.SecondBranch },
                cancellation.Token));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(1, provider.Writes);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 2));
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId));
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.Title == title));
        }
        finally
        {
            provider.AfterRefresh = null;
            cancellation.Dispose();
            await DeleteTestPatronSessionAsync(issued.Context.Id);
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task ConcurrentRotatedBarcodePickupIntentsRecheckPendingAfterNativeLock()
    {
        const int organizationId = 38133;
        const int nativePatronId = 739033;
        var provider = new PickupJournalProvider(organizationId) { PatronId = nativePatronId };
        provider.CurrentBarcodeOverride = provider.Barcode + "N";
        provider.FormerBarcode = provider.Barcode;
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider);
        var service = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
        var firstIntentReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstIntent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInitialReadReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondInitialRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialReadCount = 0;
        var intentValidationCount = 0;
        var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
        var canonicalPatron = await provider.RefreshAsync(provider.CurrentBarcodeOverride,
            organizationId, CancellationToken.None);
        var oldBarcodePatron = canonicalPatron with
        {
            Barcode = provider.Barcode,
            FormerBarcode = null,
            RequestedBarcode = null
        };

        service.AfterInitialPendingReadForTesting = async token =>
        {
            if (Interlocked.Increment(ref initialReadCount) == 2)
            {
                secondInitialReadReached.TrySetResult();
                await releaseSecondInitialRead.Task.WaitAsync(token);
            }
        };
        service.BeforeIntentValidationForTesting = async token =>
        {
            if (Interlocked.Increment(ref intentValidationCount) == 1)
            {
                firstIntentReached.TrySetResult();
                await releaseFirstIntent.Task.WaitAsync(token);
            }
        };

        async Task<(PickupMutationReceipt? Receipt, PickupMutationException? Error)> TryChangeAsync(
            PatronSnapshot patron)
        {
            try
            {
                var receipt = await service.ChangeAsync(
                    patron,
                    organizationId,
                    new PickupBranch(provider.SecondBranch, "Second"),
                    "First",
                    "patron_suggestion",
                    null,
                    null,
                    static (_, _, _) => Task.CompletedTask,
                    CancellationToken.None);
                return (receipt, null);
            }
            catch (PickupMutationException exception)
            {
                return (null, exception);
            }
        }

        var first = TryChangeAsync(oldBarcodePatron);
        Task<(PickupMutationReceipt? Receipt, PickupMutationException? Error)>? second = null;
        try
        {
            await firstIntentReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = TryChangeAsync(canonicalPatron);
            await secondInitialReadReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The second caller has observed an empty journal and is stopped before
            // attempting the app lock. Let caller one commit and finish its PUT first.
            releaseFirstIntent.TrySetResult();
            var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
            var firstReceipt = firstResult.Receipt
                ?? throw new AssertFailedException("The first native patron intent should commit before the second proceeds.");
            Assert.IsNull(firstResult.Error);
            releaseSecondInitialRead.TrySetResult();
            var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsNull(secondResult.Receipt);
            Assert.AreEqual("pickup_reconciliation_required", secondResult.Error?.Code);
            Assert.AreEqual(firstReceipt.OperationId, secondResult.Error?.OperationId);
            Assert.AreEqual(1, provider.Writes,
                "After locking, caller two must see caller one's different-barcode journal rather than dispatch another PUT.");
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId));
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.LibraryOrganizationId == organizationId));
            }
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT [Barcode], [PatronId], [State]
                FROM [asap].[PickupPreferenceOperation]
                WHERE [PatronId] = @patronId AND [LibraryOrganizationId] = @organizationId;
                """, connection);
            command.Parameters.AddWithValue("@patronId", nativePatronId);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            await using var rows = await command.ExecuteReaderAsync();
            Assert.IsTrue(await rows.ReadAsync());
            Assert.AreEqual(provider.Barcode, rows.GetString(0));
            Assert.AreEqual(nativePatronId, rows.GetInt32(1));
            Assert.AreEqual(2, rows.GetInt32(2));
            Assert.IsFalse(await rows.ReadAsync());
        }
        finally
        {
            releaseFirstIntent.TrySetResult();
            releaseSecondInitialRead.TrySetResult();
            service.AfterInitialPendingReadForTesting = null;
            service.BeforeIntentValidationForTesting = null;
            if (second is not null)
            {
                try
                {
                    await second;
                }
                catch (Exception)
                {
                }
            }
            try
            {
                await first;
            }
            catch (Exception)
            {
            }
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }
}
