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
            seed.ExternalSearchProviders.Remove(seededProviderFour);
            await seed.SaveChangesAsync();
        }
        var providers = await seed.ExternalSearchProviders.OrderBy(item => item.SortOrder).ToListAsync();
        Assert.AreEqual(3, providers.Count);
        Assert.IsFalse(providers.Any(item => item.ProviderKey == "external_search_4"));
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
            Assert.AreEqual(0, await verify.ExternalSearchProviders.CountAsync(item => item.ProviderKey == "external_search_4"));
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
            if (seededProviderFour is not null && !await restore.ExternalSearchProviders.AnyAsync(item => item.ProviderKey == "external_search_4"))
            {
                restore.ExternalSearchProviders.Add(new ExternalSearchProvider
                {
                    OrganizationId = seededProviderFour.OrganizationId,
                    ProviderKey = seededProviderFour.ProviderKey,
                    IsEnabled = seededProviderFour.IsEnabled,
                    Label = seededProviderFour.Label,
                    UrlTemplate = seededProviderFour.UrlTemplate,
                    SortOrder = seededProviderFour.SortOrder
                });
            }
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
        using var startup = factory!.CreateClient();
        await startup.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
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
                await seed.SaveChangesAsync();
            }

            var before = JsonSerializer.SerializeToElement(
                (await service.GetSettingsAsync(actor, libraryId.ToString(), CancellationToken.None)).Data);
            var stored = before.GetProperty("stored");
            var effective = before.GetProperty("effective");
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
            Assert.AreEqual(1, await verify.PatronCodeEligibilityMembers.CountAsync(item => item.OrganizationId == libraryId));
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
                DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;
                """;
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
