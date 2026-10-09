using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task RequiredSelectFeasibilityCoversLibraryAndInheritedSystemFormatEdits()
    {
        const string fieldKey = "required_select_contract";
        var formatWasEnabled = true;
        var auditBefore = await ReadAuditHighWatermarkAsync();
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[MaterialFormat]
                ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
            VALUES (1, N'authority_required_select', N'Authority required select', 9870, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            DECLARE @formatId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[PatronCustomField]
                ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
            VALUES (2, N'required_select_contract', N'select', N'Required choice', 1, 9870);
            DECLARE @fieldId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[PatronCustomFieldOption]
                ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder])
            VALUES (@fieldId, N'only_choice', N'Only choice', 1, 10);
            INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
            VALUES (2, @formatId, @fieldId, N'required', N'Required choice');
            """);

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            using var library = await ReadSettingsDocumentAsync(client, "2");
            var libraryVersion = library.RootElement.GetProperty("version").GetString()!;
            var fieldDefinitions = JsonNode.Parse(library.RootElement.GetProperty("stored").GetProperty("customFields").GetRawText())!.AsArray();
            var field = fieldDefinitions.Single(item => item?["key"]?.GetValue<string>() == fieldKey)!;
            field["options"]![0]!["enabled"] = false;
            var libraryRules = JsonNode.Parse(library.RootElement.GetProperty("stored").GetProperty("formatRules").GetRawText())!.AsArray();
            var rejectedLibraryPayload = new JsonObject
            {
                ["orgId"] = "2",
                ["version"] = libraryVersion,
                ["customFields"] = fieldDefinitions,
                ["formatRules"] = libraryRules
            };
            using (var disabledLastOption = await PostSettingsJsonAsync(client, rejectedLibraryPayload.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, disabledLastOption.StatusCode,
                    await disabledLastOption.Content.ReadAsStringAsync());
            }
            using (var libraryAfterRejected = await ReadSettingsDocumentAsync(client, "2"))
            {
                Assert.AreEqual(libraryVersion,
                    libraryAfterRejected.RootElement.GetProperty("version").GetString());
            }
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = N'required_select_contract'
                  AND optionRow.[IsEnabled] = 1;
                """));
            Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());

            using var system = await ReadSettingsDocumentAsync(client, "system");
            var systemVersion = system.RootElement.GetProperty("version").GetString()!;
            var systemFormats = CanonicalSystemFormats(system.RootElement.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("formats").GetRawText());
            var testFormat = systemFormats.Single(item => item?["code"]?.GetValue<string>() == "authority_required_select")!;
            formatWasEnabled = testFormat["isEnabled"]!.GetValue<bool>();
            testFormat["isEnabled"] = false;
            var disableSystemFormat = new JsonObject
            {
                ["orgId"] = "system",
                ["version"] = systemVersion,
                ["formats"] = systemFormats
            };
            using (var disabledFormat = await PostSettingsJsonAsync(client, disableSystemFormat.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.OK, disabledFormat.StatusCode, await disabledFormat.Content.ReadAsStringAsync());
            }

            using var libraryAfterDisable = await ReadSettingsDocumentAsync(client, "2");
            var libraryAfterDisableVersion = libraryAfterDisable.RootElement.GetProperty("version").GetString()!;
            var definitionsAfterDisable = JsonNode.Parse(libraryAfterDisable.RootElement.GetProperty("stored")
                .GetProperty("customFields").GetRawText())!.AsArray();
            var fieldAfterDisable = definitionsAfterDisable.Single(item => item?["key"]?.GetValue<string>() == fieldKey)!;
            fieldAfterDisable["options"]![0]!["enabled"] = false;
            var rulesAfterDisable = JsonNode.Parse(libraryAfterDisable.RootElement.GetProperty("stored")
                .GetProperty("formatRules").GetRawText())!.AsArray();
            var disableOptionPayload = new JsonObject
            {
                ["orgId"] = "2",
                ["version"] = libraryAfterDisableVersion,
                ["customFields"] = definitionsAfterDisable,
                ["formatRules"] = rulesAfterDisable
            };
            using (var disabledOption = await PostSettingsJsonAsync(client, disableOptionPayload.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.OK, disabledOption.StatusCode, await disabledOption.Content.ReadAsStringAsync());
            }
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = N'required_select_contract'
                  AND optionRow.[IsEnabled] = 0;
                """));

            using var systemBeforeRejectedEnable = await ReadSettingsDocumentAsync(client, "system");
            var systemVersionBeforeRejectedEnable = systemBeforeRejectedEnable.RootElement.GetProperty("version").GetString()!;
            var enableFormats = CanonicalSystemFormats(systemBeforeRejectedEnable.RootElement.GetProperty("stored")
                .GetProperty("configuredSystem").GetProperty("formats").GetRawText());
            var formatToEnable = enableFormats.Single(item => item?["code"]?.GetValue<string>() == "authority_required_select")!;
            formatToEnable["isEnabled"] = true;
            var enableSystemFormat = new JsonObject
            {
                ["orgId"] = "system",
                ["version"] = systemVersionBeforeRejectedEnable,
                ["formats"] = enableFormats
            };
            var auditBeforeRejectedEnable = await ReadAuditHighWatermarkAsync();
            using (var enabledFormat = await PostSettingsJsonAsync(client, enableSystemFormat.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, enabledFormat.StatusCode,
                    await enabledFormat.Content.ReadAsStringAsync());
            }
            using var systemAfterRejectedEnable = await ReadSettingsDocumentAsync(client, "system");
            Assert.AreEqual(systemVersionBeforeRejectedEnable,
                systemAfterRejectedEnable.RootElement.GetProperty("version").GetString());
            Assert.AreEqual(auditBeforeRejectedEnable, await ReadAuditHighWatermarkAsync());
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'authority_required_select' AND [IsEnabled] = 0;
                """));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE ruleRow
                FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = N'required_select_contract';
                DELETE optionRow
                FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = N'required_select_contract';
                DELETE FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'required_select_contract';
                UPDATE [asap].[MaterialFormat]
                SET [IsEnabled] = @formatWasEnabled, [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'authority_required_select';
                DELETE FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'authority_required_select';
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@formatWasEnabled", formatWasEnabled), ("@auditBefore", auditBefore));
        }

        static JsonArray CanonicalSystemFormats(string rawFormats)
        {
            using var document = JsonDocument.Parse(rawFormats);
            var formats = new JsonArray();
            foreach (var format in document.RootElement.EnumerateArray())
            {
                formats.Add(new JsonObject
                {
                    ["id"] = format.GetProperty("id").GetString(),
                    ["version"] = format.GetProperty("version").GetString(),
                    ["code"] = format.GetProperty("code").GetString(),
                    ["ownerOrganizationId"] = 1,
                    ["label"] = format.GetProperty("label").GetString(),
                    ["sortOrder"] = format.GetProperty("sortOrder").GetInt32(),
                    ["isEnabled"] = format.GetProperty("isEnabled").GetBoolean(),
                    ["overridden"] = true,
                    ["reset"] = false
                });
            }
            return formats;
        }
    }

    [TestMethod]
    public async Task CustomFieldRulePayloadRejectsMalformedAndUnknownDefinitionsAtomically()
    {
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[PatronCustomField]
                ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
            VALUES (2, N'rule_shape_contract', N'text', N'Rule shape', 1, 9890);
            DECLARE @fieldId bigint = SCOPE_IDENTITY();
            DECLARE @formatId bigint = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
            INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
            VALUES (2, @formatId, @fieldId, N'optional', N'Preserved label');
            """);

        long auditBefore = await ReadAuditHighWatermarkAsync();
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var settings = await ReadSettingsDocumentAsync(client, "2");
            var version = settings.RootElement.GetProperty("version").GetString()!;
            var invalidRules = new[]
            {
                "[]",
                "{\"unknown_field\":{\"mode\":\"optional\"}}",
                "{\"rule_shape_contract\":{\"mode\":\"unsupported\"}}",
                "{\"rule_shape_contract\":{\"mode\":123}}",
                "{\"rule_shape_contract\":[]}",
                "{\"rule_shape_contract\":{\"mode\":\"optional\",\"labelOverride\":123}}",
                "{\"rule_shape_contract\":{\"mode\":\"optional\",\"labelOverride\":\"A\",\"label\":\"B\"}}",
                "{\"rule_shape_contract\":{\"mode\":\"optional\",\"label\":\"B\",\"labelOverride\":\"A\"}}",
                "{\"rule_shape_contract\":{\"mode\":\"optional\",\"label\":{},\"labelOverride\":\"A\"}}",
                "{\"rule_shape_contract\":{\"mode\":\"optional\"},\"rule_shape_contract\":{\"mode\":\"hidden\"}}"
            };
            foreach (var customFields in invalidRules)
            {
                using var response = await PostSettingsJsonAsync(client,
                    $"{{\"orgId\":\"2\",\"version\":\"{version}\",\"formatRules\":[{{\"code\":\"book\",\"customFields\":{customFields}}}]}}");
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                    $"Invalid custom field rule map {customFields}: {await response.Content.ReadAsStringAsync()}");
            }

            using (var duplicateFormatRules = await PostSettingsJsonAsync(client,
                       $"{{\"orgId\":\"2\",\"version\":\"{version}\",\"formatRules\":[{{\"code\":\"book\",\"customFields\":{{\"rule_shape_contract\":{{\"mode\":\"optional\"}}}}}},{{\"code\":\"book\",\"customFields\":{{\"rule_shape_contract\":{{\"mode\":\"hidden\"}}}}}}]}}"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, duplicateFormatRules.StatusCode,
                    await duplicateFormatRules.Content.ReadAsStringAsync());
            }

            using (var emptySecondFormatRule = await PostSettingsJsonAsync(client,
                       $"{{\"orgId\":\"2\",\"version\":\"{version}\",\"formatRules\":[{{\"code\":\"book\",\"customFields\":{{\"rule_shape_contract\":{{\"mode\":\"optional\",\"labelOverride\":\"A\",\"label\":\"B\"}}}}}},{{}}]}}"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, emptySecondFormatRule.StatusCode,
                    await emptySecondFormatRule.Content.ReadAsStringAsync());
            }

            using var after = await ReadSettingsDocumentAsync(client, "2");
            Assert.AreEqual(version, after.RootElement.GetProperty("version").GetString());
            Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE ruleRow.[LibraryOrganizationId] = 2 AND field.[FieldKey] = N'rule_shape_contract'
                  AND ruleRow.[Mode] = N'optional' AND ruleRow.[LabelOverride] = N'Preserved label';
                """));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE ruleRow
                FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] = N'rule_shape_contract';
                DELETE FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'rule_shape_contract';
                """);
        }
    }

    [TestMethod]
    public async Task EverySettingsCollectionRejectsMalformedWholeReplacementWithoutChangingState()
    {
        var auditBefore = await ReadAuditHighWatermarkAsync();
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var library = await ReadSettingsDocumentAsync(client, "2");
        var libraryVersion = library.RootElement.GetProperty("version").GetString()!;
        var malformedLibraryCollections = new (string Name, string Json)[]
        {
            ("common creators", "{\"workflow\":{\"commonAuthorsList\":[null]}}"),
            ("patron codes", "{\"workflow\":{\"allowedPatronCodeIds\":[0]}}"),
            ("publication options", "{\"ui_text\":{\"publicationOptions\":[{}]}}"),
            ("publication option numeric label", "{\"ui_text\":{\"publicationOptions\":[{\"id\":7,\"label\":123}]}}"),
            ("duplicate publication option identity", "{\"ui_text\":{\"publicationOptions\":[{\"id\":\"year\",\"label\":\"Year\"},{\"key\":\"year\",\"label\":\"Another year\"}]}}"),
            ("publication option invalid enabled flag", "{\"ui_text\":{\"publicationOptions\":[{\"label\":\"Year\",\"enabled\":\"bogus\"}]}}"),
            ("publication option invalid sort order", "{\"ui_text\":{\"publicationOptions\":[{\"label\":\"Year\",\"sortOrder\":{}}]}}"),
            ("providers", "{\"providers\":[{}]}"),
            ("provider numeric label", "{\"providers\":[{\"key\":\"external_search_1\",\"label\":123}]}"),
            ("provider numeric URL", "{\"providers\":[{\"key\":\"external_search_1\",\"urlTemplate\":123}]}"),
            ("provider invalid enabled flag", "{\"providers\":[{\"key\":\"external_search_1\",\"enabled\":\"bogus\"}]}"),
            ("provider invalid sort order", "{\"providers\":[{\"key\":\"external_search_1\",\"sortOrder\":[]}]}"),
            ("formats", "{\"formats\":[{}]}"),
            ("format numeric label", "{\"formats\":[{\"code\":\"book\",\"label\":123}]}"),
            ("format invalid enabled flag", "{\"formats\":[{\"code\":\"book\",\"enabled\":\"bogus\"}]}"),
            ("format invalid sort order", "{\"formats\":[{\"code\":\"book\",\"sortOrder\":[]}]}"),
            ("format invalid author rule", "{\"formats\":[{\"code\":\"book\",\"author\":[]}]}"),
            ("format invalid custom-field rule shape", "{\"formats\":[{\"code\":\"book\",\"customFields\":[]}]}"),
            ("format rules", "{\"formatRules\":[{}]}"),
            ("format rule numeric code", "{\"formatRules\":[{\"code\":123,\"title\":{\"mode\":\"required\"}}]}"),
            ("format field numeric label", "{\"formatRules\":[{\"code\":\"book\",\"title\":{\"mode\":\"required\",\"label\":123}}]}"),
            ("format rule custom fields wrong kind", "{\"formatRules\":[{\"code\":\"book\",\"customFields\":[]}]}"),
            ("custom fields", "{\"customFields\":[{}]}"),
            ("custom field numeric label", "{\"customFields\":[{\"key\":\"numeric_label_contract\",\"type\":\"text\",\"label\":123}]}"),
            ("custom field numeric type", "{\"customFields\":[{\"key\":\"strict_type_contract\",\"type\":123,\"label\":\"Strict type\"}]}"),
            ("custom field invalid enabled flag", "{\"customFields\":[{\"key\":\"strict_enabled_contract\",\"enabled\":\"bogus\"}]}"),
            ("custom field invalid help text", "{\"customFields\":[{\"key\":\"strict_help_contract\",\"helpText\":false}]}"),
            ("custom field invalid sort order", "{\"customFields\":[{\"key\":\"strict_sort_contract\",\"sortOrder\":[]}]}"),
            ("custom field options wrong kind", "{\"customFields\":[{\"key\":\"strict_options_contract\",\"type\":\"select\",\"options\":null}]}"),
            ("custom field option numeric label", "{\"customFields\":[{\"key\":\"numeric_option_contract\",\"type\":\"select\",\"label\":\"Choice\",\"options\":[{\"id\":7,\"label\":123}]}]}"),
            ("auto-claim rules", "{\"autoClaimRules\":[{}]}"),
            ("auto-claim invalid active flag", "{\"autoClaimRules\":[{\"formatId\":1,\"active\":\"bogus\"}]}"),
            ("auto-claim invalid format ID", "{\"autoClaimRules\":[{\"formatId\":{},\"active\":false}]}"),
            ("email templates", "{\"templates\":[{}]}"),
            ("template numeric subject", "{\"templates\":[{\"templateKey\":\"numeric_subject_contract\",\"isCustom\":true,\"subject\":123,\"body\":\"Body\"}]}"),
            ("template invalid body", "{\"templates\":[{\"templateKey\":\"strict_body_contract\",\"isCustom\":true,\"subject\":\"Subject\",\"body\":{}}]}"),
            ("template invalid enabled flag", "{\"templates\":[{\"templateKey\":\"strict_enabled_contract\",\"isCustom\":true,\"subject\":\"Subject\",\"body\":\"Body\",\"enabled\":\"bogus\"}]}"),
            ("template invalid sort order", "{\"templates\":[{\"templateKey\":\"strict_sort_contract\",\"isCustom\":true,\"subject\":\"Subject\",\"body\":\"Body\",\"sortOrder\":[]}]}"),
            ("rejection templates", "{\"emails\":{\"rejection_templates\":{}}}"),
            ("branding", "{\"branding\":[]}"),
            ("branding numeric alt text", "{\"branding\":{\"altText\":123}}"),
            ("legacy format labels", "{\"ui_text\":{\"formatLabels\":[]}}"),
            ("legacy format numeric label", "{\"ui_text\":{\"formatLabels\":{\"book\":123}}}"),
            ("legacy format order unknown code", "{\"ui_text\":{\"formatOrder\":[\"not_a_format\"]}}"),
            ("legacy available formats unknown code", "{\"ui_text\":{\"availableFormats\":[\"not_a_format\"]}}")
        };
        foreach (var (name, json) in malformedLibraryCollections)
        {
            var payload = JsonNode.Parse(json)!.AsObject();
            payload["orgId"] = "2";
            payload["version"] = libraryVersion;
            using var response = await PostSettingsJsonAsync(client, payload.ToJsonString());
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Malformed {name} replacement: {await response.Content.ReadAsStringAsync()}");
        }

        using var libraryAfter = await ReadSettingsDocumentAsync(client, "2");
        Assert.AreEqual(libraryVersion, libraryAfter.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());

        using var system = await ReadSettingsDocumentAsync(client, "system");
        var systemVersion = system.RootElement.GetProperty("version").GetString()!;
        var malformedSystemCollections = new (string Name, string Json)[]
        {
            ("embed origins", "{\"origins\":[{}]}"),
            ("embed origin numeric member", "{\"origins\":[123]}"),
            ("system custom fields", "{\"customFields\":[]}"),
            ("system auto-claim rules", "{\"autoClaimRules\":[]}"),
            ("system nested custom-field rules", "{\"formats\":[{\"code\":\"book\",\"customFields\":{}}]}")
        };
        foreach (var (name, json) in malformedSystemCollections)
        {
            var payload = JsonNode.Parse(json)!.AsObject();
            payload["orgId"] = "system";
            payload["version"] = systemVersion;
            using var response = await PostSettingsJsonAsync(client, payload.ToJsonString());
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Malformed {name} replacement: {await response.Content.ReadAsStringAsync()}");
        }

        using var systemAfter = await ReadSettingsDocumentAsync(client, "system");
        Assert.AreEqual(systemVersion, systemAfter.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());

        var libraryOnlySystemCollections = new (string Name, string Json)[]
        {
            ("library participation", "{\"enabledLibraryOrgIds\":[2]}"),
            ("library embed origins", "{\"origins\":[]}")
        };
        foreach (var (name, json) in libraryOnlySystemCollections)
        {
            var payload = JsonNode.Parse(json)!.AsObject();
            payload["orgId"] = "2";
            payload["version"] = libraryVersion;
            using var response = await PostSettingsJsonAsync(client, payload.ToJsonString());
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"System-only {name} were accepted in a library save: {await response.Content.ReadAsStringAsync()}");
        }
        using var libraryAfterSystemOnly = await ReadSettingsDocumentAsync(client, "2");
        Assert.AreEqual(libraryVersion, libraryAfterSystemOnly.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
    }

    [TestMethod]
    public async Task SettingsRejectsCollectionsRepeatedAcrossSupportedLocationsAtomically()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var library = await ReadSettingsDocumentAsync(client, "2");
        using var system = await ReadSettingsDocumentAsync(client, "system");
        var libraryVersion = library.RootElement.GetProperty("version").GetString()!;
        var systemVersion = system.RootElement.GetProperty("version").GetString()!;
        var libraryStoredBefore = library.RootElement.GetProperty("stored").GetRawText();
        var systemStoredBefore = system.RootElement.GetProperty("stored").GetRawText();
        var auditBefore = await ReadAuditHighWatermarkAsync();
        var sessionCountBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];");
        var activeSessionCountBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession] WHERE [RevokedUtc] IS NULL;");
        var outboxCountBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");

        var duplicateLocations = new (string Name, string Scope, string RootName, string RootJson,
            string SectionName, string NestedName, string NestedJson)[]
        {
            ("providers", "2", "providers", "[]", "workflow", "externalSearchProviders", "{}"),
            ("common creators", "2", "commonCreators", "{}", "workflow", "commonAuthorsList", "[]"),
            ("patron codes", "2", "patronCodeIds", "{}", "workflow", "allowedPatronCodeIds", "[]"),
            ("formats", "2", "formats", "[]", "patron", "materialFormats", "{}"),
            ("legacy format labels", "2", "formatLabels", "[]", "patron", "formatLabels", "{}"),
            ("legacy format order", "2", "formatOrder", "{}", "patron", "formatOrder", "[]"),
            ("legacy available formats", "2", "availableFormats", "{}", "patron", "availableFormats", "[]"),
            ("custom fields", "2", "additionalFieldDefinitions", "[]", "patron", "customFields", "{}"),
            ("format rules", "2", "formatRules", "[]", "patron", "patronFormatRules", "{}"),
            ("branding", "2", "branding", "{}", "patron", "branding", "[]"),
            ("publication options", "2", "publicationOptionSet", "{}", "patron", "publicationOptions", "[]"),
            ("library participation", "system", "enabledLibraries", "{}", "systemSettings", "enabledLibraryOrgIds", "[]"),
            ("embed origins", "system", "origins", "{}", "systemSettings", "patronEmbedAllowedOrigins", "[]"),
            ("rejection templates", "2", "emailTemplates", "[]", "emails", "rejection_templates", "{}")
        };

        foreach (var duplicate in duplicateLocations)
        {
            var version = duplicate.Scope == "system" ? systemVersion : libraryVersion;
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject
                {
                    ["orgId"] = duplicate.Scope,
                    ["version"] = version
                };
                var section = new JsonObject
                {
                    [duplicate.NestedName] = JsonNode.Parse(duplicate.NestedJson)
                };
                if (sectionFirst)
                {
                    payload[duplicate.SectionName] = section;
                    payload[duplicate.RootName] = JsonNode.Parse(duplicate.RootJson);
                }
                else
                {
                    payload[duplicate.RootName] = JsonNode.Parse(duplicate.RootJson);
                    payload[duplicate.SectionName] = section;
                }

                using var response = await PostSettingsJsonAsync(client, payload.ToJsonString());
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                    $"Repeated {duplicate.Name} location (sectionFirst={sectionFirst}) was accepted: {await response.Content.ReadAsStringAsync()}");
            }
        }

        async Task AssertRejectedPayloadAsync(string name, JsonObject payload, string scope, string version)
        {
            payload["orgId"] = scope;
            payload["version"] = version;
            using var response = await PostSettingsJsonAsync(client, payload.ToJsonString());
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Ambiguous {name} was accepted: {await response.Content.ReadAsStringAsync()}");
        }

        foreach (var (firstSection, secondSection, scope, version) in new[]
                 {
                     ("ui_text", "patron", "2", libraryVersion),
                     ("systemSettings", "system", "system", systemVersion),
                     ("emails", "email", "2", libraryVersion)
                 })
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                if (sectionFirst)
                {
                    payload[firstSection] = new JsonObject();
                    payload[secondSection] = JsonValue.Create(42);
                }
                else
                {
                    payload[secondSection] = JsonValue.Create(42);
                    payload[firstSection] = new JsonObject();
                }
                await AssertRejectedPayloadAsync($"{firstSection}/{secondSection} sections", payload, scope, version);
            }
        }

        var systemSettingsSnapshot = system.RootElement.GetProperty("stored").GetProperty("systemSettings");
        foreach (var field in new[]
                 {
                     "staffUrl", "leapBibUrlPattern", "leapPatronUrlPattern", "formatIconUrlPattern",
                     "systemNotEnabledMessage", "misconfiguredMessage"
                 })
        {
            var oldValue = JsonNode.Parse(systemSettingsSnapshot.GetProperty(field).GetRawText());
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var systemSection = new JsonObject
                {
                    [field] = JsonValue.Create($"Conflicting {field} {Guid.NewGuid():N}")
                };
                if (sectionFirst)
                {
                    payload["systemSettings"] = systemSection;
                    payload[field] = oldValue?.DeepClone();
                }
                else
                {
                    payload[field] = oldValue?.DeepClone();
                    payload["systemSettings"] = systemSection;
                }
                await AssertRejectedPayloadAsync($"root/systemSettings {field}", payload, "system", systemVersion);
            }
        }

        foreach (var field in new[] { "systemNotEnabledMessage", "misconfiguredMessage" })
        {
            var oldValue = JsonNode.Parse(systemSettingsSnapshot.GetProperty(field).GetRawText());
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var systemSection = new JsonObject
                {
                    [field] = JsonValue.Create($"Conflicting {field} {Guid.NewGuid():N}")
                };
                var patronSection = new JsonObject
                {
                    [field] = oldValue?.DeepClone()
                };
                if (sectionFirst)
                {
                    payload["systemSettings"] = systemSection;
                    payload["patron"] = patronSection;
                }
                else
                {
                    payload["patron"] = patronSection;
                    payload["systemSettings"] = systemSection;
                }
                await AssertRejectedPayloadAsync($"systemSettings/patron {field}", payload, "system", systemVersion);
            }
        }

        foreach (var sectionFirst in new[] { false, true })
        {
            var currentFromAddress = JsonNode.Parse(system.RootElement.GetProperty("stored").GetProperty("email")
                .GetProperty("fromAddress").GetRawText());
            var currentFromName = JsonNode.Parse(system.RootElement.GetProperty("stored").GetProperty("email")
                .GetProperty("fromName").GetRawText());
            var senderPayload = new JsonObject();
            var emailSection = new JsonObject
            {
                ["fromAddress"] = JsonValue.Create("conflict@example.invalid"),
                ["fromName"] = JsonValue.Create("Conflicting sender")
            };
            var smtpSection = new JsonObject
            {
                ["fromAddress"] = currentFromAddress?.DeepClone(),
                ["fromName"] = currentFromName?.DeepClone()
            };
            if (sectionFirst)
            {
                senderPayload["smtp"] = smtpSection;
                senderPayload["email"] = emailSection;
            }
            else
            {
                senderPayload["email"] = emailSection;
                senderPayload["smtp"] = smtpSection;
            }
            await AssertRejectedPayloadAsync("email/smtp sender fallbacks", senderPayload, "system", systemVersion);

            var tokenEmailSection = new JsonObject
            {
                ["postmarkToken"] = JsonValue.Create(""),
                ["serverToken"] = JsonValue.Create((string?)null)
            };
            if (sectionFirst)
            {
                tokenEmailSection = new JsonObject
                {
                    ["serverToken"] = JsonValue.Create((string?)null),
                    ["postmarkToken"] = JsonValue.Create("")
                };
            }
            var tokenPayload = new JsonObject();
            if (sectionFirst)
            {
                tokenPayload["email"] = tokenEmailSection;
                tokenPayload["serverToken"] = JsonValue.Create("");
            }
            else
            {
                tokenPayload["serverToken"] = JsonValue.Create("");
                tokenPayload["email"] = tokenEmailSection;
            }
            await AssertRejectedPayloadAsync("email token aliases", tokenPayload, "system", systemVersion);
        }

        var polarisSnapshot = system.RootElement.GetProperty("stored").GetProperty("polaris");
        var currentPolarisUserId = JsonNode.Parse(polarisSnapshot.GetProperty("systemPolarisUserId").GetRawText());
        var otherPolarisUserId = currentPolarisUserId?.GetValue<int>() is int currentId ? currentId + 1 : 1;
        foreach (var sectionFirst in new[] { false, true })
        {
            var polarisPayload = new JsonObject();
            if (sectionFirst)
            {
                polarisPayload["userId"] = currentPolarisUserId?.DeepClone();
                polarisPayload["systemPolarisUserId"] = JsonValue.Create(otherPolarisUserId);
            }
            else
            {
                polarisPayload["systemPolarisUserId"] = JsonValue.Create(otherPolarisUserId);
                polarisPayload["userId"] = currentPolarisUserId?.DeepClone();
            }
            await AssertRejectedPayloadAsync("Polaris systemPolarisUserId/userId aliases",
                new JsonObject { ["polaris"] = polarisPayload }, "system", systemVersion);
        }

        var storedProviders = library.RootElement.GetProperty("stored").GetProperty("providers");
        var legacyProvider = storedProviders.EnumerateArray()
            .FirstOrDefault(item => item.TryGetProperty("key", out var key) && key.GetString() == "external_search_1");
        var legacyEnabled = legacyProvider.ValueKind == JsonValueKind.Object &&
                            legacyProvider.TryGetProperty("isEnabled", out var enabledValue) && enabledValue.GetBoolean();
        foreach (var sectionFirst in new[] { false, true })
        {
            var legacyProviderPayload = new JsonObject();
            var workflowSection = new JsonObject
            {
                ["externalSearch1Enabled"] = JsonValue.Create(legacyEnabled)
            };
            if (sectionFirst)
            {
                legacyProviderPayload["workflow"] = workflowSection;
                legacyProviderPayload["externalSearch1Enabled"] = JsonValue.Create(!legacyEnabled);
            }
            else
            {
                legacyProviderPayload["externalSearch1Enabled"] = JsonValue.Create(!legacyEnabled);
                legacyProviderPayload["workflow"] = workflowSection;
            }
            await AssertRejectedPayloadAsync("workflow/root legacy provider scalar", legacyProviderPayload, "2", libraryVersion);
        }

        var currentWorkflow = system.RootElement.GetProperty("stored").GetProperty("workflow");
        foreach (var fieldName in new[]
                 {
                     "suggestionLimitMessage", "commonAuthorsLabel", "commonAuthorsHelp", "commonAuthorsMessage",
                     "patronCodeEligibilityMessage", "outstandingTimeoutEnabled", "outstandingTimeoutSendEmail",
                     "holdPickupTimeoutEnabled", "pendingHoldTimeoutEnabled", "additionalCopyTimeoutEnabled",
                     "autoPromote", "commonAuthorsEnabled", "allowPatronAutoholdOptOut", "allowAnyRegisteredCardLogin",
                     "patronCodeEligibilityEnabled", "suggestionLimit", "outstandingTimeoutDays", "holdPickupTimeoutDays",
                     "pendingHoldTimeoutDays", "additionalCopyTimeoutDays", "outstandingTimeoutRejectionTemplateId",
                     "outstandingTimeoutRejectionTemplate"
                 })
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var workflowSection = new JsonObject
                {
                    [fieldName] = currentWorkflow.TryGetProperty(fieldName, out var currentField)
                        ? JsonNode.Parse(currentField.GetRawText())
                        : currentWorkflow.TryGetProperty("outstandingTimeoutRejectionTemplateId", out var currentTemplate)
                            ? JsonNode.Parse(currentTemplate.GetRawText())
                            : null
                };
                if (sectionFirst)
                {
                    payload["workflow"] = workflowSection;
                    payload[fieldName] = new JsonObject();
                }
                else
                {
                    payload[fieldName] = new JsonObject();
                    payload["workflow"] = workflowSection;
                }
                await AssertRejectedPayloadAsync($"root/nested workflow scalar {fieldName}", payload, "system", systemVersion);
            }
        }

        var currentSystemPatron = system.RootElement.GetProperty("stored").GetProperty("patron");
        foreach (var fieldName in new[]
                 {
                     "pageTitle", "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote",
                     "noEmailMessage", "successTitle", "successMessage", "alreadySubmittedMessage", "ebookMessage",
                     "eaudiobookMessage", "suggestionStatusLabel", "outstandingPurchaseStatusLabel",
                     "pendingHoldStatusLabel", "holdPlacedStatusLabel", "closedStatusLabel", "rejectedStatusLabel",
                     "holdCompletedStatusLabel", "holdNotPickedUpStatusLabel", "manualStatusLabel", "silentStatusLabel"
                 })
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var currentField = JsonNode.Parse(currentSystemPatron.GetProperty(fieldName).GetRawText());
                var payload = new JsonObject();
                var patronSection = new JsonObject { [fieldName] = currentField?.DeepClone() };
                if (sectionFirst)
                {
                    payload["patron"] = patronSection;
                    payload[fieldName] = new JsonObject();
                }
                else
                {
                    payload[fieldName] = new JsonObject();
                    payload["patron"] = patronSection;
                }
                await AssertRejectedPayloadAsync($"root/nested patron scalar {fieldName}", payload, "system", systemVersion);
            }
        }

        var currentBrandAlt = JsonNode.Parse(system.RootElement.GetProperty("stored").GetProperty("branding")
            .GetProperty("altText").GetRawText());
        foreach (var fieldName in new[]
                 {
                     "altText", "logoAlt", "logoAltText", "logoData", "contentType", "logoContentType", "fileName",
                     "logoFileName", "clearLogo", "removeLogo"
                 })
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var patronSection = new JsonObject { ["pageTitle"] = JsonNode.Parse(currentSystemPatron.GetProperty("pageTitle").GetRawText()) };
                if (sectionFirst)
                {
                    payload["patron"] = patronSection;
                    payload[fieldName] = new JsonObject();
                }
                else
                {
                    payload[fieldName] = new JsonObject();
                    payload["patron"] = patronSection;
                }
                await AssertRejectedPayloadAsync($"root/nested branding fallback {fieldName}", payload, "system", systemVersion);
            }
        }
        foreach (var rootFirst in new[] { false, true })
        {
            var payload = new JsonObject();
            var branding = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() };
            var patronSection = new JsonObject { ["logoAlt"] = currentBrandAlt?.DeepClone() };
            if (rootFirst)
            {
                payload["branding"] = branding;
                payload["patron"] = patronSection;
            }
            else
            {
                payload["patron"] = patronSection;
                payload["branding"] = branding;
            }
            await AssertRejectedPayloadAsync("root branding and nested legacy alt", payload, "system", systemVersion);

            var nestedBranding = new JsonObject();
            if (rootFirst)
            {
                nestedBranding["branding"] = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() };
                nestedBranding["logoAlt"] = currentBrandAlt?.DeepClone();
            }
            else
            {
                nestedBranding["logoAlt"] = currentBrandAlt?.DeepClone();
                nestedBranding["branding"] = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() };
            }
            payload = new JsonObject { ["patron"] = nestedBranding };
            await AssertRejectedPayloadAsync("nested branding and nested legacy alt", payload, "system", systemVersion);

            payload = new JsonObject();
            if (rootFirst)
            {
                payload["logoAlt"] = currentBrandAlt?.DeepClone();
                payload["patron"] = new JsonObject
                {
                    ["branding"] = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() }
                };
            }
            else
            {
                payload["patron"] = new JsonObject
                {
                    ["branding"] = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() }
                };
                payload["logoAlt"] = currentBrandAlt?.DeepClone();
            }
            await AssertRejectedPayloadAsync("root legacy alt and nested branding", payload, "system", systemVersion);

            payload = new JsonObject();
            if (rootFirst)
            {
                payload["branding"] = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() };
                payload["altText"] = currentBrandAlt?.DeepClone();
            }
            else
            {
                payload["altText"] = currentBrandAlt?.DeepClone();
                payload["branding"] = new JsonObject { ["altText"] = currentBrandAlt?.DeepClone() };
            }
            await AssertRejectedPayloadAsync("root branding and root legacy alt", payload, "system", systemVersion);
        }

        var currentPolaris = system.RootElement.GetProperty("stored").GetProperty("polaris");
        foreach (var fieldName in new[]
                 {
                     "host", "accessId", "staffDomain", "adminUser", "workstationId", "systemPolarisUserId",
                     "userId", "apiKey", "adminPassword", "clearApiKey", "clearAdminPassword"
                 })
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var polarisSection = new JsonObject
                {
                    ["host"] = JsonNode.Parse(currentPolaris.GetProperty("host").GetRawText())
                };
                if (sectionFirst)
                {
                    payload["polaris"] = polarisSection;
                    payload[fieldName] = new JsonObject();
                }
                else
                {
                    payload[fieldName] = new JsonObject();
                    payload["polaris"] = polarisSection;
                }
                await AssertRejectedPayloadAsync($"root/nested Polaris scalar {fieldName}", payload, "system", systemVersion);
            }
        }

        var storedEmail = system.RootElement.GetProperty("stored").GetProperty("email");
        foreach (var fieldName in new[] { "fromAddress", "fromName" })
        {
            foreach (var rootFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var emails = new JsonObject
                {
                    ["fromAddress"] = JsonNode.Parse(storedEmail.GetProperty("fromAddress").GetRawText()),
                    ["fromName"] = JsonNode.Parse(storedEmail.GetProperty("fromName").GetRawText())
                };
                var smtp = new JsonObject
                {
                    ["fromAddress"] = JsonNode.Parse(storedEmail.GetProperty("fromAddress").GetRawText()),
                    ["fromName"] = JsonNode.Parse(storedEmail.GetProperty("fromName").GetRawText())
                };
                if (rootFirst)
                {
                    payload[fieldName] = new JsonObject();
                    payload["emails"] = emails;
                    payload["smtp"] = smtp;
                }
                else
                {
                    payload["smtp"] = smtp;
                    payload["emails"] = emails;
                    payload[fieldName] = new JsonObject();
                }
                await AssertRejectedPayloadAsync($"root/email/smtp sender fallback {fieldName}", payload, "system", systemVersion);
            }

            foreach (var rootFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var emails = new JsonObject
                {
                    ["fromAddress"] = JsonNode.Parse(storedEmail.GetProperty("fromAddress").GetRawText()),
                    ["fromName"] = JsonNode.Parse(storedEmail.GetProperty("fromName").GetRawText())
                };
                var smtp = new JsonObject
                {
                    ["fromAddress"] = JsonNode.Parse(storedEmail.GetProperty("fromAddress").GetRawText()),
                    ["fromName"] = JsonNode.Parse(storedEmail.GetProperty("fromName").GetRawText())
                };
                if (rootFirst)
                {
                    payload[fieldName] = JsonValue.Create("conflicting sender value");
                    payload["emails"] = emails;
                    payload["smtp"] = smtp;
                }
                else
                {
                    payload["smtp"] = smtp;
                    payload["emails"] = emails;
                    payload[fieldName] = JsonValue.Create("conflicting sender value");
                }
                await AssertRejectedPayloadAsync($"contradictory root/email/smtp sender {fieldName}", payload, "system", systemVersion);
            }
        }

        foreach (var sectionFirst in new[] { false, true })
        {
            var payload = new JsonObject();
            var workflowSection = new JsonObject { ["externalSearch1Enabled"] = false };
            if (sectionFirst)
            {
                payload["workflow"] = workflowSection;
                payload["providers"] = new JsonArray();
            }
            else
            {
                payload["providers"] = new JsonArray();
                payload["workflow"] = workflowSection;
            }
            await AssertRejectedPayloadAsync("modern provider collection with legacy scalar", payload, "2", libraryVersion);

            workflowSection["externalSearch1Enabled"] = new JsonObject();
            await AssertRejectedPayloadAsync("modern provider collection with malformed legacy scalar", payload, "2", libraryVersion);
        }

        foreach (var sectionFirst in new[] { false, true })
        {
            var rootModernPayload = new JsonObject();
            if (sectionFirst)
            {
                rootModernPayload["workflow"] = new JsonObject { ["externalSearch1Enabled"] = false };
                rootModernPayload["providers"] = new JsonArray();
            }
            else
            {
                rootModernPayload["providers"] = new JsonArray();
                rootModernPayload["externalSearch1Enabled"] = false;
            }
            await AssertRejectedPayloadAsync("same-location modern and legacy provider edits", rootModernPayload, "2", libraryVersion);

            var nestedModernPayload = new JsonObject();
            if (sectionFirst)
            {
                nestedModernPayload["workflow"] = new JsonObject
                {
                    ["providers"] = new JsonArray(),
                    ["externalSearch1Enabled"] = false
                };
            }
            else
            {
                nestedModernPayload["workflow"] = new JsonObject
                {
                    ["externalSearch1Enabled"] = false,
                    ["providers"] = new JsonArray()
                };
            }
            await AssertRejectedPayloadAsync("same-section modern and legacy provider edits", nestedModernPayload, "2", libraryVersion);

            var nestedModernRootLegacy = new JsonObject();
            if (sectionFirst)
            {
                nestedModernRootLegacy["workflow"] = new JsonObject { ["providers"] = new JsonArray() };
                nestedModernRootLegacy["externalSearch1Enabled"] = false;
            }
            else
            {
                nestedModernRootLegacy["externalSearch1Enabled"] = false;
                nestedModernRootLegacy["workflow"] = new JsonObject { ["providers"] = new JsonArray() };
            }
            await AssertRejectedPayloadAsync("workflow provider collection with root legacy scalar", nestedModernRootLegacy, "2", libraryVersion);
        }

        var systemFormats = system.RootElement.GetProperty("stored").GetProperty("configuredSystem")
            .GetProperty("formats").EnumerateArray().ToArray();
        var bookLabel = systemFormats
            .Single(item => item.GetProperty("code").GetString() == "book").GetProperty("label").GetString();
        var currentLabels = new JsonObject { ["book"] = bookLabel };
        var currentFormatOrder = new JsonArray();
        var currentAvailableFormats = new JsonArray(systemFormats.Where(item => item.GetProperty("isEnabled").GetBoolean())
            .Select(item => JsonValue.Create(item.GetProperty("code").GetString())).ToArray());
        var legacyFormatValues = new Dictionary<string, JsonNode?>
        {
            ["formatLabels"] = currentLabels,
            ["formatOrder"] = currentFormatOrder,
            ["availableFormats"] = currentAvailableFormats
        };
        foreach (var sectionFirst in new[] { false, true })
        {
            foreach (var (legacyName, legacyValue) in legacyFormatValues)
            {
                var payload = new JsonObject();
                var patronSection = new JsonObject { [legacyName] = legacyValue?.DeepClone() };
                if (sectionFirst)
                {
                    payload["patron"] = patronSection;
                    payload["formats"] = new JsonArray();
                }
                else
                {
                    payload["formats"] = new JsonArray();
                    payload[legacyName] = legacyValue?.DeepClone();
                }
                await AssertRejectedPayloadAsync($"modern formats with legacy {legacyName}", payload, "system", systemVersion);
            }
        }

        foreach (var sectionFirst in new[] { false, true })
        {
            foreach (var (legacyName, legacyValue) in legacyFormatValues)
            {
                var payload = new JsonObject();
                var patronSection = new JsonObject();
                if (sectionFirst)
                {
                    patronSection["formats"] = new JsonArray();
                    patronSection[legacyName] = legacyValue?.DeepClone();
                }
                else
                {
                    patronSection[legacyName] = legacyValue?.DeepClone();
                    patronSection["formats"] = new JsonArray();
                }
                payload["patron"] = patronSection;
                await AssertRejectedPayloadAsync($"same-section modern formats with legacy {legacyName}", payload, "system", systemVersion);
            }
        }

        foreach (var (legacyName, legacyValue) in legacyFormatValues)
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                if (sectionFirst)
                {
                    payload["patron"] = new JsonObject { ["formats"] = new JsonArray() };
                    payload[legacyName] = legacyValue?.DeepClone();
                }
                else
                {
                    payload[legacyName] = legacyValue?.DeepClone();
                    payload["patron"] = new JsonObject { ["formats"] = new JsonArray() };
                }
                await AssertRejectedPayloadAsync($"root legacy {legacyName} with nested modern formats", payload, "system", systemVersion);
            }
        }

        var currentTimeoutTemplate = JsonNode.Parse(system.RootElement.GetProperty("stored").GetProperty("workflow")
            .GetProperty("outstandingTimeoutRejectionTemplateId").GetRawText());
        foreach (var sectionFirst in new[] { false, true })
        {
            var payload = new JsonObject();
            var workflowSection = new JsonObject();
            if (sectionFirst)
            {
                workflowSection["outstandingTimeoutRejectionTemplate"] = new JsonObject();
                workflowSection["outstandingTimeoutRejectionTemplateId"] = currentTimeoutTemplate?.DeepClone();
            }
            else
            {
                workflowSection["outstandingTimeoutRejectionTemplateId"] = currentTimeoutTemplate?.DeepClone();
                workflowSection["outstandingTimeoutRejectionTemplate"] = new JsonObject();
            }
            payload["workflow"] = workflowSection;
            await AssertRejectedPayloadAsync("timeout rejection template aliases", payload, "system", systemVersion);
        }

        foreach (var statusMapFirst in new[] { false, true })
        {
            var payload = new JsonObject();
            var patronSection = new JsonObject();
            if (statusMapFirst)
            {
                patronSection["duplicateStatusLabels"] = new JsonObject();
                patronSection["duplicateLabels"] = JsonValue.Create(42);
            }
            else
            {
                patronSection["duplicateLabels"] = JsonValue.Create(42);
                patronSection["duplicateStatusLabels"] = new JsonObject();
            }
            payload["patron"] = patronSection;
            await AssertRejectedPayloadAsync("duplicate status label map aliases", payload, "system", systemVersion);

            var currentLabel = JsonNode.Parse(system.RootElement.GetProperty("stored").GetProperty("patron")
                .GetProperty("suggestionStatusLabel").GetRawText());
            payload = new JsonObject();
            patronSection = new JsonObject();
            if (statusMapFirst)
            {
                patronSection["duplicateStatusLabels"] = new JsonObject { ["suggestion"] = currentLabel?.DeepClone() };
                patronSection["duplicateLabels"] = new JsonObject();
            }
            else
            {
                patronSection["duplicateLabels"] = new JsonObject();
                patronSection["duplicateStatusLabels"] = new JsonObject { ["suggestion"] = currentLabel?.DeepClone() };
            }
            payload["patron"] = patronSection;
            await AssertRejectedPayloadAsync("duplicate status label maps both supplied", payload, "system", systemVersion);

            payload = new JsonObject
            {
                ["patron"] = new JsonObject
                {
                    ["duplicateStatusLabels"] = new JsonObject { ["unknown_status"] = "Unknown" }
                }
            };
            await AssertRejectedPayloadAsync("unknown duplicate status label", payload, "system", systemVersion);

            payload = new JsonObject
            {
                ["patron"] = new JsonObject
                {
                    ["duplicateStatusLabels"] = new JsonObject { ["suggestion"] = JsonValue.Create(42) }
                }
            };
            await AssertRejectedPayloadAsync("wrong-kind duplicate status label", payload, "system", systemVersion);
        }

        foreach (var (status, scalarProperty) in new[]
                 {
                     ("suggestion", "suggestionStatusLabel"),
                     ("outstanding_purchase", "outstandingPurchaseStatusLabel"),
                     ("pending_hold", "pendingHoldStatusLabel"),
                     ("hold_placed", "holdPlacedStatusLabel"),
                     ("closed", "closedStatusLabel"),
                     ("rejected", "rejectedStatusLabel"),
                     ("hold_completed", "holdCompletedStatusLabel"),
                     ("hold_not_picked_up", "holdNotPickedUpStatusLabel"),
                     ("manual", "manualStatusLabel"),
                     ("silent", "silentStatusLabel")
                 })
        {
            var currentLabel = JsonNode.Parse(currentSystemPatron.GetProperty(scalarProperty).GetRawText());
            foreach (var mapFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var patronSection = new JsonObject();
                var labels = new JsonObject { [status] = currentLabel?.DeepClone() };
                if (mapFirst)
                {
                    patronSection["duplicateStatusLabels"] = labels;
                    patronSection[scalarProperty] = currentLabel?.DeepClone();
                }
                else
                {
                    patronSection[scalarProperty] = currentLabel?.DeepClone();
                    patronSection["duplicateStatusLabels"] = labels;
                }
                payload["patron"] = patronSection;
                await AssertRejectedPayloadAsync($"duplicate map and explicit {scalarProperty}", payload, "system", systemVersion);
            }
        }

        foreach (var sectionFirst in new[] { false, true })
        {
            var payload = new JsonObject();
            var currentLabel = JsonNode.Parse(currentSystemPatron.GetProperty("suggestionStatusLabel").GetRawText());
            if (sectionFirst)
            {
                payload["patron"] = new JsonObject { ["suggestionStatusLabel"] = currentLabel?.DeepClone() };
                payload["duplicateStatusLabels"] = new JsonObject();
            }
            else
            {
                payload["duplicateStatusLabels"] = new JsonObject();
                payload["patron"] = new JsonObject { ["suggestionStatusLabel"] = currentLabel?.DeepClone() };
            }
            await AssertRejectedPayloadAsync("root map with nested explicit status label", payload, "system", systemVersion);
        }

        var emailHasToken = system.RootElement.GetProperty("stored").GetProperty("email")
            .GetProperty("hasPostmarkToken").GetBoolean();
        if (!emailHasToken)
        {
            foreach (var sectionFirst in new[] { false, true })
            {
                var payload = new JsonObject();
                var emailSection = new JsonObject { ["clearPostmarkToken"] = JsonValue.Create(true) };
                if (sectionFirst)
                {
                    payload["email"] = emailSection;
                    payload["clearServerToken"] = JsonValue.Create(false);
                }
                else
                {
                    payload["clearServerToken"] = JsonValue.Create(false);
                    payload["email"] = emailSection;
                }
                await AssertRejectedPayloadAsync("contradictory Postmark clear aliases", payload, "system", systemVersion);
            }
        }

        using var libraryAfter = await ReadSettingsDocumentAsync(client, "2");
        using var systemAfter = await ReadSettingsDocumentAsync(client, "system");
        Assert.AreEqual(libraryVersion, libraryAfter.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(systemVersion, systemAfter.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(libraryStoredBefore, libraryAfter.RootElement.GetProperty("stored").GetRawText());
        Assert.AreEqual(systemStoredBefore, systemAfter.RootElement.GetProperty("stored").GetRawText());
        Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
        Assert.AreEqual(sessionCountBefore, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];"));
        Assert.AreEqual(activeSessionCountBefore,
            await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession] WHERE [RevokedUtc] IS NULL;"));
        Assert.AreEqual(outboxCountBefore, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
    }

    [TestMethod]
    public async Task WildcardEmbedOriginPostNormalizesAndInvalidEntriesRollBackOtherSettings()
    {
        var originalOrigins = await ReadSystemOriginRowsAsync();
        var originalStaffUrl = await ReadSystemStaffUrlAsync();
        var auditBefore = await ReadAuditHighWatermarkAsync();
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var validStaffUrl = $"https://authority-{Guid.NewGuid():N}.example.org";
        var invalidStaffUrl = $"https://invalid-{Guid.NewGuid():N}.example.org";
        try
        {
            using var before = await ReadSettingsDocumentAsync(client, "system");
            var beforeVersion = before.RootElement.GetProperty("version").GetString()!;
            using (var accepted = await PostSettingsJsonAsync(client, JsonSerializer.Serialize(new
                   {
                       orgId = "system",
                       version = beforeVersion,
                       systemSettings = new
                       {
                           staffUrl = validStaffUrl,
                           patronEmbedAllowedOrigins = new[] { "https://*.DOMAIN.EXAMPLE" }
                       }
                   })))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
            }

            using var acceptedSettings = await ReadSettingsDocumentAsync(client, "system");
            var acceptedVersion = acceptedSettings.RootElement.GetProperty("version").GetString()!;
            var acceptedSystem = acceptedSettings.RootElement.GetProperty("stored").GetProperty("systemSettings");
            Assert.AreEqual(validStaffUrl, acceptedSystem.GetProperty("staffUrl").GetString());
            CollectionAssert.AreEqual(new[] { "https://*.domain.example" }, acceptedSystem
                .GetProperty("patronEmbedAllowedOrigins").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin]
                WHERE [OrganizationId] = 1 AND [Origin] = N'https://*.domain.example' AND
                      [NormalizedOrigin] = N'https://*.domain.example';
                """));
            var auditAfterAccepted = await ReadAuditHighWatermarkAsync();

            foreach (var invalidOrigin in new[]
                     {
                         "https://*.domain.example/path",
                         "https://*.domain.example?query",
                         "https://*.domain.example#fragment",
                         "http://*.domain.example"
                     })
            {
                using var rejected = await PostSettingsJsonAsync(client, JsonSerializer.Serialize(new
                {
                    orgId = "system",
                    version = acceptedVersion,
                    systemSettings = new
                    {
                        staffUrl = invalidStaffUrl,
                        patronEmbedAllowedOrigins = new[] { invalidOrigin }
                    }
                }));
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode,
                    $"Wildcard origin {invalidOrigin} was accepted: {await rejected.Content.ReadAsStringAsync()}");
                using var afterRejected = await ReadSettingsDocumentAsync(client, "system");
                Assert.AreEqual(acceptedVersion, afterRejected.RootElement.GetProperty("version").GetString());
                var afterSystem = afterRejected.RootElement.GetProperty("stored").GetProperty("systemSettings");
                Assert.AreEqual(validStaffUrl, afterSystem.GetProperty("staffUrl").GetString());
                CollectionAssert.AreEqual(new[] { "https://*.domain.example" }, afterSystem
                    .GetProperty("patronEmbedAllowedOrigins").EnumerateArray().Select(item => item.GetString()).ToArray());
                Assert.AreEqual(auditAfterAccepted, await ReadAuditHighWatermarkAsync());
                Assert.AreEqual(1, await ReadCountAsync("""
                    SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin]
                    WHERE [OrganizationId] = 1 AND [Origin] = N'https://*.domain.example' AND
                          [NormalizedOrigin] = N'https://*.domain.example';
                    """));
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[SystemSettings]
                SET [StaffApplicationUrl] = @staffUrl, [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OrganizationId] = 1;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@staffUrl", (object?)originalStaffUrl ?? DBNull.Value), ("@auditBefore", auditBefore));
            await RestoreSystemOriginRowsAsync(originalOrigins);
        }
    }

    [TestMethod]
    public async Task SettingsCollectionsRejectContradictoryIdentitiesAndWrongScopesAtomically()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var firstFieldKey = $"identity_first_{suffix}";
        var secondFieldKey = $"identity_second_{suffix}";
        var firstTemplateKey = $"identity_template_first_{suffix}";
        var secondTemplateKey = $"identity_template_second_{suffix}";
        var resetFormatCode = $"identity_format_{suffix}";
        var resetProviderKey = $"identity_provider_{suffix}";
        var resetFormatOverrideLabel = $"Library format override {suffix}";
        var resetProviderOverrideLabel = $"Library provider override {suffix}";
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long firstFieldId = 0;
        long secondFieldId = 0;
        long resetFormatId = 0;
        long resetProviderId = 0;
        var auditBefore = await ReadAuditHighWatermarkAsync();
        var sessionsBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];");
        var outboxBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var firstField = new PatronCustomField
                {
                    LibraryOrganizationId = 2, FieldKey = firstFieldKey, FieldType = "text",
                    Label = "First identity", IsEnabled = true, SortOrder = 9810
                };
                var secondField = new PatronCustomField
                {
                    LibraryOrganizationId = 2, FieldKey = secondFieldKey, FieldType = "text",
                    Label = "Second identity", IsEnabled = true, SortOrder = 9820
                };
                var firstTemplateRow = new EmailTemplate
                {
                    OrganizationId = 1, TemplateKey = firstTemplateKey, DisplayName = "First identity template",
                    SubjectTemplate = "First subject", BodyTemplate = "First body", SortOrder = 9870
                };
                var secondTemplateRow = new EmailTemplate
                {
                    OrganizationId = 1, TemplateKey = secondTemplateKey, DisplayName = "Second identity template",
                    SubjectTemplate = "Second subject", BodyTemplate = "Second body", SortOrder = 9880
                };
                var resetFormat = new MaterialFormat
                {
                    OwnerOrganizationId = 1, Code = resetFormatCode, Label = "Identity reset format",
                    SortOrder = 9890, IsEnabled = true, CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                    UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime
                };
                var resetProviderRow = new ExternalSearchProvider
                {
                    OrganizationId = 1, ProviderKey = resetProviderKey, Label = "Identity reset provider",
                    UrlTemplate = "https://provider.example.test/?id={{id}}", IsEnabled = true, SortOrder = 9890
                };
                seed.PatronCustomFields.AddRange(firstField, secondField);
                seed.EmailTemplates.AddRange(firstTemplateRow, secondTemplateRow);
                seed.MaterialFormats.Add(resetFormat);
                seed.ExternalSearchProviders.Add(resetProviderRow);
                await seed.SaveChangesAsync();
                firstFieldId = firstField.Id;
                secondFieldId = secondField.Id;
                resetFormatId = resetFormat.Id;
                resetProviderId = resetProviderRow.Id;
                seed.MaterialFormatOverrides.Add(new MaterialFormatOverride
                {
                    LibraryOrganizationId = 2, MaterialFormatId = resetFormatId,
                    Label = resetFormatOverrideLabel, IsEnabled = false
                });
                seed.ExternalSearchProviderOverrides.Add(new ExternalSearchProviderOverride
                {
                    LibraryOrganizationId = 2, ExternalSearchProviderId = resetProviderId,
                    IsEnabled = false, Label = resetProviderOverrideLabel
                });
                seed.EmailTemplates.Add(new EmailTemplate
                {
                    OrganizationId = 2, TemplateKey = firstTemplateKey, SourceTemplateId = firstTemplateRow.Id,
                    DisplayName = "First library override", SubjectTemplate = "Library baseline subject",
                    BodyTemplate = "Library baseline body", SortOrder = 9870
                });
                await seed.SaveChangesAsync();
            }

            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var library = await ReadSettingsDocumentAsync(client, "2");
            using var system = await ReadSettingsDocumentAsync(client, "system");
            var libraryVersion = library.RootElement.GetProperty("version").GetString()!;
            var systemVersion = system.RootElement.GetProperty("version").GetString()!;
            var formats = library.RootElement.GetProperty("stored").GetProperty("formats").EnumerateArray().ToArray();
            var book = formats.Single(item => item.GetProperty("code").GetString() == "book");
            var dvd = formats.Single(item => item.GetProperty("code").GetString() == "dvd");
            var providers = system.RootElement.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("providers").EnumerateArray().ToArray();
            Assert.IsTrue(providers.Length >= 2, "The baseline must contain multiple configured providers.");
            var resetProvider = providers.Single(item => item.GetProperty("key").GetString() == resetProviderKey);
            var templates = system.RootElement.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("templates").EnumerateArray().ToArray();
            Assert.IsTrue(templates.Length >= 2, "The baseline must contain multiple configured templates.");
            var firstTemplate = templates.Single(item => item.GetProperty("templateKey").GetString() == firstTemplateKey);
            var secondTemplate = templates.Single(item => item.GetProperty("templateKey").GetString() == secondTemplateKey);
            var firstTemplateId = long.Parse(firstTemplate.GetProperty("id").GetString()!);

            async Task AssertRejectedAsync(string scope, string version, string name, JsonObject edit)
            {
                edit["orgId"] = scope;
                edit["version"] = version;
                using var response = await PostSettingsJsonAsync(client, edit.ToJsonString());
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                    $"Invalid {name} was accepted: {await response.Content.ReadAsStringAsync()}");
            }

            await AssertRejectedAsync("2", libraryVersion, "format ID/code identity", new JsonObject
            {
                ["formats"] = new JsonArray(new JsonObject
                {
                    ["id"] = book.GetProperty("id").GetString(),
                    ["code"] = dvd.GetProperty("code").GetString(),
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "materialFormatId/code identity alias", new JsonObject
            {
                ["formats"] = new JsonArray(new JsonObject
                {
                    ["materialFormatId"] = book.GetProperty("id").GetString(),
                    ["code"] = dvd.GetProperty("code").GetString(),
                    ["label"] = "Must not be applied"
                })
            });
            foreach (var contradictoryFormatAliases in new[]
                     {
                         new JsonObject
                         {
                             ["id"] = book.GetProperty("id").GetString(),
                             ["materialFormatId"] = dvd.GetProperty("id").GetString(),
                             ["code"] = "book",
                             ["label"] = "Must not be applied"
                         },
                         new JsonObject
                         {
                             ["materialFormatId"] = dvd.GetProperty("id").GetString(),
                             ["id"] = book.GetProperty("id").GetString(),
                             ["code"] = "book",
                             ["label"] = "Must not be applied"
                         }
                     })
            {
                await AssertRejectedAsync("2", libraryVersion, "contradictory format ID aliases", new JsonObject
                {
                    ["formats"] = new JsonArray(contradictoryFormatAliases)
                });
            }
            await AssertRejectedAsync("2", libraryVersion, "format reset with active override", new JsonObject
            {
                ["formats"] = new JsonArray(new JsonObject
                {
                    ["id"] = resetFormatId,
                    ["code"] = resetFormatCode,
                    ["reset"] = true,
                    ["overridden"] = true,
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "null format override intent", new JsonObject
            {
                ["formats"] = new JsonArray(new JsonObject
                {
                    ["id"] = resetFormatId,
                    ["code"] = resetFormatCode,
                    ["reset"] = true,
                    ["overridden"] = null
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "wrong-kind format override intent", new JsonObject
            {
                ["formats"] = new JsonArray(new JsonObject
                {
                    ["id"] = resetFormatId,
                    ["code"] = resetFormatCode,
                    ["reset"] = true,
                    ["overridden"] = new JsonArray()
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "unknown format ID with a valid code", new JsonObject
            {
                ["formats"] = new JsonArray(new JsonObject
                {
                    ["id"] = 999999999,
                    ["code"] = book.GetProperty("code").GetString(),
                    ["label"] = "Must not be applied"
                })
            });
            foreach (var duplicateFormats in new[]
                     {
                         new JsonArray(
                             new JsonObject { ["id"] = book.GetProperty("id").GetString(), ["label"] = "First duplicate format" },
                             new JsonObject { ["code"] = "book", ["label"] = "Second duplicate format" }),
                         new JsonArray(
                             new JsonObject { ["code"] = "book", ["label"] = "First duplicate format" },
                             new JsonObject { ["id"] = book.GetProperty("id").GetString(), ["label"] = "Second duplicate format" })
                     })
            {
                await AssertRejectedAsync("2", libraryVersion, "duplicate format ID/code target", new JsonObject
                {
                    ["formats"] = duplicateFormats
                });
            }
            await AssertRejectedAsync("2", libraryVersion, "custom-field ID/key identity", new JsonObject
            {
                ["customFields"] = new JsonArray(new JsonObject
                {
                    ["id"] = firstFieldId,
                    ["key"] = secondFieldKey,
                    ["type"] = "text",
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "contradictory custom-field key aliases", new JsonObject
            {
                ["customFields"] = new JsonArray(new JsonObject
                {
                    ["id"] = firstFieldId,
                    ["key"] = firstFieldKey,
                    ["fieldKey"] = secondFieldKey,
                    ["type"] = "text",
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "contradictory custom-field type aliases", new JsonObject
            {
                ["customFields"] = new JsonArray(new JsonObject
                {
                    ["key"] = firstFieldKey,
                    ["type"] = "text",
                    ["fieldType"] = "select",
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "unknown custom-field ID with a known key", new JsonObject
            {
                ["customFields"] = new JsonArray(new JsonObject
                {
                    ["id"] = 999999999,
                    ["key"] = firstFieldKey,
                    ["type"] = "text",
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("system", systemVersion, "provider ID/key identity", new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject
                {
                    ["id"] = providers[0].GetProperty("id").GetString(),
                    ["key"] = providers[1].GetProperty("key").GetString(),
                    ["label"] = "Must not be applied"
                })
            });
            foreach (var contradictoryProviderAliases in new[]
                     {
                         new JsonObject
                         {
                             ["key"] = providers[0].GetProperty("key").GetString(),
                             ["providerKey"] = providers[1].GetProperty("key").GetString(),
                             ["label"] = "Must not be applied"
                         },
                         new JsonObject
                         {
                             ["providerKey"] = providers[1].GetProperty("key").GetString(),
                             ["key"] = providers[0].GetProperty("key").GetString(),
                             ["label"] = "Must not be applied"
                         }
                     })
            {
                await AssertRejectedAsync("system", systemVersion, "contradictory provider key aliases", new JsonObject
                {
                    ["providers"] = new JsonArray(contradictoryProviderAliases)
                });
            }
            await AssertRejectedAsync("2", libraryVersion, "provider reset with active override", new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject
                {
                    ["id"] = resetProvider.GetProperty("id").GetString(),
                    ["key"] = resetProviderKey,
                    ["reset"] = true,
                    ["overridden"] = true,
                    ["label"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "null provider override intent", new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject
                {
                    ["id"] = resetProvider.GetProperty("id").GetString(),
                    ["key"] = resetProviderKey,
                    ["reset"] = true,
                    ["overridden"] = null
                })
            });
            await AssertRejectedAsync("2", libraryVersion, "wrong-kind provider override intent", new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject
                {
                    ["id"] = resetProvider.GetProperty("id").GetString(),
                    ["key"] = resetProviderKey,
                    ["reset"] = true,
                    ["overridden"] = new JsonArray()
                })
            });
            await AssertRejectedAsync("system", systemVersion, "unknown provider ID with a known key", new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject
                {
                    ["id"] = 999999999,
                    ["key"] = providers[0].GetProperty("key").GetString(),
                    ["label"] = "Must not be applied",
                    ["urlTemplate"] = providers[0].GetProperty("urlTemplate").GetString()
                })
            });
            await AssertRejectedAsync("system", systemVersion, "provider ID/key duplicate identity", new JsonObject
            {
                ["providers"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = providers[0].GetProperty("id").GetString(),
                        ["key"] = providers[0].GetProperty("key").GetString(),
                        ["label"] = "First attempted change"
                    },
                    new JsonObject
                    {
                        ["key"] = providers[0].GetProperty("key").GetString(),
                        ["label"] = "Second attempted change"
                    })
            });
            await AssertRejectedAsync("system", systemVersion, "template ID/key identity", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["id"] = firstTemplate.GetProperty("id").GetString(),
                    ["templateKey"] = secondTemplate.GetProperty("templateKey").GetString(),
                    ["subject"] = "Must not be applied",
                    ["body"] = "Must not be applied"
                })
            });
            foreach (var contradictoryTemplateAliases in new[]
                     {
                         new JsonObject
                         {
                             ["templateKey"] = firstTemplateKey,
                             ["key"] = secondTemplateKey,
                             ["subject"] = "Must not be applied",
                             ["body"] = "Must not be applied"
                         },
                         new JsonObject
                         {
                             ["key"] = secondTemplateKey,
                             ["templateKey"] = firstTemplateKey,
                             ["subject"] = "Must not be applied",
                             ["body"] = "Must not be applied"
                         }
                     })
            {
                await AssertRejectedAsync("system", systemVersion, "contradictory template key aliases", new JsonObject
                {
                    ["templates"] = new JsonArray(contradictoryTemplateAliases)
                });
            }
            await AssertRejectedAsync("system", systemVersion, "template reset with active override", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["templateKey"] = firstTemplateKey,
                    ["reset"] = true,
                    ["overridden"] = true,
                    ["subject"] = "Must not be applied",
                    ["body"] = "Must not be applied"
                })
            });
            await AssertRejectedAsync("system", systemVersion, "null template override intent", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["templateKey"] = firstTemplateKey,
                    ["reset"] = true,
                    ["overridden"] = null
                })
            });
            await AssertRejectedAsync("system", systemVersion, "wrong-kind template override intent", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["templateKey"] = firstTemplateKey,
                    ["reset"] = true,
                    ["overridden"] = new JsonArray()
                })
            });
            await AssertRejectedAsync("system", systemVersion, "contradictory template visibility flags", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["templateKey"] = firstTemplateKey,
                    ["enabled"] = true,
                    ["hidden"] = true
                })
            });
            await AssertRejectedAsync("system", systemVersion, "contradictory hidden aliases", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["templateKey"] = firstTemplateKey,
                    ["hidden"] = false,
                    ["isHidden"] = true
                })
            });
            foreach (var duplicateSystemTemplates in new[]
                     {
                         new JsonArray(
                             new JsonObject { ["id"] = firstTemplate.GetProperty("id").GetString(), ["templateKey"] = firstTemplateKey, ["subject"] = "First duplicate subject", ["body"] = "First duplicate body" },
                             new JsonObject { ["templateKey"] = firstTemplateKey, ["subject"] = "Second duplicate subject", ["body"] = "Second duplicate body" }),
                         new JsonArray(
                             new JsonObject { ["templateKey"] = firstTemplateKey, ["subject"] = "First duplicate subject", ["body"] = "First duplicate body" },
                             new JsonObject { ["id"] = firstTemplate.GetProperty("id").GetString(), ["templateKey"] = firstTemplateKey, ["subject"] = "Second duplicate subject", ["body"] = "Second duplicate body" })
                     })
            {
                await AssertRejectedAsync("system", systemVersion, "duplicate system template ID/key target", new JsonObject
                {
                    ["templates"] = duplicateSystemTemplates
                });
            }
            await AssertRejectedAsync("2", libraryVersion, "duplicate library template lineage", new JsonObject
            {
                ["templates"] = new JsonArray(
                    new JsonObject
                    {
                        ["sourceTemplateId"] = firstTemplateId,
                        ["templateKey"] = firstTemplateKey,
                        ["overridden"] = true,
                        ["subject"] = "First duplicate library subject"
                    },
                    new JsonObject
                    {
                        ["sourceTemplateId"] = firstTemplateId,
                        ["templateKey"] = firstTemplateKey,
                        ["overridden"] = true,
                        ["body"] = "Second duplicate library body"
                    })
            });
            await AssertRejectedAsync("2", libraryVersion, "template organization scope", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["organizationId"] = 1,
                    ["templateKey"] = "scope_mismatch"
                })
            });
            await AssertRejectedAsync("system", systemVersion, "system template organization scope", new JsonObject
            {
                ["templates"] = new JsonArray(new JsonObject
                {
                    ["organizationId"] = 2,
                    ["templateKey"] = "scope_mismatch",
                    ["subject"] = "Must not be applied",
                    ["body"] = "Must not be applied"
                })
            });

            using var libraryAfter = await ReadSettingsDocumentAsync(client, "2");
            using var systemAfter = await ReadSettingsDocumentAsync(client, "system");
            Assert.AreEqual(libraryVersion, libraryAfter.RootElement.GetProperty("version").GetString());
            Assert.AreEqual(systemVersion, systemAfter.RootElement.GetProperty("version").GetString());
            Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
            Assert.AreEqual(sessionsBefore, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[PatronSession];"));
            Assert.AreEqual(outboxBefore, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            var fieldsAfter = libraryAfter.RootElement.GetProperty("stored").GetProperty("customFields").EnumerateArray().ToArray();
            Assert.AreEqual("First identity", fieldsAfter.Single(item => item.GetProperty("id").GetString() == firstFieldId.ToString())
                .GetProperty("label").GetString());
            Assert.AreEqual(secondFieldKey, fieldsAfter.Single(item => item.GetProperty("id").GetString() == secondFieldId.ToString())
                .GetProperty("key").GetString());
            var formatsAfter = libraryAfter.RootElement.GetProperty("stored").GetProperty("formats").EnumerateArray().ToArray();
            Assert.AreEqual(book.GetProperty("label").GetString(),
                formatsAfter.Single(item => item.GetProperty("code").GetString() == "book").GetProperty("label").GetString());
            var providersAfter = systemAfter.RootElement.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("providers").EnumerateArray().ToArray();
            Assert.AreEqual(providers[0].GetProperty("label").GetString(),
                providersAfter.Single(item => item.GetProperty("id").GetString() == providers[0].GetProperty("id").GetString())
                    .GetProperty("label").GetString());
            Assert.AreEqual(0, await ReadCountAsync($"""
                SELECT COUNT(*) FROM [asap].[MaterialFormatOverride]
                WHERE [LibraryOrganizationId] = 2 AND [MaterialFormatId] = {long.Parse(book.GetProperty("id").GetString()!)}
                  AND [Label] IN (N'First duplicate format', N'Second duplicate format');
                """));
            Assert.AreEqual(1, await ReadCountAsync($"""
                SELECT COUNT(*) FROM [asap].[MaterialFormatOverride]
                WHERE [LibraryOrganizationId] = 2 AND [MaterialFormatId] = {resetFormatId}
                  AND [Label] = N'{resetFormatOverrideLabel}' AND [IsEnabled] = 0;
                """));
            Assert.AreEqual(1, await ReadCountAsync($"""
                SELECT COUNT(*) FROM [asap].[ExternalSearchProviderOverride]
                WHERE [LibraryOrganizationId] = 2 AND [ExternalSearchProviderId] = {resetProviderId}
                  AND [Label] = N'{resetProviderOverrideLabel}' AND [IsEnabled] = 0;
                """));
            Assert.AreEqual(1, await ReadCountAsync($"""
                SELECT COUNT(*) FROM [asap].[ExternalSearchProvider]
                WHERE [Id] = {resetProviderId} AND [ProviderKey] = N'{resetProviderKey}'
                  AND [Label] = N'Identity reset provider';
                """));
            Assert.AreEqual(3, await ReadCountAsync($"""
                SELECT COUNT(*) FROM [asap].[EmailTemplate]
                WHERE ([Id] = {firstTemplateId} AND [SubjectTemplate] = N'First subject' AND [BodyTemplate] = N'First body')
                   OR ([OrganizationId] = 1 AND [TemplateKey] = N'{secondTemplateKey}' AND [SubjectTemplate] = N'Second subject' AND [BodyTemplate] = N'Second body')
                   OR ([OrganizationId] = 2 AND [SourceTemplateId] = {firstTemplateId}
                       AND [SubjectTemplate] = N'Library baseline subject' AND [BodyTemplate] = N'Library baseline body');
                """));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[PatronCustomField] WHERE [Id] IN (@firstId, @secondId);
                DELETE FROM [asap].[MaterialFormatOverride]
                WHERE [LibraryOrganizationId] = 2 AND [MaterialFormatId] IN (@resetFormatId,
                    (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'))
                  AND ([MaterialFormatId] = @resetFormatId OR [Label] IN (N'First duplicate format', N'Second duplicate format'));
                DELETE FROM [asap].[MaterialFormat] WHERE [Id] = @resetFormatId;
                DELETE FROM [asap].[ExternalSearchProviderOverride]
                WHERE [LibraryOrganizationId] = 2 AND [ExternalSearchProviderId] = @resetProviderId;
                DELETE FROM [asap].[ExternalSearchProvider] WHERE [Id] = @resetProviderId;
                DELETE FROM [asap].[EmailTemplate]
                WHERE [OrganizationId] = 2 AND [TemplateKey] = @firstTemplateKey
                  AND [SourceTemplateId] = (SELECT [Id] FROM [asap].[EmailTemplate]
                      WHERE [OrganizationId] = 1 AND [TemplateKey] = @firstTemplateKey);
                DELETE FROM [asap].[EmailTemplate]
                WHERE [OrganizationId] = 1 AND [TemplateKey] IN (@firstTemplateKey, @secondTemplateKey);
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """,
                ("@firstId", firstFieldId), ("@secondId", secondFieldId),
                ("@resetFormatId", resetFormatId), ("@resetProviderId", resetProviderId),
                ("@firstTemplateKey", firstTemplateKey), ("@secondTemplateKey", secondTemplateKey),
                ("@auditBefore", auditBefore));
        }
    }

    [TestMethod]
    public async Task DuplicateAutoClaimFormatsAreRejectedRegardlessOfActiveRuleOrdering()
    {
        const int organizationId = 88791;
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long staffId = 0;
        long formatId = 0;
        var auditBefore = await ReadAuditHighWatermarkAsync();
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            var format = await seed.MaterialFormats.AsNoTracking()
                .SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
            formatId = format.Id;
            seed.Organizations.Add(new Organization
            {
                Id = organizationId,
                DisplayName = "Auto-claim contract library",
                Abbreviation = "ACL",
                OrganizationCodeId = 2,
                ParentOrganizationId = 1,
                IsActive = true
            });
            var staff = new StaffUser
            {
                UserPrincipalName = $"auto-claim-contract-{Guid.NewGuid():N}@example.org",
                Role = StaffRole.Staff,
                OrganizationId = organizationId,
                IsActive = true
            };
            staff.NormalizedUserPrincipalName = staff.UserPrincipalName.ToUpperInvariant();
            seed.StaffUsers.Add(staff);
            await seed.SaveChangesAsync();
            staffId = staff.Id;
            seed.FormatAutoClaimRules.Add(new FormatAutoClaimRule
            {
                LibraryOrganizationId = organizationId,
                MaterialFormatId = formatId,
                StaffUserId = staffId,
                IsActive = true,
                CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime
            });
            await seed.SaveChangesAsync();
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var settings = await ReadSettingsDocumentAsync(client, organizationId.ToString());
            var version = settings.RootElement.GetProperty("version").GetString()!;

            JsonObject Rule(bool active) => new()
            {
                ["materialFormatId"] = formatId,
                ["staffUserId"] = staffId,
                ["active"] = active
            };
            foreach (var rules in new[]
                     {
                         new JsonArray(Rule(false), Rule(true)),
                         new JsonArray(Rule(true), Rule(false))
                     })
            {
                var payload = new JsonObject
                {
                    ["orgId"] = organizationId.ToString(),
                    ["version"] = version,
                    ["autoClaimRules"] = rules
                };
                using var response = await PostSettingsJsonAsync(client, payload.ToJsonString());
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                    $"Duplicate auto-claim format was accepted: {await response.Content.ReadAsStringAsync()}");
                Assert.AreEqual(1, await ReadCountAsync($"SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = {organizationId} AND [MaterialFormatId] = {formatId} AND [StaffUserId] = {staffId} AND [IsActive] = 1;"));
                using var after = await ReadSettingsDocumentAsync(client, organizationId.ToString());
                Assert.AreEqual(version, after.RootElement.GetProperty("version").GetString());
                Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
            }

            var contradictoryAliasPayload = new JsonObject
            {
                ["orgId"] = organizationId.ToString(),
                ["version"] = version,
                ["autoClaimRules"] = new JsonArray(new JsonObject
                {
                    ["materialFormatId"] = formatId,
                    ["formatId"] = formatId + 1,
                    ["staffUserId"] = staffId,
                    ["active"] = false
                })
            };
            using (var response = await PostSettingsJsonAsync(client, contradictoryAliasPayload.ToJsonString()))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                    $"Contradictory auto-claim format aliases were accepted: {await response.Content.ReadAsStringAsync()}");
            }
            Assert.AreEqual(1, await ReadCountAsync($"SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = {organizationId} AND [MaterialFormatId] = {formatId} AND [StaffUserId] = {staffId} AND [IsActive] = 1;"));
            using (var after = await ReadSettingsDocumentAsync(client, organizationId.ToString()))
            {
                Assert.AreEqual(version, after.RootElement.GetProperty("version").GetString());
            }
            Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
        }
        finally
        {
            await ExecuteNonQueryAsync("DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @organizationId; DELETE FROM [asap].[StaffUser] WHERE [Id] = @staffId; DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId; DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;",
                ("@organizationId", organizationId), ("@staffId", staffId), ("@auditBefore", auditBefore));
        }
    }

    [TestMethod]
    public async Task ActiveBranchTitleRequestCannotBeMutatedAsAnActiveLibrary()
    {
        long requestId;
        byte[] originalVersion;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 101 AND [OrganizationCodeId] = 3;
                DECLARE @formatId bigint = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
                VALUES (101, N'authority-branch', N'Branch authority request', 0, @formatId, N'suggestion', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = CAST(SCOPE_IDENTITY() AS bigint);
                SELECT @requestId, [RowVersion] FROM [asap].[TitleRequest] WHERE [Id] = @requestId;
                """;
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            requestId = reader.GetInt64(0);
            originalVersion = (byte[])reader[1];
        }

        var outboxBefore = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var response = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestId}/claim",
                new { version = StaffVersion.Encode(originalVersion) });
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("organization_inactive", result.RootElement.GetProperty("code").GetString());

            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var verify = new SqlCommand("""
                SELECT [Status], [RowVersion],
                    (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId),
                    (SELECT COUNT(*) FROM [asap].[EmailOutbox])
                FROM [asap].[TitleRequest] WHERE [Id] = @requestId;
                """, connection);
            verify.Parameters.AddWithValue("@requestId", requestId);
            await using var row = await verify.ExecuteReaderAsync();
            Assert.IsTrue(await row.ReadAsync());
            Assert.AreEqual("suggestion", row.GetString(0));
            CollectionAssert.AreEqual(originalVersion, (byte[])row[1]);
            Assert.AreEqual(0, row.GetInt32(2));
            Assert.AreEqual(outboxBefore, row.GetInt32(3));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[TitleRequest] WHERE [Id] = @requestId;
                UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 101 AND [OrganizationCodeId] = 3;
                """, ("@requestId", requestId));
        }
    }

    [TestMethod]
    public async Task SettingsBrowserJourneyRoundTripsEditorSnapshotsThroughRealSqlAndHttp()
    {
        factory!.UseKestrel(0);
        const string retiredKey = "authority_retired";
        const string dynamicKey = "browser_select";
        var originalSystemCodes = await ReadPatronCodeRowsAsync(1);
        var originalCodes = await ReadPatronCodeRowsAsync(2);
        var originalOrganizationActive = await ReadCountAsync("SELECT CONVERT(int, [IsActive]) FROM [asap].[Organization] WHERE [Id] = 2;");
        var originalOrigins = await ReadSystemOriginRowsAsync();
        var auditBefore = await ReadAuditHighWatermarkAsync();
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (3, N'Participation Library Three', N'P3', 2, 1, 0),
                   (4, N'Inactive Library Four', N'P4', 2, 1, 0);
            UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 2;
            DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] IN (1, 2);
            DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] IN (1, 2);
            INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (1), (2);
            INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId])
            VALUES (1, 1), (1, 2), (2, 1), (2, 2);
            """);
        var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        provider.SetReferenceData(
            [
                new PolarisOrganizationSnapshot(1, "System", null, 1, null),
                new PolarisOrganizationSnapshot(2, "Test Library", "TEST", 2, 1),
                new PolarisOrganizationSnapshot(3, "Participation Library Three", "P3", 2, 1),
                new PolarisOrganizationSnapshot(4, "Inactive Library Four", "P4", 2, 1),
                new PolarisOrganizationSnapshot(101, "Main Library Branch", "MAIN", 3, 2)
            ],
            [new PolarisPatronCodeSnapshot(1, "Child"), new PolarisPatronCodeSnapshot(2, "Adult"),
             new PolarisPatronCodeSnapshot(3, "Juvenile")]);
        await ExecuteNonQueryAsync("""
            DECLARE @bookId bigint = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
            INSERT INTO [asap].[PatronCustomField]
                ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
            VALUES (2, N'authority_retired', N'select', N'Historical preference', 0, 5);
            DECLARE @retiredId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[PatronCustomFieldOption]
                ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder])
            VALUES (@retiredId, N'old_zeta', N'Old Zeta', 0, 20),
                   (@retiredId, N'old_alpha', N'Old Alpha', 0, 10);
            INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode], [LabelOverride])
            VALUES (2, @bookId, @retiredId, N'optional', N'Historical label');
            """);

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory.CreateClient();
            var baseAddress = client.BaseAddress
                ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
            var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifactDirectory = Path.Combine(repositoryRoot, ".artifacts", "browser", $"settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifactDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = "node",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "settings.cjs"));
            startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
            startInfo.ArgumentList.Add(artifactDirectory);
            startInfo.ArgumentList.Add(actor.Id.ToString());
            startInfo.ArgumentList.Add(actor.EntraTenantId.ToString());
            startInfo.ArgumentList.Add(actor.AuthenticationEmail);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the settings browser runner.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.AreEqual(0, process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}");

            using (var report = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "settings-browser-results.json"))))
            {
                Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
                Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
                Assert.IsTrue(report.RootElement.GetProperty("postedEditorPayload").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("systemParticipationRoundTrip").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("unrelatedSystemSavePreservedParticipation").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("systemPatronCodesRoundTrip").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("systemUnrelatedSavePreservedPatronCodes").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("libraryPatronCodesRoundTrip").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("libraryUnrelatedSavePreservedPatronCodes").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("libraryEmptyPatronCodeReplacementRoundTrip").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("libraryPatronCodeResetRoundTrip").GetBoolean());
                Assert.IsTrue(report.RootElement.GetProperty("wildcardOriginNormalized").GetBoolean());
                Assert.HasCount(2, report.RootElement.GetProperty("states").EnumerateArray().ToArray());
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(connection, """
                    SELECT COUNT(*) FROM [asap].[Organization]
                    WHERE [OrganizationCodeId] = 2 AND [IsActive] = 1 AND [Id] NOT IN (2, 3);
                    """));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [OrganizationCodeId] = 2 AND [IsActive] = 1;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 3 AND [OrganizationCodeId] = 2 AND [IsActive] = 1;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 4 AND [OrganizationCodeId] = 2 AND [IsActive] = 0;"));
                var systemCodeRows = await ReadPatronCodeRowsAsync(1);
                Assert.AreEqual(1, systemCodeRows.SetCount);
                CollectionAssert.AreEqual(new[] { 1, 2, 3 }, systemCodeRows.Values);
                var libraryCodeRows = await ReadPatronCodeRowsAsync(2);
                Assert.AreEqual(0, libraryCodeRows.SetCount,
                    "The UI reset must delete the empty library replacement set and resume inheritance.");
                CollectionAssert.AreEqual(Array.Empty<int>(), libraryCodeRows.Values);
                Assert.AreEqual(1, await ScalarAsync(connection, """
                    SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin]
                    WHERE [OrganizationId] = 1 AND [Origin] = N'https://*.domain.example' AND
                          [NormalizedOrigin] = N'https://*.domain.example';
                    """));
            }

            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var saved = await ReadSettingsDocumentAsync(client, "2");
            var fields = saved.RootElement.GetProperty("stored").GetProperty("customFields").EnumerateArray().ToArray();
            var retiredField = fields.Single(item => item.GetProperty("key").GetString() == retiredKey);
            var dynamicField = fields.Single(item => item.GetProperty("key").GetString() == dynamicKey);
            Assert.IsTrue(retiredField.GetProperty("sortOrder").GetInt32() < dynamicField.GetProperty("sortOrder").GetInt32());
            var savedOptions = dynamicField.GetProperty("options").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "zeta", "alpha" }, savedOptions
                .Select(item => item.GetProperty("id").GetString()).ToArray());
            var rule = saved.RootElement.GetProperty("stored").GetProperty("formatRules").EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() == "book");
            var savedRules = rule.GetProperty("customFields");
            Assert.AreEqual("optional", savedRules.GetProperty(retiredKey).GetProperty("mode").GetString());
            Assert.AreEqual("Historical label", savedRules.GetProperty(retiredKey).GetProperty("labelOverride").GetString());
            Assert.AreEqual("required", savedRules.GetProperty(dynamicKey).GetProperty("mode").GetString());
            Assert.AreEqual("Browser choice", savedRules.GetProperty(dynamicKey).GetProperty("labelOverride").GetString());
            Assert.IsFalse(saved.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("allowedPatronCodeIds").GetProperty("exists").GetBoolean());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, saved.RootElement.GetProperty("effective")
                .GetProperty("allowedPatronCodeIds").EnumerateArray().Select(item => item.GetInt32()).ToArray());
            CollectionAssert.AreEqual(Array.Empty<int>(), saved.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("allowedPatronCodeIds").GetProperty("values").EnumerateArray()
                .Select(item => item.GetInt32()).ToArray());
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'browser_select' AND [FieldType] = N'select' AND [SortOrder] > 5;
                """));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE ruleRow
                FROM [asap].[MaterialFormatCustomFieldRule] ruleRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = ruleRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] IN (N'authority_retired', N'browser_select');
                DELETE optionRow
                FROM [asap].[PatronCustomFieldOption] optionRow
                JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
                WHERE field.[LibraryOrganizationId] = 2 AND field.[FieldKey] IN (N'authority_retired', N'browser_select');
                DELETE FROM [asap].[PatronCustomField]
                WHERE [LibraryOrganizationId] = 2 AND [FieldKey] IN (N'authority_retired', N'browser_select');
                UPDATE [asap].[Organization] SET [IsActive] = @organizationActive WHERE [Id] = 2;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (3, 4);
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                """, ("@organizationActive", originalOrganizationActive), ("@auditBefore", auditBefore));
            await RestoreSystemOriginRowsAsync(originalOrigins);
            await RestorePatronCodeRowsAsync(1, originalSystemCodes);
            await RestorePatronCodeRowsAsync(2, originalCodes);
        }
    }

    [TestMethod]
    public async Task ParticipationRejectsInvalidAndAmbiguousIdsBeforeChangingAnyLibrary()
    {
        const int unclassifiedId = 88771;
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive])
            VALUES (88771, N'Unclassified active row', 1);
            INSERT INTO [asap].[PatronSession]
                ([TokenHash], [Barcode], [EffectiveOrganizationId], [CreatedUtc], [ExpiresUtc])
            VALUES (HASHBYTES('SHA2_256', CONVERT(varbinary(100), N'settings-authority-participation-session')),
                N'participation-test', 2, SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()));
            """);

        long auditBefore;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            auditBefore = Convert.ToInt64(await new SqlCommand(
                "SELECT ISNULL(MAX([Id]), 0) FROM [asap].[AdministrativeAudit];", connection).ExecuteScalarAsync());
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            using var settings = await ReadSettingsDocumentAsync(client, "system");
            var version = settings.RootElement.GetProperty("version").GetString()!;
            var outboxBeforeInvalid = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];");
            var requestEventsBeforeInvalid = await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];");
            var invalidLists = new[]
            {
                "null",
                "[2, null]",
                "[2, \"3\"]",
                "[2, 2]",
                "[2.5]",
                "[1]",
                "[101]",
                "[88772]",
                "[{}]"
            };
            foreach (var list in invalidLists)
            {
                using var response = await PostSettingsJsonAsync(client,
                    $"{{\"orgId\":\"system\",\"version\":\"{version}\",\"enabledLibraryOrgIds\":{list}}}");
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                    $"Participation input {list}: {await response.Content.ReadAsStringAsync()}");
            }

            using (var duplicateKey = await PostSettingsJsonAsync(client,
                       $"{{\"orgId\":\"system\",\"version\":\"{version}\",\"enabledLibraryOrgIds\":[2],\"enabledLibraryOrgIds\":[101]}}"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, duplicateKey.StatusCode,
                    await duplicateKey.Content.ReadAsStringAsync());
            }
            using (var conflictingAliases = await PostSettingsJsonAsync(client,
                       $"{{\"orgId\":\"system\",\"version\":\"{version}\",\"enabledLibraryOrgIds\":[2],\"enabledLibraries\":[101]}}"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, conflictingAliases.StatusCode,
                    await conflictingAliases.Content.ReadAsStringAsync());
            }

            using (var afterInvalid = await ReadSettingsDocumentAsync(client, "system"))
            {
                Assert.AreEqual(version, afterInvalid.RootElement.GetProperty("version").GetString());
            }
            Assert.AreEqual(outboxBeforeInvalid, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            Assert.AreEqual(requestEventsBeforeInvalid, await ReadCountAsync("SELECT COUNT(*) FROM [asap].[TitleRequestEvent];"));

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [IsActive] = 1 AND [OrganizationCodeId] = 2;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = @id AND [IsActive] = 1 AND [OrganizationCodeId] IS NULL;"
                        .Replace("@id", unclassifiedId.ToString(), StringComparison.Ordinal)));
                Assert.AreEqual(1, await ScalarAsync(connection, """
                    SELECT COUNT(*) FROM [asap].[PatronSession]
                    WHERE [Barcode] = N'participation-test' AND [RevokedUtc] IS NULL;
                    """));
                Assert.AreEqual(auditBefore, Convert.ToInt64(await new SqlCommand(
                    "SELECT ISNULL(MAX([Id]), 0) FROM [asap].[AdministrativeAudit];", connection).ExecuteScalarAsync()));
            }

            using (var acceptedStrings = await PostSettingsJsonAsync(client,
                       $"{{\"orgId\":\"system\",\"version\":\"{version}\",\"enabledLibraryOrgIds\":[\"2\"]}}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, acceptedStrings.StatusCode,
                    await acceptedStrings.Content.ReadAsStringAsync());
            }

            using var current = await ReadSettingsDocumentAsync(client, "system");
            var currentVersion = current.RootElement.GetProperty("version").GetString()!;
            using (var empty = await PostSettingsJsonAsync(client,
                       $"{{\"orgId\":\"system\",\"version\":\"{currentVersion}\",\"enabledLibraryOrgIds\":[]}}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode, await empty.Content.ReadAsStringAsync());
            }
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [IsActive] = 0;"));
                Assert.AreEqual(1, await ScalarAsync(connection, """
                    SELECT COUNT(*) FROM [asap].[PatronSession]
                    WHERE [Barcode] = N'participation-test' AND [RevokedUtc] IS NOT NULL;
                    """));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 88771 AND [IsActive] = 1 AND [OrganizationCodeId] IS NULL;"));
            }

            using var afterClear = await ReadSettingsDocumentAsync(client, "system");
            using var restored = await PostSettingsJsonAsync(client,
                $"{{\"orgId\":\"system\",\"version\":\"{afterClear.RootElement.GetProperty("version").GetString()}\",\"enabledLibraryOrgIds\":[2]}}");
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode, await restored.Content.ReadAsStringAsync());
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 2;
                DELETE FROM [asap].[PatronSession]
                WHERE [TokenHash] = HASHBYTES('SHA2_256', CONVERT(varbinary(100), N'settings-authority-participation-session'));
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                DELETE FROM [asap].[Organization] WHERE [Id] = 88771;
                """, ("@auditBefore", auditBefore));
        }
    }

    [TestMethod]
    public async Task PolarisRefreshEstablishesNativeLibraryAuthorityAndRejectsDuplicateSnapshotsAtomically()
    {
        const int libraryId = 88781;
        const int branchId = 88782;
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var objectId = Guid.NewGuid();
        var email = $"unclassified-{Guid.NewGuid():N}@example.org";
        var auditBefore = 0L;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            auditBefore = Convert.ToInt64(await new SqlCommand(
                "SELECT ISNULL(MAX([Id]), 0) FROM [asap].[AdministrativeAudit];", connection).ExecuteScalarAsync());
        }

        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive])
            VALUES (88781, N'Legacy active unknown', 1);
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (88782, N'Branch reference', 3, 88781, 1);
            INSERT INTO [asap].[PatronSettings] ([OrganizationId], [LoginNote], [UpdatedUtc])
            VALUES (88781, N'Preserve library setting', SYSUTCDATETIME()),
                   (88782, N'Preserve branch history', SYSUTCDATETIME());
            INSERT INTO [asap].[PatronSession]
                ([TokenHash], [Barcode], [EffectiveOrganizationId], [CreatedUtc], [ExpiresUtc])
            VALUES
                (HASHBYTES('SHA2_256', CONVERT(varbinary(100), N'unknown-library-session')), N'unknown-library', 88781, SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME())),
                (HASHBYTES('SHA2_256', CONVERT(varbinary(100), N'branch-session')), N'branch-session', 88782, SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()));
            """);
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES (@tenantId, @objectId, @email, UPPER(@email), N'Unclassified staff', N'staff', 88781, 1);
            """, ("@tenantId", tenantId), ("@objectId", objectId), ("@email", email));

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            var staffEligibility = factory!.Services.GetRequiredService<StaffEligibilityService>();
            var blockedStaff = await staffEligibility.FindByEmailAsync(email, tenantId, libraryId,
                StaffRoleRequirement.Any, requireParticipation: true, CancellationToken.None);
            Assert.AreEqual(StaffEligibilityOutcome.InvalidIdentity, blockedStaff.Outcome);
            Assert.AreEqual("organization_not_found", (await factory.Services.GetRequiredService<AdministrationService>()
                .GetSettingsAsync(actor, LibraryScope.ForLibrary(libraryId), CancellationToken.None)).Code);

            var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            provider.SetReferenceData(
                [
                    new PolarisOrganizationSnapshot(libraryId, "Trusted Library", "TRL", 2, 1),
                    new PolarisOrganizationSnapshot(branchId, "Branch reference", "BR", 3, libraryId)
                ],
                []);
            var administration = factory.Services.GetRequiredService<AdministrationService>();
            using (var validSync = await client.PostAsJsonAsync("/api/asap/staff/organizations/sync", new { }))
            {
                Assert.AreEqual(HttpStatusCode.OK, validSync.StatusCode, await validSync.Content.ReadAsStringAsync());
                using var validSyncBody = JsonDocument.Parse(await validSync.Content.ReadAsStringAsync());
                Assert.AreEqual("synced", validSyncBody.RootElement.GetProperty("code").GetString());
            }

            var admittedStaff = await staffEligibility.FindByEmailAsync(email, tenantId, libraryId,
                StaffRoleRequirement.Any, requireParticipation: true, CancellationToken.None);
            Assert.AreEqual(StaffEligibilityOutcome.Allowed, admittedStaff.Outcome);
            Assert.AreEqual("ok", (await administration.GetSettingsAsync(actor, LibraryScope.ForLibrary(libraryId), CancellationToken.None)).Code);
            Assert.AreEqual("organization_not_found", (await administration.GetSettingsAsync(actor,
                LibraryScope.ForLibrary(branchId), CancellationToken.None)).Code);

            byte[] branchVersion;
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 88781 AND [OrganizationCodeId] = 2 AND [ParentOrganizationId] = 1 AND [IsActive] = 1;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 88782 AND [OrganizationCodeId] = 3 AND [ParentOrganizationId] = 88781 AND [IsActive] = 0;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 88781 AND [LoginNote] = N'Preserve library setting';"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 88782 AND [LoginNote] = N'Preserve branch history';"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[PatronSession] WHERE [Barcode] = N'unknown-library' AND [RevokedUtc] IS NULL;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[PatronSession] WHERE [Barcode] = N'branch-session' AND [RevokedUtc] IS NOT NULL;"));
                await using var command = new SqlCommand(
                    "SELECT [RowVersion] FROM [asap].[Organization] WHERE [Id] = 88782;", connection);
                branchVersion = (byte[])(await command.ExecuteScalarAsync())!;
            }

            var branchActivation = await administration.SetOrganizationActiveAsync(actor, branchId, true, null,
                StaffVersion.Encode(branchVersion), CancellationToken.None);
            Assert.AreEqual("organization_not_library", branchActivation.Code);

            using var settingsBefore = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var versionBefore = settingsBefore.RootElement.GetProperty("version").GetString();
            long auditAfterValidSync;
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                auditAfterValidSync = Convert.ToInt64(await new SqlCommand(
                    "SELECT ISNULL(MAX([Id]), 0) FROM [asap].[AdministrativeAudit];", connection).ExecuteScalarAsync());
            }

            provider.SetReferenceData(
                [
                    new PolarisOrganizationSnapshot(libraryId, "Corrupt duplicate", "BAD", 3, branchId),
                    new PolarisOrganizationSnapshot(libraryId, "Trusted Library", "TRL", 2, 1)
                ],
                []);
            using (var invalidSync = await client.PostAsJsonAsync("/api/asap/staff/organizations/sync", new { }))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidSync.StatusCode, await invalidSync.Content.ReadAsStringAsync());
                using var invalidSyncBody = JsonDocument.Parse(await invalidSync.Content.ReadAsStringAsync());
                Assert.AreEqual("polaris_organizations_invalid", invalidSyncBody.RootElement.GetProperty("code").GetString());
            }
            using var settingsAfter = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.AreEqual(versionBefore, settingsAfter.RootElement.GetProperty("version").GetString());
            Assert.AreEqual("Preserve library setting", await ReadSettingTextAsync(libraryId, "LoginNote"));
            Assert.AreEqual(auditAfterValidSync, Convert.ToInt64(await ReadAuditHighWatermarkAsync()));
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[Organization]
                WHERE [Id] = 88781 AND [DisplayName] = N'Trusted Library' AND [OrganizationCodeId] = 2 AND [IsActive] = 1;
                """));
            Assert.AreEqual(1, await ReadCountAsync("""
                SELECT COUNT(*) FROM [asap].[PatronSession]
                WHERE [Barcode] = N'unknown-library' AND [RevokedUtc] IS NULL;
                """));
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;
                DELETE FROM [asap].[PatronSession] WHERE [Barcode] IN (N'unknown-library', N'branch-session');
                DELETE FROM [asap].[StaffUser] WHERE [UserPrincipalName] = @email;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] IN (88781, 88782);
                DELETE FROM [asap].[Organization] WHERE [Id] IN (88781, 88782);
                """, ("@auditBefore", auditBefore), ("@email", email));
        }
    }

    private static async Task<HttpResponseMessage> PostSettingsJsonAsync(HttpClient client, string json) =>
        await client.PostAsync("/api/asap/staff/settings",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<int> ReadCountAsync(string sql)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        return await ScalarAsync(connection, sql);
    }

    private static async Task<long> ReadAuditHighWatermarkAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT ISNULL(MAX([Id]), 0) FROM [asap].[AdministrativeAudit];", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadSettingTextAsync(int organizationId, string column)
    {
        if (column != "LoginNote")
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [LoginNote] FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;", connection);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        return await command.ExecuteScalarAsync() as string;
    }

    private sealed record SystemOriginRow(long Id, string Origin, string NormalizedOrigin, DateTime CreatedUtc);

    private static async Task<IReadOnlyList<SystemOriginRow>> ReadSystemOriginRowsAsync()
    {
        var rows = new List<SystemOriginRow>();
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT [Id], [Origin], [NormalizedOrigin], [CreatedUtc]
            FROM [asap].[PatronEmbedAllowedOrigin]
            WHERE [OrganizationId] = 1
            ORDER BY [Id];
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SystemOriginRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        }
        return rows;
    }

    private static async Task<string?> ReadSystemStaffUrlAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [StaffApplicationUrl] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;", connection);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task RestoreSystemOriginRowsAsync(IReadOnlyList<SystemOriginRow> rows)
    {
        await ExecuteNonQueryAsync("DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;");
        foreach (var row in rows)
        {
            await ExecuteNonQueryAsync("""
                SET IDENTITY_INSERT [asap].[PatronEmbedAllowedOrigin] ON;
                INSERT INTO [asap].[PatronEmbedAllowedOrigin]
                    ([Id], [OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc])
                VALUES (@id, 1, @origin, @normalizedOrigin, @createdUtc);
                SET IDENTITY_INSERT [asap].[PatronEmbedAllowedOrigin] OFF;
                """, ("@id", row.Id), ("@origin", row.Origin),
                ("@normalizedOrigin", row.NormalizedOrigin), ("@createdUtc", row.CreatedUtc));
        }
    }

    private static async Task RestorePatronCodeRowsAsync(int organizationId, PatronCodeRows rows)
    {
        await ExecuteNonQueryAsync("DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId; DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @organizationId;",
            ("@organizationId", organizationId));
        if (rows.SetCount == 0)
        {
            return;
        }

        await ExecuteNonQueryAsync("INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@organizationId);",
            ("@organizationId", organizationId));
        foreach (var codeId in rows.Values)
        {
            await ExecuteNonQueryAsync(
                "INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId]) VALUES (@organizationId, @codeId);",
                ("@organizationId", organizationId), ("@codeId", codeId));
        }
    }
}
