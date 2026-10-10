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
    public async Task PublicSessionRejectsDifferentNativePatronWithTheSameBarcodeBeforePickupIntent()
    {
        const int organizationId = 38110;
        var provider = new PickupJournalProvider(organizationId)
        {
            PatronId = 739000,
            AllowAuthentication = true
        };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider);
        try
        {
            using var client = scoped.CreateClient();
            using var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
            {
                barcode = provider.Barcode,
                pin = "1234",
                libraryOrgId = organizationId
            });
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", loginBody.RootElement.GetProperty("token").GetString());
            provider.PatronId = 739001;
            var title = "Same barcode different native patron " + Guid.NewGuid().ToString("N");
            var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);

            using var response = await PostPatronSuggestionAsync(client, title, provider.SecondBranch);

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("patron_session_invalid", body.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(0, provider.Writes);
            Assert.AreEqual(0, await PickupJournalCountAsync(provider.Barcode, state: null));
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId));
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.Title == title));
            var session = await verify.PatronSessions.SingleAsync(item => item.Barcode == provider.Barcode);
            Assert.AreEqual(739000, session.NativePatronId);
        }
        finally
        {
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task PublicBarcodeRotationKeepsDuplicateAndWeeklyLimitAccountingOnNativeIdentity()
    {
        const int organizationId = 38111;
        const int nativePatronId = 739011;
        var provider = new PickupJournalProvider(organizationId) { PatronId = nativePatronId };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider, suggestionLimit: 2);
        try
        {
            var issued = await IssueNativePatronSessionAsync(scoped.Services, provider.Barcode,
                nativePatronId, organizationId);
            using var client = scoped.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);

            var firstTitle = "Before barcode rotation " + Guid.NewGuid().ToString("N");
            using (var first = await PostPatronSuggestionAsync(client, firstTitle, provider.FirstBranch,
                       "9780000000001"))
            {
                Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync());
            }

            var afterFirst = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
            provider.CurrentBarcodeOverride = provider.Barcode + "N";
            provider.FormerBarcode = provider.Barcode;
            var duplicateTitle = "Duplicate after barcode rotation " + Guid.NewGuid().ToString("N");
            using (var duplicate = await PostPatronSuggestionAsync(client, duplicateTitle, provider.FirstBranch,
                       "9780000000001"))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, await duplicate.Content.ReadAsStringAsync());
                Assert.AreEqual(afterFirst, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId),
                    "A cross-alias duplicate must not create a second request, event, or outbox row.");
            }

            var rotatedTitle = "After barcode rotation " + Guid.NewGuid().ToString("N");
            using (var rotated = await PostPatronSuggestionAsync(client, rotatedTitle, provider.FirstBranch,
                       "9780000000002"))
            {
                Assert.AreEqual(HttpStatusCode.Created, rotated.StatusCode, await rotated.Content.ReadAsStringAsync());
            }

            var afterRotated = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
            var limitedTitle = "Native identity weekly limit " + Guid.NewGuid().ToString("N");
            using (var limited = await PostPatronSuggestionAsync(client, limitedTitle, provider.FirstBranch,
                       "9780000000003"))
            {
                Assert.AreEqual(HttpStatusCode.NotAcceptable, limited.StatusCode, await limited.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
                Assert.AreEqual("suggestion_limit_reached", body.RootElement.GetProperty("code").GetString());
                Assert.AreEqual(afterRotated, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId),
                    "The native identity limit must reject before request, event, or outbox creation.");
            }

            await using var verify = await contexts.CreateDbContextAsync();
            var requests = await verify.TitleRequests.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .OrderBy(item => item.Id)
                .ToArrayAsync();
            Assert.HasCount(2, requests);
            Assert.AreEqual(provider.Barcode, requests[0].Barcode);
            Assert.AreEqual(provider.CurrentBarcodeOverride, requests[1].Barcode);
            Assert.IsTrue(requests.All(item => item.PatronIdSnapshot == nativePatronId));
            Assert.AreEqual(0, provider.Writes, "The selected branch already matched the provider preference.");
        }
        finally
        {
            await DeleteTestPatronSessionAsyncByBarcode(provider.Barcode);
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    public async Task RotatedBarcodeNewPickupAndHoldRemainFencedByTheSameUncertainNativeIdentity()
    {
        const int organizationId = 38112;
        const int nativePatronId = 739021;
        var provider = new PickupJournalProvider(organizationId)
        {
            PatronId = nativePatronId,
            FailBeforeEffect = true
        };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var request = await SeedPickupJournalRequestAsync(contexts, provider);
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var pickup = scoped.Services.GetRequiredService<StaffPickupService>();
            var started = await pickup.UpdateAsync(actor, request.Id,
                new PickupPreferenceInput(StaffVersion.Encode(request.RowVersion), provider.SecondBranch,
                    provider.FirstBranch, true), CancellationToken.None);
            Assert.AreEqual("pickup_outcome_unconfirmed", started.Code);
            var operationId = started.OperationId ?? throw new AssertFailedException("The uncertain pickup must return its journal identity.");
            Assert.AreEqual(1, provider.Writes);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 1));

            provider.CurrentBarcodeOverride = provider.Barcode + "N";
            provider.FormerBarcode = provider.Barcode;
            var issued = await IssueNativePatronSessionAsync(scoped.Services, provider.Barcode,
                nativePatronId, organizationId);
            using var client = scoped.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
            var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
            var title = "New rotated identity pickup must wait " + Guid.NewGuid().ToString("N");

            using var suggestion = await PostPatronSuggestionAsync(client, title, provider.SecondBranch);

            Assert.AreEqual(HttpStatusCode.Conflict, suggestion.StatusCode, await suggestion.Content.ReadAsStringAsync());
            using (var body = JsonDocument.Parse(await suggestion.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("pickup_reconciliation_required", body.RootElement.GetProperty("code").GetString());
                Assert.AreEqual(operationId.ToString(), body.RootElement.GetProperty("operationId").GetString());
            }
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId));

            var hold = await scoped.Services.GetRequiredService<HoldPlacementService>()
                .PlaceBackgroundAsync(request.Id, request.RowVersion, CancellationToken.None);
            Assert.AreEqual("pickup_reconciliation_required", hold.Code);
            Assert.AreEqual(1, provider.Writes, "Neither the rotated pickup nor hold retry may repeat provider work.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 1));
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.HoldPlacementOperations.CountAsync(item => item.TitleRequestId == request.Id));
            Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.Title == title));
            var preserved = await verify.TitleRequests.SingleAsync(item => item.Id == request.Id);
            Assert.IsNull(preserved.PatronIdSnapshot, "The legacy request stays unanchored; the journal carries the verified operation identity.");
            Assert.AreEqual(provider.FirstBranch, preserved.PreferredPickupBranchId);
        }
        finally
        {
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"papierrorcode\":0}")]
    public async Task PinnedMalformedPickupPutFlowsThroughHttpAndRetainsOneAmbiguousJournal(string malformedResponse)
    {
        const string barcode = "20000000004004";
        const int nativePatronId = 739041;
        const int organizationId = 2;
        var handler = new MalformedPickupPutPapiHandler(barcode, nativePatronId, malformedResponse);
        var provider = await CreatePolarisProviderAsync(handler, "pickup-malformed-put-" + Guid.NewGuid().ToString("N"));
        await using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var issued = await IssueNativePatronSessionAsync(scoped.Services, barcode, nativePatronId, organizationId);
        var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
        using var client = scoped.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        var title = "Malformed pinned pickup response " + Guid.NewGuid().ToString("N");

        try
        {
            using var first = await PostPatronSuggestionAsync(client, title, 102);
            Assert.AreEqual(HttpStatusCode.Conflict, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.AreEqual("pickup_outcome_unconfirmed", firstBody.RootElement.GetProperty("code").GetString());
            var operationId = firstBody.RootElement.GetProperty("operationId").GetGuid();
            Assert.AreNotEqual(Guid.Empty, operationId);
            Assert.AreEqual(1, handler.UpdateCalls,
                "The registered production Polaris provider must issue the actual pickup PUT through its pinned serializer.");

            using var retry = await PostPatronSuggestionAsync(client, title, 102);
            Assert.AreEqual(HttpStatusCode.Conflict, retry.StatusCode, await retry.Content.ReadAsStringAsync());
            using var retryBody = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
            Assert.AreEqual("pickup_reconciliation_required", retryBody.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(operationId, retryBody.RootElement.GetProperty("operationId").GetGuid());
            Assert.AreEqual(1, handler.UpdateCalls, "A retry must retain the same ambiguous intent without another PUT.");
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId));

            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.Title == title));
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT [PatronId], [State], [CompletedUtc], [DispatchFinishedUtc]
                FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                """, connection);
            command.Parameters.AddWithValue("@id", operationId);
            await using var journal = await command.ExecuteReaderAsync();
            Assert.IsTrue(await journal.ReadAsync());
            Assert.AreEqual(nativePatronId, journal.GetInt32(0));
            Assert.AreEqual(1, journal.GetInt32(1));
            Assert.IsTrue(journal.IsDBNull(2));
            Assert.IsFalse(journal.IsDBNull(3));
            Assert.IsFalse(await journal.ReadAsync());
        }
        finally
        {
            await DeleteTestPatronSessionAsync(issued.Context.Id);
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode AND [PatronId] = @patronId;",
                ("@barcode", barcode), ("@patronId", nativePatronId));
        }
    }

    [TestMethod]
    public async Task StaffSuggestionKeepsPickupJournalAndFinalRequestOnTheOriginalNativePatron()
    {
        const int organizationId = 38113;
        const int nativePatronId = 739031;
        var provider = new PickupJournalProvider(organizationId) { PatronId = nativePatronId };
        await using var scoped = CreatePickupJournalFactory(provider);
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await SeedNativeIdentityPickupLibraryAsync(contexts, provider);
        var actor = await ReadConfiguredSuperAdminAsync();
        var title = "Staff post-PUT patron identity changed " + Guid.NewGuid().ToString("N");
        var input = new StaffSuggestionInput(
            organizationId, provider.Barcode, "book", title, "Test Author", null, "Coming soon", null, null,
            provider.SecondBranch, provider.FirstBranch, true, false, new Dictionary<string, string?>(),
            CurrentPreferredPickupBranchObservedAtLoad: true);
        try
        {
            provider.AfterRefresh = _ =>
            {
                if (provider.Reads == 3)
                {
                    provider.PatronId = nativePatronId + 1;
                }
                return Task.CompletedTask;
            };
            var service = scoped.Services.GetRequiredService<StaffSuggestionService>();
            var before = await ReadNativeIdentitySideEffectsAsync(contexts, organizationId);
            var changed = await Assert.ThrowsExactlyAsync<StaffSuggestionException>(() =>
                service.CreateAsync(actor, input, CancellationToken.None));
            Assert.AreEqual("request_not_created_pickup_changed", changed.Code);
            var partial = changed.Response as PatronSuggestionPickupChangedFailure
                ?? throw new AssertFailedException("The accepted pickup change must remain visible as partial truth.");
            var operationId = partial.OperationId
                ?? throw new AssertFailedException("The accepted pickup change must retain its journal identity.");
            Assert.AreEqual(1, provider.Writes);
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 2));
            Assert.AreEqual(before, await ReadNativeIdentitySideEffectsAsync(contexts, organizationId));
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.Title == title));
            }

            provider.AfterRefresh = null;
            provider.PatronId = nativePatronId;
            var recovered = await service.CreateAsync(actor, input, CancellationToken.None);
            Assert.IsGreaterThan(0, recovered.Id);
            Assert.AreEqual(1, provider.Writes, "The original confirmed A journal is recovered without another PUT.");
            Assert.AreEqual(1, await PickupJournalCountAsync(provider.Barcode, state: 3));
            await using var final = await contexts.CreateDbContextAsync();
            var request = await final.TitleRequests.SingleAsync(item => item.Id == recovered.Id);
            Assert.AreEqual(nativePatronId, request.PatronIdSnapshot);
            Assert.AreEqual(provider.Barcode, request.Barcode);
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var journal = new SqlCommand("""
                SELECT [PatronId], [TitleRequestId], [State]
                FROM [asap].[PickupPreferenceOperation] WHERE [Id] = @id;
                """, connection);
            journal.Parameters.AddWithValue("@id", operationId);
            await using var rows = await journal.ExecuteReaderAsync();
            Assert.IsTrue(await rows.ReadAsync());
            Assert.AreEqual(nativePatronId, rows.GetInt32(0));
            Assert.AreEqual(recovered.Id, rows.GetInt64(1));
            Assert.AreEqual(3, rows.GetInt32(2));
        }
        finally
        {
            await CleanupNativeIdentityPickupLibraryAsync(organizationId);
        }
    }

    private async Task SeedNativeIdentityPickupLibraryAsync(
        IDbContextFactory<AsapDbContext> contexts,
        PickupJournalProvider provider,
        int? suggestionLimit = null)
    {
        await using var context = await contexts.CreateDbContextAsync();
        context.Organizations.Add(new Organization
        {
            Id = provider.OrganizationId,
            DisplayName = "Native identity pickup library",
            OrganizationCodeId = OrganizationAuthority.LibraryOrganizationCodeId,
            ParentOrganizationId = 1,
            IsActive = true
        });
        context.Organizations.Add(new Organization
        {
            Id = provider.FirstBranch,
            DisplayName = "Native identity registered branch",
            OrganizationCodeId = 3,
            ParentOrganizationId = provider.OrganizationId,
            IsActive = false
        });
        if (suggestionLimit.HasValue)
        {
            context.WorkflowSettings.Add(new WorkflowSettings
            {
                OrganizationId = provider.OrganizationId,
                SuggestionLimit = suggestionLimit,
                UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
            });
        }
        await context.SaveChangesAsync();
    }

    private static async Task<IssuedPatronSession> IssueNativePatronSessionAsync(
        IServiceProvider services,
        string barcode,
        int nativePatronId,
        int organizationId)
    {
        var issued = await services.GetRequiredService<PatronSessionService>().IssueAsync(
            barcode, nativePatronId, organizationId, organizationId, organizationId, CancellationToken.None);
        Assert.IsNotNull(issued, "The native identity fixture requires an active code-2 library.");
        return issued;
    }

    private static Task<HttpResponseMessage> PostPatronSuggestionAsync(
        HttpClient client,
        string title,
        int pickupBranchId,
        string? identifier = null) => client.PostAsJsonAsync("/api/asap/patron/suggestions", new
        {
            format = "book",
            title,
            author = "Test Author",
            isbn = identifier,
            publication = "Coming soon",
            preferredPickupBranchId = pickupBranchId,
            autohold = true,
            customFields = new Dictionary<string, string?>()
        });

    private static async Task<(int Requests, int Events, int Outbox)> ReadNativeIdentitySideEffectsAsync(
        IDbContextFactory<AsapDbContext> contexts,
        int organizationId)
    {
        await using var context = await contexts.CreateDbContextAsync();
        var requests = await context.TitleRequests.CountAsync(item => item.LibraryOrganizationId == organizationId);
        var requestIds = await context.TitleRequests.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .Select(item => item.Id)
            .ToArrayAsync();
        var events = await context.TitleRequestEvents.CountAsync(item => requestIds.Contains(item.TitleRequestId));
        var outbox = await context.EmailOutbox.CountAsync(item => item.OrganizationId == organizationId);
        return (requests, events, outbox);
    }

    private static async Task CleanupNativeIdentityPickupLibraryAsync(int organizationId)
    {
        await ExecuteNonQueryAsync("""
            DELETE FROM [asap].[HoldPlacementOperation] WHERE [TitleRequestId] IN
                (SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org);
            DELETE FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] IN
                (SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org);
            DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN
                (SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org);
            DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN
                (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org);
            DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org;
            DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId] = @org;
            DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @org;
            DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = @org;
            DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
            DELETE FROM [asap].[Organization] WHERE [Id] IN (@org, @org * 100 + 1);
            """, ("@org", organizationId));
    }

    private static Task DeleteTestPatronSessionAsyncByBarcode(string barcode) => ExecuteNonQueryAsync(
        "DELETE FROM [asap].[PatronSession] WHERE [Barcode] = @barcode;", ("@barcode", barcode));

    private sealed class MalformedPickupPutPapiHandler(
        string barcode,
        int nativePatronId,
        string malformedResponse) : HttpMessageHandler
    {
        public int UpdateCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/authenticator/", StringComparison.Ordinal))
            {
                return Task.FromResult(BoundaryResponse(
                    $"{{\"PAPIErrorCode\":0,\"PatronID\":{nativePatronId},\"AccessToken\":\"pickup-token\",\"AccessSecret\":\"pickup-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}}"));
            }
            if (path.EndsWith("/basicdata", StringComparison.Ordinal))
            {
                return Task.FromResult(BoundaryResponse(JsonSerializer.Serialize(new
                {
                    PAPIErrorCode = 0,
                    PatronBasicData = new
                    {
                        PatronID = nativePatronId,
                        Barcode = barcode,
                        EmailAddress = "native-pickup@example.org",
                        NameFirst = "Native",
                        NameLast = "Pickup",
                        PatronCodeID = 1,
                        PatronOrgID = 2,
                        RequestPickupBranchID = 101
                    }
                })));
            }
            if (path.EndsWith("/pickupbranches", StringComparison.Ordinal))
            {
                return Task.FromResult(BoundaryResponse(
                    "{\"PAPIErrorCode\":2,\"PickupBranchesRows\":[{\"ID\":101},{\"ID\":102}]}"));
            }
            if (path.Contains("/organizations/", StringComparison.Ordinal))
            {
                return Task.FromResult(BoundaryResponse("""
                    {"PAPIErrorCode":2,"OrganizationsGetRows":[
                      {"OrganizationID":1,"OrganizationCodeID":1,"ParentOrganizationID":null,"DisplayName":"System"},
                      {"OrganizationID":2,"OrganizationCodeID":2,"ParentOrganizationID":1,"DisplayName":"Main Library"},
                      {"OrganizationID":101,"OrganizationCodeID":3,"ParentOrganizationID":2,"DisplayName":"Main Branch"},
                      {"OrganizationID":102,"OrganizationCodeID":3,"ParentOrganizationID":2,"DisplayName":"North Branch"}
                    ]}
                    """));
            }
            if (request.Method == HttpMethod.Put && path.Contains("/patron/", StringComparison.Ordinal))
            {
                UpdateCalls++;
                return Task.FromResult(BoundaryResponse(malformedResponse));
            }

            throw new AssertFailedException("Unexpected pinned Polaris pickup request: " + request.Method + " " + path);
        }
    }
}
