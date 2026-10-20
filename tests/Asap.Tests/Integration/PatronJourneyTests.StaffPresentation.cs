using System.Globalization;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffPresentationBrowserPreservesHeaderNavigationLookupOrderAndTabGeometry()
    {
        const int first = 383091;
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var setup = await contexts.CreateDbContextAsync())
        {
            setup.Organizations.AddRange(new[] { "Zebra presentation", "alpha presentation", "Alpha presentation" }
                .Select((name, index) => new Organization
                {
                    Id = first + index, DisplayName = name, OrganizationCodeId = 2,
                    ParentOrganizationId = 1, IsActive = true
                }));
            await setup.SaveChangesAsync();
        }
        try
        {
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"staff-presentation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "staff-presentation.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.Organizations.Where(item => item.Id >= first && item.Id < first + 3).ExecuteDeleteAsync();
        }
    }
}
