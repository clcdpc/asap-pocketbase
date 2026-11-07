using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
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
        Dictionary<long, string> titleVersions;
        Dictionary<long, string> copyVersions;
        Dictionary<long, string> emailVersions;
        TitleRequest[] appearanceTitles = [];
        AdditionalCopyRequest[] appearanceCopies = [];
        List<EmailOutbox> appearanceEmails = [];
        var progressCreated = false;
        try
        {
            await using (var setup = await UnifiedStaffContextAsync())
            {
                setup.Organizations.Add(new Organization { Id = library, DisplayName = "Remediation fixture library", OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true });
                await setup.SaveChangesAsync();
                var format = await setup.MaterialFormats.FirstAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
                setup.TitleRequests.AddRange(Enumerable.Range(0, 3).Select(index => new TitleRequest
                {
                    LibraryOrganizationId = library, Barcode = $"remediation-{index}", Title = $"Remediation title {index}",
                    Author = $"Fixture author {index}", MaterialFormatId = format.Id, Status = "suggestion",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-index), UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                }));
                var now = timeProvider!.GetUtcNow().UtcDateTime;
                appearanceTitles = new[] { "outstanding_purchase", "pending_hold", "hold_placed", "closed" }.Select((status, index) => new TitleRequest
                {
                    LibraryOrganizationId = library, Barcode = $"appearance-{index}", Title = $"Remediation {status} title",
                    Author = "Synthetic appearance author", MaterialFormatId = format.Id, Status = status,
                    CloseReason = status == "closed" ? "manual" : null, BibId = 45600 + index,
                    CreatedUtc = now.AddMinutes(-10 - index), UpdatedUtc = now
                }).ToArray();
                setup.TitleRequests.AddRange(appearanceTitles);
                appearanceCopies = new[] { "open", "closed" }.Select((status, index) => new AdditionalCopyRequest
                {
                    LibraryOrganizationId = library, BibId = 45610 + index, Title = $"Remediation {status} additional copy with a long synthetic title",
                    Author = index == 0 ? "Synthetic appearance author" : null, Status = status,
                    Notes = "Accepted additional-copy notes.", CreatedByDisplayName = "Synthetic appearance staff",
                    CreatedUtc = now.AddMinutes(-20 - index), UpdatedUtc = now, ClosedUtc = status == "closed" ? now : null
                }).ToArray();
                setup.AdditionalCopyRequests.AddRange(appearanceCopies);
                setup.QueueProgress.Add(new QueueProgress { QueueName = QueueNames.IdentifierProcessing, ScopeOrganizationId = library,
                    LastOutcomeCode = "cycle_complete", UpdatedUtc = now });
                await setup.SaveChangesAsync();
                progressCreated = true;
                var title = await setup.TitleRequests.Where(item => item.LibraryOrganizationId == library && item.Status == "suggestion").OrderBy(item => item.Id).FirstAsync();
                foreach (var status in new[] { "pending", "sent", "failed", "suppressed" })
                {
                    appearanceEmails.Add(new EmailOutbox
                    {
                        OrganizationId = library, TitleRequestId = title.Id, MessageKind = "confirmation",
                        DeliveryClass = "business_event", Status = status, ToAddress = "appearance@example.org",
                        FromAddress = "asap@example.org",
                        Subject = "Synthetic appearance evidence", BodyText = "No provider delivery is performed.",
                        CreatedUtc = now, NextAttemptUtc = status == "pending" ? now.AddYears(10) : null,
                        SentUtc = status == "sent" ? now : null, SuppressedUtc = status == "suppressed" ? now : null,
                        SuppressionReason = status == "suppressed" ? "recipient_domain_not_allowed" : null,
                        LastErrorCode = status == "failed" ? "mail_not_configured" : null
                    });
                }
                setup.EmailOutbox.AddRange(appearanceEmails);
                await setup.SaveChangesAsync();
                titleVersions = (await setup.TitleRequests.AsNoTracking().Where(item => item.LibraryOrganizationId == library).ToArrayAsync())
                    .ToDictionary(item => item.Id, item => Convert.ToBase64String(item.RowVersion));
                copyVersions = (await setup.AdditionalCopyRequests.AsNoTracking().Where(item => item.LibraryOrganizationId == library).ToArrayAsync())
                    .ToDictionary(item => item.Id, item => Convert.ToBase64String(item.RowVersion));
                emailVersions = (await setup.EmailOutbox.AsNoTracking().Where(item => item.OrganizationId == library).ToArrayAsync())
                    .ToDictionary(item => item.Id, item => Convert.ToBase64String(item.RowVersion));
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
            var titles = await verify.TitleRequests.AsNoTracking().Where(item => item.LibraryOrganizationId == library).ToArrayAsync();
            Assert.AreEqual(titleVersions.Count, titles.Length);
            foreach (var title in titles)
            {
                Assert.AreEqual(titleVersions[title.Id], Convert.ToBase64String(title.RowVersion), "Appearance and discarded drafts never update title requests.");
            }
            var copies = await verify.AdditionalCopyRequests.AsNoTracking().Where(item => item.LibraryOrganizationId == library).ToArrayAsync();
            Assert.AreEqual(copyVersions.Count, copies.Length);
            foreach (var copy in copies)
            {
                Assert.AreEqual(copyVersions[copy.Id], Convert.ToBase64String(copy.RowVersion), "Appearance never updates additional copies.");
            }
            var messages = await verify.EmailOutbox.AsNoTracking().Where(item => item.OrganizationId == library).ToArrayAsync();
            Assert.AreEqual(emailVersions.Count, messages.Length);
            foreach (var message in messages)
            {
                Assert.AreEqual(emailVersions[message.Id], Convert.ToBase64String(message.RowVersion), "Browsing never changes email evidence.");
            }
        }
        finally
        {
            await using var cleanup = await UnifiedStaffContextAsync();
            var emailIds = appearanceEmails.Select(item => item.Id).Where(id => id > 0).ToArray();
            await DeleteOwnedEmailFixtureAsync(cleanup, cleanup.EmailOutbox.Where(item => item.OrganizationId == library && emailIds.Contains(item.Id)));
            var copyIds = appearanceCopies.Select(item => item.Id).Where(id => id > 0).ToArray();
            await cleanup.AdditionalCopyRequests.Where(item => item.LibraryOrganizationId == library && copyIds.Contains(item.Id)).ExecuteDeleteAsync();
            var titleIds = appearanceTitles.Select(item => item.Id).Where(id => id > 0).ToArray();
            await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library && titleIds.Contains(item.Id)).ExecuteDeleteAsync();
            if (progressCreated)
            {
                await cleanup.QueueProgress.Where(item => item.ScopeOrganizationId == library && item.QueueName == QueueNames.IdentifierProcessing).ExecuteDeleteAsync();
            }
        }
    }
}
