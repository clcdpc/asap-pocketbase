using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task DirectLibraryBrandingMutationsRemoveOnlyEmptyOverridesAndResetBothValues()
    {
        const int libraryId = 92327;
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var service = factory.Services.GetRequiredService<AdministrationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        Branding? originalSystem;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            originalSystem = await setup.Branding.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == 1);
            setup.Organizations.Add(new Organization
            {
                Id = libraryId,
                DisplayName = "Branding cleanup library",
                Abbreviation = "BCL",
                IsActive = true
            });
            var system = await setup.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (system is null)
            {
                system = new Branding { OrganizationId = 1 };
                setup.Branding.Add(system);
            }
            system.LogoData = [1, 2, 3];
            system.LogoContentType = "image/png";
            system.LogoFileName = "system.png";
            system.LogoAltText = "System alt";
            system.UpdatedUtc = DateTime.UtcNow;
            await setup.SaveChangesAsync();
        }

        async Task<JsonElement> CurrentSettingsAsync() => JsonSerializer.SerializeToElement(
            (await service.GetSettingsAsync(actor, libraryId.ToString(), CancellationToken.None)).Data);

        async Task SeedBrandingAsync(bool image, string? alt)
        {
            await using var seed = await contextFactory.CreateDbContextAsync();
            var row = await seed.Branding.SingleOrDefaultAsync(item => item.OrganizationId == libraryId);
            if (row is null)
            {
                row = new Branding { OrganizationId = libraryId };
                seed.Branding.Add(row);
            }
            row.LogoData = image ? [4, 5, 6] : null;
            row.LogoContentType = image ? "image/png" : null;
            row.LogoFileName = image ? "library.png" : null;
            row.LogoAltText = alt;
            row.UpdatedUtc = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        async Task AssertInheritedAsync(string oldVersion)
        {
            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.IsFalse(await verify.Branding.AnyAsync(item => item.OrganizationId == libraryId));
            var settings = await CurrentSettingsAsync();
            Assert.AreEqual("System alt", settings.GetProperty("effective").GetProperty("LogoAltText").GetString());
            Assert.IsTrue(settings.GetProperty("effective").GetProperty("HasLogo").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, settings.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("branding").GetProperty("version").ValueKind);
            Assert.IsFalse(settings.GetProperty("hasOverrides").GetBoolean());
            Assert.AreNotEqual(oldVersion, settings.GetProperty("version").GetString());
            var effectiveBranding = await factory.Services.GetRequiredService<PatronConfigurationService>()
                .GetBrandingAsync(libraryId, CancellationToken.None);
            Assert.IsNotNull(effectiveBranding);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, effectiveBranding.LogoData!);
            Assert.AreEqual("system.png", effectiveBranding.LogoFileName);
            Assert.AreEqual("System alt", effectiveBranding.LogoAltText);
            var stale = await service.SaveLogoAsync(actor, libraryId.ToString(), [], "", "", "New alt", false,
                oldVersion, CancellationToken.None);
            Assert.AreEqual("stale_version", stale.Code);
        }

        try
        {
            // Clearing alt without a prior row must not insert a phantom override.
            var empty = await CurrentSettingsAsync();
            var noRowClear = await service.SaveLogoAsync(actor, libraryId.ToString(), [], "", "", "", false,
                empty.GetProperty("version").GetString(), CancellationToken.None);
            Assert.AreEqual("branding_saved", noRowClear.Code);
            await using (var verify = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsFalse(await verify.Branding.AnyAsync(item => item.OrganizationId == libraryId));
            }

            // A legacy empty row is also removed by the next direct branding write.
            await SeedBrandingAsync(false, null);
            var legacyEmpty = await CurrentSettingsAsync();
            var clearLegacyEmpty = await service.SaveLogoAsync(actor, libraryId.ToString(), [], "", "", "", false,
                legacyEmpty.GetProperty("version").GetString(), CancellationToken.None);
            Assert.AreEqual("branding_saved", clearLegacyEmpty.Code);
            await AssertInheritedAsync(legacyEmpty.GetProperty("version").GetString()!);

            await SeedBrandingAsync(false, "Library alt");
            var altOnly = await CurrentSettingsAsync();
            var altOnlyVersion = altOnly.GetProperty("version").GetString()!;
            using (var clearAlt = await client.PostAsync($"/api/asap/staff/settings/logo?orgId={libraryId}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["version"] = altOnlyVersion,
                    ["logoAlt"] = ""
                })))
            {
                Assert.AreEqual(System.Net.HttpStatusCode.OK, clearAlt.StatusCode);
                using var result = JsonDocument.Parse(await clearAlt.Content.ReadAsStringAsync());
                Assert.AreEqual("branding_saved", result.RootElement.GetProperty("code").GetString());
            }
            await AssertInheritedAsync(altOnlyVersion);

            await SeedBrandingAsync(true, "Library alt");
            var imageAndAlt = await CurrentSettingsAsync();
            var clearWithImage = await service.SaveLogoAsync(actor, libraryId.ToString(), [], "", "", "", false,
                imageAndAlt.GetProperty("version").GetString(), CancellationToken.None);
            Assert.AreEqual("branding_saved", clearWithImage.Code);
            await using (var verify = await contextFactory.CreateDbContextAsync())
            {
                var row = await verify.Branding.SingleAsync(item => item.OrganizationId == libraryId);
                Assert.IsNotNull(row.LogoData);
                Assert.IsNull(row.LogoAltText);
            }
            var inheritedAlt = await CurrentSettingsAsync();
            Assert.AreEqual("System alt", inheritedAlt.GetProperty("effective").GetProperty("LogoAltText").GetString());
            Assert.IsTrue(inheritedAlt.GetProperty("hasOverrides").GetBoolean());
            var effectiveWithImage = await factory.Services.GetRequiredService<PatronConfigurationService>()
                .GetBrandingAsync(libraryId, CancellationToken.None);
            Assert.IsNotNull(effectiveWithImage);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, effectiveWithImage.LogoData!);
            Assert.AreEqual("System alt", effectiveWithImage.LogoAltText);

            await SeedBrandingAsync(true, "Library alt");
            var beforeImageOnlyClear = await CurrentSettingsAsync();
            var clearImageOnly = await service.SaveLogoAsync(actor, libraryId.ToString(), [], "", "", null, true,
                beforeImageOnlyClear.GetProperty("version").GetString(), CancellationToken.None);
            Assert.AreEqual("branding_saved", clearImageOnly.Code);
            await using (var verify = await contextFactory.CreateDbContextAsync())
            {
                var row = await verify.Branding.SingleAsync(item => item.OrganizationId == libraryId);
                Assert.IsNull(row.LogoData);
                Assert.AreEqual("Library alt", row.LogoAltText);
            }
            var effectiveAfterImageClear = await factory.Services.GetRequiredService<PatronConfigurationService>()
                .GetBrandingAsync(libraryId, CancellationToken.None);
            Assert.IsNotNull(effectiveAfterImageClear);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, effectiveAfterImageClear.LogoData!);
            Assert.AreEqual("Library alt", effectiveAfterImageClear.LogoAltText);

            foreach (var withAlt in new[] { false, true })
            {
                await SeedBrandingAsync(true, withAlt ? "Library alt" : null);
                var beforeReset = await CurrentSettingsAsync();
                var oldVersion = beforeReset.GetProperty("version").GetString()!;
                using var response = await client.DeleteAsync(
                    $"/api/asap/staff/settings/logo?orgId={libraryId}&version={Uri.EscapeDataString(oldVersion)}");
                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("branding_saved", result.RootElement.GetProperty("code").GetString());
                var afterReset = await CurrentSettingsAsync();
                Assert.AreEqual(afterReset.GetProperty("version").GetString(),
                    result.RootElement.GetProperty("data").GetProperty("version").GetString());
                await AssertInheritedAsync(oldVersion);
            }
        }
        finally
        {
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            var libraryBranding = await cleanup.Branding.SingleOrDefaultAsync(item => item.OrganizationId == libraryId);
            if (libraryBranding is not null)
            {
                cleanup.Branding.Remove(libraryBranding);
            }
            var system = await cleanup.Branding.SingleAsync(item => item.OrganizationId == 1);
            if (originalSystem is null)
            {
                cleanup.Branding.Remove(system);
            }
            else
            {
                system.LogoData = originalSystem.LogoData;
                system.LogoContentType = originalSystem.LogoContentType;
                system.LogoFileName = originalSystem.LogoFileName;
                system.LogoAltText = originalSystem.LogoAltText;
                system.UpdatedUtc = originalSystem.UpdatedUtc;
            }
            await cleanup.SaveChangesAsync();
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;";
            command.Parameters.AddWithValue("@organizationId", libraryId);
            await command.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task SettingsStructuredNoEditSavePreservesThreeProvidersAndNonDefaultFormats()
    {
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<AdministrationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();

        await using var seed = await contextFactory.CreateDbContextAsync();
        var seededProviderFour = await seed.ExternalSearchProviders
            .SingleOrDefaultAsync(item => item.ProviderKey == "external_search_4");
        if (seededProviderFour is not null)
        {
            Assert.IsFalse(seededProviderFour.IsEnabled);
            Assert.AreEqual(string.Empty, seededProviderFour.Label);
            Assert.AreEqual(string.Empty, seededProviderFour.UrlTemplate);
        }
        var providers = await seed.ExternalSearchProviders.OrderBy(item => item.SortOrder).ToListAsync();
        Assert.AreEqual(3, providers.Count(item =>
            item.IsEnabled || !string.IsNullOrWhiteSpace(item.Label) || !string.IsNullOrWhiteSpace(item.UrlTemplate)));
        var providerTwo = providers.Single(item => item.ProviderKey == "external_search_2");
        var originalProviderLabel = providerTwo.Label;
        var book = await seed.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var dvd = await seed.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "dvd");
        var originalBook = FormatSnapshot.From(book);
        var originalDvd = FormatSnapshot.From(dvd);
        try
        {
            book.Label = "Corrective printed books";
            book.SortOrder = 43;
            book.IsEnabled = true;
            book.AuthorMode = "optional";
            book.AuthorLabel = "Corrective creator";
            book.IdentifierMode = "required";
            book.IdentifierLabel = "Corrective ISBN";
            book.PublicationMode = "hidden";
            dvd.Label = "Corrective video discs";
            dvd.SortOrder = 17;
            dvd.IsEnabled = false;
            dvd.MessageBehavior = "message";
            dvd.Message = "Corrective video message";
            await seed.SaveChangesAsync();

            var before = JsonSerializer.SerializeToElement(
                (await service.GetSettingsAsync(actor, "system", CancellationToken.None)).Data);
            var beforeFormats = before.GetProperty("stored").GetProperty("formats")
                .EnumerateArray().Select(StructuredFormat).ToArray();
            var beforeProviders = before.GetProperty("effective").GetProperty("externalSearchProviders")
                .EnumerateArray().Select(StructuredProvider).ToArray();
            Assert.AreEqual(3, beforeProviders.Length);
            Assert.IsFalse(beforeProviders.Any(item => item.key == "external_search_4"));
            Assert.AreEqual(3, before.GetProperty("stored").GetProperty("providers").GetArrayLength());
            var configuredSystemProviders = before.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("providers").EnumerateArray().ToArray();
            Assert.AreEqual(3, configuredSystemProviders.Length);
            Assert.IsFalse(configuredSystemProviders.Any(item =>
                item.GetProperty("key").GetString() == "external_search_4"));

            var noEdit = await service.SaveSettingsAsync(
                actor,
                JsonSerializer.SerializeToElement(new
                {
                    orgId = "system",
                    version = before.GetProperty("version").GetString(),
                    providers = beforeProviders,
                    formats = beforeFormats,
                    workflow = new
                    {
                        suggestionLimit = before.GetProperty("stored").GetProperty("workflow")
                            .GetProperty("suggestionLimit").GetInt32()
                    }
                }),
                CancellationToken.None);
            Assert.AreEqual("saved", noEdit.Code);

            var afterNoEdit = JsonSerializer.SerializeToElement(
                (await service.GetSettingsAsync(actor, "system", CancellationToken.None)).Data);
            AssertFormatState(afterNoEdit, "book", "Corrective printed books", 43, true, "none", null,
                "optional", "Corrective creator", "required", "Corrective ISBN", "hidden");
            AssertFormatState(afterNoEdit, "dvd", "Corrective video discs", 17, false, "message", "Corrective video message",
                "required", "Director/Actors/Producer", "hidden", "UPC", "required");
            Assert.AreEqual(3, afterNoEdit.GetProperty("stored").GetProperty("providers").GetArrayLength());

            var editedProviders = beforeProviders.Select(item => item.key == "external_search_2"
                ? item with { label = "Corrective edited research" }
                : item).ToArray();
            var edited = await service.SaveSettingsAsync(
                actor,
                JsonSerializer.SerializeToElement(new
                {
                    orgId = "system",
                    version = afterNoEdit.GetProperty("version").GetString(),
                    providers = editedProviders,
                    formats = beforeFormats
                }),
                CancellationToken.None);
            Assert.AreEqual("saved", edited.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual("Corrective edited research", await verify.ExternalSearchProviders
                .Where(item => item.ProviderKey == "external_search_2").Select(item => item.Label).SingleAsync());
            var verifiedProviderFour = await verify.ExternalSearchProviders
                .SingleOrDefaultAsync(item => item.ProviderKey == "external_search_4");
            if (seededProviderFour is null)
            {
                Assert.IsNull(verifiedProviderFour);
            }
            else
            {
                Assert.IsNotNull(verifiedProviderFour);
                Assert.AreEqual(seededProviderFour.IsEnabled, verifiedProviderFour.IsEnabled);
                Assert.AreEqual(seededProviderFour.Label, verifiedProviderFour.Label);
                Assert.AreEqual(seededProviderFour.UrlTemplate, verifiedProviderFour.UrlTemplate);
            }
            Assert.IsTrue(await verify.MaterialFormats.AnyAsync(item => item.IsEnabled),
                "An unrelated settings save must not disable every material format.");
            var verifiedBook = await verify.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
            var verifiedDvd = await verify.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "dvd");
            Assert.AreEqual("Corrective printed books", verifiedBook.Label);
            Assert.AreEqual("Corrective video discs", verifiedDvd.Label);
            Assert.IsFalse(verifiedDvd.IsEnabled);
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var restoreProvider = await restore.ExternalSearchProviders.SingleAsync(item => item.ProviderKey == "external_search_2");
            restoreProvider.Label = originalProviderLabel;
            originalBook.Apply(await restore.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book"));
            originalDvd.Apply(await restore.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "dvd"));
            await restore.SaveChangesAsync();
        }

        static StructuredProviderInput StructuredProvider(JsonElement item) => new(
            item.GetProperty("id").GetString()!,
            item.GetProperty("key").GetString()!,
            item.GetProperty("isEnabled").GetBoolean(),
            item.GetProperty("label").GetString()!,
            item.GetProperty("urlTemplate").GetString()!,
            item.GetProperty("sortOrder").GetInt32());

        static object StructuredFormat(JsonElement item) => new
        {
            id = item.GetProperty("id").GetString(),
            code = item.GetProperty("code").GetString(),
            ownerOrganizationId = item.GetProperty("ownerOrganizationId").GetString(),
            label = item.GetProperty("label").GetString(),
            sortOrder = item.GetProperty("sortOrder").GetInt32(),
            isEnabled = item.GetProperty("isEnabled").GetBoolean(),
            messageBehavior = OptionalString(item, "messageBehavior") ?? "none",
            message = OptionalString(item, "message"),
            title = new { mode = OptionalString(item, "titleMode") ?? "required", label = OptionalString(item, "titleLabel") ?? "Title" },
            author = new { mode = OptionalString(item, "authorMode") ?? "optional", label = OptionalString(item, "authorLabel") ?? "Author" },
            identifier = new { mode = OptionalString(item, "identifierMode") ?? "optional", label = OptionalString(item, "identifierLabel") ?? "Identifier number" },
            publication = new { mode = OptionalString(item, "publicationMode") ?? "optional", label = OptionalString(item, "publicationLabel") ?? "Publication Timing" },
            customFields = new Dictionary<string, object>()
        };

        static string? OptionalString(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

        static void AssertFormatState(
            JsonElement settings,
            string code,
            string label,
            int sortOrder,
            bool enabled,
            string behavior,
            string? message,
            string authorMode,
            string authorLabel,
            string identifierMode,
            string identifierLabel,
            string publicationMode)
        {
            var format = settings.GetProperty("stored").GetProperty("formats").EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() == code);
            Assert.AreEqual(label, format.GetProperty("label").GetString());
            Assert.AreEqual(sortOrder, format.GetProperty("sortOrder").GetInt32());
            Assert.AreEqual(enabled, format.GetProperty("isEnabled").GetBoolean());
            Assert.AreEqual(behavior, format.GetProperty("messageBehavior").GetString());
            Assert.AreEqual(message, OptionalString(format, "message"));
            Assert.AreEqual(authorMode, format.GetProperty("authorMode").GetString());
            Assert.AreEqual(authorLabel, format.GetProperty("authorLabel").GetString());
            Assert.AreEqual(identifierMode, format.GetProperty("identifierMode").GetString());
            Assert.AreEqual(identifierLabel, format.GetProperty("identifierLabel").GetString());
            Assert.AreEqual(publicationMode, format.GetProperty("publicationMode").GetString());
        }
    }

    [TestMethod]
    public async Task LibraryStructuredNoEditSavePreservesOverridesCustomFieldsAndAutoClaimRules()
    {
        const int libraryId = 92323;
        var historicalStaffUpn = $"inactive-auto-claim-{Guid.NewGuid():N}@example.org";
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var service = factory.Services.GetRequiredService<AdministrationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();

        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                seed.Organizations.Add(new Organization
                {
                    Id = libraryId,
                    DisplayName = "Corrective settings library",
                    Abbreviation = "CSL",
                    IsActive = true
                });
                await seed.SaveChangesAsync();
                seed.WorkflowSettings.Add(new WorkflowSettings
                {
                    OrganizationId = libraryId,
                    OutstandingTimeoutDays = 72,
                    PendingHoldTimeoutDays = 19,
                    AutoPromote = false
                });
                seed.EmailTemplates.Add(new EmailTemplate
                {
                    OrganizationId = libraryId,
                    TemplateKey = "rejection:local-budget",
                    DisplayName = "Local budget review",
                    SubjectTemplate = "Harbor budget decision",
                    BodyTemplate = "Harbor deferred this request because of local budget.",
                    IsCustom = true,
                    SortOrder = 80
                });
                var historicalStaff = new StaffUser
                {
                    UserPrincipalName = historicalStaffUpn,
                    NormalizedUserPrincipalName = historicalStaffUpn.ToUpperInvariant(),
                    Role = "staff",
                    OrganizationId = libraryId,
                    IsActive = false
                };
                seed.StaffUsers.Add(historicalStaff);
                await seed.SaveChangesAsync();
                seed.CommonCreatorSets.Add(new CommonCreatorSet { OrganizationId = libraryId });
                seed.CommonCreatorTerms.AddRange(
                    new CommonCreatorTerm { OrganizationId = libraryId, Value = "Library Creator B", SortOrder = 17 },
                    new CommonCreatorTerm { OrganizationId = libraryId, Value = "Library Creator A", SortOrder = 41 });
                seed.PatronCodeEligibilitySets.Add(new PatronCodeEligibilitySet { OrganizationId = libraryId });
                seed.PatronCodeEligibilityMembers.Add(new PatronCodeEligibilityMember
                {
                    OrganizationId = libraryId,
                    PatronCodeId = "1"
                });
                seed.PublicationOptionSets.Add(new PublicationOptionSet { OrganizationId = libraryId });
                seed.PublicationOptions.Add(new PublicationOption
                {
                    OrganizationId = libraryId,
                    OptionKey = "local_release",
                    Label = "Local release",
                    IsEnabled = true,
                    SortOrder = 19
                });
                var provider = await seed.ExternalSearchProviders.SingleAsync(item => item.ProviderKey == "external_search_2");
                seed.ExternalSearchProviderOverrides.Add(new ExternalSearchProviderOverride
                {
                    LibraryOrganizationId = libraryId,
                    ExternalSearchProviderId = provider.Id,
                    Label = "Library research index"
                });
                var book = await seed.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
                seed.MaterialFormatOverrides.Add(new MaterialFormatOverride
                {
                    LibraryOrganizationId = libraryId,
                    MaterialFormatId = book.Id,
                    Label = "Library books",
                    SortOrder = 23,
                    IdentifierMode = "hidden",
                    IdentifierLabel = "Local ISBN"
                });
                var customFormat = new MaterialFormat
                {
                    OwnerOrganizationId = libraryId,
                    Code = "corrective_zine",
                    Label = "Corrective zine",
                    SortOrder = 57,
                    IsEnabled = true,
                    MessageBehavior = "message",
                    Message = "Bring local zines to the desk.",
                    TitleMode = "required",
                    TitleLabel = "Zine title",
                    AuthorMode = "optional",
                    AuthorLabel = "Maker",
                    IdentifierMode = "hidden",
                    IdentifierLabel = "Identifier",
                    PublicationMode = "optional",
                    PublicationLabel = "Issue date",
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                };
                var customField = new PatronCustomField
                {
                    LibraryOrganizationId = libraryId,
                    FieldKey = "audience_note",
                    FieldType = "select",
                    Label = "Audience note",
                    HelpText = "Choose the intended audience.",
                    IsEnabled = true,
                    SortOrder = 37
                };
                seed.MaterialFormats.Add(customFormat);
                seed.PatronCustomFields.Add(customField);
                await seed.SaveChangesAsync();
                seed.PatronCustomFieldOptions.AddRange(
                    new PatronCustomFieldOption
                    {
                        PatronCustomFieldId = customField.Id,
                        OptionKey = "general",
                        Label = "General readers",
                        IsEnabled = true,
                        SortOrder = 13
                    },
                    new PatronCustomFieldOption
                    {
                        PatronCustomFieldId = customField.Id,
                        OptionKey = "specialist",
                        Label = "Specialists",
                        IsEnabled = false,
                        SortOrder = 31
                    });
                seed.MaterialFormatCustomFieldRules.Add(new MaterialFormatCustomFieldRule
                {
                    LibraryOrganizationId = libraryId,
                    MaterialFormatId = customFormat.Id,
                    PatronCustomFieldId = customField.Id,
                    Mode = "required",
                    LabelOverride = "Zine audience"
                });
                seed.FormatAutoClaimRules.Add(new FormatAutoClaimRule
                {
                    LibraryOrganizationId = libraryId,
                    MaterialFormatId = customFormat.Id,
                    StaffUserId = actor.Id,
                    IsActive = true,
                    CreatedUtc = DateTime.UtcNow
                });
                seed.FormatAutoClaimRules.Add(new FormatAutoClaimRule
                {
                    LibraryOrganizationId = libraryId,
                    MaterialFormatId = book.Id,
                    StaffUserId = historicalStaff.Id,
                    IsActive = false,
                    CreatedUtc = DateTime.UtcNow.AddDays(-10),
                    DeactivatedUtc = DateTime.UtcNow.AddDays(-5)
                });
                await seed.SaveChangesAsync();
            }

            var beforeData =
                (await service.GetSettingsAsync(actor, libraryId.ToString(), CancellationToken.None)).Data;
            var before = JsonSerializer.SerializeToElement(beforeData);
            using (var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                       TestArtifactPaths.FindRepositoryFile(
                           "tests", "fixtures", "settings", "canonical-library-settings-response.json"))))
            {
                var httpShapedBefore = JsonSerializer.SerializeToElement(
                    beforeData, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                AssertJsonShape(fixtureDocument.RootElement, httpShapedBefore, "$settings");
                var expectedFormatRule = fixtureDocument.RootElement.GetProperty("effective")
                    .GetProperty("formats").EnumerateArray()
                    .Single(item => item.GetProperty("code").GetString() == "zine")
                    .GetProperty("customFields").GetProperty("audience_note");
                var actualFormatRule = before.GetProperty("effective").GetProperty("formats")
                    .EnumerateArray().Single(item => item.GetProperty("code").GetString() == "corrective_zine")
                    .GetProperty("customFields").GetProperty("audience_note");
                AssertJsonShape(expectedFormatRule, actualFormatRule, "$settings.effective.formats[].customFields.audience_note");
            }
            var stored = before.GetProperty("stored");
            var effective = before.GetProperty("effective");
            byte[] creatorsVersion;
            long[] creatorTermIds;
            byte[] patronCodesVersion;
            byte[] publicationVersion;
            long[] publicationOptionIds;
            byte[] providerOverrideVersion;
            byte[] formatOverrideVersion;
            long customFormatId;
            byte[] customFormatVersion;
            long customFieldId;
            byte[] customFieldVersion;
            long[] customFieldOptionIds;
            byte[] customFieldRuleVersion;
            long customTemplateId;
            byte[] customTemplateVersion;
            Dictionary<long, byte[]> autoClaimRuleVersions;
            await using (var noEditSnapshot = await contextFactory.CreateDbContextAsync())
            {
                creatorsVersion = (await noEditSnapshot.CommonCreatorSets.AsNoTracking()
                    .SingleAsync(item => item.OrganizationId == libraryId)).RowVersion.ToArray();
                creatorTermIds = await noEditSnapshot.CommonCreatorTerms.AsNoTracking()
                    .Where(item => item.OrganizationId == libraryId).OrderBy(item => item.SortOrder)
                    .Select(item => item.Id).ToArrayAsync();
                patronCodesVersion = (await noEditSnapshot.PatronCodeEligibilitySets.AsNoTracking()
                    .SingleAsync(item => item.OrganizationId == libraryId)).RowVersion.ToArray();
                publicationVersion = (await noEditSnapshot.PublicationOptionSets.AsNoTracking()
                    .SingleAsync(item => item.OrganizationId == libraryId)).RowVersion.ToArray();
                publicationOptionIds = await noEditSnapshot.PublicationOptions.AsNoTracking()
                    .Where(item => item.OrganizationId == libraryId).Select(item => item.Id).ToArrayAsync();
                providerOverrideVersion = (await noEditSnapshot.ExternalSearchProviderOverrides.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == libraryId)).RowVersion.ToArray();
                formatOverrideVersion = (await noEditSnapshot.MaterialFormatOverrides.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == libraryId)).RowVersion.ToArray();
                var customFormat = await noEditSnapshot.MaterialFormats.AsNoTracking()
                    .SingleAsync(item => item.OwnerOrganizationId == libraryId && item.Code == "corrective_zine");
                customFormatId = customFormat.Id;
                customFormatVersion = customFormat.RowVersion.ToArray();
                var customField = await noEditSnapshot.PatronCustomFields.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == libraryId);
                customFieldId = customField.Id;
                customFieldVersion = customField.RowVersion.ToArray();
                customFieldOptionIds = await noEditSnapshot.PatronCustomFieldOptions.AsNoTracking()
                    .Where(item => item.PatronCustomFieldId == customFieldId).OrderBy(item => item.SortOrder)
                    .Select(item => item.Id).ToArrayAsync();
                customFieldRuleVersion = (await noEditSnapshot.MaterialFormatCustomFieldRules.AsNoTracking()
                    .SingleAsync(item => item.LibraryOrganizationId == libraryId)).RowVersion.ToArray();
                var customTemplate = await noEditSnapshot.EmailTemplates.AsNoTracking()
                    .SingleAsync(item => item.OrganizationId == libraryId && item.TemplateKey == "rejection:local-budget");
                customTemplateId = customTemplate.Id;
                customTemplateVersion = customTemplate.RowVersion.ToArray();
                autoClaimRuleVersions = await noEditSnapshot.FormatAutoClaimRules.AsNoTracking()
                    .Where(item => item.LibraryOrganizationId == libraryId)
                    .ToDictionaryAsync(item => item.Id, item => item.RowVersion.ToArray());
            }
            var effectiveFormats = effective.GetProperty("formats").EnumerateArray()
                .ToDictionary(item => item.GetProperty("code").GetString()!);
            var formats = stored.GetProperty("formats").EnumerateArray().Select(item =>
            {
                var code = item.GetProperty("code").GetString()!;
                var resolved = effectiveFormats[code];
                return (object)new
                {
                    id = item.GetProperty("id").GetString(),
                    code,
                    ownerOrganizationId = item.GetProperty("ownerOrganizationId").GetString(),
                    label = item.GetProperty("label").GetString(),
                    sortOrder = item.GetProperty("sortOrder").GetInt32(),
                    isEnabled = item.GetProperty("isEnabled").GetBoolean(),
                    messageBehavior = resolved.GetProperty("messageBehavior").GetString(),
                    message = resolved.GetProperty("message").ValueKind == JsonValueKind.Null ? null : resolved.GetProperty("message").GetString(),
                    title = resolved.GetProperty("title").Clone(),
                    author = resolved.GetProperty("author").Clone(),
                    identifier = resolved.GetProperty("identifier").Clone(),
                    publication = resolved.GetProperty("publication").Clone(),
                    customFields = resolved.GetProperty("customFields").Clone()
                };
            }).ToArray();
            var providers = effective.GetProperty("externalSearchProviders").EnumerateArray().Select(item => new
            {
                id = item.GetProperty("id").GetString(),
                key = item.GetProperty("key").GetString(),
                isEnabled = item.GetProperty("isEnabled").GetBoolean(),
                label = item.GetProperty("label").GetString(),
                urlTemplate = item.GetProperty("urlTemplate").GetString(),
                sortOrder = item.GetProperty("sortOrder").GetInt32()
            }).ToArray();
            var creators = stored.GetProperty("commonCreators").EnumerateArray().Select(item => item.GetString()).ToArray();

            var saved = await service.SaveSettingsAsync(
                actor,
                JsonSerializer.SerializeToElement(new
                {
                    orgId = libraryId.ToString(),
                    version = before.GetProperty("version").GetString(),
                    workflow = new { commonAuthorsList = string.Join('\n', creators) },
                    ui_text = new
                    {
                        publicationOptions = stored.GetProperty("libraryOverride").GetProperty("publicationOptions")
                            .GetProperty("values").Clone()
                    },
                    providers,
                    formats,
                    customFields = stored.GetProperty("customFields").Clone(),
                    formatClaimRules = stored.GetProperty("autoClaimRules").Clone()
                }),
                CancellationToken.None);
            Assert.AreEqual("saved", saved.Code);

            await using var verify = await contextFactory.CreateDbContextAsync();
            CollectionAssert.AreEqual(
                new[] { "Library Creator B", "Library Creator A" },
                await verify.CommonCreatorTerms.Where(item => item.OrganizationId == libraryId)
                    .OrderBy(item => item.SortOrder).Select(item => item.Value).ToArrayAsync());
            Assert.AreEqual("Library research index", await verify.ExternalSearchProviderOverrides
                .Where(item => item.LibraryOrganizationId == libraryId).Select(item => item.Label).SingleAsync());
            Assert.AreEqual("Library books", await verify.MaterialFormatOverrides
                .Where(item => item.LibraryOrganizationId == libraryId).Select(item => item.Label).SingleAsync());
            Assert.AreEqual(1, await verify.MaterialFormats.CountAsync(item => item.OwnerOrganizationId == libraryId && item.Code == "corrective_zine"));
            var field = await verify.PatronCustomFields.SingleAsync(item => item.LibraryOrganizationId == libraryId);
            Assert.AreEqual("audience_note", field.FieldKey);
            Assert.AreEqual("Choose the intended audience.", field.HelpText);
            CollectionAssert.AreEqual(
                new[] { "general", "specialist" },
                await verify.PatronCustomFieldOptions.Where(item => item.PatronCustomFieldId == field.Id)
                    .OrderBy(item => item.SortOrder).Select(item => item.OptionKey).ToArrayAsync());
            Assert.AreEqual(1, await verify.MaterialFormatCustomFieldRules.CountAsync(item =>
                item.LibraryOrganizationId == libraryId && item.Mode == "required" && item.LabelOverride == "Zine audience"));
            Assert.AreEqual(actor.Id, await verify.FormatAutoClaimRules.Where(item =>
                    item.LibraryOrganizationId == libraryId && item.IsActive)
                .Select(item => item.StaffUserId).SingleAsync());
            CollectionAssert.AreEqual(creatorsVersion, (await verify.CommonCreatorSets.AsNoTracking()
                .SingleAsync(item => item.OrganizationId == libraryId)).RowVersion,
                "an unchanged creator set must not be rewritten");
            CollectionAssert.AreEqual(creatorTermIds, await verify.CommonCreatorTerms.AsNoTracking()
                .Where(item => item.OrganizationId == libraryId).OrderBy(item => item.SortOrder)
                .Select(item => item.Id).ToArrayAsync(), "unchanged creator term identities must be preserved");
            CollectionAssert.AreEqual(patronCodesVersion, (await verify.PatronCodeEligibilitySets.AsNoTracking()
                .SingleAsync(item => item.OrganizationId == libraryId)).RowVersion,
                "an unchanged patron-code set must not be rewritten");
            CollectionAssert.AreEqual(publicationVersion, (await verify.PublicationOptionSets.AsNoTracking()
                .SingleAsync(item => item.OrganizationId == libraryId)).RowVersion,
                "an unchanged publication option set must not be rewritten");
            CollectionAssert.AreEqual(publicationOptionIds, await verify.PublicationOptions.AsNoTracking()
                .Where(item => item.OrganizationId == libraryId).Select(item => item.Id).ToArrayAsync(),
                "unchanged publication option identities must be preserved");
            CollectionAssert.AreEqual(providerOverrideVersion, (await verify.ExternalSearchProviderOverrides.AsNoTracking()
                .SingleAsync(item => item.LibraryOrganizationId == libraryId)).RowVersion,
                "an unchanged provider override must not be rewritten");
            CollectionAssert.AreEqual(formatOverrideVersion, (await verify.MaterialFormatOverrides.AsNoTracking()
                .SingleAsync(item => item.LibraryOrganizationId == libraryId)).RowVersion,
                "an unchanged format override must not be rewritten");
            var reloadedCustomFormat = await verify.MaterialFormats.AsNoTracking()
                .SingleAsync(item => item.Id == customFormatId);
            CollectionAssert.AreEqual(customFormatVersion, reloadedCustomFormat.RowVersion,
                "an unchanged library-owned format must not be rewritten");
            var reloadedCustomField = await verify.PatronCustomFields.AsNoTracking()
                .SingleAsync(item => item.Id == customFieldId);
            CollectionAssert.AreEqual(customFieldVersion, reloadedCustomField.RowVersion,
                "an unchanged custom field must not be rewritten");
            CollectionAssert.AreEqual(customFieldOptionIds, await verify.PatronCustomFieldOptions.AsNoTracking()
                .Where(item => item.PatronCustomFieldId == customFieldId).OrderBy(item => item.SortOrder)
                .Select(item => item.Id).ToArrayAsync(), "unchanged custom field option identities must be preserved");
            CollectionAssert.AreEqual(customFieldRuleVersion, (await verify.MaterialFormatCustomFieldRules.AsNoTracking()
                .SingleAsync(item => item.LibraryOrganizationId == libraryId)).RowVersion,
                "an unchanged format custom-field rule must not be rewritten");
            CollectionAssert.AreEqual(customTemplateVersion, (await verify.EmailTemplates.AsNoTracking()
                .SingleAsync(item => item.Id == customTemplateId)).RowVersion,
                "an unchanged library-owned rejection template must not be rewritten");
            foreach (var (ruleId, rowVersion) in autoClaimRuleVersions)
            {
                CollectionAssert.AreEqual(rowVersion, (await verify.FormatAutoClaimRules.AsNoTracking()
                    .SingleAsync(item => item.Id == ruleId)).RowVersion,
                    "unchanged current and historical auto-claim rules must not be rewritten");
            }
            var historicalRule = await verify.FormatAutoClaimRules.AsNoTracking()
                .SingleAsync(item => item.LibraryOrganizationId == libraryId && !item.IsActive);
            Assert.AreEqual(historicalStaffUpn.ToUpperInvariant(), await verify.StaffUsers.AsNoTracking()
                .Where(item => item.Id == historicalRule.StaffUserId).Select(item => item.NormalizedUserPrincipalName).SingleAsync());
            Assert.IsFalse(historicalRule.IsActive, "an inactive historical assignment must remain inactive after an unrelated save");
            Assert.AreEqual(1, await verify.PatronCodeEligibilityMembers.CountAsync(item => item.OrganizationId == libraryId));

            var intentionalLabel = "Community audience";
            var formatsForLabelEdit = JsonNode.Parse(JsonSerializer.Serialize(formats))!.AsArray();
            var customFormatNode = formatsForLabelEdit.Single(item => item?["code"]?.GetValue<string>() == "corrective_zine")!;
            customFormatNode["customFields"]!["audience_note"]!["labelOverride"] = intentionalLabel;
            using (var currentSettings = await ReadSettingsDocumentAsync(client, libraryId.ToString()))
            using (var saveLabel = await SaveSettingsDocumentAsync(client, currentSettings.RootElement,
                       libraryId.ToString(), new Dictionary<string, object?> { ["formats"] = formatsForLabelEdit }))
            {
                Assert.AreEqual("saved", saveLabel.RootElement.GetProperty("code").GetString());
            }
            using (var reloaded = await ReadSettingsDocumentAsync(client, libraryId.ToString()))
            {
                Assert.AreEqual(intentionalLabel, reloaded.RootElement.GetProperty("effective").GetProperty("formats")
                    .EnumerateArray().Single(item => item.GetProperty("code").GetString() == "corrective_zine")
                    .GetProperty("customFields").GetProperty("audience_note").GetProperty("labelOverride").GetString(),
                    "an intentional format-specific labelOverride edit must persist through the Settings endpoint and reload");
            }
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText =
                """
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @organizationId;
                DELETE o FROM [asap].[PatronCustomFieldOption] o
                    INNER JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId]
                    WHERE f.[LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[ExternalSearchProviderOverride] WHERE [LibraryOrganizationId] = @organizationId;
                DELETE FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @organizationId;
                DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId;
                DELETE FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @historicalStaff;
                DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;
                """;
            cleanup.Parameters.AddWithValue("@organizationId", libraryId);
            cleanup.Parameters.AddWithValue("@historicalStaff", historicalStaffUpn.ToUpperInvariant());
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static void AssertJsonShape(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            Assert.AreEqual(JsonValueKind.Object, actual.ValueKind, $"{path} must be an object.");
            foreach (var expectedProperty in expected.EnumerateObject())
            {
                Assert.IsTrue(actual.TryGetProperty(expectedProperty.Name, out var actualValue),
                    $"{path}.{expectedProperty.Name} is missing from the backend response.");
                if (expectedProperty.Name is "customFields" or "duplicateStatusLabels")
                {
                    continue;
                }

                if ((expectedProperty.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) &&
                    actualValue.ValueKind == expectedProperty.Value.ValueKind)
                {
                    AssertJsonShape(expectedProperty.Value, actualValue, $"{path}.{expectedProperty.Name}");
                }
            }

            return;
        }

        if (expected.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Assert.AreEqual(JsonValueKind.Array, actual.ValueKind, $"{path} must be an array.");
        if (expected.GetArrayLength() == 0 || actual.GetArrayLength() == 0)
        {
            return;
        }

        var expectedSample = expected[0];
        var actualSample = actual[0];
        if ((expectedSample.ValueKind is JsonValueKind.Object or JsonValueKind.Array) &&
            actualSample.ValueKind == expectedSample.ValueKind)
        {
            AssertJsonShape(expectedSample, actualSample, $"{path}[0]");
        }
    }

    [TestMethod]
    public async Task CustomFormatDeleteUsesRowVersionRejectsReferencesAndPersistsDeletion()
    {
        const int libraryId = 92326;
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long removableFormatId;
        long referencedFormatId;

        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            setup.Organizations.Add(new Organization
            {
                Id = libraryId,
                DisplayName = "Custom format deletion library",
                Abbreviation = "CFD",
                IsActive = true
            });
            await setup.SaveChangesAsync();
            var now = DateTime.UtcNow;
            var removable = new MaterialFormat
            {
                OwnerOrganizationId = libraryId,
                Code = "closure_removable",
                Label = "Closure removable",
                SortOrder = 91,
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            var referenced = new MaterialFormat
            {
                OwnerOrganizationId = libraryId,
                Code = "closure_referenced",
                Label = "Closure referenced",
                SortOrder = 92,
                IsEnabled = true,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            setup.MaterialFormats.AddRange(removable, referenced);
            await setup.SaveChangesAsync();
            removableFormatId = removable.Id;
            referencedFormatId = referenced.Id;
        }

        try
        {
            async Task<JsonDocument> ReadSettings(string scope) => await ReadSettingsDocumentAsync(client, scope);
            async Task<string> DeleteFormat(HttpClient requestClient, long id, string version)
            {
                var uri = $"/api/asap/staff/settings/formats/{id}?version={Uri.EscapeDataString(version)}";
                using var response = await requestClient.DeleteAsync(uri);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.GetProperty("code").GetString()!;
            }
            string ReadFormatVersion(JsonElement settings, long id) => settings.GetProperty("stored").GetProperty("formats")
                .EnumerateArray().Single(format => format.GetProperty("id").GetString() == id.ToString())
                .GetProperty("version").GetString()!;

            using var current = await ReadSettings(libraryId.ToString());
            var removableVersion = ReadFormatVersion(current.RootElement, removableFormatId);
            var referencedVersion = ReadFormatVersion(current.RootElement, referencedFormatId);
            Assert.AreEqual("stale_version", await DeleteFormat(client, removableFormatId, Convert.ToBase64String(new byte[8])));
            await using (var afterStale = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsTrue(await afterStale.MaterialFormats.AnyAsync(item => item.Id == removableFormatId),
                    "a stale delete must leave the custom format untouched");
            }

            await using (var seedReference = await contextFactory.CreateDbContextAsync())
            {
                seedReference.FormatAutoClaimRules.Add(new FormatAutoClaimRule
                {
                    LibraryOrganizationId = libraryId,
                    MaterialFormatId = referencedFormatId,
                    StaffUserId = null,
                    IsActive = false,
                    CreatedUtc = DateTime.UtcNow
                });
                await seedReference.SaveChangesAsync();
            }
            Assert.AreEqual("format_referenced", await DeleteFormat(client, referencedFormatId, referencedVersion),
                "even inactive historical auto-claim rows keep the referenced format durable");
            await using (var removeReference = await contextFactory.CreateDbContextAsync())
            {
                removeReference.FormatAutoClaimRules.RemoveRange(await removeReference.FormatAutoClaimRules
                    .Where(item => item.MaterialFormatId == referencedFormatId).ToListAsync());
                await removeReference.SaveChangesAsync();
            }

            Assert.AreEqual("format_deleted", await DeleteFormat(client, removableFormatId, removableVersion));
            using (var afterDelete = await ReadSettings(libraryId.ToString()))
            {
                Assert.IsFalse(afterDelete.RootElement.GetProperty("stored").GetProperty("formats")
                    .EnumerateArray().Any(format => format.GetProperty("id").GetString() == removableFormatId.ToString()),
                    "a successful custom format delete must remain deleted after GET reload");
            }
            await using (var deletedSql = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsFalse(await deletedSql.MaterialFormats.AnyAsync(item => item.Id == removableFormatId));
            }

            using (var latest = await ReadSettings(libraryId.ToString()))
            {
                var systemBook = latest.RootElement.GetProperty("stored").GetProperty("formats")
                    .EnumerateArray().Single(format => format.GetProperty("ownerOrganizationId").GetString() == "1" &&
                        format.GetProperty("code").GetString() == "book");
                Assert.AreEqual("system_format_durable", await DeleteFormat(client,
                    long.Parse(systemBook.GetProperty("id").GetString()!), systemBook.GetProperty("version").GetString()!));
            }

            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var unauthorized = await anonymous.DeleteAsync(
                $"/api/asap/staff/settings/formats/{referencedFormatId}?version={Uri.EscapeDataString(referencedVersion)}");
            Assert.AreEqual(System.Net.HttpStatusCode.Redirect, unauthorized.StatusCode,
                "the direct delete endpoint must remain behind staff authorization");
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @organizationId; DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId; DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId; DELETE FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = @organizationId; DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;";
            cleanup.Parameters.AddWithValue("@organizationId", libraryId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task SettingsTemplatesPreserveSystemInheritanceAndLibraryMutationSemantics()
    {
        const int libraryId = 92324;
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long systemTemplateId;
        string? originalSubject;
        string? originalBody;
        string? originalDisplayName;
        bool originalHidden;
        int originalSortOrder;
        byte[] originalVersion;

        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var systemTemplate = await setup.EmailTemplates.SingleAsync(item =>
                item.OrganizationId == 1 && item.TemplateKey == "suggestion_submitted");
            systemTemplateId = systemTemplate.Id;
            originalSubject = systemTemplate.SubjectTemplate;
            originalBody = systemTemplate.BodyTemplate;
            originalDisplayName = systemTemplate.DisplayName;
            originalHidden = systemTemplate.IsHidden;
            originalSortOrder = systemTemplate.SortOrder;
            originalVersion = systemTemplate.RowVersion.ToArray();
            setup.Organizations.Add(new Organization
            {
                Id = libraryId,
                DisplayName = "Template closure library",
                Abbreviation = "TCL",
                IsActive = true
            });
            await setup.SaveChangesAsync();
        }

        try
        {
            async Task<JsonElement> ReadSettings(string scope)
            {
                using var result = await ReadSettingsDocumentAsync(client, scope);
                return result.RootElement.Clone();
            }

            async Task SaveSettings(string scope, JsonElement before, object emails, object? uiText = null)
            {
                using var result = await SaveSettingsDocumentAsync(client, before, scope,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["emails"] = emails,
                        ["workflow"] = new { },
                        ["ui_text"] = uiText ?? new { }
                    });
                Assert.AreEqual("saved", result.RootElement.GetProperty("code").GetString());
            }

            var systemBefore = await ReadSettings("system");
            using (var systemFixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                       TestArtifactPaths.FindRepositoryFile(
                           "tests", "fixtures", "settings", "canonical-system-settings-response.json"))))
            {
                AssertJsonShape(systemFixture.RootElement, systemBefore, "$systemSettings");
            }
            await SaveSettings("system", systemBefore, new { postmarkToken = "", clearPostmarkToken = false });
            await using (var afterSystemNoEdit = await contextFactory.CreateDbContextAsync())
            {
                var current = await afterSystemNoEdit.EmailTemplates.AsNoTracking().SingleAsync(item => item.Id == systemTemplateId);
                Assert.AreEqual(originalSubject, current.SubjectTemplate);
                Assert.AreEqual(originalBody, current.BodyTemplate);
                CollectionAssert.AreEqual(originalVersion, current.RowVersion,
                    "a system no-edit email save must not touch a customized persisted template");
            }

            var editedSystemSubject = $"Closure system subject {Guid.NewGuid():N}";
            var systemBeforeEdit = await ReadSettings("system");
            await SaveSettings("system", systemBeforeEdit, new Dictionary<string, object?>
            {
                ["postmarkToken"] = "",
                ["clearPostmarkToken"] = false,
                ["suggestion_submitted"] = new { subject = editedSystemSubject }
            });
            await using (var afterSystemEdit = await contextFactory.CreateDbContextAsync())
            {
                var current = await afterSystemEdit.EmailTemplates.AsNoTracking().SingleAsync(item => item.Id == systemTemplateId);
                Assert.AreEqual(editedSystemSubject, current.SubjectTemplate,
                    "editing one standard template must update that persisted template");
                Assert.AreEqual(originalBody, current.BodyTemplate,
                    "editing a subject must preserve the existing template body");
            }

            var libraryBefore = await ReadSettings(libraryId.ToString());
            using (var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
                       TestArtifactPaths.FindRepositoryFile(
                           "tests", "fixtures", "settings", "canonical-library-settings-response.json"))))
            {
                AssertJsonShape(fixtureDocument.RootElement, libraryBefore, "$settings");
            }
            var libraryStored = libraryBefore.GetProperty("stored");
            var libraryEffective = libraryBefore.GetProperty("effective");
            var noEditProviders = libraryStored.GetProperty("providers").EnumerateArray().Select(provider =>
            {
                var resolved = libraryEffective.GetProperty("externalSearchProviders").EnumerateArray()
                    .Single(item => item.GetProperty("key").GetString() == provider.GetProperty("key").GetString());
                return (object)new
                {
                    id = provider.GetProperty("id").GetString(),
                    key = provider.GetProperty("key").GetString(),
                    isEnabled = provider.GetProperty("isEnabled").GetBoolean(),
                    label = provider.GetProperty("label").GetString(),
                    urlTemplate = provider.GetProperty("urlTemplate").GetString(),
                    sortOrder = resolved.GetProperty("sortOrder").GetInt32(),
                    overridden = provider.GetProperty("overridden").GetBoolean()
                };
            }).ToArray();
            var noEditFormats = libraryStored.GetProperty("formats").EnumerateArray().Select(format =>
            {
                var resolved = libraryEffective.GetProperty("formats").EnumerateArray()
                    .Single(item => item.GetProperty("code").GetString() == format.GetProperty("code").GetString());
                return (object)new
                {
                    id = format.GetProperty("id").GetString(),
                    code = format.GetProperty("code").GetString(),
                    ownerOrganizationId = format.GetProperty("ownerOrganizationId").GetString(),
                    label = format.GetProperty("label").GetString(),
                    sortOrder = format.GetProperty("sortOrder").GetInt32(),
                    isEnabled = format.GetProperty("isEnabled").GetBoolean(),
                    messageBehavior = format.GetProperty("messageBehavior").GetString(),
                    message = format.GetProperty("message").ValueKind == JsonValueKind.Null
                        ? null
                        : format.GetProperty("message").GetString(),
                    title = resolved.GetProperty("title").Clone(),
                    author = resolved.GetProperty("author").Clone(),
                    identifier = resolved.GetProperty("identifier").Clone(),
                    publication = resolved.GetProperty("publication").Clone(),
                    customFields = resolved.GetProperty("customFields").Clone(),
                    overridden = format.GetProperty("overridden").GetBoolean()
                };
            }).ToArray();
            var noEditCustomFields = libraryStored.GetProperty("customFields").EnumerateArray()
                .Select(item => (object)item.Clone()).ToArray();
            var noEditActiveClaims = libraryStored.GetProperty("autoClaimRules").EnumerateArray()
                .Where(item => item.GetProperty("active").GetBoolean())
                .Select(item => (object)new
                {
                    materialFormatId = item.GetProperty("materialFormatId").GetString(),
                    staffUserId = item.GetProperty("staffUserId").GetString(),
                    active = true
                }).ToArray();
            using (var noEditPost = await SaveSettingsDocumentAsync(client, libraryBefore, libraryId.ToString(),
                       new Dictionary<string, object?>(StringComparer.Ordinal)
                       {
                           ["workflow"] = new { },
                           ["ui_text"] = new { },
                           ["emails"] = new { postmarkToken = "", clearPostmarkToken = false },
                           ["providers"] = noEditProviders,
                           ["formats"] = noEditFormats,
                           ["customFields"] = noEditCustomFields,
                           ["formatClaimRules"] = noEditActiveClaims
                       }))
            {
                Assert.AreEqual("saved", noEditPost.RootElement.GetProperty("code").GetString());
            }
            await using (var afterLibraryNoEdit = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsFalse(await afterLibraryNoEdit.WorkflowSettings.AnyAsync(item => item.OrganizationId == libraryId),
                    "an empty workflow payload must not create a local workflow override row");
                Assert.IsFalse(await afterLibraryNoEdit.PatronSettings.AnyAsync(item => item.OrganizationId == libraryId),
                    "an empty patron payload must not create a local patron override row");
                Assert.IsFalse(await afterLibraryNoEdit.EmailSettings.AnyAsync(item => item.OrganizationId == libraryId),
                    "an inherited sender no-edit save must not create an empty local email row");
                Assert.IsFalse(await afterLibraryNoEdit.EmailTemplates.AnyAsync(item => item.OrganizationId == libraryId),
                    "an inherited template no-edit save must not create a library template override");
                Assert.IsFalse(await afterLibraryNoEdit.Branding.AnyAsync(item => item.OrganizationId == libraryId),
                    "an inherited logo-alt no-edit save must not create local branding");
                Assert.IsFalse(await afterLibraryNoEdit.CommonCreatorSets.AnyAsync(item => item.OrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.PatronCodeEligibilitySets.AnyAsync(item => item.OrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.PublicationOptionSets.AnyAsync(item => item.OrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.ExternalSearchProviderOverrides.AnyAsync(item => item.LibraryOrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.MaterialFormatOverrides.AnyAsync(item => item.LibraryOrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.MaterialFormats.AnyAsync(item => item.OwnerOrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.PatronCustomFields.AnyAsync(item => item.LibraryOrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.MaterialFormatCustomFieldRules.AnyAsync(item => item.LibraryOrganizationId == libraryId));
                Assert.IsFalse(await afterLibraryNoEdit.FormatAutoClaimRules.AnyAsync(item => item.LibraryOrganizationId == libraryId));
            }

            var blankBranding = await ReadSettings(libraryId.ToString());
            await SaveSettings(libraryId.ToString(), blankBranding,
                new { postmarkToken = "", clearPostmarkToken = false }, new { logoAlt = "" });
            await using (var afterBlankBranding = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsFalse(await afterBlankBranding.Branding.AnyAsync(item => item.OrganizationId == libraryId),
                    "clearing inherited logo alt text must remove the override instead of materializing an empty branding row");
            }

            var editedBranding = await ReadSettings(libraryId.ToString());
            var libraryAlt = $"Closure logo alt {Guid.NewGuid():N}";
            await SaveSettings(libraryId.ToString(), editedBranding,
                new { postmarkToken = "", clearPostmarkToken = false }, new { logoAlt = libraryAlt });
            await using (var afterBrandingEdit = await contextFactory.CreateDbContextAsync())
            {
                Assert.AreEqual(libraryAlt, await afterBrandingEdit.Branding.Where(item => item.OrganizationId == libraryId)
                    .Select(item => item.LogoAltText).SingleAsync());
            }

            var resetBranding = await ReadSettings(libraryId.ToString());
            await SaveSettings(libraryId.ToString(), resetBranding,
                new { postmarkToken = "", clearPostmarkToken = false }, new { logoAlt = (string?)null });
            await using (var afterBrandingReset = await contextFactory.CreateDbContextAsync())
            {
                Assert.IsFalse(await afterBrandingReset.Branding.AnyAsync(item => item.OrganizationId == libraryId),
                    "resetting the last local branding value must remove the sparse branding row");
            }

            var libraryTemplateSubject = $"Closure library subject {Guid.NewGuid():N}";
            var libraryBeforeOverride = await ReadSettings(libraryId.ToString());
            await SaveSettings(libraryId.ToString(), libraryBeforeOverride, new Dictionary<string, object?>
            {
                ["postmarkToken"] = "",
                ["clearPostmarkToken"] = false,
                ["suggestion_submitted"] = new
                {
                    templateKey = "suggestion_submitted",
                    sourceTemplateId = systemTemplateId.ToString(),
                    subject = libraryTemplateSubject
                }
            });
            var withOverride = await ReadSettings(libraryId.ToString());
            var sparseOverride = withOverride.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().Single();
            Assert.AreEqual(systemTemplateId.ToString(), sparseOverride.GetProperty("sourceTemplateId").GetString());
            Assert.AreEqual(libraryTemplateSubject, sparseOverride.GetProperty("subject").GetString());
            var libraryTemplateId = long.Parse(sparseOverride.GetProperty("id").GetString()!);
            var libraryTemplateVersion = sparseOverride.GetProperty("version").GetString();

            await SaveSettings(libraryId.ToString(), withOverride, new { postmarkToken = "", clearPostmarkToken = false });
            await using (var afterSparseNoEdit = await contextFactory.CreateDbContextAsync())
            {
                var current = await afterSparseNoEdit.EmailTemplates.AsNoTracking().SingleAsync(item => item.Id == libraryTemplateId);
                Assert.AreEqual(libraryTemplateSubject, current.SubjectTemplate);
                CollectionAssert.AreEqual(Convert.FromBase64String(libraryTemplateVersion!), current.RowVersion,
                    "an unrelated save must leave the sparse template override rowversion unchanged");
            }

            var resetVersion = await ReadSettings(libraryId.ToString());
            await SaveSettings(libraryId.ToString(), resetVersion, new Dictionary<string, object?>
            {
                ["postmarkToken"] = "",
                ["clearPostmarkToken"] = false,
                ["suggestion_submitted"] = new
                {
                    templateKey = "suggestion_submitted",
                    sourceTemplateId = systemTemplateId.ToString(),
                    reset = true
                }
            });
            var afterReset = await ReadSettings(libraryId.ToString());
            Assert.AreEqual(0, afterReset.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").GetArrayLength(),
                "resetting a library standard template must remove the sparse row and restore inheritance");
            Assert.AreEqual(editedSystemSubject, afterReset.GetProperty("emails").GetProperty("templates").EnumerateArray()
                .Single(item => item.GetProperty("templateKey").GetString() == "suggestion_submitted")
                .GetProperty("subject").GetString());

            const string customTemplateKey = "rejection:closure-custom";
            var beforeCustom = await ReadSettings(libraryId.ToString());
            await SaveSettings(libraryId.ToString(), beforeCustom, new Dictionary<string, object?>
            {
                ["postmarkToken"] = "",
                ["clearPostmarkToken"] = false,
                ["rejection_templates"] = new object[]
                {
                    new
                    {
                        templateKey = customTemplateKey,
                        isCustom = true,
                        displayName = "Closure custom reason",
                        subject = "Closure decision",
                        body = "The library recorded a custom rejection reason."
                    }
                }
            });
            var withCustom = await ReadSettings(libraryId.ToString());
            var custom = withCustom.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().Single(item =>
                    item.GetProperty("templateKey").GetString() == customTemplateKey);
            Assert.IsTrue(custom.GetProperty("isCustom").GetBoolean());
            Assert.AreEqual("Closure custom reason", custom.GetProperty("displayName").GetString());
            Assert.IsTrue(long.Parse(custom.GetProperty("id").GetString()!) > 0,
                "persisted template IDs must be represented as decimal strings");

            await SaveSettings(libraryId.ToString(), withCustom, new Dictionary<string, object?>
            {
                ["postmarkToken"] = "",
                ["clearPostmarkToken"] = false,
                ["rejection_templates"] = new object[]
                {
                    new { templateKey = customTemplateKey, isCustom = true, reset = true }
                }
            });
            var afterCustomReset = await ReadSettings(libraryId.ToString());
            Assert.IsFalse(afterCustomReset.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().Any(item =>
                    item.GetProperty("templateKey").GetString() == customTemplateKey),
                "deleting a custom rejection template must remove it from SQL");
        }
        finally
        {
            await using (var restore = await contextFactory.CreateDbContextAsync())
            {
                var systemTemplate = await restore.EmailTemplates.SingleAsync(item => item.Id == systemTemplateId);
                systemTemplate.SubjectTemplate = originalSubject;
                systemTemplate.BodyTemplate = originalBody;
                systemTemplate.DisplayName = originalDisplayName;
                systemTemplate.IsHidden = originalHidden;
                systemTemplate.SortOrder = originalSortOrder;
                await restore.SaveChangesAsync();
            }

            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;";
            cleanup.Parameters.AddWithValue("@organizationId", libraryId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed record StructuredProviderInput(
        string id,
        string key,
        bool isEnabled,
        string label,
        string urlTemplate,
        int sortOrder);

    private sealed record FormatSnapshot(
        string Label,
        int SortOrder,
        bool IsEnabled,
        string? MessageBehavior,
        string? Message,
        string? TitleMode,
        string? TitleLabel,
        string? AuthorMode,
        string? AuthorLabel,
        string? IdentifierMode,
        string? IdentifierLabel,
        string? PublicationMode,
        string? PublicationLabel)
    {
        public static FormatSnapshot From(MaterialFormat row) => new(
            row.Label, row.SortOrder, row.IsEnabled, row.MessageBehavior, row.Message,
            row.TitleMode, row.TitleLabel, row.AuthorMode, row.AuthorLabel,
            row.IdentifierMode, row.IdentifierLabel, row.PublicationMode, row.PublicationLabel);

        public void Apply(MaterialFormat row)
        {
            row.Label = Label;
            row.SortOrder = SortOrder;
            row.IsEnabled = IsEnabled;
            row.MessageBehavior = MessageBehavior;
            row.Message = Message;
            row.TitleMode = TitleMode;
            row.TitleLabel = TitleLabel;
            row.AuthorMode = AuthorMode;
            row.AuthorLabel = AuthorLabel;
            row.IdentifierMode = IdentifierMode;
            row.IdentifierLabel = IdentifierLabel;
            row.PublicationMode = PublicationMode;
            row.PublicationLabel = PublicationLabel;
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
