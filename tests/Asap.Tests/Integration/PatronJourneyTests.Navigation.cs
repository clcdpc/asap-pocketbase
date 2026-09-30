using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task RelatedRequestsAndWorkflowContextStayWithinAuthorizedScopeAndUseEffectiveSettings()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var libraryStaff = superAdmin with { Role = "staff", OrganizationId = 2 };
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var title = $"Related navigation {Guid.NewGuid():N}";
        const int otherLibraryId = 93456;
        long firstId = 0;
        long secondId = 0;
        long foreignId = 0;
        long copyId = 0;
        bool? originalSystemAutoPromote = null;
        bool? originalSystemTimeout = null;
        int? originalSystemDays = null;
        bool? originalSystemCopyTimeout = null;
        int? originalSystemCopyDays = null;
        bool? originalLibraryAutoPromote = null;
        bool? originalLibraryTimeout = null;
        bool? originalLibraryCopyTimeout = null;
        var addedLibraryWorkflow = false;
        try
        {
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                var system = await context.WorkflowSettings.SingleAsync(item => item.OrganizationId == 1);
                var library = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == 2);
                addedLibraryWorkflow = library is null;
                library ??= new WorkflowSettings { OrganizationId = 2 };
                if (addedLibraryWorkflow) context.WorkflowSettings.Add(library);
                originalSystemAutoPromote = system.AutoPromote;
                originalSystemTimeout = system.OutstandingTimeoutEnabled;
                originalSystemDays = system.OutstandingTimeoutDays;
                originalSystemCopyTimeout = system.AdditionalCopyTimeoutEnabled;
                originalSystemCopyDays = system.AdditionalCopyTimeoutDays;
                originalLibraryAutoPromote = library.AutoPromote;
                originalLibraryTimeout = library.OutstandingTimeoutEnabled;
                originalLibraryCopyTimeout = library.AdditionalCopyTimeoutEnabled;
                system.AutoPromote = true;
                system.OutstandingTimeoutEnabled = true;
                system.OutstandingTimeoutDays = 8;
                system.AdditionalCopyTimeoutEnabled = true;
                system.AdditionalCopyTimeoutDays = 12;
                library.AutoPromote = false;
                library.OutstandingTimeoutEnabled = false;
                library.AdditionalCopyTimeoutEnabled = false;
                context.Organizations.Add(new Organization
                {
                    Id = otherLibraryId, DisplayName = "Related foreign library", IsActive = true
                });
                var formatId = await context.MaterialFormats.Select(item => item.Id).FirstAsync();
                var now = timeProvider!.GetUtcNow().UtcDateTime;
                var first = new TitleRequest
                {
                    LibraryOrganizationId = 2, Barcode = "20000000009341", Title = title,
                    Identifier = "978-0000-09341", MaterialFormatId = formatId,
                    Status = "suggestion", CreatedUtc = now, UpdatedUtc = now
                };
                var second = new TitleRequest
                {
                    LibraryOrganizationId = 2, Barcode = "20000000009342", Title = title,
                    Identifier = "978000009341", MaterialFormatId = formatId,
                    Status = "pending_hold", CreatedUtc = now, UpdatedUtc = now
                };
                var foreign = new TitleRequest
                {
                    LibraryOrganizationId = otherLibraryId, Barcode = "20000000009343", Title = title,
                    MaterialFormatId = formatId, Status = "closed", CloseReason = "rejected",
                    CreatedUtc = now, UpdatedUtc = now
                };
                context.TitleRequests.AddRange(first, second, foreign);
                var copy = new AdditionalCopyRequest
                {
                    LibraryOrganizationId = 2, BibId = 9341, Title = title,
                    Status = "open", CreatedUtc = now, UpdatedUtc = now
                };
                context.AdditionalCopyRequests.Add(copy);
                await context.SaveChangesAsync();
                firstId = first.Id;
                secondId = second.Id;
                foreignId = foreign.Id;
                copyId = copy.Id;
            }

            var titles = factory.Services.GetRequiredService<TitleRequestViewService>();
            var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
            var scoped = await titles.ListAsync(libraryStaff, LibraryScope.All, CancellationToken.None);
            Assert.IsNotNull(scoped);
            var firstScoped = scoped.Items.Single(item => item.Id == firstId.ToString());
            Assert.AreEqual(1, firstScoped.RelatedRequests?.Count);
            Assert.AreEqual(2, firstScoped.RelatedRequests!.StatusAndLibraryCounts.Single().LibraryOrgId);
            Assert.IsFalse(firstScoped.WorkflowContext!.AutoPromote);
            Assert.IsFalse(firstScoped.WorkflowContext.OutstandingTimeoutEnabled);
            Assert.IsFalse(scoped.Items.Any(item => item.Id == foreignId.ToString()));
            Assert.IsNull(await titles.GetAsync(libraryStaff, foreignId.ToString(), CancellationToken.None));

            var detail = await titles.GetAsync(libraryStaff, firstId.ToString(), CancellationToken.None);
            Assert.AreEqual(1, detail?.RelatedRequests?.Count);
            var all = await titles.ListAsync(superAdmin, LibraryScope.All, CancellationToken.None);
            Assert.IsNotNull(all);
            Assert.AreEqual(2, all.Items.Single(item => item.Id == firstId.ToString()).RelatedRequests?.Count);
            var selected = await titles.ListAsync(superAdmin, LibraryScope.ForLibrary(2), CancellationToken.None);
            Assert.IsNotNull(selected);
            Assert.AreEqual(1, selected.Items.Single(item => item.Id == firstId.ToString()).RelatedRequests?.Count);
            Assert.AreEqual(1, (await titles.GetAsync(superAdmin, firstId.ToString(), CancellationToken.None, LibraryScope.ForLibrary(2)))?.RelatedRequests?.Count);
            Assert.AreEqual(2, (await titles.GetAsync(superAdmin, firstId.ToString(), CancellationToken.None, LibraryScope.All))?.RelatedRequests?.Count);
            Assert.IsNull(await titles.GetAsync(superAdmin, foreignId.ToString(), CancellationToken.None, LibraryScope.ForLibrary(2)));
            var foreignDto = all.Items.Single(item => item.Id == foreignId.ToString());
            Assert.IsTrue(foreignDto.WorkflowContext!.AutoPromote);
            Assert.IsTrue(foreignDto.WorkflowContext.OutstandingTimeoutEnabled);
            Assert.AreEqual(8, foreignDto.WorkflowContext.OutstandingTimeoutDays);
            Assert.AreEqual(2, all.Items.Single(item => item.Id == secondId.ToString()).RelatedRequests?.Count);

            var scopedCopies = await copies.ListAsync(libraryStaff, LibraryScope.All, "open", CancellationToken.None);
            Assert.IsNotNull(scopedCopies);
            Assert.IsFalse(scopedCopies.Items.Single(item => item.Id == copyId.ToString()).TimeoutContext!.Enabled);

            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                var organization = await context.Organizations.SingleAsync(item => item.Id == otherLibraryId);
                organization.IsActive = false;
                await context.SaveChangesAsync();
            }
            Assert.IsNull(await titles.ListAsync(superAdmin, LibraryScope.ForLibrary(otherLibraryId), CancellationToken.None));
            Assert.IsTrue((await titles.ListAsync(superAdmin, LibraryScope.All, CancellationToken.None))!
                .Items.Any(item => item.Id == foreignId.ToString()));
            Assert.IsNotNull(await titles.GetAsync(superAdmin, foreignId.ToString(), CancellationToken.None, LibraryScope.All));
        }
        finally
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            if (copyId > 0) await context.AdditionalCopyRequests.Where(item => item.Id == copyId).ExecuteDeleteAsync();
            var ids = new[] { firstId, secondId, foreignId }.Where(id => id > 0).ToArray();
            if (ids.Length > 0) await context.TitleRequests.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync();
            if (addedLibraryWorkflow)
            {
                await context.WorkflowSettings.Where(item => item.OrganizationId == 2).ExecuteDeleteAsync();
            }
            else
            {
                var library = await context.WorkflowSettings.SingleAsync(item => item.OrganizationId == 2);
                library.AutoPromote = originalLibraryAutoPromote;
                library.OutstandingTimeoutEnabled = originalLibraryTimeout;
                library.AdditionalCopyTimeoutEnabled = originalLibraryCopyTimeout;
            }
            var system = await context.WorkflowSettings.SingleAsync(item => item.OrganizationId == 1);
            system.AutoPromote = originalSystemAutoPromote;
            system.OutstandingTimeoutEnabled = originalSystemTimeout;
            system.OutstandingTimeoutDays = originalSystemDays;
            system.AdditionalCopyTimeoutEnabled = originalSystemCopyTimeout;
            system.AdditionalCopyTimeoutDays = originalSystemCopyDays;
            await context.SaveChangesAsync();
            await context.Organizations.Where(item => item.Id == otherLibraryId).ExecuteDeleteAsync();
        }
    }
}
