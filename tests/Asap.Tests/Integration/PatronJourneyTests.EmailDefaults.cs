using System.Globalization;
using System.Text.Json;
using Asap.Tests.Email;
using Asap.Web.Features.Email;
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
    public async Task PatronActionSeededAndFallbackCopyAgreeWithoutAddingHoldDispatch()
    {
        const int library = 99082;
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ActionPatronEmailProvider();
        using var scoped = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        var contexts = scoped.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        scoped.Services.GetRequiredService<DeterministicTestingPatronProvider>()
            .SetBib(9001, library, new BibValidationResult(true, "Default email BIB"));
        await using var seed = await contexts.CreateDbContextAsync();
        var original = await seed.EmailTemplates.AsNoTracking().Where(item => item.OrganizationId == 1 &&
            (item.TemplateKey == "purchase_approved" || item.TemplateKey == "already_owned" || item.TemplateKey == "rejected"))
            .ToListAsync();
        Assert.AreEqual(3, original.Count);
        seed.Organizations.Add(new Organization
        {
            Id = library, DisplayName = "Default action email library", OrganizationCodeId = 2,
            ParentOrganizationId = 1, IsActive = true
        });
        await seed.SaveChangesAsync();
        seed.EmailSettings.Add(new EmailSettings
        {
            OrganizationId = library, FromAddress = "default-notices@example.org", FromName = "Defaults"
        });
        await seed.SaveChangesAsync();
        var format = await seed.MaterialFormats.AsNoTracking().FirstAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        try
        {
            var mutations = scoped.Services.GetRequiredService<TitleRequestMutationService>();
            foreach (var fallback in new[] { false, true })
            {
                if (fallback)
                {
                    await seed.EmailTemplates.Where(item => item.OrganizationId == 1 &&
                        (item.TemplateKey == "purchase_approved" || item.TemplateKey == "already_owned" || item.TemplateKey == "rejected"))
                        .ExecuteUpdateAsync(update => update.SetProperty(item => item.SubjectTemplate, "")
                            .SetProperty(item => item.BodyTemplate, ""));
                }
                foreach (var (key, action) in new[]
                {
                    ("purchase_approved", "purchase"), ("already_owned", "alreadyOwn"), ("rejected", "reject")
                })
                {
                    var barcode = $"2{Guid.NewGuid():N}"[..14];
                    provider.PatronIds.Add(barcode, 990820 + provider.PatronIds.Count);
                    var request = new TitleRequest
                    {
                        LibraryOrganizationId = library, Barcode = barcode,
                        PatronIdSnapshot = provider.PatronIds[barcode], NameFirst = "Original",
                        Title = "Default copy title", Author = "Default author", MaterialFormatId = format.Id,
                        AutoHold = false, Status = "suggestion", BibId = action == "alreadyOwn" ? 9001 : null,
                        BibIdStaffVerified = action == "alreadyOwn", CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                        UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                    };
                    seed.TitleRequests.Add(request);
                    await seed.SaveChangesAsync();
                    var result = await mutations.ActionAsync(actor, request.Id,
                        new TitleRequestActionInput { Version = Convert.ToBase64String(request.RowVersion), Action = action }.ToCommand(),
                        CancellationToken.None);
                    Assert.AreEqual("updated", result.Code, key);
                    await using var verify = await contexts.CreateDbContextAsync();
                    var prefix = key == "rejected" ? $"rejection:{request.Id}:" : $"staff-patron-action:{key}:{request.Id}:";
                    var mail = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.BusinessKey != null &&
                        item.BusinessKey.StartsWith(prefix));
                    var expected = BuiltInEmailTemplateExpectations.All.Single(item => item.Key == key);
                    var patron = await provider.RefreshAsync(barcode, library, CancellationToken.None);
                    var rendered = PatronEmailTemplateRenderer.Render(
                        new EffectiveEmailTemplate(key, expected.Subject, expected.Body), patron,
                        request.Title, request.Author, format.Label, barcode);
                    Assert.AreEqual(rendered.Subject, mail.Subject, key + fallback);
                    Assert.AreEqual(rendered.BodyText, mail.BodyText, key + fallback);
                    Assert.AreEqual(rendered.BodyHtml, mail.BodyHtml, key + fallback);
                    Assert.IsFalse(await verify.HoldPlacementOperations.AnyAsync(item => item.TitleRequestId == request.Id));
                }
            }
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.TitleRequestWorkflowTags.Where(item => cleanup.TitleRequests.Any(request =>
                request.Id == item.TitleRequestId && request.LibraryOrganizationId == library)).ExecuteDeleteAsync();
            await cleanup.TitleRequestEvents.Where(item => cleanup.TitleRequests.Any(request =>
                request.Id == item.TitleRequestId && request.LibraryOrganizationId == library)).ExecuteDeleteAsync();
            await cleanup.EmailOutbox.Where(item => item.OrganizationId == library).ExecuteDeleteAsync();
            await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library).ExecuteDeleteAsync();
            foreach (var saved in original)
            {
                var row = await cleanup.EmailTemplates.SingleAsync(item => item.Id == saved.Id);
                row.SubjectTemplate = saved.SubjectTemplate;
                row.BodyTemplate = saved.BodyTemplate;
            }
            await cleanup.SaveChangesAsync();
            await CleanupScalarSettingsTestDataAsync(library, "", "");
        }
    }

    [TestMethod]
    public async Task EmailDefaultsBrowserDisplaysFiveSeedsAndPreservesSavedTextAndSparseInheritance()
    {
        const int library = 99081;
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var initialContext = await contexts.CreateDbContextAsync();
        var initialTemplates = await initialContext.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == 1).ToListAsync();
        foreach (var expected in BuiltInEmailTemplateExpectations.All)
        {
            var row = initialTemplates.Single(item => item.TemplateKey == expected.Key);
            Assert.AreEqual(expected.Subject, row.SubjectTemplate, expected.Key);
            Assert.AreEqual(expected.Body, row.BodyTemplate, expected.Key);
        }
        initialContext.Organizations.Add(new Organization
        {
            Id = library, DisplayName = "Email defaults library", OrganizationCodeId = 2,
            ParentOrganizationId = 1, IsActive = true
        });
        await initialContext.SaveChangesAsync();
        try
        {
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"email-defaults-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "expected.json"),
                JsonSerializer.Serialize(BuiltInEmailTemplateExpectations.All,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "email-defaults.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                library.ToString(CultureInfo.InvariantCulture), actor.Id.ToString(CultureInfo.InvariantCulture),
                actor.EntraTenantId.ToString(), actor.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"Browser artifacts: {artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

            await using var verify = await contexts.CreateDbContextAsync();
            var savedSystem = await verify.EmailTemplates.AsNoTracking().Where(item => item.OrganizationId == 1).ToListAsync();
            foreach (var expected in BuiltInEmailTemplateExpectations.All)
            {
                var row = savedSystem.Single(item => item.TemplateKey == expected.Key);
                var original = initialTemplates.Single(item => item.TemplateKey == expected.Key);
                Assert.AreEqual(original.Id, row.Id);
                Assert.AreEqual(expected.Body + "\n\n{{firstName}} {{lastName}}", row.BodyTemplate);
                Assert.AreEqual(expected.Subject + " {{barcode}}" +
                    (expected.Key == "suggestion_submitted" ? " updated" : ""), row.SubjectTemplate);
            }
            var libraryRow = await verify.EmailTemplates.AsNoTracking().SingleAsync(item => item.OrganizationId == library);
            Assert.AreEqual("suggestion_submitted", libraryRow.TemplateKey);
            Assert.AreEqual(initialTemplates.Single(item => item.TemplateKey == "suggestion_submitted").Id,
                libraryRow.SourceTemplateId);
            Assert.IsNull(libraryRow.SubjectTemplate, "A body-only edit must preserve sparse subject inheritance.");
            Assert.AreEqual("Library receipt {{title}} for {{name}}", libraryRow.BodyTemplate);
            Assert.IsFalse(libraryRow.IsCustom);
        }
        finally
        {
            await using var restore = await contexts.CreateDbContextAsync();
            await restore.EmailTemplates.Where(item => item.OrganizationId == library).ExecuteDeleteAsync();
            foreach (var original in initialTemplates)
            {
                var row = await restore.EmailTemplates.SingleAsync(item => item.Id == original.Id);
                row.SubjectTemplate = original.SubjectTemplate;
                row.BodyTemplate = original.BodyTemplate;
            }
            await restore.SaveChangesAsync();
            await CleanupScalarSettingsTestDataAsync(library, "", "");
        }
    }
}
