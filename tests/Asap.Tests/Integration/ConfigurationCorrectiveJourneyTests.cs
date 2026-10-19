using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task FailedPolarisOrganizationFetchReportsDependencyFailureWithoutLocalWrite()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var failingFactory = CreateApplicationFactory(
            configurationPath, new FailingOrganizationReferenceProvider());
        using var client = failingFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var service = failingFactory.Services.GetRequiredService<AdministrationService>();
        async Task<string?> VersionAsync() => JsonSerializer.SerializeToElement(
            (await service.GetSettingsAsync(actor, LibraryScope.System, CancellationToken.None)).Data)
            .GetProperty("version").GetString();
        var before = await VersionAsync();
        using var response = await client.PostAsync("/api/asap/staff/organizations/sync", null);
        Assert.AreEqual(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_unavailable", body.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(before, await VersionAsync());
    }

    private sealed class FailingOrganizationReferenceProvider : IPolarisReferenceProvider
    {
        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(false, 0));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken cancellationToken) =>
            throw new PolarisOperationalException("testing_organization_failure", "Organization fetch failed.");

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>([]);
    }

    [TestMethod]
    public async Task RepeatedScopedSetSavePreservesRowsAndDoesNotCreateEmptyOverrides()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var administration = factory.Services.GetRequiredService<AdministrationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        const int organizationId = 91906;
        await using var context = await contextFactory.CreateDbContextAsync();
        context.Organizations.Add(new Organization
        {
            Id = organizationId, DisplayName = "Set preservation", OrganizationCodeId = 2,
            ParentOrganizationId = 1, IsActive = true
        });
        await context.SaveChangesAsync();
        async Task<string> VersionAsync() => JsonSerializer.SerializeToElement(
            (await administration.GetSettingsAsync(actor, LibraryScope.ForLibrary(organizationId), CancellationToken.None)).Data)
            .GetProperty("version").GetString()!;
        try
        {
            var version = await VersionAsync();
            var first = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new
            {
                orgId = organizationId.ToString(), version,
                workflow = new { commonCreators = new[] { "Creator A" }, allowedPatronCodeIds = new[] { "1" } },
                patron = new { publicationOptions = new[] { new { key = "year", label = "Year", enabled = true, sortOrder = 10 } } }
            })), CancellationToken.None);
            Assert.AreEqual("saved", first.Code);
            version = await VersionAsync();
            var creatorId = await context.CommonCreatorTerms.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.Id).SingleAsync();
            var optionId = await context.PublicationOptions.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.Id).SingleAsync();
            var creatorVersion = await context.CommonCreatorSets.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.RowVersion).SingleAsync();
            var patronCodeVersion = await context.PatronCodeEligibilitySets.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.RowVersion).SingleAsync();
            var publicationVersion = await context.PublicationOptionSets.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.RowVersion).SingleAsync();
            var second = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new
            {
                orgId = organizationId.ToString(), version,
                workflow = new { commonCreators = new[] { "Creator A" }, allowedPatronCodeIds = new[] { "1" } },
                patron = new { publicationOptions = new[] { new { key = "year", label = "Year", enabled = true, sortOrder = 10 } } }
            })), CancellationToken.None);
            Assert.AreEqual("saved", second.Code);
            Assert.AreEqual(version, await VersionAsync());
            Assert.AreEqual(creatorId, await context.CommonCreatorTerms.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.Id).SingleAsync());
            Assert.AreEqual(optionId, await context.PublicationOptions.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.Id).SingleAsync());
            CollectionAssert.AreEqual(creatorVersion, await context.CommonCreatorSets.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.RowVersion).SingleAsync());
            CollectionAssert.AreEqual(patronCodeVersion, await context.PatronCodeEligibilitySets.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.RowVersion).SingleAsync());
            CollectionAssert.AreEqual(publicationVersion, await context.PublicationOptionSets.Where(item => item.OrganizationId == organizationId)
                .Select(item => item.RowVersion).SingleAsync());
            Assert.IsFalse(await context.WorkflowSettings.AnyAsync(item => item.OrganizationId == organizationId));
            Assert.IsFalse(await context.PatronSettings.AnyAsync(item => item.OrganizationId == organizationId));
            Assert.IsFalse(await context.EmailSettings.AnyAsync(item => item.OrganizationId == organizationId));

            var empty = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new
            {
                orgId = organizationId.ToString(), version = await VersionAsync(),
                workflow = new { commonCreators = Array.Empty<string>(), allowedPatronCodeIds = Array.Empty<int>() },
                patron = new { publicationOptions = Array.Empty<object>() }
            })), CancellationToken.None);
            Assert.AreEqual("saved", empty.Code);
            var emptySettings = JsonSerializer.SerializeToElement(
                (await administration.GetSettingsAsync(actor, LibraryScope.ForLibrary(organizationId), CancellationToken.None)).Data);
            var emptyOverrides = emptySettings.GetProperty("stored").GetProperty("libraryOverride");
            Assert.IsTrue(emptyOverrides.GetProperty("commonCreators").GetProperty("exists").GetBoolean());
            Assert.IsTrue(emptyOverrides.GetProperty("allowedPatronCodeIds").GetProperty("exists").GetBoolean());
            Assert.IsTrue(emptyOverrides.GetProperty("publicationOptions").GetProperty("exists").GetBoolean());
            Assert.AreEqual(0, emptyOverrides.GetProperty("commonCreators").GetProperty("values").GetArrayLength());
            Assert.AreEqual(0, emptyOverrides.GetProperty("allowedPatronCodeIds").GetProperty("values").GetArrayLength());
            Assert.AreEqual(0, emptyOverrides.GetProperty("publicationOptions").GetProperty("values").GetArrayLength());
            Assert.IsTrue(await context.PatronCodeEligibilitySets.AnyAsync(item => item.OrganizationId == organizationId));
            Assert.IsFalse(await context.PatronCodeEligibilityMembers.AnyAsync(item => item.OrganizationId == organizationId));
            var configurations = factory.Services.GetRequiredService<PatronConfigurationService>();
            var effectiveEmpty = await configurations.GetAsync(organizationId, CancellationToken.None);
            Assert.AreEqual(0, effectiveEmpty!.CommonCreators.Count);
            Assert.AreEqual(0, effectiveEmpty.AllowedPatronCodeIds.Count);
            Assert.AreEqual(0, effectiveEmpty.PublicationOptions.Count);

            var reset = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new
            {
                orgId = organizationId.ToString(), version = await VersionAsync(),
                workflow = new { commonCreators = (string[]?)null, allowedPatronCodeIds = (int[]?)null },
                patron = new { publicationOptions = (object[]?)null }
            })), CancellationToken.None);
            Assert.AreEqual("saved", reset.Code);
            var resetSettings = JsonSerializer.SerializeToElement(
                (await administration.GetSettingsAsync(actor, LibraryScope.ForLibrary(organizationId), CancellationToken.None)).Data);
            var resetOverrides = resetSettings.GetProperty("stored").GetProperty("libraryOverride");
            Assert.IsFalse(resetOverrides.GetProperty("commonCreators").GetProperty("exists").GetBoolean());
            Assert.IsFalse(resetOverrides.GetProperty("allowedPatronCodeIds").GetProperty("exists").GetBoolean());
            Assert.IsFalse(resetOverrides.GetProperty("publicationOptions").GetProperty("exists").GetBoolean());
            Assert.IsFalse(await context.PatronCodeEligibilitySets.AnyAsync(item => item.OrganizationId == organizationId));
            var inherited = await configurations.GetAsync(organizationId, CancellationToken.None);
            var system = await configurations.GetAsync(1, CancellationToken.None);
            Assert.IsFalse(string.IsNullOrWhiteSpace(system!.PageTitle));
            CollectionAssert.AreEqual(system!.CommonCreators.ToArray(), inherited!.CommonCreators.ToArray());
            CollectionAssert.AreEquivalent(system!.AllowedPatronCodeIds.ToArray(), inherited!.AllowedPatronCodeIds.ToArray());
            CollectionAssert.AreEqual(system.PublicationOptions.ToArray(), inherited.PublicationOptions.ToArray());

            var blankText = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new
            {
                orgId = organizationId.ToString(), version = await VersionAsync(),
                patron = new { pageTitle = "   " }
            })), CancellationToken.None);
            Assert.AreEqual("saved", blankText.Code);
            Assert.IsFalse(await context.PatronSettings.AnyAsync(item => item.OrganizationId == organizationId));
            Assert.AreEqual(system.PageTitle,
                (await configurations.GetAsync(organizationId, CancellationToken.None))!.PageTitle);
        }
        finally
        {
            context.CommonCreatorTerms.RemoveRange(context.CommonCreatorTerms.Where(item => item.OrganizationId == organizationId));
            context.CommonCreatorSets.RemoveRange(context.CommonCreatorSets.Where(item => item.OrganizationId == organizationId));
            context.PatronCodeEligibilityMembers.RemoveRange(context.PatronCodeEligibilityMembers.Where(item => item.OrganizationId == organizationId));
            context.PatronCodeEligibilitySets.RemoveRange(context.PatronCodeEligibilitySets.Where(item => item.OrganizationId == organizationId));
            context.PublicationOptions.RemoveRange(context.PublicationOptions.Where(item => item.OrganizationId == organizationId));
            context.PublicationOptionSets.RemoveRange(context.PublicationOptionSets.Where(item => item.OrganizationId == organizationId));
            context.AdministrativeAudits.RemoveRange(context.AdministrativeAudits.Where(item => item.OrganizationId == organizationId));
            await context.SaveChangesAsync();
            context.Organizations.Remove(await context.Organizations.SingleAsync(item => item.Id == organizationId));
            await context.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task SystemNullClearsCommonCreatorsAndPublicationOptionsToEmptySets()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var administration = factory.Services.GetRequiredService<AdministrationService>();

        async Task<JsonElement> ReadSystemAsync() => JsonSerializer.SerializeToElement(
            (await administration.GetSettingsAsync(actor, LibraryScope.System, CancellationToken.None)).Data);

        static (string Id, string Label, bool Enabled, int SortOrder)[] ReadOptions(JsonElement settings) => settings
            .GetProperty("stored").GetProperty("configuredSystem").GetProperty("publicationOptions")
            .GetProperty("values").EnumerateArray()
            .Select(item => (
                item.GetProperty("id").GetString()!,
                item.GetProperty("label").GetString()!,
                item.GetProperty("enabled").GetBoolean(),
                item.GetProperty("sortOrder").GetInt32()))
            .ToArray();

        var original = await ReadSystemAsync();
        var originalCreatorsSnapshot = original.GetProperty("stored").GetProperty("configuredSystem").GetProperty("commonCreators");
        var originalOptionsSnapshot = original.GetProperty("stored").GetProperty("configuredSystem").GetProperty("publicationOptions");
        Assert.IsTrue(originalCreatorsSnapshot.GetProperty("exists").GetBoolean());
        Assert.IsTrue(originalOptionsSnapshot.GetProperty("exists").GetBoolean());
        var originalCreators = originalCreatorsSnapshot.GetProperty("values").EnumerateArray()
            .Select(item => item.GetProperty("value").GetString()!).ToArray();
        var originalOptions = originalOptionsSnapshot.GetProperty("values").EnumerateArray()
            .Select(item => new
            {
                id = item.GetProperty("id").GetString()!,
                label = item.GetProperty("label").GetString()!,
                enabled = item.GetProperty("enabled").GetBoolean(),
                sortOrder = item.GetProperty("sortOrder").GetInt32()
            }).ToArray();
        var changed = false;

        async Task RestoreSystemSetsAsync()
        {
            var latest = await ReadSystemAsync();
            var restored = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor,
                JsonSerializer.SerializeToElement(new
                {
                    orgId = "system",
                    version = latest.GetProperty("version").GetString(),
                    workflow = new { commonAuthorsList = originalCreators },
                    patron = new { publicationOptions = originalOptions }
                })), CancellationToken.None);
            Assert.AreEqual("saved", restored.Code);
            changed = false;
        }

        try
        {
            changed = true;
            var seeded = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor,
                JsonSerializer.SerializeToElement(new
                {
                    orgId = "system",
                    version = original.GetProperty("version").GetString(),
                    workflow = new { commonAuthorsList = new[] { $"System null clear {Guid.NewGuid():N}" } },
                    patron = new
                    {
                        publicationOptions = new[]
                        {
                            new
                            {
                                id = $"system_null_{Guid.NewGuid():N}",
                                label = "System null clear option",
                                enabled = true,
                                sortOrder = 10
                            }
                        }
                    }
                })), CancellationToken.None);
            Assert.AreEqual("saved", seeded.Code);

            var beforeClear = await ReadSystemAsync();
            var cleared = await administration.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor,
                JsonSerializer.SerializeToElement(new
                {
                    orgId = "system",
                    version = beforeClear.GetProperty("version").GetString(),
                    workflow = new { commonAuthorsList = (string[]?)null },
                    patron = new { publicationOptions = (object[]?)null }
                })), CancellationToken.None);
            Assert.AreEqual("saved", cleared.Code);

            var empty = await ReadSystemAsync();
            var configuredSystem = empty.GetProperty("stored").GetProperty("configuredSystem");
            Assert.IsTrue(configuredSystem.GetProperty("commonCreators").GetProperty("exists").GetBoolean());
            Assert.AreEqual(0, configuredSystem.GetProperty("commonCreators").GetProperty("values").GetArrayLength());
            Assert.IsTrue(configuredSystem.GetProperty("publicationOptions").GetProperty("exists").GetBoolean());
            Assert.AreEqual(0, configuredSystem.GetProperty("publicationOptions").GetProperty("values").GetArrayLength());
            Assert.AreEqual(1, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = 1;"));
            Assert.AreEqual(0, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 1;"));
            Assert.AreEqual(1, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = 1;"));
            Assert.AreEqual(0, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 1;"));

            await RestoreSystemSetsAsync();
            var restoredSettings = await ReadSystemAsync();
            CollectionAssert.AreEqual(originalCreators,
                restoredSettings.GetProperty("stored").GetProperty("configuredSystem").GetProperty("commonCreators")
                    .GetProperty("values").EnumerateArray().Select(item => item.GetProperty("value").GetString()).ToArray());
            CollectionAssert.AreEqual(ReadOptions(original), ReadOptions(restoredSettings));
        }
        finally
        {
            if (changed)
            {
                await RestoreSystemSetsAsync();
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SystemVersionMutationsLockEveryOrganizationBeforeActor(bool branding)
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<AdministrationService>();
        var settings = JsonSerializer.SerializeToElement((await service.GetSettingsAsync(actor, LibraryScope.System, CancellationToken.None)).Data);
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
            ? service.SaveLogoAsync(actor, LibraryScope.System, [], "", "", "Ordering probe", false, version, CancellationToken.None)
            : service.SaveSettingsAsync(actor, AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new { orgId = "system", version, patron = new { } })), CancellationToken.None);
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
        var organization = new Organization
        {
            Id = 91905, DisplayName = "Version participation",
            OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true
        };
        context.Organizations.Add(organization);
        await context.SaveChangesAsync();
        async Task<string> VersionAsync(string scope)
        {
            var result = await administration.GetSettingsAsync(actor, ParseScope(scope), CancellationToken.None);
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
                AdministrationSettingsBinding.Bind(actor, JsonSerializer.SerializeToElement(new { orgId = scope, version = before, patron = new { loginNote = "Stale draft" } })), CancellationToken.None);
            Assert.AreEqual("stale_version", rejected.Code, scope);
            Assert.AreEqual(after, await VersionAsync(scope), "A rejected write must leave the snapshot unchanged.");
        }
        var origin = new PatronEmbedAllowedOrigin
        {
            OrganizationId = 1, Origin = "https://version-corrective.example.org",
            NormalizedOrigin = "https://version-corrective.example.org", CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime
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
            var session = await IssueTestPatronSessionAsync("20000000003910");
            var result = await service.CreateAsync(session,
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
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[PatronSession] WHERE [Barcode] = @barcode;",
                ("@barcode", "20000000003910"));
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
    private static LibraryScope ParseScope(string value)
    {
        Assert.IsTrue(LibraryScope.TryParse(value, LibraryScope.System, out var scope));
        return scope;
    }

}
