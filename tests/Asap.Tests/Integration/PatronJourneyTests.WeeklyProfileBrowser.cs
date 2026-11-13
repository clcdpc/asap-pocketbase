using System.Globalization;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task WeeklyProfileBrowserShowsPrimaryAccountInfoAndPreservesPreferenceContractsAcrossAccessibleLayouts()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        await WithWeeklyRecipientsAsync("primary@example.org", null, true, async id =>
        {
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using var context = await contexts.CreateDbContextAsync();
            var row = await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == id);
            var actor = await ReadCorrectiveStaffAsync(row);
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"weekly-profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "weekly-profile.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(6, report.RootElement.GetProperty("states").GetArrayLength());
            var saved = await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == id);
            Assert.AreEqual("primary@example.org", saved.NotificationEmail);
            Assert.IsFalse(saved.NewPatronRequestEmailEnabled);
            Assert.AreEqual("additional@example.org", saved.WeeklyActionSummaryEmail);
            Assert.IsFalse(saved.WeeklyActionSummaryEnabled);
        });
    }
}
