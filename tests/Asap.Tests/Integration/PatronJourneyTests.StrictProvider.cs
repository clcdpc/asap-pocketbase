using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(3502)]
    [DataRow(3503)]
    public async Task SharedStrictProviderProvesSelectedContextPickupStateAndNativeHoldInputs(int organizationId)
    {
        var provider = new DeterministicTestingPatronProvider();
        var barcode = $"strict-card-{organizationId}";
        var bibId = 9000 + organizationId;
        var branchId = organizationId * 100 + 1;
        var patronId = 7000 + organizationId;
        var snapshot = new PatronSnapshot(patronId, barcode, "strict@example.org", "Strict", "Patron", 1, "Adult",
            350201, 3502, "Home Library", branchId);
        provider.SetReferenceData([new(1, "System", null, 0, null), new(3502, "West Library", "W", 2, 1),
            new(3503, "East Library", "E", 2, 1)], [new(1, "Adult")]);
        provider.AddPatron(snapshot, [new(branchId, "First pickup"), new(branchId + 1, "Second pickup")], organizationId);
        provider.AllowPickupUpdate(barcode, organizationId, branchId + 1);
        provider.SetPatronSearch("Strict Patron", organizationId, [snapshot]);
        provider.SetBib(bibId, organizationId, new(true, "Strict Title", "Strict Author", "2026", "Book", "FOUND"), new(1, 0, 1, true, true));
        provider.SetBibSearch("title", "Strict Title", "", "", organizationId,
            new([new(bibId, "Strict Title", "Strict Author", "2026", "Book", "FOUND")], 1));
        provider.SetIdentifierResult("FOUND", organizationId, new(IdentifierLookupOutcome.Found, bibId));
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (3502, N'Home Library', 2, 1, 1), (3503, N'Selected Library', 2, 1, 1),
                   (350201, N'Registered Home Branch', 3, 3502, 0);
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId], [AllowAnyRegisteredCardLogin], [UpdatedUtc])
                VALUES (@org, 1, SYSUTCDATETIME());
            """, ("@org", organizationId));
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.RemoveAll<IPolarisReferenceProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
            services.AddSingleton<IPolarisReferenceProvider>(provider);
        }));
        long? requestId = null;
        try
        {
            using var client = scopedFactory.CreateClient();
            using var login = await client.PostAsJsonAsync("/api/asap/patron/login", new { barcode, pin = "1234", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            using var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("token").GetString());
            using var continued = await client.GetAsync("/api/asap/patron/session");
            Assert.AreEqual(HttpStatusCode.OK, continued.StatusCode, await continued.Content.ReadAsStringAsync());
            using var submitted = await client.PostAsJsonAsync("/api/asap/patron/suggestions", new
            {
                format = "book", title = "Strict Title", author = "Strict Author", isbn = "FOUND", publication = "Coming soon",
                preferredPickupBranchId = branchId + 1, autohold = true, customFields = new Dictionary<string, string?>()
            });
            Assert.AreEqual(HttpStatusCode.Created, submitted.StatusCode, await submitted.Content.ReadAsStringAsync());
            using var result = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            var requestIdElement = result.RootElement.GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, requestIdElement.ValueKind);
            var requestIdText = requestIdElement.GetString();
            Assert.IsNotNull(requestIdText);
            Assert.IsTrue(long.TryParse(requestIdText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedRequestId));
            Assert.AreEqual(parsedRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), requestIdText);
            requestId = parsedRequestId;
            Assert.AreEqual(branchId + 1, (await provider.RefreshAsync(barcode, organizationId, CancellationToken.None)).PreferredPickupBranchId);
            var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            var actor = await ReadConfiguredSuperAdminAsync();
            using var staff = scopedFactory.CreateClient();
            AddTestingStaffHeaders(staff, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            staff.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(staff));
            using var searched = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { query = "Strict Patron", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            using var bib = await staff.PostAsJsonAsync("/api/asap/staff/bib-lookup", new { mode = "title", query = "Strict Title", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, bib.StatusCode, await bib.Content.ReadAsStringAsync());
            await using var db = await contexts.CreateDbContextAsync();
            var row = await db.TitleRequests.SingleAsync(item => item.Id == requestId);
            Assert.AreEqual(organizationId, row.LibraryOrganizationId);
            Assert.AreEqual(bibId, row.BibId);
            using var promoted = await staff.PostAsJsonAsync($"/api/asap/staff/title-requests/{row.Id}/action", new
            {
                version = StaffVersion.Encode(row.RowVersion), action = "catalogFound", bibid = bibId, staffSelectedBibId = bibId
            });
            Assert.AreEqual(HttpStatusCode.OK, promoted.StatusCode, await promoted.Content.ReadAsStringAsync());
            await db.Entry(row).ReloadAsync();
            var settings = await db.PolarisSettings.SingleAsync(item => item.OrganizationId == 1);
            var conversation = Guid.Parse("84cf5f4b-4d38-4a28-bcc7-e903f77bc80a");
            var create = new HoldCreateCommand(patronId, bibId, branchId + 1, organizationId, settings.WorkstationId!.Value, settings.SystemPolarisUserId!.Value);
            var reply = new HoldReplyCommand(conversation, "group", "qualifier", organizationId);
            provider.ExpectCreate(create, new(HoldProviderOutcome.ReplyRequired, conversation, null, "group", "qualifier", 3, 5, "declared_reply"));
            provider.ExpectReply(reply, new(HoldProviderOutcome.FinalSuccess, conversation, 50000 + organizationId, "group", "qualifier", 2, 1, "declared_success"));
            var placed = await scopedFactory.Services.GetRequiredService<HoldPlacementService>().PlaceBackgroundAsync(row.Id, row.RowVersion, CancellationToken.None);
            Assert.AreEqual("updated", placed.Code);
            Assert.AreEqual(RequestStatus.HoldPlaced, placed.FinalStatus);
            provider.SetHolds(barcode, organizationId, [new(50000 + organizationId, bibId, 1, "Active", branchId + 1, barcode)]);
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, organizationId, row.Id);
            Assert.AreEqual("completed", (await scopedFactory.Services.GetRequiredService<WorkflowProcessingService>().ProcessWorkflowAsync(organizationId)).Code);
            Assert.AreEqual(create, provider.CreateCommands.Single());
            Assert.AreEqual(reply, provider.ReplyCommands.Single());
            provider.VerifyNoOutstandingWrites();
            Assert.AreEqual(1, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PickupUpdate));
            Assert.IsTrue(provider.Calls.Where(call => call.Operation != TestingPolarisOperation.Authenticate)
                .All(call => call.OrganizationId == organizationId), "Every member operation must use the selected scope, even when home differs.");
        }
        finally
        {
            if (requestId.HasValue)
            {
                await DeleteRequestAsync(requestId.Value);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @org;
                DELETE FROM [asap].[QueueProgress] WHERE [ScopeOrganizationId] = @org;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (3502,3503,350201);
                """, ("@org", organizationId));
        }
    }
}
