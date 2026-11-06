using System.Globalization;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task RequestConfirmationBrowserPreservesChoicesCancellationAndAuthorizedRetries()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var purchase = await SeedPolarisQueueAsync(autoHold: true);
        await using var rejection = await SeedPolarisQueueAsync(autoHold: true);
        await using var savedBib = await SeedPolarisQueueAsync(autoHold: true);
        await using var optout = await SeedPolarisQueueAsync();
        await using var untouched = await SeedPolarisQueueAsync(autoHold: true);
        var fixtures = new[] { purchase, rejection, savedBib, optout, untouched };
        for (var index = 0; index < fixtures.Length; index++)
        {
            var item = fixtures[index];
            await PrepareAlreadyOwnFixtureAsync(item, index is 2 or 3 ? 9001 : null, index is 2 or 3);
            await using var setup = await item.Contexts.CreateDbContextAsync();
            var row = await setup.TitleRequests.SingleAsync(value => value.Id == item.Source.Id);
            row.Title = $"Confirmation fixture {index}";
            row.Email = index == 4 ? null : index == 0 ? $"{new string('a', 55)}+x&y@example.org" : "synthetic@example.org";
            row.PatronIdSnapshot = checked(88001 + (int)item.Source.Id);
            row.NameFirst = "Synthetic";
            row.NameLast = "Patron";
            await setup.SaveChangesAsync();
            item.Source.Title = row.Title;
            item.Source.RowVersion = row.RowVersion;
        }
        var template = new EmailTemplate
        {
            OrganizationId = 2, TemplateKey = $"rejection:confirmation_{Guid.NewGuid():N}",
            DisplayName = "Confirmation rejection <safe text>", IsCustom = true,
            SubjectTemplate = "Request update: {{title}}", BodyTemplate = "Hello {{firstName}}, we declined {{title}}."
        };
        await using (var setup = await purchase.Contexts.CreateDbContextAsync())
        {
            var staff = await setup.StaffUsers.SingleAsync(value => value.Id == purchase.Actor.Id);
            staff.PurchaseReminderDefault = true;
            staff.NotificationEmail = "synthetic.staff@example.org";
            setup.EmailTemplates.Add(template);
            await setup.SaveChangesAsync();
        }
        try
        {
            using var client = factory.CreateClient();
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "i439", $"request-confirmations-{Guid.NewGuid():N}");
            var actor = purchase.Actor;
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "request-confirmations.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                new[] { actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                    template.Id.ToString(CultureInfo.InvariantCulture) }
                    .Concat(fixtures.Select(item => item.Source.Id.ToString(CultureInfo.InvariantCulture))).ToArray());
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.IsTrue(report.RootElement.GetProperty("cancellation").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("retries").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("otherPanels").GetBoolean());
            await using var verify = await purchase.Contexts.CreateDbContextAsync();
            foreach (var (item, status) in new[] { (purchase, "outstanding_purchase"), (rejection, "closed"), (savedBib, "pending_hold"), (optout, "closed") })
            {
                var row = await verify.TitleRequests.AsNoTracking().SingleAsync(value => value.Id == item.Source.Id);
                Assert.AreEqual(status, row.Status);
                Assert.AreEqual(item.Source.AutoHold, row.AutoHold);
                Assert.AreEqual(item.Source.Title, row.Title);
                Assert.AreEqual(item.Source.Notes, row.Notes);
                Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(value => value.TitleRequestId == row.Id && value.EventType == "status_changed"));
            }
            var reminderKey = $"purchase-reminder:{purchase.Source.Id}:";
            var rejectionKey = $"rejection:{rejection.Source.Id}:";
            Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(value => value.BusinessKey!.StartsWith(reminderKey)));
            Assert.AreEqual(1, await verify.EmailOutbox.CountAsync(value => value.BusinessKey!.StartsWith(rejectionKey)));
            foreach (var item in new[] { savedBib, optout, untouched })
            {
                var key = $"purchase-reminder:{item.Source.Id}:";
                Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(value => value.BusinessKey!.StartsWith(key)));
            }
            await AssertQueueUnchangedAsync(untouched);
            var untouchedRejection = $"rejection:{untouched.Source.Id}:";
            Assert.AreEqual(0, await verify.EmailOutbox.CountAsync(value => value.BusinessKey!.StartsWith(untouchedRejection)));
            Assert.AreEqual(0, purchase.Provider.Calls.Count(value => value.Operation == TestingPolarisOperation.PickupUpdate));
        }
        finally
        {
            await using var cleanup = await purchase.Contexts.CreateDbContextAsync();
            foreach (var item in fixtures)
            {
                var reminderKey = $"purchase-reminder:{item.Source.Id}:";
                var rejectionKey = $"rejection:{item.Source.Id}:";
                await cleanup.EmailOutbox.Where(value => value.BusinessKey!.StartsWith(reminderKey) || value.BusinessKey!.StartsWith(rejectionKey)).ExecuteDeleteAsync();
            }
            await cleanup.EmailTemplates.Where(value => value.Id == template.Id).ExecuteDeleteAsync();
        }
    }
}
