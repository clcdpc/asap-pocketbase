using System.Globalization;
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
    public async Task StaffQueueAndDetailQuickCreateBrowserUsesFreshContextAndExplicitAllScope()
    {
        const int library = 39401;
        const int home = 39402;
        const int registration = 394021;
        const int pickup = 394031;
        const string barcode = "workflow-live-patron";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new DeterministicTestingPatronProvider();
        var patron = new PatronSnapshot(394101, barcode, "fresh-browser@example.org", "Fresh", "Browser Patron", 1, "Adult",
            registration, home, "Recorded home library", null);
        provider.AddPatron(patron, [new(pickup, "Live eligible pickup")], library);
        var excluded = patron with { Barcode = barcode + "-ineligible", PatronId = 394102, PatronCodeId = 999 };
        provider.AddPatron(excluded, [new(pickup, "Live eligible pickup")], library);
        provider.SetPatronSearch("Excluded Patron", library, [excluded]);
        provider.AllowPickupUpdate(barcode, library, pickup);
        provider.SetIdentifierResult("BROWSER-MATCH", library, new(IdentifierLookupOutcome.DefinitiveNotFound));
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@org,N'Browser servicing library',2,1,1),(@home,N'Recorded home library',2,1,1),
                (@registration,N'Fresh registered branch',3,@home,0),(@pickup,N'Live eligible pickup',3,@home,0);
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId],[AllowAnyRegisteredCardLogin],[PatronCodeEligibilityEnabled],[PatronCodeEligibilityMessage],[UpdatedUtc])
            VALUES (@org,1,1,N'Please contact the library.',SYSUTCDATETIME());
            INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@org);
            INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId],[PatronCodeId]) VALUES (@org,1);
            """, ("@org", library), ("@home", home), ("@registration", registration), ("@pickup", pickup));
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        scopedFactory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = scopedFactory.CreateClient();
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long sourceId = 0;
        long claimantId = 0;
        var title = "Quick Create Browser " + Guid.NewGuid().ToString("N");
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var format = await setup.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
                var claimant = new StaffUser { OrganizationId = library, Role = "staff", IsActive = true,
                    EntraTenantId = actor.EntraTenantId, UserPrincipalName = "workflow-claimant@example.org",
                    NormalizedUserPrincipalName = "WORKFLOW-CLAIMANT@EXAMPLE.ORG", DisplayName = "Browser claimant" };
                setup.StaffUsers.Add(claimant);
                var source = new TitleRequest { LibraryOrganizationId = library, Barcode = barcode, PatronIdSnapshot = 394199,
                    PatronOrganizationId = registration, NameFirst = "Old", NameLast = "Snapshot", Email = "old-snapshot@example.org",
                    PreferredPickupBranchId = 101, PreferredPickupBranchName = "Historical ASAP pickup", Title = "Quick-create source",
                    Author = "Old author", Identifier = "BROWSER-MATCH", MaterialFormatId = format.Id, Status = "suggestion",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                setup.TitleRequests.Add(source);
                await setup.SaveChangesAsync();
                sourceId = source.Id; claimantId = claimant.Id;
                setup.FormatAutoClaimRules.Add(new FormatAutoClaimRule { LibraryOrganizationId = library,
                    MaterialFormatId = format.Id, StaffUserId = claimant.Id, IsActive = true,
                    CreatedUtc = timeProvider.GetUtcNow().UtcDateTime });
                await setup.SaveChangesAsync();
            }
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"staff-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "staff-workflow-completion.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                sourceId.ToString(CultureInfo.InvariantCulture), library.ToString(CultureInfo.InvariantCulture), barcode,
                pickup.ToString(CultureInfo.InvariantCulture), title);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            await using var verify = await contexts.CreateDbContextAsync();
            var created = await verify.TitleRequests.SingleAsync(item => item.LibraryOrganizationId == library && item.Title == title);
            Assert.AreEqual(patron.PatronId, created.PatronIdSnapshot);
            Assert.AreEqual(patron.Email, created.Email);
            Assert.AreEqual(pickup, created.PreferredPickupBranchId);
            Assert.AreEqual(registration, created.PatronOrganizationId);
            Assert.AreEqual(library, created.StaffLibraryOrganizationIdCreatedBy);
            Assert.AreEqual(claimantId, created.ClaimedByStaffUserId);
            Assert.AreEqual("automatic_format_rule", created.ClaimType);
            Assert.AreEqual("not_found", created.IsbnCheckStatus);
            Assert.IsTrue(await (from link in verify.TitleRequestWorkflowTags join tag in verify.WorkflowTags on link.WorkflowTagId equals tag.Id
                where link.TitleRequestId == created.Id && tag.Code == "duplicate_suggestion" select link).AnyAsync());
            Assert.AreEqual(1, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PickupUpdate));
            Assert.IsFalse(await verify.EmailOutbox.AnyAsync(item => item.OrganizationId == library));
            Assert.IsTrue(provider.Calls.Where(call => call.Operation == TestingPolarisOperation.Refresh).All(call => call.OrganizationId == library));
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            foreach (var id in await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library).Select(item => item.Id).ToArrayAsync())
            {
                await DeleteRequestAsync(id);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId]=@org;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId]=@org;
                DELETE FROM [asap].[StaffUser] WHERE [Id]=@claimant;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@registration,@pickup);
                """, ("@org", library), ("@claimant", claimantId), ("@registration", registration), ("@pickup", pickup));
            await CleanupScalarSettingsTestDataAsync(library, "", "");
            await ExecuteNonQueryAsync("DELETE FROM [asap].[Organization] WHERE [Id]=@home;", ("@home", home));
        }
    }
}
