using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task DirectHoldPlacementRevokedDuringProviderReadCannotStartCreate()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.BlockHoldRead();
        await using var holdFactory = HoldAuthorityFactory(provider);
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var requestId = await SeedPendingHoldRequestAsync("Revoked direct create", "20000000003211", 93211);
        var version = await HoldRequestVersionAsync(client, requestId);

        var placement = client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold", new { version });
        try
        {
            await provider.HoldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await SetHoldActorActiveAsync(actor.Id, false);
        }
        finally
        {
            provider.CompleteBlockedHoldRead();
        }
        try
        {
            using var response = await placement;
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
            Assert.IsFalse(body.RootElement.GetProperty("providerOutcomeRecorded").GetBoolean());
            Assert.AreEqual(0, provider.CreateCount);
            var state = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("pending_hold", state.RequestStatus);
            Assert.AreEqual("operator_required", state.State);
            Assert.AreEqual("acquired", state.Phase);
            Assert.AreEqual("staff_authority_changed_before_dispatch", state.LastErrorCode);
            Assert.IsFalse(state.HasOwner);
            Assert.IsFalse(state.HasLease);
            Assert.IsFalse(state.Completed);
            Assert.IsFalse(state.CreateStarted);
            Assert.AreEqual(0, state.PlacedEvents);

            var background = await holdFactory.Services.GetRequiredService<HoldPlacementService>()
                .RecoverBackgroundOperationAsync(state.OperationId, null, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", background.Code);
            Assert.AreEqual(0, provider.CreateCount);
            Assert.AreEqual(0, (await ReadHoldAuthorityStateAsync(requestId)).PlacedEvents);

            await SetHoldActorActiveAsync(actor.Id, true);
            var reconcileVersion = await HoldOperationVersionAsync(client, requestId);
            using var reconcile = await client.PostAsJsonAsync(
                $"/api/asap/staff/hold-operations/{state.OperationId}/reconcile",
                new { version = reconcileVersion });
            Assert.AreEqual(HttpStatusCode.OK, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
            Assert.AreEqual(1, provider.CreateCount);
            Assert.AreEqual(1, provider.ReplyCount);
            var completed = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("hold_placed", completed.RequestStatus);
            Assert.AreEqual("succeeded", completed.State);
            Assert.AreEqual(1, completed.PlacedEvents);
        }
        finally
        {
            await SetHoldActorActiveAsync(actor.Id, true);
        }
    }

    [TestMethod]
    public async Task ReconcileRevokedDuringProviderReadCannotStartCreate()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.BlockHoldRead();
        await using var holdFactory = HoldAuthorityFactory(provider);
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var requestId = await SeedPendingHoldRequestAsync("Revoked recovery create", "20000000003212", 93212);
        var operationId = await SeedHoldAuthorityOperationAsync(requestId, "acquired", provider.RequestGuid);
        var version = await HoldOperationVersionAsync(client, requestId);

        var reconcile = client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/reconcile", new { version });
        try
        {
            await provider.HoldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await SetHoldActorActiveAsync(actor.Id, false);
        }
        finally
        {
            provider.CompleteBlockedHoldRead();
        }
        try
        {
            using var response = await reconcile;
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("hold_resolution_forbidden", body.RootElement.GetProperty("code").GetString());
            Assert.IsFalse(body.RootElement.GetProperty("providerOutcomeRecorded").GetBoolean());
            Assert.AreEqual(0, provider.CreateCount);
            var state = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("operator_required", state.State);
            Assert.AreEqual("acquired", state.Phase);
            Assert.AreEqual("staff_authority_changed_before_dispatch", state.LastErrorCode);
            Assert.IsFalse(state.HasOwner);
            Assert.IsFalse(state.HasLease);
            Assert.IsFalse(state.Completed);
            Assert.IsFalse(state.CreateStarted);
            Assert.AreEqual(2L, state.Epoch);
            Assert.AreEqual(0, state.PlacedEvents);

            var background = await holdFactory.Services.GetRequiredService<HoldPlacementService>()
                .RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", background.Code);
            Assert.AreEqual(0, provider.CreateCount);
            Assert.AreEqual(0, (await ReadHoldAuthorityStateAsync(requestId)).PlacedEvents);
        }
        finally
        {
            await SetHoldActorActiveAsync(actor.Id, true);
        }
    }

    [TestMethod]
    public async Task CrashedAcquiredHoldStillRecoversAutomatically()
    {
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        await using var holdFactory = HoldAuthorityFactory(provider);
        var requestId = await SeedPendingHoldRequestAsync("Crashed acquired recovery", "20000000003215", 93215);
        var operationId = await SeedHoldAuthorityOperationAsync(requestId, "acquired", provider.RequestGuid);
        await ExecuteNonQueryAsync(
            "UPDATE [asap].[HoldPlacementOperation] SET [OwnerToken] = NEWID(), " +
            "[LeaseExpiresUtc] = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE [Id] = @id;",
            ("@id", operationId));
        var expired = await ReadHoldAuthorityStateAsync(requestId);
        Assert.IsTrue(expired.HasOwner);
        Assert.IsTrue(expired.HasLease);
        Assert.AreEqual("acquired", expired.Phase);
        Assert.AreEqual("in_progress", expired.State);

        var recovered = await holdFactory.Services.GetRequiredService<HoldPlacementService>()
            .RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);

        Assert.AreEqual("updated", recovered.Code);
        Assert.AreEqual(1, provider.CreateCount);
        Assert.AreEqual(1, provider.ReplyCount);
        var completed = await ReadHoldAuthorityStateAsync(requestId);
        Assert.AreEqual("hold_placed", completed.RequestStatus);
        Assert.AreEqual("succeeded", completed.State);
        Assert.AreEqual(2L, completed.Epoch);
        Assert.AreEqual(1, completed.PlacedEvents);
    }

    [TestMethod]
    public async Task ReplyRequiredCreateEvidenceSurvivesRevocationBeforeReply()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.BlockCreate();
        await using var holdFactory = HoldAuthorityFactory(provider);
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var requestId = await SeedPendingHoldRequestAsync("Revoked direct reply", "20000000003213", 93213);
        var version = await HoldRequestVersionAsync(client, requestId);

        var placement = client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold", new { version });
        try
        {
            await provider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await SetHoldActorActiveAsync(actor.Id, false);
        }
        finally
        {
            provider.CompleteBlockedCreateWithConfiguredResult();
        }
        try
        {
            using var response = await placement;
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
            Assert.IsTrue(body.RootElement.GetProperty("providerOutcomeRecorded").GetBoolean());
            Assert.AreEqual(1, provider.CreateCount);
            Assert.AreEqual(0, provider.ReplyCount);
            var state = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("pending_hold", state.RequestStatus);
            Assert.AreEqual("in_progress", state.State);
            Assert.AreEqual("reply_ready", state.Phase);
            Assert.AreEqual("reply_required", state.ResultCode);
            Assert.AreEqual("create_status_5_reply_required", state.EvidenceKind);
            Assert.IsFalse(state.HasOwner);
            Assert.IsTrue(state.CreateStarted);
            Assert.IsFalse(state.ReplyStarted);
            Assert.AreEqual(0, state.PlacedEvents);

            var recovered = await holdFactory.Services.GetRequiredService<HoldPlacementService>()
                .RecoverBackgroundOperationAsync(state.OperationId, null, CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            Assert.AreEqual(1, provider.CreateCount);
            Assert.AreEqual(1, provider.ReplyCount);
            var completed = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("hold_placed", completed.RequestStatus);
            Assert.AreEqual("succeeded", completed.State);
            Assert.AreEqual(1, completed.PlacedEvents);
        }
        finally
        {
            await SetHoldActorActiveAsync(actor.Id, true);
        }
    }

    [TestMethod]
    public async Task RecordedProviderSuccessSurvivesRevokedFinalizerAndBackgroundCompletes()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        provider.BlockRefresh();
        await using var holdFactory = HoldAuthorityFactory(provider);
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var requestId = await SeedPendingHoldRequestAsync("Revoked finalizer", "20000000003214", 93214);
        var operationId = await SeedHoldAuthorityOperationAsync(requestId, "result_recorded", provider.RequestGuid);
        var version = await HoldOperationVersionAsync(client, requestId);

        var reconcile = client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/reconcile", new { version });
        try
        {
            await provider.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await SetHoldActorActiveAsync(actor.Id, false);
        }
        finally
        {
            provider.CompleteBlockedRefresh();
        }
        try
        {
            using var response = await reconcile;
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("hold_resolution_forbidden", body.RootElement.GetProperty("code").GetString());
            Assert.IsTrue(body.RootElement.GetProperty("providerOutcomeRecorded").GetBoolean());
            var recorded = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("pending_hold", recorded.RequestStatus);
            Assert.AreEqual("result_recorded", recorded.Phase);
            Assert.AreEqual("success", recorded.ResultCode);
            Assert.AreEqual("documented_reply_success", recorded.EvidenceKind);
            Assert.IsFalse(recorded.HasOwner);
            Assert.AreEqual(0, recorded.PlacedEvents);

            var recovered = await holdFactory.Services.GetRequiredService<HoldPlacementService>()
                .RecoverBackgroundOperationAsync(operationId, null, CancellationToken.None);
            Assert.AreEqual("updated", recovered.Code);
            var completed = await ReadHoldAuthorityStateAsync(requestId);
            Assert.AreEqual("hold_placed", completed.RequestStatus);
            Assert.AreEqual("succeeded", completed.State);
            Assert.AreEqual("documented_reply_success", completed.EvidenceKind);
            Assert.AreEqual(1, completed.PlacedEvents);
            Assert.AreEqual(0, provider.CreateCount);
            Assert.AreEqual(0, provider.ReplyCount);
        }
        finally
        {
            await SetHoldActorActiveAsync(actor.Id, true);
        }
    }

    private WebApplicationFactory<Program> HoldAuthorityFactory(ScriptedHoldProvider provider) =>
        factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));

    private static async Task<string> HoldRequestVersionAsync(HttpClient client, long requestId)
    {
        using var response = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("version").GetString()!;
    }

    private static async Task<string> HoldOperationVersionAsync(HttpClient client, long requestId)
    {
        using var response = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("holdOperation").GetProperty("version").GetString()!;
    }

    private static async Task SetHoldActorActiveAsync(long actorId, bool active) =>
        await ExecuteNonQueryAsync(
            "UPDATE [asap].[StaffUser] SET [IsActive] = @active WHERE [Id] = @id;",
            ("@active", active), ("@id", actorId));

    private static async Task<long> SeedHoldAuthorityOperationAsync(long requestId, string phase, Guid requestGuid)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO [asap].[HoldPlacementOperation]
                ([TitleRequestId], [PatronBarcodeSnapshot], [PatronIdSnapshot], [BibIdSnapshot],
                 [PickupBranchIdSnapshot], [RequestingOrganizationIdSnapshot], [WorkstationIdSnapshot],
                 [PolarisUserIdSnapshot], [AttemptNumber], [State], [Phase], [ExecutionEpoch],
                 [RequestStartedUtc], [CreateStartedUtc], [CreateResponseObservedUtc],
                 [PolarisRequestGuid], [PolarisHoldId], [ResultCode], [OutcomeEvidenceKind])
            SELECT [Id], [Barcode], N'7105', [BibId], 101, 1, 1, N'1', 1, N'in_progress', @phase, 1,
                   SYSUTCDATETIME(),
                   CASE WHEN @phase = N'result_recorded' THEN SYSUTCDATETIME() ELSE NULL END,
                   CASE WHEN @phase = N'result_recorded' THEN SYSUTCDATETIME() ELSE NULL END,
                   CASE WHEN @phase = N'result_recorded' THEN @requestGuid ELSE NULL END,
                   CASE WHEN @phase = N'result_recorded' THEN N'8123' ELSE NULL END,
                   CASE WHEN @phase = N'result_recorded' THEN N'success' ELSE NULL END,
                   CASE WHEN @phase = N'result_recorded' THEN N'documented_reply_success' ELSE NULL END
            FROM [asap].[TitleRequest] WHERE [Id] = @requestId;
            SELECT CONVERT(bigint, SCOPE_IDENTITY());
            """, connection);
        command.Parameters.AddWithValue("@requestId", requestId);
        command.Parameters.AddWithValue("@phase", phase);
        command.Parameters.AddWithValue("@requestGuid", requestGuid.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record HoldAuthorityState(
        long OperationId, string RequestStatus, string State, string Phase, string? ResultCode,
        string? EvidenceKind, string? LastErrorCode,
        bool HasOwner, bool HasLease, bool Completed, bool CreateStarted, bool ReplyStarted,
        long Epoch, int PlacedEvents);

    private static async Task<HoldAuthorityState> ReadHoldAuthorityStateAsync(long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT operation.[Id], request.[Status], operation.[State], operation.[Phase], operation.[ResultCode],
                   operation.[OutcomeEvidenceKind], operation.[LastErrorCode], operation.[OwnerToken],
                   operation.[LeaseExpiresUtc], operation.[CompletedUtc], operation.[CreateStartedUtc],
                   operation.[ReplyStartedUtc], operation.[ExecutionEpoch],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = request.[Id] AND [EventType] = N'hold_placed')
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[TitleRequestId] = request.[Id]
            WHERE request.[Id] = @requestId;
            """, connection);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new HoldAuthorityState(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            !reader.IsDBNull(7), !reader.IsDBNull(8), !reader.IsDBNull(9),
            !reader.IsDBNull(10), !reader.IsDBNull(11),
            reader.GetInt64(12), reader.GetInt32(13));
    }
}
