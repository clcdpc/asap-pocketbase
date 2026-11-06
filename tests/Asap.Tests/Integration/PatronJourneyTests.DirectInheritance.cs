using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task DirectLibraryInheritancePreservesEqualPinsNullableBlanksAndExplicitResetBoundaries()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var library = await ReserveSettingsCourseLibraryIdAsync();
        var scope = library.ToString();
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"direct_{suffix}";
        const string rawMessage = "  retained source\r\nwith whitespace  ";
        using var originalSystem = await ReadSettingsDocumentAsync(client, "system");
        var originalSuccessTitle = originalSystem.RootElement.GetProperty("stored").GetProperty("configuredSystem")
            .GetProperty("patron").GetProperty("successTitle").GetString();
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@org, N'Direct inheritance witness', N'DIW', 2, 1, 1);
                UPDATE [asap].[PatronSettings] SET [SuccessTitle] = NULL WHERE [OrganizationId] = 1;
                INSERT INTO [asap].[ExternalSearchProvider]
                    ([OrganizationId], [ProviderKey], [Label], [UrlTemplate], [IsEnabled], [SortOrder])
                VALUES (1, @key, N'System catalog', N'https://catalog.example.org?q={query}', 0, 0);
                INSERT INTO [asap].[MaterialFormat]
                    ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
                VALUES (1, @key, N'Shared format', 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@org, @key + N'_owned', N'Owned format', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO [asap].[EmailTemplate]
                    ([OrganizationId], [TemplateKey], [DisplayName], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder])
                VALUES (1, N'rejection:' + @key, N'System rejection', N'System subject', N'System body', 0, 0, 0),
                       (@org, N'rejection:' + @key + N'_owned', N'Owned rejection', N'Owned subject', N'Owned body', 0, 1, 1);
                INSERT INTO [asap].[PatronCustomField]
                    ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
                VALUES (@org, @key, N'text', N'Owned field', 1, 0);
                INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
                SELECT @org, format.[Id], field.[Id], N'optional', N'Owned rule'
                FROM [asap].[MaterialFormat] format CROSS JOIN [asap].[PatronCustomField] field
                WHERE format.[OwnerOrganizationId] = 1 AND format.[Code] = @key
                  AND field.[LibraryOrganizationId] = @org AND field.[FieldKey] = @key;
                """, ("@org", library), ("@key", key));
            using var initial = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual("Suggestion Submitted", initial.RootElement.GetProperty("systemValues").GetProperty("patron").GetProperty("successTitle").GetString());
            var source = initial.RootElement.GetProperty("stored").GetProperty("configuredSystem");
            var format = source.GetProperty("formats").EnumerateArray().Single(item => item.GetProperty("code").GetString() == key);
            var provider = source.GetProperty("providers").EnumerateArray().Single(item => item.GetProperty("key").GetString() == key);
            var template = source.GetProperty("templates").EnumerateArray().Single(item => item.GetProperty("templateKey").GetString() == $"rejection:{key}");
            var formatId = format.GetProperty("id").GetString();
            var providerId = provider.GetProperty("id").GetString();
            var templateId = template.GetProperty("id").GetString();
            async Task SaveAsync(string selectedScope, Dictionary<string, object?> values)
            {
                using var before = await ReadSettingsDocumentAsync(client, selectedScope);
                using var result = await SaveSettingsDocumentAsync(client, before.RootElement, selectedScope, values);
                Assert.AreEqual("saved", result.RootElement.GetProperty("code").GetString());
            }
            JsonElement Raw(JsonDocument document, string collection) => document.RootElement.GetProperty("stored").GetProperty("libraryOverride").GetProperty(collection);
            JsonElement RawFormat(JsonDocument document) => Raw(document, "formats").EnumerateArray().Single(item =>
                item.GetProperty("materialFormatId").GetString() == formatId);
            JsonElement RawProvider(JsonDocument document) => Raw(document, "providers").EnumerateArray().Single(item => item.GetProperty("id").GetString() == providerId);

            await SaveAsync(scope, new()
            {
                ["workflow"] = new { suggestionLimit = 5, commonAuthorsEnabled = false, commonAuthorsList = Array.Empty<string>(), allowedPatronCodeIds = Array.Empty<int>() },
                ["patron"] = new { successTitle = "Library title", successMessage = rawMessage, publicationOptions = Array.Empty<object>() },
                ["providers"] = new[] { new { id = providerId, pinOverrides = true, label = "System catalog", isEnabled = false } },
                ["formats"] = new[] { new { id = formatId, version = format.GetProperty("version").GetString(), pinOverrides = true,
                    label = "Shared format", sortOrder = 0, isEnabled = false, message = "", author = new { mode = "optional", label = "  " } } },
                ["templates"] = new[] { new { templateKey = $"rejection:{key}", sourceTemplateId = templateId, subject = "System subject", body = rawMessage } }
            });
            using var pinned = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual(5, Raw(pinned, "workflow").GetProperty("suggestionLimit").GetInt32());
            Assert.IsFalse(Raw(pinned, "workflow").GetProperty("commonAuthorsEnabled").GetBoolean());
            Assert.AreEqual(rawMessage, Raw(pinned, "patron").GetProperty("successMessage").GetString());
            Assert.AreEqual("Library title", pinned.RootElement.GetProperty("effective").GetProperty("successTitle").GetString());
            Assert.AreEqual("Suggestion Submitted", pinned.RootElement.GetProperty("systemValues").GetProperty("patron").GetProperty("successTitle").GetString(),
                "The inherited comparison uses the actual System runtime default, not the current Library effective title.");
            Assert.AreEqual("", RawFormat(pinned).GetProperty("message").GetString());
            Assert.AreEqual("  ", RawFormat(pinned).GetProperty("authorLabel").GetString());
            Assert.AreEqual("optional", RawFormat(pinned).GetProperty("authorMode").GetString());
            Assert.AreEqual(0, RawFormat(pinned).GetProperty("sortOrder").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, RawFormat(pinned).GetProperty("titleMode").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, RawProvider(pinned).GetProperty("urlTemplate").ValueKind);
            foreach (var setName in new[] { "publicationOptions", "commonCreators", "allowedPatronCodeIds" })
            {
                Assert.IsTrue(Raw(pinned, setName).GetProperty("exists").GetBoolean());
                Assert.AreEqual(0, Raw(pinned, setName).GetProperty("values").GetArrayLength());
            }
            var acceptedProvider = RawProvider(pinned).GetRawText();
            var acceptedFormat = RawFormat(pinned).GetRawText();
            var ownedFields = pinned.RootElement.GetProperty("stored").GetProperty("customFields").GetRawText();
            var ownedRules = pinned.RootElement.GetProperty("stored").GetProperty("formatRules").GetRawText();
            var ownedTemplate = Raw(pinned, "templates").EnumerateArray().Single(item => item.GetProperty("isCustom").GetBoolean()).GetRawText();
            await SaveAsync(scope, new() { ["patron"] = new { loginNote = "Unrelated sparse edit" } });
            using var unrelated = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual(acceptedProvider, RawProvider(unrelated).GetRawText());
            Assert.AreEqual(acceptedFormat, RawFormat(unrelated).GetRawText());
            await SaveAsync(scope, new() { ["providers"] = new[] { new { id = providerId, pinOverrides = true, label = "" } } });
            using var blank = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual("", RawProvider(blank).GetProperty("label").GetString());
            Assert.IsFalse(RawProvider(blank).GetProperty("isEnabled").GetBoolean());
            acceptedProvider = RawProvider(blank).GetRawText();

            await SaveAsync("system", new()
            {
                ["providers"] = new[] { new { id = providerId, label = "Later system catalog", isEnabled = true, urlTemplate = "https://catalog.example.org/later?q={query}" } },
                ["formats"] = new[] { new { id = formatId, label = "Later shared format", sortOrder = 7, isEnabled = true } },
                ["templates"] = new[] { new { templateKey = $"rejection:{key}", subject = "Later system subject", body = "Later system body" } }
            });
            using var later = await ReadSettingsDocumentAsync(client, scope);
            var effectiveFormat = later.RootElement.GetProperty("effective").GetProperty("formats").EnumerateArray().Single(item => item.GetProperty("id").GetString() == formatId);
            Assert.AreEqual("Shared format", effectiveFormat.GetProperty("label").GetString());
            Assert.IsFalse(effectiveFormat.GetProperty("isEnabled").GetBoolean());
            Assert.AreEqual("", effectiveFormat.GetProperty("message").GetString());
            Assert.AreEqual(acceptedProvider, RawProvider(later).GetRawText());

            await SaveAsync(scope, new()
            {
                ["providers"] = new[] { new { id = providerId, pinOverrides = true, label = (string?)null } },
                ["formats"] = new[] { new { id = formatId, version = RawFormat(later).GetProperty("version").GetString(),
                    pinOverrides = true, author = new { label = (string?)null } } },
                ["templates"] = new[] { new { templateKey = $"rejection:{key}", sourceTemplateId = templateId, subject = (string?)null } }
            });
            using var columnsReset = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual(JsonValueKind.Null, RawProvider(columnsReset).GetProperty("label").ValueKind);
            Assert.IsFalse(RawProvider(columnsReset).GetProperty("isEnabled").GetBoolean());
            Assert.AreEqual("optional", RawFormat(columnsReset).GetProperty("authorMode").GetString());
            Assert.AreEqual(JsonValueKind.Null, RawFormat(columnsReset).GetProperty("authorLabel").ValueKind);
            Assert.AreEqual("", RawFormat(columnsReset).GetProperty("message").GetString());
            var resetTemplate = Raw(columnsReset, "templates").EnumerateArray().Single(item => !item.GetProperty("isCustom").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, resetTemplate.GetProperty("subject").ValueKind);
            Assert.AreEqual(rawMessage, resetTemplate.GetProperty("body").GetString());

            using var stale = await client.PostAsJsonAsync("/api/asap/staff/settings", new { orgId = scope,
                version = pinned.RootElement.GetProperty("version").GetString(), providers = new[] { new { id = providerId, reset = true } } });
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            using var afterConflict = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual(columnsReset.RootElement.GetProperty("stored").GetRawText(), afterConflict.RootElement.GetProperty("stored").GetRawText());

            await SaveAsync(scope, new()
            {
                ["workflow"] = new { suggestionLimit = (int?)null, commonAuthorsEnabled = (bool?)null, commonAuthorsList = (string[]?)null, allowedPatronCodeIds = (int[]?)null },
                ["patron"] = new { successMessage = (string?)null, publicationOptions = (object[]?)null },
                ["providers"] = new[] { new { id = providerId, reset = true } },
                ["formats"] = new[] { new { id = formatId, version = RawFormat(columnsReset).GetProperty("version").GetString(), reset = true } },
                ["templates"] = new[] { new { templateKey = $"rejection:{key}", sourceTemplateId = templateId, reset = true } }
            });
            using var reset = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual(0, Raw(reset, "providers").GetArrayLength());
            Assert.IsFalse(Raw(reset, "formats").EnumerateArray().Any(item => item.GetProperty("kind").GetString() == "systemOverride"));
            Assert.AreEqual(ownedFields, reset.RootElement.GetProperty("stored").GetProperty("customFields").GetRawText());
            Assert.AreEqual(ownedRules, reset.RootElement.GetProperty("stored").GetProperty("formatRules").GetRawText());
            Assert.AreEqual(ownedTemplate, Raw(reset, "templates").EnumerateArray().Single().GetRawText());
            Assert.IsTrue(Raw(reset, "formats").EnumerateArray().Any(item => item.GetProperty("code").GetString() == key + "_owned"));
            foreach (var setName in new[] { "publicationOptions", "commonCreators", "allowedPatronCodeIds" })
            {
                Assert.IsFalse(Raw(reset, setName).GetProperty("exists").GetBoolean());
            }
            await SaveAsync("system", new() { ["providers"] = new[] { new { id = providerId, label = "Following future system" } },
                ["formats"] = new[] { new { id = formatId, label = "Following future format" } } });
            using var following = await ReadSettingsDocumentAsync(client, scope);
            Assert.AreEqual("Following future format", following.RootElement.GetProperty("effective").GetProperty("formats").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == formatId).GetProperty("label").GetString());
            Assert.AreEqual("Following future system", following.RootElement.GetProperty("effective").GetProperty("externalSearchProviders").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == providerId).GetProperty("label").GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[ExternalSearchProviderOverride] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'rejection:' + @key;
                DELETE FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = @key;
                DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[MaterialFormat] WHERE [Code] IN (@key, @key + N'_owned');
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[Organization] WHERE [Id] = @org;
                UPDATE [asap].[PatronSettings] SET [SuccessTitle] = @title WHERE [OrganizationId] = 1;
                """, ("@org", library), ("@key", key), ("@title", (object?)originalSuccessTitle ?? DBNull.Value));
        }
    }
}
