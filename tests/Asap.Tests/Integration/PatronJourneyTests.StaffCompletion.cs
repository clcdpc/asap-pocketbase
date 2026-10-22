using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffLookupCapsAndDeduplicatesCandidatesAndNeverFallsBackFromExplicitOrForbiddenIdentity()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new DeterministicTestingPatronProvider();
        var candidates = Enumerable.Range(1, 12).Select(index => new PatronSnapshot(393000 + index,
            $"candidate-{index}", "current@example.org", "Fresh", $"Candidate {index}", 1, "Adult", 101, 2,
            "Main library", 101)).ToArray();
        foreach (var candidate in candidates)
        {
            provider.AddPatron(candidate, [new(101, "Current pickup")], 2);
        }
        provider.SetPatronSearch("Candidate Name", 2, [candidates[0], candidates[0] with { Barcode = "CANDIDATE-1" }, .. candidates.Skip(1)]);
        provider.SetFailure(new(TestingPolarisOperation.Refresh, 2, "explicit-miss"),
            new PolarisOperationalException("polaris_patron_not_found", "Controlled definitive miss"));
        provider.AddPatron(candidates[0] with { Barcode = "forbidden-shaped", HomeLibraryOrganizationId = 393999 }, [], 2);
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        using var staff = scopedFactory.CreateClient();
        AddTestingStaffHeaders(staff, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        staff.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(staff));
        using var response = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { query = "Candidate Name", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("multiple_matches", body.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(9, body.RootElement.GetProperty("matches").GetArrayLength());
        Assert.AreEqual(9, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.Refresh));
        Assert.IsFalse(provider.Calls.Any(call => call.Key == candidates[9].Barcode), "Only the first ten raw candidates may be refreshed.");
        using var selected = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { barcode = candidates[1].Barcode, libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        using var selection = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        Assert.AreEqual(candidates[1].Barcode, selection.RootElement.GetProperty("patron").GetProperty("patron").GetProperty("barcode").GetString());
        using var missing = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { barcode = "explicit-miss", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var forbidden = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { query = "forbidden-shaped", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.AreEqual(1, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PatronSearch),
            "Neither an explicit candidate miss nor a definitive forbidden refresh may become a name search.");
    }

    [TestMethod]
    public async Task StaffCreationKeepsClaimDuplicateQueueAndOriginalConfirmationAcrossIdentifierOutcomes()
    {
        const int library = 39101;
        const int home = 39102;
        const int registration = 391021;
        const int pickup = 391031;
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new DeterministicTestingPatronProvider();
        var patrons = Enumerable.Range(1, 5).Select(index => new PatronSnapshot(391100 + index,
            $"completion-card-{index}", index == 4 ? null : $"fresh-{index}@example.org", "Current", $"Patron {index}",
            1, "Adult", registration, home, "Home library", pickup)).ToArray();
        foreach (var patron in patrons)
        {
            provider.AddPatron(patron, [new(pickup, "Live remote pickup")], library);
        }
        provider.SetIdentifierResult("MATCH", library, new(IdentifierLookupOutcome.Found, 391901,
            CatalogTitle: "Enriched catalog title", CatalogAuthor: "Enriched catalog author"));
        provider.SetIdentifierResult("MISSING", library, new(IdentifierLookupOutcome.DefinitiveNotFound));
        provider.SetIdentifierResult("RETRY", library, new(IdentifierLookupOutcome.TransientFailure,
            ErrorCode: "controlled_timeout"));
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@library,N'Servicing library',2,1,1),(@home,N'Home library',2,1,1),
                   (@registration,N'Registered branch',3,@home,0),(@pickup,N'Live remote pickup',3,@home,0);
            INSERT INTO [asap].[WorkflowSettings]
                ([OrganizationId],[AllowAnyRegisteredCardLogin],[SuggestionLimit],[PatronCodeEligibilityEnabled],[UpdatedUtc])
            VALUES (@library,1,1,1,SYSUTCDATETIME());
            INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@library);
            INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId],[PatronCodeId]) VALUES (@library,1);
            INSERT INTO [asap].[EmailSettings] ([OrganizationId],[FromAddress],[UpdatedUtc])
            VALUES (@library,N'staff@example.org',SYSUTCDATETIME());
            INSERT INTO [asap].[EmailTemplate] ([OrganizationId],[TemplateKey],[SourceTemplateId],[SubjectTemplate],[BodyTemplate],[IsHidden],[IsCustom],[SortOrder])
            SELECT @library,N'suggestion_submitted',[Id],N'Original {{title}}',N'{{title}}|{{author}}|{{barcode}}',0,0,10
            FROM [asap].[EmailTemplate] WHERE [OrganizationId]=1 AND [TemplateKey]=N'suggestion_submitted';
            """, ("@library", library), ("@home", home), ("@registration", registration), ("@pickup", pickup));
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var requestIds = new List<long>();
        long claimantId = 0;
        long foreignRequestId = 0;
        long existingQuotaRequestId = 0;
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var claimant = new StaffUser { OrganizationId = library, Role = "staff", IsActive = true,
                    EntraTenantId = actor.EntraTenantId, UserPrincipalName = "completion-staff@example.org",
                    NormalizedUserPrincipalName = "COMPLETION-STAFF@EXAMPLE.ORG", DisplayName = "Completion claimant" };
                setup.StaffUsers.Add(claimant);
                await setup.SaveChangesAsync();
                claimantId = claimant.Id;
                var formatId = await setup.MaterialFormats.Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
                    .Select(item => item.Id).SingleAsync();
                setup.FormatAutoClaimRules.Add(new FormatAutoClaimRule { LibraryOrganizationId = library,
                    MaterialFormatId = formatId, StaffUserId = claimant.Id, IsActive = true,
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime });
                var foreign = new TitleRequest { LibraryOrganizationId = home, PatronOrganizationId = registration,
                    Barcode = patrons[0].Barcode, PatronIdSnapshot = patrons[0].PatronId, Title = "Foreign queue original",
                    Identifier = "MATCH", MaterialFormatId = formatId, Status = "suggestion",
                    CreatedUtc = timeProvider.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                setup.TitleRequests.Add(foreign);
                var existingQuota = new TitleRequest { LibraryOrganizationId = library, PatronOrganizationId = registration,
                    Barcode = patrons[0].Barcode, PatronIdSnapshot = patrons[0].PatronId, Title = "Already Used Weekly Quota",
                    MaterialFormatId = formatId, Status = "suggestion", CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
                    UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                setup.TitleRequests.Add(existingQuota);
                await setup.SaveChangesAsync();
                foreignRequestId = foreign.Id;
                existingQuotaRequestId = existingQuota.Id;
            }
            using var staff = scopedFactory.CreateClient();
            AddTestingStaffHeaders(staff, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            staff.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(staff));
            var identifiers = new string?[] { "MATCH", "MATCH", "MISSING", null, "RETRY" };
            for (var index = 0; index < patrons.Length; index++)
            {
                using var result = await staff.PostAsJsonAsync("/api/asap/staff/suggestions", new
                {
                    libraryOrgId = library, barcode = patrons[index].Barcode, format = "book",
                    title = $"Original Submission {index}", author = "Original Author", identifier = identifiers[index],
                    publication = "Coming soon", preferredPickupBranchId = pickup,
                    currentPreferredPickupBranchIdAtLoad = pickup, autohold = true,
                    emailPatronConfirmation = index is 0 or 3, customFields = new Dictionary<string, string?>()
                });
                Assert.AreEqual(HttpStatusCode.Created, result.StatusCode, await result.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
                var id = body.RootElement.GetProperty("id");
                Assert.AreEqual(JsonValueKind.String, id.ValueKind);
                requestIds.Add(long.Parse(id.GetString()!, System.Globalization.CultureInfo.InvariantCulture));
                Assert.AreEqual(index == 0 ? "queued" : index == 3 ? "suppressed" : "not_requested",
                    body.RootElement.GetProperty("notificationStatus").GetString());
            }
            using var listed = await staff.GetAsync($"/api/asap/staff/title-requests?scope={library}");
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            using var queue = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var items = queue.RootElement.GetProperty("items").EnumerateArray().ToArray();
            foreach (var (id, index) in requestIds.Select((id, index) => (id, index)))
            {
                var visible = items.Single(item => item.GetProperty("id").GetString() == id.ToString());
                Assert.AreEqual(library, visible.GetProperty("libraryOrgId").GetInt32());
                Assert.AreEqual(claimantId.ToString(), visible.GetProperty("claimedByStaffUserId").GetString());
                Assert.AreEqual("automatic_format_rule", visible.GetProperty("claimType").GetString());
                Assert.AreEqual(index == 1, visible.GetProperty("workflowTags").EnumerateArray()
                    .Any(tag => tag.GetString() == "Duplicate suggestion"));
            }
            await using var verify = await contexts.CreateDbContextAsync();
            var rows = await verify.TitleRequests.Where(item => requestIds.Contains(item.Id)).OrderBy(item => item.Id).ToArrayAsync();
            CollectionAssert.AreEqual(new[] { "found", "found", "not_found", "skipped_no_isbn", "pending" },
                rows.Select(item => item.IsbnCheckStatus).ToArray());
            Assert.AreEqual(1, rows[4].IsbnCheckRetryCount);
            Assert.AreEqual("controlled_timeout", rows[4].IsbnCheckLastErrorCode);
            Assert.AreEqual("Enriched catalog title (Original Submission 0)", rows[0].Title);
            Assert.AreEqual("Enriched catalog author (Original Author)", rows[0].Author);
            Assert.AreEqual(391901, rows[0].BibId);
            Assert.IsTrue(rows.All(item => item.LibraryOrganizationId == library &&
                item.StaffLibraryOrganizationIdCreatedBy == library && item.PatronOrganizationId == registration &&
                item.PreferredPickupBranchId == pickup && item.ClaimedByStaffUserId == claimantId));
            Assert.AreEqual(patrons[0].PatronId, rows[0].PatronIdSnapshot);
            Assert.AreEqual(patrons[0].Email, rows[0].Email);
            foreach (var id in requestIds)
            {
                var created = await verify.TitleRequestEvents.SingleAsync(item => item.TitleRequestId == id && item.EventType == "created");
                Assert.AreEqual("staff", created.ActorType);
                Assert.AreEqual(actor.Id, created.StaffUserId);
            }
            var mail = await verify.EmailOutbox.Where(item => item.OrganizationId == library).OrderBy(item => item.Id).ToArrayAsync();
            Assert.AreEqual(2, mail.Length, "Opting out must create no submission email.");
            Assert.AreEqual("Original Original Submission 0", mail[0].Subject);
            Assert.AreEqual("Original Submission 0|Original Author|completion-card-1", mail[0].BodyText);
            Assert.AreEqual(patrons[0].Email, mail[0].ToAddress);
            Assert.AreEqual("pending", mail[0].Status);
            Assert.AreEqual(1, dispatcher!.EnqueuedIds.Count(id => id == mail[0].Id));
            Assert.AreEqual("suppressed", mail[1].Status);
            Assert.AreEqual("recipient_missing", mail[1].SuppressionReason);
            Assert.IsFalse(provider.Calls.Any(call => call.Operation == TestingPolarisOperation.PickupUpdate));
            using var limitedStaff = scopedFactory.CreateClient();
            AddTestingStaffHeaders(limitedStaff, claimantId, actor.EntraTenantId, "completion-staff@example.org");
            limitedStaff.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(limitedStaff));
            var callsBeforeForgery = provider.Calls.Count;
            using var forged = await limitedStaff.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = home, barcode = patrons[0].Barcode, format = "book", title = "Forged scope cannot create",
                author = "Author", preferredPickupBranchId = pickup, currentPreferredPickupBranchIdAtLoad = pickup,
                emailPatronConfirmation = false, customFields = new Dictionary<string, string?>()
            });
            Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);
            Assert.AreEqual(callsBeforeForgery, provider.Calls.Count, "Forged library scope is rejected before provider work.");

            // This campaign preserves #335: staff bypasses the count limit, not patron-code eligibility.
            provider.AddPatron(patrons[0] with { PatronCodeId = 999 }, [new(pickup, "Live remote pickup")], library);
            using var excluded = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { barcode = patrons[0].Barcode, libraryOrgId = library });
            Assert.AreEqual(HttpStatusCode.Forbidden, excluded.StatusCode);
            await ExecuteNonQueryAsync("UPDATE [asap].[WorkflowSettings] SET [AllowAnyRegisteredCardLogin]=0 WHERE [OrganizationId]=@org;", ("@org", library));
            using var wrongHome = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new { barcode = patrons[1].Barcode, libraryOrgId = library });
            Assert.AreEqual(HttpStatusCode.Forbidden, wrongHome.StatusCode);
            using var wrongHomeCreate = await staff.PostAsJsonAsync("/api/asap/staff/suggestions", new
            {
                libraryOrgId = library, barcode = patrons[1].Barcode, format = "book", title = "Forbidden home cannot create",
                author = "Author", preferredPickupBranchId = pickup, currentPreferredPickupBranchIdAtLoad = pickup,
                emailPatronConfirmation = false, customFields = new Dictionary<string, string?>()
            });
            Assert.AreEqual(HttpStatusCode.Forbidden, wrongHomeCreate.StatusCode);
            Assert.AreEqual(6, await verify.TitleRequests.CountAsync(item => item.LibraryOrganizationId == library));
        }
        finally
        {
            foreach (var id in requestIds)
            {
                await DeleteRequestAsync(id);
            }
            if (foreignRequestId > 0)
            {
                await DeleteRequestAsync(foreignRequestId);
            }
            if (existingQuotaRequestId > 0)
            {
                await DeleteRequestAsync(existingQuotaRequestId);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId]=@org;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId]=@org;
                DELETE FROM [asap].[StaffUser] WHERE [Id]=@claimant;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@registration,@pickup);
                """, ("@org", library), ("@claimant", claimantId), ("@registration", registration), ("@pickup", pickup));
            await CleanupScalarSettingsTestDataAsync(library, "", "");
            await ExecuteNonQueryAsync("DELETE FROM [asap].[Organization] WHERE [Id]=@home;", ("@home", home));
        }
    }
}
