using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task AdditionalCopyCountsMatchAuthorizedQueuesIncludingHistoricalTasks()
    {
        const string path = "/api/asap/staff/additional-copies";
        using var startup = factory!.CreateClient();
        var super = await ReadConfiguredSuperAdminAsync();
        using var client = await CreateAuthenticatedStaffClientAsync(factory, super);
        var allBefore = await client.GetFromJsonAsync<AdditionalCopyCounts>($"{path}/counts?scope=all");
        Assert.IsNotNull(allBefore);
        await ExecuteNonQueryAsync("""
            INSERT INTO asap.Organization (Id,DisplayName,OrganizationCodeId,ParentOrganizationId,IsActive)
            VALUES (52701,N'Count library',2,1,1),(52702,N'Empty count library',2,1,1),(52703,N'Inactive count library',2,1,0);
            INSERT INTO asap.AdditionalCopyRequest (LibraryOrganizationId,BibId,Title,Status,CreatedUtc,UpdatedUtc,ClosedUtc)
            VALUES (52701,123,N'First',N'OPEN',GETUTCDATE(),GETUTCDATE(),NULL),
                   (52701,124,N'Second',N'OPEN',GETUTCDATE(),GETUTCDATE(),NULL),
                   (52701,125,N'Third',N'closed',GETUTCDATE(),GETUTCDATE(),GETUTCDATE()),
                   (52703,126,N'Historical',N'closed',GETUTCDATE(),GETUTCDATE(),GETUTCDATE());
            """);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        var versions = db.AdditionalCopyRequests.Where(item => item.LibraryOrganizationId >= 52701 && item.LibraryOrganizationId <= 52703)
            .OrderBy(item => item.Id).Select(item => item.RowVersion);
        var before = await versions.ToArrayAsync();
        try
        {
            async Task Check(HttpClient reader, string requested, AdditionalCopyCounts expected)
            {
                Assert.AreEqual(expected, await reader.GetFromJsonAsync<AdditionalCopyCounts>($"{path}/counts?scope={requested}"));
                foreach (var (status, total) in new[] { ("open", expected.OpenCount), ("closed", expected.ClosedCount) })
                {
                    var list = await reader.GetFromJsonAsync<JsonElement>($"{path}?scope={requested}&status={status}");
                    Assert.AreEqual(expected.Scope, list.GetProperty("scope").GetString());
                    Assert.AreEqual(total, list.GetProperty("items").GetArrayLength());
                }
            }
            var local = new AdditionalCopyCounts("52701", 2, 1);
            await Check(client, "52701", local);
            await Check(client, "52702", new AdditionalCopyCounts("52702", 0, 0));
            await Check(client, "all", new AdditionalCopyCounts("all", allBefore.OpenCount + 2, allBefore.ClosedCount + 2));
            foreach (var scope in new[] { "invalid", "system", "1", "52703", "99999999" })
            {
                using var counts = await client.GetAsync($"{path}/counts?scope={scope}");
                using var list = await client.GetAsync($"{path}?scope={scope}");
                Assert.AreEqual(HttpStatusCode.BadRequest, counts.StatusCode);
                Assert.AreEqual(list.StatusCode, counts.StatusCode);
            }
            foreach (var role in new[] { StaffRole.Staff, StaffRole.Admin })
            {
                var user = await CreateCorrectiveStaffAsync(super, role, 52701);
                try
                {
                    using var scoped = await CreateAuthenticatedStaffClientAsync(factory, await ReadCorrectiveStaffAsync(user));
                    await Check(scoped, "all", local);
                    await Check(scoped, "52702", local);
                    await Check(scoped, "system", local);
                }
                finally
                {
                    await DeactivateCorrectiveStaffAsync(user.Id);
                }
            }
            CollectionAssert.AreEqual(before.Select(Convert.ToHexString).ToArray(),
                (await versions.ToArrayAsync()).Select(Convert.ToHexString).ToArray());
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM asap.AdditionalCopyRequest WHERE LibraryOrganizationId BETWEEN 52701 AND 52703;
                UPDATE asap.Organization SET IsActive=0 WHERE Id BETWEEN 52701 AND 52703;
                """);
        }
    }
}
