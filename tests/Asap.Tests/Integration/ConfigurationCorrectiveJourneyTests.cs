using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SystemVersionMutationsLockEveryOrganizationBeforeActor(bool branding)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<AdministrationService>();
        var settings = JsonSerializer.SerializeToElement((await service.GetSettingsAsync(actor, "system", CancellationToken.None)).Data);
        var version = settings.GetProperty("version").GetString();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var existingBranding = await context.Branding.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == 1);
        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var organizationLock = new SqlCommand("SELECT [Id] FROM [asap].[Organization] WITH (XLOCK,HOLDLOCK) WHERE [Id] = 2;", blocker, transaction))
            await organizationLock.ExecuteScalarAsync();
        var mutation = branding
            ? service.SaveLogoAsync(actor, "system", [], "", "", "Ordering probe", false, version, CancellationToken.None)
            : service.SaveSettingsAsync(actor, JsonSerializer.SerializeToElement(new { orgId = "system", version, patron = new { } }), CancellationToken.None);
        try
        {
            await WaitForCorrectiveSqlBlockAsync(blocker.ServerProcessId);
            // The transaction owning library 2 must still be able to acquire the same
            // actor, as a workflow does. A settings-first Staff lock would time out here.
            await using var staffLock = new SqlCommand("SET LOCK_TIMEOUT 1000; SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;", blocker, transaction);
            staffLock.Parameters.AddWithValue("@id", actor.Id);
            Assert.AreEqual(actor.Id, Convert.ToInt64(await staffLock.ExecuteScalarAsync()));
        }
        finally
        {
            await transaction.CommitAsync();
            Assert.AreEqual(branding ? "branding_saved" : "saved", (await mutation).Code);
            if (branding)
            {
                var current = await context.Branding.SingleAsync(item => item.OrganizationId == 1);
                if (existingBranding is null) context.Branding.Remove(current);
                else
                {
                    current.LogoAltText = existingBranding.LogoAltText;
                    current.UpdatedUtc = existingBranding.UpdatedUtc;
                }
                await context.SaveChangesAsync();
            }
        }
    }

    [TestMethod]
    public async Task SettingsVersionCoversOriginsParticipationAndBothBrandingScopesInCanonicalOrder()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var administration = factory.Services.GetRequiredService<AdministrationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var organization = new Organization { Id = 91905, DisplayName = "Version participation", IsActive = true };
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        async Task<string> VersionAsync(string scope)
        {
            var result = await administration.GetSettingsAsync(actor, scope, CancellationToken.None);
            Assert.AreEqual("ok", result.Code);
            return JsonSerializer.SerializeToElement(result.Data).GetProperty("version").GetString()!;
        }
        async Task AssertChangeAsync(string scope, Func<Task> change)
        {
            var before = await VersionAsync(scope);
            for (var index = 0; index < 3; index++) Assert.AreEqual(before, await VersionAsync(scope));
            await change();
            var after = await VersionAsync(scope);
            Assert.AreNotEqual(before, after);
            var rejected = await administration.SaveSettingsAsync(actor,
                JsonSerializer.SerializeToElement(new { orgId = scope, version = before, patron = new { loginNote = "Stale draft" } }), CancellationToken.None);
            Assert.AreEqual("stale_version", rejected.Code, scope);
            Assert.AreEqual(after, await VersionAsync(scope), "A rejected write must leave the snapshot unchanged.");
        }
        var origin = new PatronEmbedAllowedOrigin
        {
            OrganizationId = 1, Origin = "https://version-corrective.example.org",
            NormalizedOrigin = "https://version-corrective.example.org", CreatedUtc = DateTime.UtcNow
        };
        var systemBranding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 1);
        var libraryBranding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 2);
        var systemAlt = systemBranding?.LogoAltText;
        var libraryAlt = libraryBranding?.LogoAltText;
        var createdSystem = systemBranding is null;
        var createdLibrary = libraryBranding is null;
        systemBranding ??= new Branding { OrganizationId = 1 };
        libraryBranding ??= new Branding { OrganizationId = 2 };
        try
        {
            await AssertChangeAsync("system", async () => { context.PatronEmbedAllowedOrigins.Add(origin); await context.SaveChangesAsync(); });
            await AssertChangeAsync("system", async () => { organization.IsActive = false; await context.SaveChangesAsync(); });
            await AssertChangeAsync("2", async () =>
            {
                if (createdSystem) context.Branding.Add(systemBranding);
                systemBranding.LogoAltText = $"Changed system alt {Guid.NewGuid():N}";
                await context.SaveChangesAsync();
            });
            await AssertChangeAsync("2", async () =>
            {
                if (createdLibrary) context.Branding.Add(libraryBranding);
                libraryBranding.LogoAltText = $"Changed local alt {Guid.NewGuid():N}";
                await context.SaveChangesAsync();
            });
            var systemVersion = await VersionAsync("system");
            var libraryVersion = await VersionAsync("2");
            await context.Database.ExecuteSqlRawAsync("CREATE NONCLUSTERED INDEX [IX_Corrective_TemplateVersion] ON [asap].[EmailTemplate] ([OrganizationId], [RowVersion] DESC);");
            try
            {
                await context.Database.ExecuteSqlRawAsync("ALTER INDEX ALL ON [asap].[PatronCodeEligibilityMember] REBUILD;");
                for (var index = 0; index < 3; index++)
                {
                    Assert.AreEqual(systemVersion, await VersionAsync("system"));
                    Assert.AreEqual(libraryVersion, await VersionAsync("2"));
                }
            }
            finally { await context.Database.ExecuteSqlRawAsync("DROP INDEX [IX_Corrective_TemplateVersion] ON [asap].[EmailTemplate];"); }
        }
        finally
        {
            context.PatronEmbedAllowedOrigins.Remove(origin);
            if (createdSystem) context.Branding.Remove(systemBranding); else systemBranding.LogoAltText = systemAlt;
            if (createdLibrary) context.Branding.Remove(libraryBranding); else libraryBranding.LogoAltText = libraryAlt;
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    [DataRow("system-enabled")]
    [DataRow("library-sparse")]
    [DataRow("library-hidden")]
    [DataRow("system-hidden")]
    [DataRow("library-sparse-system-hidden")]
    public async Task SubmissionTemplateVisibilityAndSparseLineageControlDeliveryWithoutBlockingBusinessCommit(string mode)
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var system = await context.EmailTemplates.SingleAsync(item => item.OrganizationId == 1 && item.TemplateKey == "suggestion_submitted");
        var library = await context.EmailTemplates.SingleOrDefaultAsync(item => item.OrganizationId == 2 && item.SourceTemplateId == system.Id);
        var oldSystemHidden = system.IsHidden;
        var oldSubject = system.SubjectTemplate;
        var oldBody = system.BodyTemplate;
        var existingLibrary = library is not null;
        var oldLibraryHidden = library?.IsHidden;
        var oldLibrarySubject = library?.SubjectTemplate;
        var oldLibraryBody = library?.BodyTemplate;
        try
        {
            system.IsHidden = mode.Contains("system-hidden", StringComparison.Ordinal);
            system.SubjectTemplate = "Corrective received: {{title}}";
            system.BodyTemplate = "System body for {{title}}";
            if (mode.StartsWith("library", StringComparison.Ordinal))
            {
                library ??= new EmailTemplate { OrganizationId = 2, SourceTemplateId = system.Id, TemplateKey = system.TemplateKey, IsCustom = false };
                if (!existingLibrary) context.EmailTemplates.Add(library);
                library.IsHidden = mode == "library-hidden";
                library.SubjectTemplate = null;
                library.BodyTemplate = "Local body for {{title}}";
            }
            else if (library is not null) context.EmailTemplates.Remove(library);
            await context.SaveChangesAsync();
            var sender = new RecordingEmailSender();
            var localDispatcher = new RecordingOutboxDispatcher();
            var service = CreatePatronSuggestionService(["example.org"], localDispatcher, sender);
            var title = $"Template visibility {Guid.NewGuid():N}";
            var result = await service.CreateAsync(new PatronSessionContext(9091, "20000000003910", 2, 2, 2, DateTime.UtcNow.AddHours(1)),
                Suggestion(title), CancellationToken.None);
            var committedTitle = await context.TitleRequests.Where(item => item.Id == result.Id).Select(item => item.Title).SingleAsync();
            var outboxId = await FindSubmissionOutboxIdAsync(result.Id);
            var outbox = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == outboxId);
            var hidden = mode.EndsWith("hidden", StringComparison.Ordinal);
            Assert.AreEqual(hidden ? "suppressed" : "pending", outbox.Status);
            Assert.AreEqual(hidden ? "template_missing" : null, outbox.SuppressionReason);
            Assert.AreEqual(hidden ? 0 : 1, localDispatcher.EnqueuedIds.Count);
            if (!hidden)
            {
                Assert.AreEqual($"Corrective received: {committedTitle}", outbox.Subject);
                StringAssert.Contains(outbox.BodyHtml ?? outbox.BodyText!, $"{(mode == "library-sparse" ? "Local" : "System")} body for {committedTitle}");
                await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default).DeliverAsync(outboxId, CancellationToken.None);
                Assert.AreEqual("sent", (await ReadOutboxStateAsync(outboxId)).Status);
            }
            if (mode.StartsWith("library", StringComparison.Ordinal))
                Assert.AreEqual(system.Id, (await context.EmailTemplates.AsNoTracking().SingleAsync(item => item.Id == library!.Id)).SourceTemplateId);
        }
        finally
        {
            system.IsHidden = oldSystemHidden;
            system.SubjectTemplate = oldSubject;
            system.BodyTemplate = oldBody;
            if (library is not null)
            {
                if (!existingLibrary) context.EmailTemplates.Remove(library);
                else
                {
                    if (context.Entry(library).State == EntityState.Detached) context.EmailTemplates.Add(library);
                    library.IsHidden = oldLibraryHidden!.Value;
                    library.SubjectTemplate = oldLibrarySubject;
                    library.BodyTemplate = oldLibraryBody;
                }
            }
            await context.SaveChangesAsync();
        }
    }
}
