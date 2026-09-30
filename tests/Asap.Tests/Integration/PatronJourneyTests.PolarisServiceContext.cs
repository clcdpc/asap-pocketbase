using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(3472)]
    [DataRow(3473)]
    public async Task ServiceFlowsKeepTheirEffectiveSelectedAndRequestLibraryContext(int organizationId)
    {
        var provider = new ServicingContextProvider(organizationId);
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (@org, N'Context Library', 1);
            IF @org <> 3472
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (3472, N'Patron Home Library', 1);
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (@branch, N'Context Branch', 0);
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId], [AllowAnyRegisteredCardLogin], [UpdatedUtc])
                VALUES (@org, 1, SYSUTCDATETIME());
            """, ("@org", organizationId), ("@branch", provider.RegisteredBranchId));
        var requests = new List<long>();
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        try
        {
            using var patron = scopedFactory.CreateClient();
            using var login = await patron.PostAsJsonAsync("/api/asap/patron/login", new {
                barcode = provider.Barcode, pin = "1234", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            using var session = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            patron.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                session.RootElement.GetProperty("token").GetString());
            using var continued = await patron.GetAsync("/api/asap/patron/session");
            Assert.AreEqual(HttpStatusCode.OK, continued.StatusCode, await continued.Content.ReadAsStringAsync());

            using var staff = scopedFactory.CreateClient();
            var actor = await ReadConfiguredSuperAdminAsync();
            AddTestingStaffHeaders(staff, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            staff.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(staff));
            using var searched = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new {
                query = "Context Patron", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            foreach (var input in new object[] {
                new { mode = "title", query = "Context Title", libraryOrgId = organizationId },
                new { mode = "bib", bibId = 9001, libraryOrgId = organizationId } })
            {
                using var result = await staff.PostAsJsonAsync("/api/asap/staff/bib-lookup", input);
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
            }

            async Task<TitleRequest> AddRequest(string status, bool verified = true)
            {
                await using var db = await contexts.CreateDbContextAsync();
                var row = new TitleRequest {
                    LibraryOrganizationId = organizationId, PatronOrganizationId = provider.RegisteredBranchId,
                    Barcode = provider.Barcode, Title = "Context " + Guid.NewGuid(), AutoHold = true,
                    MaterialFormatId = await db.MaterialFormats.Where(x => x.OwnerOrganizationId == 1 && x.Code == "book")
                        .Select(x => x.Id).SingleAsync(), Status = status, BibId = verified ? 9001 : null,
                    BibIdStaffVerified = verified, Identifier = "FOUND", IsbnCheckStatus = verified ? "found" : "pending",
                    PreferredPickupBranchId = provider.CurrentPickup, PreferredPickupBranchName = "Context Branch",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                db.TitleRequests.Add(row);
                await db.SaveChangesAsync();
                requests.Add(row.Id);
                return row;
            }

            var pending = await AddRequest("pending_hold");
            using var options = await staff.PostAsJsonAsync($"/api/asap/staff/title-requests/{pending.Id}/pickup-options", new { });
            Assert.AreEqual(HttpStatusCode.OK, options.StatusCode, await options.Content.ReadAsStringAsync());
            using var optionsBody = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
            using var updated = await staff.PostAsJsonAsync($"/api/asap/staff/title-requests/{pending.Id}/pickup-preference", new {
                version = optionsBody.RootElement.GetProperty("version").GetString(),
                preferredPickupBranchId = provider.RegisteredBranchId + 1,
                currentPreferredPickupBranchIdAtLoad = provider.CurrentPickup });
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            await using (var db = await contexts.CreateDbContextAsync())
            {
                pending = await db.TitleRequests.SingleAsync(x => x.Id == pending.Id);
            }
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placed = await placement.PlaceBackgroundAsync(pending.Id, pending.RowVersion, CancellationToken.None);
            Assert.AreEqual("updated", placed.Code);
            Assert.AreEqual("hold_placed", placed.FinalStatus);
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, organizationId, pending.Id);
            var workflow = scopedFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await workflow.ProcessWorkflowAsync(organizationId, CancellationToken.None)).Code);

            var identifier = await AddRequest("suggestion", verified: false);
            await PrepareSingleItemCycleAsync(QueueNames.IdentifierProcessing, organizationId, identifier.Id);
            Assert.AreEqual("completed", (await workflow.ProcessIdentifierAsync(organizationId, CancellationToken.None)).Code);
            await using (var db = await contexts.CreateDbContextAsync())
            {
                Assert.AreEqual(9001, (await db.TitleRequests.SingleAsync(x => x.Id == identifier.Id)).BibId);
            }

            // Reconciliation observes the same owning library without dispatching another write.
            provider.HoldExists = false;
            provider.AmbiguousCreate = true;
            provider.Barcode += "R";
            var recovering = await AddRequest("pending_hold");
            var ambiguous = await placement.PlaceBackgroundAsync(recovering.Id, recovering.RowVersion, CancellationToken.None);
            Assert.IsNotNull(ambiguous.OperationId, ambiguous.Code);
            provider.HoldExists = true;
            var recovered = await placement.RecoverBackgroundOperationAsync(ambiguous.OperationId.Value,
                organizationId, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", recovered.Code);
            Assert.IsNull(recovered.FinalStatus);
            Assert.AreEqual(2, provider.CreateCount);
            Assert.AreEqual(1, provider.ReplyCount);
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var evidence = await db.HoldPlacementOperations.SingleAsync(x => x.Id == ambiguous.OperationId.Value);
                Assert.AreEqual("operator_required", evidence.State);
                Assert.IsNull(evidence.PolarisHoldId, "A same-BIB hold is not proof of this attempted write.");
            }
            CollectionAssert.IsSubsetOf(new[] { "refresh", "pickup-read", "pickup-write", "patron-search",
                "bib-search", "bib-detail", "holdings", "identifier", "holds", "checkouts", "create", "reply" },
                provider.Calls.ToArray());
        }
        finally
        {
            foreach (var request in requests)
            {
                await DeleteRequestAsync(request);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN
                    (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org);
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @org;
                DELETE FROM [asap].[QueueProgress] WHERE [ScopeOrganizationId] = @org;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@org, 3472, @branch);
                """, ("@org", organizationId), ("@branch", provider.RegisteredBranchId));
        }
    }

    private sealed class ServicingContextProvider(int organizationId) : IPatronProvider, IStaffPolarisProvider
    {
        public string Barcode { get; set; } = "200000000" + organizationId;
        public int RegisteredBranchId => 347201;
        public int CurrentPickup { get; private set; } = 347201;
        public bool HoldExists { get; set; }
        public bool AmbiguousCreate { get; set; }
        public int CreateCount { get; private set; }
        public int ReplyCount { get; private set; }
        public HashSet<string> Calls { get; } = [];
        private readonly Guid conversation = Guid.NewGuid();
        private void Check(string operation, int context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(organizationId, context, operation);
            Calls.Add(operation);
        }
        private PatronSnapshot Patron(string barcode) => new(7001, barcode, "context@example.org", "Context", "Patron",
            1, "Adult", RegisteredBranchId, 3472, "Patron Home Library", CurrentPickup);
        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(Barcode, barcode);
            Assert.AreEqual("1234", pin);
            return Task.FromResult(Patron(barcode));
        }
        public Task<PatronSnapshot> RefreshAsync(string barcode, int context, CancellationToken token)
        {
            Check("refresh", context, token);
            Assert.AreEqual(Barcode, barcode);
            return Task.FromResult(Patron(barcode));
        }
        public Task<int?> GetPatronIdAsync(string barcode, int context, CancellationToken token)
        {
            Check("patron-id", context, token);
            return Task.FromResult<int?>(7001);
        }
        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron, int context, CancellationToken token)
        {
            Check("pickup-read", context, token);
            return Task.FromResult<IReadOnlyList<PickupBranch>>([
                new(RegisteredBranchId, "Context Branch"), new(RegisteredBranchId + 1, "Second Context Branch")]);
        }
        public Task UpdatePreferredPickupBranchAsync(string barcode, int pickup, int context, CancellationToken token)
        {
            Check("pickup-write", context, token);
            Assert.AreEqual(RegisteredBranchId + 1, pickup);
            CurrentPickup = pickup;
            return Task.CompletedTask;
        }
        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, int context, CancellationToken token)
        {
            Check("identifier", context, token);
            return Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.Found, 9001));
        }
        public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(string query, int context, CancellationToken token)
        {
            Check("patron-search", context, token);
            return Task.FromResult<IReadOnlyList<PatronSnapshot>>([Patron(Barcode)]);
        }
        public Task<StaffBibSearchResult> SearchBibsAsync(string mode, string query, string title, string author,
            int context, CancellationToken token)
        {
            Check("bib-search", context, token);
            return Task.FromResult(new StaffBibSearchResult([new(9001, "Context Title", null, null, "Book", "FOUND")], 1));
        }
        public Task<BibValidationResult> ValidateBibAsync(int bibId, int context, CancellationToken token)
        {
            Check("bib-detail", context, token);
            return Task.FromResult(new BibValidationResult(true, "Context Title"));
        }
        public Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(int bibId, int context, CancellationToken token)
        {
            Check("holdings", context, token);
            return Task.FromResult(new StaffBibHoldingsSummary(1, 0, 1, true, true));
        }
        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int context, CancellationToken token)
        {
            Check("holds", context, token);
            return Task.FromResult<IReadOnlyList<PolarisHoldSnapshot>>(HoldExists
                ? [new(8123, 9001, 1, "Active", CurrentPickup, Barcode)] : []);
        }
        public Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(string barcode, int context, CancellationToken token)
        {
            Check("checkouts", context, token);
            return Task.FromResult<IReadOnlyList<PolarisCheckoutSnapshot>>([]);
        }
        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken token)
        {
            Check("create", command.RequestingOrganizationId, token);
            CreateCount++;
            Assert.AreEqual(CurrentPickup, command.PickupBranchId);
            Assert.AreEqual(7001, command.PatronId);
            Assert.AreEqual(9001, command.BibId);
            Assert.AreEqual(99, command.WorkstationId);
            Assert.AreEqual(42, command.PolarisUserId);
            return Task.FromResult(AmbiguousCreate
                ? new HoldProviderResult(HoldProviderOutcome.Ambiguous, null, null, null, null, null, null, "test_ambiguous")
                : new HoldProviderResult(HoldProviderOutcome.ReplyRequired, conversation, null, "group", "qualifier", 3, 5, "reply_required"));
        }
        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken token)
        {
            Check("reply", command.RequestingOrganizationId, token);
            ReplyCount++;
            Assert.AreEqual(conversation, command.RequestGuid);
            HoldExists = true;
            return Task.FromResult(new HoldProviderResult(HoldProviderOutcome.FinalSuccess,
                conversation, 8123, "group", "qualifier", 2, 1, "success"));
        }
    }
}
