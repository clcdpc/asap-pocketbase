using System.Net;
using System.Net.Http.Json;
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
    [DataRow(3482, "conflict")]
    [DataRow(3483, "conflict")]
    [DataRow(3482, "ambiguous")]
    [DataRow(3483, "ambiguous")]
    [DataRow(3482, "cancel")]
    [DataRow(3483, "cancel")]
    [DataRow(3482, "cancel_oce")]
    [DataRow(3483, "cancel_oce")]
    [DataRow(3482, "cancel_failure")]
    [DataRow(3483, "cancel_failure")]
    [DataRow(3482, "cancel_generic")]
    [DataRow(3483, "cancel_generic")]
    public async Task PickupJournalRecoversObservedEffectsWithoutRepeatingWrites(int organizationId, string failure)
    {
        var provider = new PickupJournalProvider(organizationId);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var cancelled = new CancellationTokenSource();
            provider.AfterEffect = async token =>
            {
                Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 1));
                // NOWAIT detects a transaction/row lock held over the provider call.
                await ExecuteNonQueryAsync("""
                    UPDATE [asap].[TitleRequest] WITH (NOWAIT) SET [Notes] = N'Concurrent local change'
                    WHERE [Id] = @id;
                    """, ("@id", request.Id));
                if (failure == "ambiguous")
                {
                    throw new PolarisOperationalException("testing_uncertain_pickup", "Connection lost after effect.");
                }
                if (failure == "cancel")
                {
                    cancelled.Cancel();
                }
                if (failure.StartsWith("cancel_", StringComparison.Ordinal))
                {
                    cancelled.Cancel();
                    if (failure == "cancel_oce")
                    {
                        throw new OperationCanceledException("The provider observed cancellation after its external effect.", token);
                    }
                    if (failure == "cancel_failure")
                    {
                        throw new PolarisOperationalException("testing_uncertain_pickup", "The provider failed after cancellation and effect.");
                    }
                    throw new InvalidOperationException("The provider failed after cancellation and effect.");
                }
            };
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var input = new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
                provider.FirstBranch, true);
            if (failure is "cancel" or "cancel_oce")
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() =>
                    service.UpdateAsync(actor, request.Id, input, cancelled.Token));
            }
            else if (failure == "cancel_failure")
            {
                var partial = await service.UpdateAsync(actor, request.Id, input, cancelled.Token);
                Assert.AreEqual("pickup_outcome_unconfirmed", partial.Code,
                    "A genuine provider fault remains the outcome when caller cancellation coincides.");
                Assert.IsNotNull(partial.OperationId);
                Assert.IsFalse(partial.PickupChanged);
            }
            else if (failure == "cancel_generic")
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    service.UpdateAsync(actor, request.Id, input, cancelled.Token));
            }
            else
            {
                var partial = await service.UpdateAsync(actor, request.Id, input, CancellationToken.None);
                Assert.AreEqual(failure == "conflict" ? "pickup_changed_request_not_updated" : "pickup_outcome_unconfirmed",
                    partial.Code);
                Assert.IsNotNull(partial.OperationId);
                Assert.AreEqual(failure == "conflict", partial.PickupChanged);
            }
            Assert.AreEqual(provider.SecondBranch, provider.Current);
            Assert.AreEqual(1, provider.Writes);
            var knownSuccess = failure is "conflict" or "cancel";
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, knownSuccess ? 2 : 1));
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var unchanged = await db.TitleRequests.SingleAsync(x => x.Id == request.Id);
                Assert.AreEqual(provider.FirstBranch, unchanged.PreferredPickupBranchId);
                Assert.AreEqual("Concurrent local change", unchanged.Notes);
                Assert.AreEqual(0, await db.TitleRequestEvents.CountAsync(x => x.TitleRequestId == request.Id));
            }

            provider.AfterEffect = null;
            var options = await service.GetOptionsAsync(actor, request.Id, CancellationToken.None);
            Assert.AreEqual("loaded", options.Code);
            var recovered = await service.UpdateAsync(actor, request.Id,
                new(options.Options!.Version, provider.SecondBranch, provider.SecondBranch, true), CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, provider.Writes, "A live read, not another write, must reconcile the old attempt.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 3));
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(provider.SecondBranch,
                (await verify.TitleRequests.SingleAsync(x => x.Id == request.Id)).PreferredPickupBranchId);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(x => x.TitleRequestId == request.Id &&
                x.EventType == (knownSuccess ? "pickup_preference_changed" : "pickup_preference_reconciled")));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("generic_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task StaffPickupPreIntentCancellationStopsBeforeJournalOrRequestMutation(string providerMode)
    {
        const int organizationId = 3484;
        var provider = new PickupJournalProvider(organizationId);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        using var cancellation = new CancellationTokenSource();
        var actor = await ReadConfiguredSuperAdminAsync();
        provider.AfterRefresh = token =>
        {
            switch (providerMode)
            {
                case "return_cancelled":
                    cancellation.Cancel();
                    return Task.CompletedTask;
                case "operation_cancelled":
                    cancellation.Cancel();
                    throw new OperationCanceledException("Refresh returned cancellation after caller cancellation.", token);
                case "failure_cancelled":
                    cancellation.Cancel();
                    throw new PolarisOperationalException("testing_pickup_refresh", "Refresh failed after cancellation.");
                case "generic_cancelled":
                    cancellation.Cancel();
                    throw new InvalidOperationException("Refresh failed after cancellation.");
                case "failure_uncancelled":
                    throw new PolarisOperationalException("testing_pickup_refresh", "Refresh failed without cancellation.");
                default:
                    throw new AssertFailedException($"Unknown pickup provider mode {providerMode}.");
            }
        };

        try
        {
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var input = new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
                provider.FirstBranch, true);
            if (providerMode is "failure_cancelled" or "failure_uncancelled")
            {
                Assert.AreEqual("pickup_provider_error", (await service.UpdateAsync(
                    actor, request.Id, input, cancellation.Token)).Code);
            }
            else if (providerMode == "generic_cancelled")
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.UpdateAsync(
                    actor, request.Id, input, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.UpdateAsync(
                    actor, request.Id, input, cancellation.Token));
            }
            Assert.AreEqual(providerMode != "failure_uncancelled", cancellation.IsCancellationRequested);

            Assert.AreEqual(1, provider.Reads);
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            await using var verify = await contexts.CreateDbContextAsync();
            var after = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
            Assert.AreEqual("pending_hold", after.Status);
            Assert.AreEqual(provider.FirstBranch, after.PreferredPickupBranchId);
            CollectionAssert.AreEqual(request.RowVersion, after.RowVersion);
            Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task LivePickupReadCannotReconcileAnActiveDispatch()
    {
        var provider = new PickupJournalProvider(3482);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        var effectObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AfterEffect = async token => {
            effectObserved.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var actor = await ReadConfiguredSuperAdminAsync();
        var pickup = scoped.Services.GetRequiredService<StaffPickupService>();
        var input = new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
            provider.FirstBranch, true);
        var original = pickup.UpdateAsync(actor, request.Id, input, CancellationToken.None);
        try
        {
            await effectObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var blocked = await pickup.UpdateAsync(actor, request.Id, input, CancellationToken.None);
            Assert.AreEqual("pickup_reconciliation_required", blocked.Code);
            var operatorBlocked = await pickup.ReconcileAsync(actor, blocked.OperationId!.Value,
                new(input.Version, provider.SecondBranch, true, true), CancellationToken.None);
            Assert.AreEqual("pickup_reconciliation_required", operatorBlocked.Code,
                "Even an operator acknowledgment cannot resolve a known active invocation.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 1));
            var view = await scoped.Services.GetRequiredService<TitleRequestViewService>()
                .GetAsync(actor, request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            Assert.IsNotNull(view);
            Assert.IsNotNull(view.PickupOperation);
            Assert.AreEqual(provider.SecondBranch, view.PickupOperation.TargetBranchId);
            Assert.AreEqual("pickup_reconciliation_required", view.Capabilities.BlockingReason);
            Assert.IsFalse(view.Capabilities.CanChangeWorkflowState);
            Assert.AreEqual(1, provider.Writes);
        }
        finally
        {
            release.TrySetResult();
            var completed = await original;
            Assert.AreEqual("updated", completed.Code);
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow(3482)]
    [DataRow(3483)]
    public async Task OperatorPickupRecoveryAcceptsLiveStateWithoutAnotherWrite(int organizationId)
    {
        var provider = new PickupJournalProvider(organizationId) { FailBeforeEffect = true };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var version = StaffVersion.Encode(request.RowVersion);
            var attempt = await service.UpdateAsync(actor, request.Id,
                new(version, provider.SecondBranch, provider.FirstBranch, true), CancellationToken.None);
            Assert.AreEqual("pickup_outcome_unconfirmed", attempt.Code);
            Assert.IsNotNull(attempt.OperationId);
            var input = new PickupReconciliationInput(version, provider.FirstBranch, true, true);
            Assert.AreEqual("pickup_reconciliation_required", (await service.ReconcileAsync(actor,
                attempt.OperationId.Value, input with { ConfirmOriginalDispatchEnded = false }, CancellationToken.None)).Code);
            var reads = provider.Reads;
            Assert.AreEqual("staff_scope_forbidden", (await service.ReconcileAsync(actor with { EntraTenantId = Guid.NewGuid() },
                attempt.OperationId.Value, input, CancellationToken.None)).Code);
            Assert.AreEqual(reads, provider.Reads, "Revoked authority must reject before provider reads.");
            Assert.AreEqual("pickup_changed_since_load", (await service.ReconcileAsync(actor,
                attempt.OperationId.Value, input with { CurrentPreferredPickupBranchIdAtLoad = null }, CancellationToken.None)).Code);

            using var client = scoped.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var resolved = await client.PostAsJsonAsync(
                $"/api/asap/staff/pickup-operations/{attempt.OperationId}/reconcile", input);
            Assert.AreEqual(HttpStatusCode.OK, resolved.StatusCode, await resolved.Content.ReadAsStringAsync());
            using var response = JsonDocument.Parse(await resolved.Content.ReadAsStringAsync());
            Assert.IsTrue(response.RootElement.GetProperty("committed").GetBoolean());
            Assert.AreEqual(request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                response.RootElement.GetProperty("request").GetProperty("id").GetString());
            Assert.IsTrue(response.RootElement.GetProperty("confirmedByRead").GetBoolean());
            Assert.AreEqual(1, provider.Writes);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 3));
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT [ToPickupBranchId], [ObservedPickupBranchId], [ResolvedByStaffUserId], [ConfirmedByRead]
                FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                """, connection);
            command.Parameters.AddWithValue("@id", attempt.OperationId.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(provider.SecondBranch, reader.GetInt32(0), "Original intent must remain exact.");
            Assert.AreEqual(provider.FirstBranch, reader.GetInt32(1), "Resolution must preserve actual observed state.");
            Assert.AreEqual(actor.Id, reader.GetInt64(2));
            Assert.IsTrue(reader.GetBoolean(3));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow("return_cancelled")]
    [DataRow("operation_cancelled")]
    [DataRow("failure_cancelled")]
    [DataRow("generic_cancelled")]
    [DataRow("failure_uncancelled")]
    public async Task OperatorPickupRecoveryCancellationPreservesJournalForRetry(string providerMode)
    {
        const int organizationId = 3485;
        var provider = new PickupJournalProvider(organizationId) { FailBeforeEffect = true };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var service = scoped.Services.GetRequiredService<StaffPickupService>();
            var version = StaffVersion.Encode(request.RowVersion);
            var attempted = await service.UpdateAsync(actor, request.Id,
                new(version, provider.SecondBranch, provider.FirstBranch, true), CancellationToken.None);
            Assert.AreEqual("pickup_outcome_unconfirmed", attempted.Code);
            Assert.IsNotNull(attempted.OperationId);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 1));

            provider.AfterRefresh = token =>
            {
                switch (providerMode)
                {
                    case "return_cancelled":
                        cancellation.Cancel();
                        return Task.CompletedTask;
                    case "operation_cancelled":
                        cancellation.Cancel();
                        throw new OperationCanceledException("Recovery read returned cancellation after caller cancellation.", token);
                    case "failure_cancelled":
                        cancellation.Cancel();
                        throw new PolarisOperationalException("testing_pickup_recovery", "Recovery read failed after cancellation.");
                    case "generic_cancelled":
                        cancellation.Cancel();
                        throw new InvalidOperationException("Recovery read failed after cancellation.");
                    case "failure_uncancelled":
                        throw new PolarisOperationalException("testing_pickup_recovery", "Recovery read failed without cancellation.");
                    default:
                        throw new AssertFailedException($"Unknown pickup recovery mode {providerMode}.");
                }
            };
            var reconciliation = new PickupReconciliationInput(version, provider.FirstBranch, true, true);
            if (providerMode is "failure_cancelled" or "failure_uncancelled")
            {
                Assert.AreEqual("pickup_provider_error", (await service.ReconcileAsync(
                    actor, attempted.OperationId.Value, reconciliation, cancellation.Token)).Code);
            }
            else if (providerMode == "generic_cancelled")
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ReconcileAsync(
                    actor, attempted.OperationId.Value, reconciliation, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReconcileAsync(
                    actor, attempted.OperationId.Value, reconciliation, cancellation.Token));
            }
            Assert.AreEqual(providerMode != "failure_uncancelled", cancellation.IsCancellationRequested);

            Assert.AreEqual(1, provider.Writes, "Recovery must only read the provider and never repeat its original PUT.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 1));
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var after = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
                Assert.AreEqual("pending_hold", after.Status);
                Assert.AreEqual(provider.FirstBranch, after.PreferredPickupBranchId);
                CollectionAssert.AreEqual(request.RowVersion, after.RowVersion);
                Assert.AreEqual(0, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == request.Id));
            }

            provider.AfterRefresh = null;
            var recovered = await service.ReconcileAsync(actor, attempted.OperationId.Value,
                reconciliation, CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, provider.Writes, "Retry must not dispatch another preference write.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 3));
            await using var verifyRecovery = await contexts.CreateDbContextAsync();
            Assert.AreEqual(1, await verifyRecovery.TitleRequestEvents.CountAsync(item =>
                item.TitleRequestId == request.Id && item.EventType == "pickup_preference_reconciled"));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow(3482, "patron_suggestion", true)]
    [DataRow(3483, "staff_suggestion", true)]
    [DataRow(3482, "request", false)]
    [DataRow(3483, "request", false)]
    public async Task OperatorRecoveryHandlesLostFinishEvidenceAndPreservesFrozenSnapshots(
        int organizationId, string origin, bool observedNull)
    {
        var provider = new PickupJournalProvider(organizationId) { FailBeforeEffect = true };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var patron = await provider.RefreshAsync(provider.Barcode, organizationId, CancellationToken.None);
            var failure = await Assert.ThrowsAsync<PickupMutationException>(() =>
                scoped.Services.GetRequiredService<PickupPreferenceMutationService>().ChangeAsync(
                    patron, organizationId, new(provider.SecondBranch, "Second"), "First", origin,
                    origin == "request" ? request.Id : null, actor.Id,
                    static (_, _, _) => Task.CompletedTask, CancellationToken.None));
            // Simulate a restart after durable intent, with no finish marker.
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[PickupPreferenceOperation] SET [DispatchFinishedUtc] = NULL WHERE [Id] = @id;
                """, ("@id", failure.OperationId));
            if (origin == "request")
            {
                await ExecuteNonQueryAsync("""
                    UPDATE [asap].[TitleRequest] SET [Status] = N'hold_placed' WHERE [Id] = @id;
                    """, ("@id", request.Id));
            }
            provider.Current = observedNull ? null : provider.SecondBranch;
            string? version = null;
            if (origin == "request")
            {
                await using var read = await contexts.CreateDbContextAsync();
                version = StaffVersion.Encode((await read.TitleRequests.SingleAsync(x => x.Id == request.Id)).RowVersion);
            }
            var result = await scoped.Services.GetRequiredService<StaffPickupService>().ReconcileAsync(actor,
                failure.OperationId, new(version, provider.Current, true, true), CancellationToken.None);
            Assert.AreEqual("updated", result.Code);
            Assert.AreEqual(1, provider.Writes, "Operator recovery must never dispatch another preference write.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 3));
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(provider.FirstBranch,
                (await verify.TitleRequests.SingleAsync(x => x.Id == request.Id)).PreferredPickupBranchId,
                "A frozen snapshot or unrelated request must retain its accepted pickup.");
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT [ObservedPickupBranchId], [DispatchFinishedUtc], [TitleRequestId]
                FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                """, connection);
            command.Parameters.AddWithValue("@id", failure.OperationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            if (observedNull)
            {
                Assert.IsTrue(reader.IsDBNull(0), "Observed NULL must remain distinct from omitted observation.");
                Assert.IsTrue(reader.IsDBNull(2), "A failed creation must not fabricate an accepted request.");
            }
            else
            {
                Assert.AreEqual(provider.SecondBranch, reader.GetInt32(0));
                Assert.AreEqual(request.Id, reader.GetInt64(2));
            }
            Assert.IsFalse(reader.IsDBNull(1));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow(3482, true, false)]
    [DataRow(3483, true, false)]
    [DataRow(3482, false, false)]
    [DataRow(3483, false, false)]
    [DataRow(3482, true, true)]
    [DataRow(3483, true, true)]
    public async Task PickupNullAndNonNullObservationsFenceInterveningChanges(
        int organizationId, bool observedNull, bool alreadyLive)
    {
        var provider = new PickupJournalProvider(organizationId);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        provider.Current = provider.SecondBranch;
        try
        {
            using var client = scoped.CreateClient();
            var actor = await ReadConfiguredSuperAdminAsync();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{request.Id}/pickup-preference",
                new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion),
                    alreadyLive ? provider.SecondBranch : provider.FirstBranch,
                    observedNull ? null : provider.FirstBranch, true));
            Assert.AreEqual(alreadyLive ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            if (!alreadyLive)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("pickup_changed_since_load", body.RootElement.GetProperty("code").GetString());
            }
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task UncertainPickupIsNotRepeatedAndBlocksNewHoldDispatch()
    {
        var provider = new PickupJournalProvider(3482) { FailBeforeEffect = true };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var pickup = scoped.Services.GetRequiredService<StaffPickupService>();
            var input = new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
                provider.FirstBranch, true);
            Assert.AreEqual("pickup_outcome_unconfirmed",
                (await pickup.UpdateAsync(actor, request.Id, input, CancellationToken.None)).Code);
            provider.FailBeforeEffect = false;
            Assert.AreEqual("pickup_reconciliation_required",
                (await pickup.UpdateAsync(actor, request.Id, input, CancellationToken.None)).Code);
            Assert.AreEqual(1, provider.Writes);
            var hold = await scoped.Services.GetRequiredService<HoldPlacementService>()
                .PlaceBackgroundAsync(request.Id, request.RowVersion, CancellationToken.None);
            Assert.AreEqual("pickup_reconciliation_required", hold.Code);
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.HoldPlacementOperations.CountAsync(x => x.TitleRequestId == request.Id));
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 1));
        }
        finally
        {
            await CleanupPickupJournalLibraryAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow(3482)]
    [DataRow(3483)]
    public async Task PatronSuggestionValidatesBeforePickupAndRecordsPostEffectFailure(int organizationId)
    {
        var provider = new PickupJournalProvider(organizationId);
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedPickupJournalRequestAsync(contexts, provider);
        var session = await IssueTestPatronSessionAsync(provider.Barcode, organizationId, provider.OrganizationId * 10);
        var suggestions = scoped.Services.GetRequiredService<PatronSuggestionService>();
        try
        {
            await Assert.ThrowsAsync<PatronFlowException>(() => suggestions.CreateAsync(session,
                Suggestion("") with { PreferredPickupBranchId = provider.SecondBranch }, CancellationToken.None));
            Assert.AreEqual(0, provider.Writes, "Invalid form input must not mutate a preference.");
            provider.AfterEffect = token => ExecuteNonQueryAsync(
                "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @org;", ("@org", organizationId));
            var failure = await Assert.ThrowsAsync<PatronFlowException>(() => suggestions.CreateAsync(session,
                Suggestion("Pickup durability " + Guid.NewGuid()) with { PreferredPickupBranchId = provider.SecondBranch },
                CancellationToken.None));
            var partial = failure.Response as PatronSuggestionPickupChangedFailure;
            Assert.IsNotNull(partial);
            Assert.IsNotNull(partial.OperationId);
            Assert.IsTrue(partial.PickupPreferenceChanged);
            Assert.AreEqual(1, provider.Writes);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 2));
        }
        finally
        {
            await DeleteTestPatronSessionAsync(session.Id);
            await CleanupPickupJournalLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow(8, "Active", "hold_unclaimed")]
    [DataRow(9, "active", "hold_expired")]
    [DataRow(16, "Unknown translation", "hold_cancelled")]
    [DataRow(3, "Expired", null)]
    [DataRow(1, "CANCELLED", null)]
    public void TerminalHoldPolicyUsesNativeStatusRegardlessOfPresentation(int id, string description, string? reason)
    {
        var row = new PolarisHoldSnapshot(8123, 9001, id, description, 101);
        Assert.AreEqual(reason, PolarisHoldStatusPolicy.TerminalReason(row.StatusId));
        Assert.AreEqual(reason is not null, HoldPlacementService.IsTerminal(row.StatusId));
    }

    private WebApplicationFactory<Program> CreatePickupJournalFactory(PickupJournalProvider provider) =>
        factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));

    private async Task<TitleRequest> SeedPickupJournalRequestAsync(
        IDbContextFactory<AsapDbContext> contexts, PickupJournalProvider provider)
    {
        await using var context = await contexts.CreateDbContextAsync();
        context.Organizations.Add(new Organization
        {
            Id = provider.OrganizationId,
            DisplayName = "Pickup library",
            OrganizationCodeId = OrganizationAuthority.LibraryOrganizationCodeId,
            IsActive = true
        });
        context.Organizations.Add(new Organization
        {
            Id = provider.FirstBranch,
            DisplayName = "Pickup registered branch",
            OrganizationCodeId = 3,
            ParentOrganizationId = provider.OrganizationId,
            IsActive = false
        });
        var request = new TitleRequest {
            LibraryOrganizationId = provider.OrganizationId, PatronOrganizationId = provider.FirstBranch,
            Barcode = provider.Barcode, Title = "Pickup request " + Guid.NewGuid(), AutoHold = true,
            BibId = 9001, BibIdStaffVerified = true, Status = "pending_hold", IsbnCheckStatus = "found",
            PreferredPickupBranchId = provider.FirstBranch, PreferredPickupBranchName = "First",
            MaterialFormatId = await context.MaterialFormats.Where(x => x.OwnerOrganizationId == 1 && x.Code == "book")
                .Select(x => x.Id).SingleAsync(), CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
        context.TitleRequests.Add(request);
        await context.SaveChangesAsync();
        return request;
    }

    private static async Task<int> PickupJournalCountAsync(string barcode, int? state)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM [asap].[PickupPreferenceOperation]
            WHERE [Barcode] = @barcode AND (@state IS NULL OR [State] = @state);
            """, connection);
        command.Parameters.AddWithValue("@barcode", barcode);
        command.Parameters.Add("@state", System.Data.SqlDbType.Int).Value = (object?)state ?? DBNull.Value;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task CleanupPickupJournalLibraryAsync(int organizationId)
    {
        await ExecuteNonQueryAsync("""
            DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId] = @org;
            DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @org;
            DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN
                (SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org);
            DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org;
            DELETE FROM [asap].[Organization] WHERE [Id] IN (@org, @org * 100 + 1);
            """, ("@org", organizationId));
    }

    private sealed class PickupJournalProvider : IPatronProvider
    {
        public PickupJournalProvider(int organizationId)
        {
            OrganizationId = organizationId;
            PatronId = organizationId * 10;
            HomeLibraryOrganizationId = organizationId;
            Current = organizationId * 100 + 1;
        }

        public int OrganizationId { get; }
        public string Barcode => "2000000000" + OrganizationId;
        public string CurrentBarcodeOverride { get; set; } = "";
        public string? FormerBarcode { get; set; }
        public string? RequestedBarcode { get; set; }
        private string CurrentBarcode => string.IsNullOrEmpty(CurrentBarcodeOverride)
            ? Barcode
            : CurrentBarcodeOverride;
        public int FirstBranch => OrganizationId * 100 + 1;
        public int SecondBranch => FirstBranch + 1;
        public int PatronId { get; set; }
        public int? PatronCodeId { get; set; } = 1;
        public int HomeLibraryOrganizationId { get; set; }
        public int? Current { get; set; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool AllowAuthentication { get; set; }
        public bool FailBeforeEffect { get; set; }
        public Exception? NoEffectFailure { get; set; }
        public Func<CancellationToken, Task>? AfterEffect { get; set; }
        public Func<CancellationToken, Task>? AfterRefresh { get; set; }
        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken token)
        {
            if (!AllowAuthentication)
            {
                throw new InvalidOperationException("Authentication is not part of this scenario.");
            }
            Check(barcode, OrganizationId, token);
            return Task.FromResult(new PatronSnapshot(PatronId, CurrentBarcode, "pickup@example.org",
                "Pickup", "Patron", PatronCodeId, "Adult", FirstBranch, HomeLibraryOrganizationId, "Pickup library", Current,
                FormerBarcode, RequestedBarcode));
        }
        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, int context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(OrganizationId, context);
            return Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.TransientFailure));
        }
        private void Check(string barcode, int context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.IsTrue(new[] { Barcode, CurrentBarcode, FormerBarcode }
                .Where(item => item is not null)
                .Contains(barcode, StringComparer.OrdinalIgnoreCase),
                $"Unexpected verified barcode alias {barcode}.");
            Assert.AreEqual(OrganizationId, context);
        }
        public async Task<PatronSnapshot> RefreshAsync(string barcode, int context, CancellationToken token)
        {
            Check(barcode, context, token);
            Reads++;
            if (AfterRefresh is { } afterRefresh)
            {
                await afterRefresh(token);
            }
            var requestedBarcode = RequestedBarcode ??
                (!string.Equals(barcode, CurrentBarcode, StringComparison.OrdinalIgnoreCase) ? barcode : null);
            return new PatronSnapshot(PatronId, CurrentBarcode, "pickup@example.org",
                "Pickup", "Patron", PatronCodeId, "Adult", FirstBranch, HomeLibraryOrganizationId, "Pickup library", Current,
                FormerBarcode, requestedBarcode);
        }
        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron, int context, CancellationToken token)
        {
            Check(patron.Barcode, context, token);
            return Task.FromResult<IReadOnlyList<PickupBranch>>([new(FirstBranch, "First"), new(SecondBranch, "Second")]);
        }
        public async Task UpdatePreferredPickupBranchAsync(string barcode, int branch, int context, CancellationToken token)
        {
            Check(barcode, context, token);
            Assert.AreEqual(SecondBranch, branch);
            Writes++;
            if (NoEffectFailure is not null)
            {
                throw NoEffectFailure;
            }
            if (FailBeforeEffect)
            {
                throw new PolarisOperationalException("testing_uncertain_pickup", "No confirmed provider result.");
            }
            Current = branch;
            if (AfterEffect is not null)
            {
                await AfterEffect(token);
            }
        }
    }
}
