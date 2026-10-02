using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("patron", false)]
    [DataRow("staff", false)]
    [DataRow("request", false)]
    [DataRow("patron", true)]
    [DataRow("staff", true)]
    [DataRow("request", true)]
    public async Task FirstPickupFromPapiZeroJournalsNullThroughEveryHttpEntryPoint(string entryPoint, bool uncertain)
    {
        var seed = new PickupJournalProvider(3484);
        var handler = new FirstPickupPapiHandler(seed) { DisconnectAfterWrite = uncertain };
        var provider = await CreatePolarisProviderAsync(handler);
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, seed);
        try
        {
            handler.BeforeWrite = async () =>
            {
                Assert.AreEqual(1, await PickupJournalCountAsync(seed.Barcode, 1), "Intent must commit before HTTP dispatch.");
                await using var connection = new SqlConnection(databaseConnectionString);
                await connection.OpenAsync();
                await using var command = new SqlCommand("""
                    UPDATE [asap].[PickupPreferenceOperation] WITH (NOWAIT)
                    SET [ToPickupBranchName] = [ToPickupBranchName]
                    WHERE [Barcode] = @barcode AND [CompletedUtc] IS NULL;
                    SELECT [FromPickupBranchId] FROM [asap].[PickupPreferenceOperation]
                    WHERE [Barcode] = @barcode AND [CompletedUtc] IS NULL;
                    """, connection);
                command.Parameters.AddWithValue("@barcode", seed.Barcode);
                Assert.IsInstanceOfType<DBNull>(await command.ExecuteScalarAsync(), "No SQL transaction may span PAPI; prior pickup must be SQL NULL.");
            };
            using var client = scoped.CreateClient();
            var actor = await ReadConfiguredSuperAdminAsync();
            if (entryPoint == "patron")
            {
                using var login = await client.PostAsJsonAsync("/api/asap/patron/login", new { barcode = seed.Barcode, pin = "1234", libraryOrgId = seed.OrganizationId });
                Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
            }
            else
            {
                AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
                client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            }
            var title = "First pickup " + Guid.NewGuid();
            Task<HttpResponseMessage> Submit() => entryPoint switch
            {
                "patron" => client.PostAsJsonAsync("/api/asap/patron/suggestions", Suggestion(title) with { Isbn = null, PreferredPickupBranchId = seed.SecondBranch }),
                "staff" => client.PostAsJsonAsync("/api/asap/staff/suggestions", new
                {
                    libraryOrgId = seed.OrganizationId, barcode = seed.Barcode, format = "book", title,
                    author = "Author", publication = "Coming soon", preferredPickupBranchId = seed.SecondBranch,
                    currentPreferredPickupBranchIdAtLoad = (int?)null, currentPreferredPickupBranchObservedAtLoad = true,
                    autohold = true, emailPatronConfirmation = false, customFields = new Dictionary<string, string?>()
                }),
                "request" => client.PostAsJsonAsync($"/api/asap/staff/title-requests/{request.Id}/pickup-preference",
                    new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), seed.SecondBranch, null, true)),
                _ => throw new AssertFailedException("Unknown entry point.")
            };
            using var first = await Submit();
            Assert.AreNotEqual(HttpStatusCode.InternalServerError, first.StatusCode, await first.Content.ReadAsStringAsync());
            if (uncertain)
            {
                Assert.IsFalse(first.IsSuccessStatusCode);
                Assert.AreEqual(1, await PickupJournalCountAsync(seed.Barcode, 1));
                using var retry = await Submit();
                Assert.IsTrue(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.IsTrue(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
            }
            Assert.AreEqual(1, handler.Writes, "Recovery must accept the observed value without replaying a write.");
            await using var verify = new SqlConnection(databaseConnectionString);
            await verify.OpenAsync();
            await using var journal = new SqlCommand("""
                SELECT [FromPickupBranchId], [ToPickupBranchId], [State], [ConfirmedByRead], [ObservedPickupBranchId]
                FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                """, verify);
            journal.Parameters.AddWithValue("@barcode", seed.Barcode);
            await using var rows = await journal.ExecuteReaderAsync();
            Assert.IsTrue(await rows.ReadAsync());
            Assert.IsTrue(rows.IsDBNull(0));
            Assert.AreEqual(seed.SecondBranch, rows.GetInt32(1));
            Assert.AreEqual(3, rows.GetInt32(2));
            Assert.AreEqual(uncertain, rows.GetBoolean(3));
            Assert.AreEqual(seed.SecondBranch, rows.GetInt32(4));
            Assert.IsFalse(await rows.ReadAsync());
        }
        finally
        {
            await CleanupPickupRemediationAsync(seed.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow("request")]
    [DataRow("staff")]
    public async Task FirstPickupObservedNullFencesAnInterveningPapiPreference(string entryPoint)
    {
        var seed = new PickupJournalProvider(3484);
        var handler = new FirstPickupPapiHandler(seed);
        var provider = await CreatePolarisProviderAsync(handler);
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, seed);
        try
        {
            var loaded = await provider.RefreshAsync(seed.Barcode, seed.OrganizationId, CancellationToken.None);
            Assert.IsNull(loaded.PreferredPickupBranchId);
            handler.CurrentPickup = seed.FirstBranch;
            using var client = scoped.CreateClient();
            var actor = await ReadConfiguredSuperAdminAsync();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = entryPoint == "request"
                ? await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{request.Id}/pickup-preference", new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), seed.SecondBranch, null, true))
                : await client.PostAsJsonAsync("/api/asap/staff/suggestions", new
                {
                    libraryOrgId = seed.OrganizationId, barcode = seed.Barcode, format = "book", title = "Stale null " + Guid.NewGuid(),
                    author = "Author", publication = "Coming soon", preferredPickupBranchId = seed.SecondBranch,
                    currentPreferredPickupBranchIdAtLoad = (int?)null, currentPreferredPickupBranchObservedAtLoad = true,
                    autohold = true, emailPatronConfirmation = false, customFields = new Dictionary<string, string?>()
                });
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("pickup_changed_since_load", body.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(0, handler.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(seed.Barcode, null));
        }
        finally
        {
            await CleanupPickupRemediationAsync(seed.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow("programming")]
    [DataRow("rejection")]
    public async Task PickupJournalRetainsSafeNoEffectEvidenceAndAllowsOnlyANewIntent(string failure)
    {
        var provider = new PickupJournalProvider(3484) { Current = null };
        provider.NoEffectFailure = failure switch
        {
            "local" => new PolarisMutationNotDispatchedException(new PolarisOperationalException("polaris_configuration_incomplete", "Missing credentials")),
            "programming" => new PolarisMutationNotDispatchedException(new ArgumentException("Invalid local request")),
            "rejection" => new PolarisPickupRejectedException(-3622),
            _ => throw new AssertFailedException("Unknown failure")
        };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var patron = await provider.RefreshAsync(provider.Barcode, provider.OrganizationId, CancellationToken.None);
            var service = scoped.Services.GetRequiredService<PickupPreferenceMutationService>();
            Task<PickupMutationReceipt?> Change() => service.ChangeAsync(patron, provider.OrganizationId,
                new PickupBranch(provider.SecondBranch, "Second"), null, "request", request.Id, null, CancellationToken.None);
            if (failure == "programming")
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(Change);
            }
            else
            {
                await Assert.ThrowsAsync<PolarisOperationalException>(Change);
            }
            Assert.IsNull(provider.Current);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 4));
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using (var journal = new SqlCommand("""
                SELECT [FromPickupBranchId], [ProviderConfirmedUtc], [CompletedUtc], [FailureCode]
                FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                """, connection))
            {
                journal.Parameters.AddWithValue("@barcode", provider.Barcode);
                await using var rows = await journal.ExecuteReaderAsync();
                Assert.IsTrue(await rows.ReadAsync());
                Assert.IsTrue(rows.IsDBNull(0));
                Assert.IsTrue(rows.IsDBNull(1), "No-effect outcomes must not fabricate provider success.");
                Assert.IsFalse(rows.IsDBNull(2));
                Assert.AreEqual(failure == "rejection" ? "documented_rejection_-3622" : "local_not_dispatched", rows.GetString(3));
            }
            provider.NoEffectFailure = null;
            var receipt = await Change();
            Assert.IsNotNull(receipt);
            Assert.IsNull(receipt.FromBranchId);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 4), "Durable failed evidence is retained.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, 2), "A safe new attempt gets a separate intent.");
        }
        finally
        {
            await CleanupPickupRemediationAsync(provider.OrganizationId);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task StandalonePickupRejectsNonBranchSelectionBeforeLoadingTheRequest(int sentinel)
    {
        var service = factory!.Services.GetRequiredService<StaffPickupService>();
        var result = await service.UpdateAsync(await ReadConfiguredSuperAdminAsync(), 0,
            new PickupPreferenceInput(StaffVersion.Encode(new byte[8]), sentinel, null, true), CancellationToken.None);
        Assert.AreEqual("invalid_pickup", result.Code);
    }

    [TestMethod]
    [DataRow("from", 0)]
    [DataRow("from", 1)]
    [DataRow("to", 0)]
    [DataRow("to", 1)]
    [DataRow("observed", 0)]
    [DataRow("observed", 1)]
    public async Task PickupJournalSqlRejectsEveryNonBranchIdentity(string field, int sentinel)
    {
        var provider = new PickupJournalProvider(3484);
        await using var scoped = CreatePickupJournalFactory(provider);
        await SeedPickupJournalRequestAsync(scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>(), provider);
        try
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT INTO [asap].[PickupPreferenceOperation]
                    ([Id], [Barcode], [PatronId], [LibraryOrganizationId], [Origin], [FromPickupBranchId],
                     [ToPickupBranchId], [ToPickupBranchName], [ObservedPickupBranchId], [State], [DispatchStartedUtc], [ConfirmedByRead])
                VALUES (NEWID(), @barcode, 7001, @org, N'patron_suggestion', @from, @to, N'Second', @observed, 1, SYSUTCDATETIME(), 0);
                """, connection);
            command.Parameters.AddWithValue("@barcode", provider.Barcode);
            command.Parameters.AddWithValue("@org", provider.OrganizationId);
            command.Parameters.AddWithValue("@from", field == "from" ? sentinel : provider.FirstBranch);
            command.Parameters.AddWithValue("@to", field == "to" ? sentinel : provider.SecondBranch);
            command.Parameters.AddWithValue("@observed", field == "observed" ? sentinel : provider.SecondBranch);
            var error = await Assert.ThrowsExactlyAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.AreEqual(547, error.Number);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, null));
        }
        finally
        {
            await CleanupPickupRemediationAsync(provider.OrganizationId);
        }
    }

    private static async Task CleanupPickupRemediationAsync(int organizationId)
    {
        await ExecuteNonQueryAsync("""
            DELETE FROM [asap].[HoldPlacementOperation] WHERE [TitleRequestId] IN
                (SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org);
            DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN
                (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org);
            DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org;
            DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @org;
            """, ("@org", organizationId));
        await CleanupPickupJournalLibraryAsync(organizationId);
    }

    private sealed class FirstPickupPapiHandler(PickupJournalProvider seed) : HttpMessageHandler
    {
        public int CurrentPickup { get; set; }
        public int Writes { get; private set; }
        public bool DisconnectAfterWrite { get; init; }
        public Func<Task>? BeforeWrite { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/authenticator/", StringComparison.Ordinal))
            {
                return BoundaryResponse(JsonSerializer.Serialize(new { PAPIErrorCode = 0, PatronID = seed.OrganizationId * 10, AccessToken = "first-pickup-token", AccessSecret = "first-pickup-secret", AuthExpDate = "2030-01-01T00:00:00Z" }));
            }
            if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                return BoundaryResponse(JsonSerializer.Serialize(new { PAPIErrorCode = 0, PatronBasicData = new
                {
                    PatronID = seed.OrganizationId * 10, Barcode = seed.Barcode, EmailAddress = "pickup@example.org",
                    NameFirst = "First", NameLast = "Pickup", PatronCodeID = 1, PatronOrgID = seed.FirstBranch, RequestPickupBranchID = CurrentPickup
                } }));
            }
            if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                return BoundaryResponse(JsonSerializer.Serialize(new { PAPIErrorCode = 3, OrganizationsGetRows = new[]
                {
                    new { OrganizationID = seed.OrganizationId, OrganizationCodeID = 2, DisplayName = "Pickup library", ParentOrganizationID = 1 },
                    new { OrganizationID = seed.FirstBranch, OrganizationCodeID = 3, DisplayName = "First", ParentOrganizationID = seed.OrganizationId },
                    new { OrganizationID = seed.SecondBranch, OrganizationCodeID = 3, DisplayName = "Second", ParentOrganizationID = seed.OrganizationId }
                } }));
            }
            if (path.EndsWith("/pickupbranches", StringComparison.Ordinal))
            {
                return BoundaryResponse(JsonSerializer.Serialize(new { PAPIErrorCode = 2, PickupBranchesRows = new[] { new { ID = seed.FirstBranch }, new { ID = seed.SecondBranch } } }));
            }
            Assert.AreEqual(HttpMethod.Put, request.Method, path);
            Assert.IsTrue(path.EndsWith("/patron/" + seed.Barcode, StringComparison.Ordinal), path);
            Assert.IsTrue(path.Contains($"/100/{seed.OrganizationId}/", StringComparison.Ordinal), "Write context must be the servicing library.");
            if (BeforeWrite is not null)
            {
                await BeforeWrite();
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.AreEqual(seed.SecondBranch, body.RootElement.GetProperty("RequestPickupBranchID").GetInt32());
            Writes++;
            CurrentPickup = seed.SecondBranch;
            if (DisconnectAfterWrite)
            {
                throw new HttpRequestException("Connection lost after pickup effect.");
            }
            return BoundaryResponse("""{"PAPIErrorCode":0}""");
        }
    }
}
