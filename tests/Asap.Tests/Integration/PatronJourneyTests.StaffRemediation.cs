using System.Globalization;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task StaffProvisionedAccountsSaveAndDeactivateOverHttpWithoutDisplayName(bool previouslySignedIn)
    {
        factory!.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        factory.ClientOptions.AllowAutoRedirect = false;
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var email = $"provisioned-{Guid.NewGuid():N}@example.org";
        using var created = await client.PostAsJsonAsync("/api/asap/staff/users", new { email, role = "staff", organizationId = 2 });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        using var creation = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = long.Parse(creation.RootElement.GetProperty("user").GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
        if (previouslySignedIn)
        {
            await using var setup = await UnifiedStaffContextAsync();
            var user = await setup.StaffUsers.SingleAsync(item => item.Id == id);
            user.EntraTenantId = actor.EntraTenantId;
            user.EntraObjectId = Guid.NewGuid();
            user.LastLoginUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            await setup.SaveChangesAsync();
        }
        var before = await ReadUnifiedStaffAsync(id);
        Assert.IsNull(before.DisplayName);
        Assert.AreEqual(previouslySignedIn, before.LastLoginUtc.HasValue);
        const int destination = 46402;
        await using (var setup = await UnifiedStaffContextAsync())
        {
            if (!await setup.Organizations.AnyAsync(item => item.Id == destination))
            {
                setup.Organizations.Add(new Organization { Id = destination, DisplayName = "Provisioned account destination", OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true });
                await setup.SaveChangesAsync();
            }
        }
        var input = new StaffUpdateInput(StaffVersion.Encode(before.RowVersion), email, "", "admin", destination);
        using (var unprotected = factory.CreateClient())
        {
            AddTestingStaffHeaders(unprotected, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            using var rejectedSave = await unprotected.PutAsJsonAsync($"/api/asap/staff/users/{id}", input);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejectedSave.StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/users/{id}")
            {
                Content = JsonContent.Create(new StaffDeactivateInput(StaffVersion.Encode(before.RowVersion)))
            };
            using var rejectedDeactivation = await unprotected.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejectedDeactivation.StatusCode);
            await AssertUnifiedStaffUnchangedAsync(before, 1);
        }
        using var saved = await client.PutAsJsonAsync($"/api/asap/staff/users/{id}", input);
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var accepted = await ReadUnifiedStaffAsync(id);
        Assert.IsNull(accepted.DisplayName);
        Assert.AreEqual("admin", accepted.Role);
        Assert.AreEqual(destination, accepted.OrganizationId);
        Assert.AreEqual(email, accepted.NotificationEmail);
        Assert.AreEqual(before.EntraObjectId, accepted.EntraObjectId);
        Assert.AreEqual(before.LastLoginUtc, accepted.LastLoginUtc);
        using var stale = await client.PutAsJsonAsync($"/api/asap/staff/users/{id}", input with { DisplayName = "Must not persist" });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertUnifiedStaffUnchangedAsync(accepted, 2);
        using var deactivate = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/users/{id}")
        {
            Content = JsonContent.Create(new StaffDeactivateInput(StaffVersion.Encode(accepted.RowVersion)))
        };
        using var deactivated = await client.SendAsync(deactivate);
        Assert.AreEqual(HttpStatusCode.OK, deactivated.StatusCode, await deactivated.Content.ReadAsStringAsync());
        var inactive = await ReadUnifiedStaffAsync(id);
        Assert.IsFalse(inactive.IsActive);
        Assert.IsNull(inactive.DisplayName);
        Assert.AreEqual(before.LastLoginUtc, inactive.LastLoginUtc);
        CollectionAssert.AreEqual(new[] { "staff_created", "staff_lifecycle_updated", "staff_deactivated" }, await UnifiedStaffAuditsAsync(id));
        using var replay = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/users/{id}") { Content = JsonContent.Create(new StaffDeactivateInput(StaffVersion.Encode(accepted.RowVersion))) };
        using var replayed = await client.SendAsync(replay);
        Assert.AreEqual(HttpStatusCode.Conflict, replayed.StatusCode);
        await AssertUnifiedStaffUnchangedAsync(inactive, 3);
    }

    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task StaffRemediationBrowserVerifiesKeyboardLabelsThemeAndProvisionedAccountMutations()
    {
        const int library = 46401;
        factory!.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await using (var setup = await UnifiedStaffContextAsync())
        {
            setup.Organizations.Add(new Organization { Id = library, DisplayName = "Remediation fixture library", OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true });
            var format = await setup.MaterialFormats.FirstAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
            setup.TitleRequests.AddRange(Enumerable.Range(0, 3).Select(index => new TitleRequest
            {
                LibraryOrganizationId = library, Barcode = $"remediation-{index}", Title = $"Remediation title {index}",
                Author = $"Fixture author {index}", MaterialFormatId = format.Id, Status = "suggestion",
                CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-index), UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
            }));
            await setup.SaveChangesAsync();
        }
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "browser", $"staff-remediation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "staff-remediation.cjs",
            client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts, actor.Id.ToString(CultureInfo.InvariantCulture),
            actor.EntraTenantId.ToString(), actor.AuthenticationEmail, library.ToString(CultureInfo.InvariantCulture));
        Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        await using var verify = await UnifiedStaffContextAsync();
        var users = await verify.StaffUsers.AsNoTracking().Where(item => item.OrganizationId == library).ToArrayAsync();
        Assert.AreEqual(2, users.Length);
        Assert.IsTrue(users.All(item => !item.IsActive && item.LastLoginUtc == null && item.EntraTenantId == null && item.EntraObjectId == null));
        Assert.IsTrue(users.All(item => item.NotificationEmail == item.UserPrincipalName));
        Assert.AreEqual(2, await verify.AdministrativeAudits.CountAsync(item => item.OrganizationId == library && item.Action == "staff_deactivated"));
    }
}
