using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private static readonly string[] LegacyWorkflowFields =
    [
        "suggestionLimit", "suggestionLimitMessage", "outstandingTimeoutEnabled", "outstandingTimeoutDays",
        "outstandingTimeoutSendEmail", "outstandingTimeoutRejectionTemplateId", "holdPickupTimeoutEnabled",
        "holdPickupTimeoutDays", "pendingHoldTimeoutEnabled", "pendingHoldTimeoutDays",
        "additionalCopyTimeoutEnabled", "additionalCopyTimeoutDays", "autoPromote", "commonAuthorsEnabled",
        "commonAuthorsLabel", "commonAuthorsHelp", "commonAuthorsMessage", "allowPatronAutoholdOptOut",
        "allowAnyRegisteredCardLogin", "patronCodeEligibilityEnabled", "patronCodeEligibilityMessage",
        "commonAuthorsList", "allowedPatronCodeIds"
    ];
    private static readonly string[] LegacyPatronFields =
    [
        "pageTitle", "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote",
        "noEmailMessage", "successTitle", "successMessage", "alreadySubmittedMessage", "ebookMessage",
        "eaudiobookMessage", "systemNotEnabledMessage", "misconfiguredMessage", "duplicateStatusLabels",
        "publicationOptions", "logoAlt"
    ];
    private static readonly string[] LegacyFormatFields =
    [
        "code", "label", "sortOrder", "isEnabled", "messageBehavior", "message", "titleMode",
        "titleLabel", "authorMode", "authorLabel", "identifierMode", "identifierLabel",
        "publicationMode", "publicationLabel"
    ];

    [TestMethod]
    public async Task LegacySettingsNoEditRoundTripsPreserveInheritedSparseBlankAndSecretRows()
    {
        const int inheritedId = 92340;
        const int sparseId = 92341;
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var protector = factory.Services.GetRequiredService<IntegrationCredentialProtector>();
        string? originalApiKey;
        string? originalPassword;
        int? originalPolarisUserId;
        byte[] sparseWorkflowVersion;
        byte[] sparsePatronVersion;
        byte[] sparseEmailVersion;
        string sparseSecret;

        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            await using var transaction = await seed.Database.BeginTransactionAsync();
            var polaris = await seed.PolarisSettings.SingleAsync(item => item.OrganizationId == 1);
            originalApiKey = polaris.ProtectedApiKey;
            originalPassword = polaris.ProtectedAdminPassword;
            originalPolarisUserId = polaris.SystemPolarisUserId;
            polaris.SystemPolarisUserId = 4701;
            polaris.ProtectedApiKey = protector.Protect("legacy-settings-test-api-key");
            polaris.ProtectedAdminPassword = protector.Protect("legacy-settings-test-password");
            seed.Organizations.AddRange(
                new Organization { Id = inheritedId, DisplayName = "Legacy inherited settings", IsActive = true },
                new Organization { Id = sparseId, DisplayName = "Legacy sparse settings", IsActive = true });
            await seed.SaveChangesAsync();
            var sparseWorkflow = new WorkflowSettings { OrganizationId = sparseId, SuggestionLimit = 7 };
            var sparsePatron = new PatronSettings { OrganizationId = sparseId, EbookMessage = "" };
            var sparseEmail = new EmailSettings
            {
                OrganizationId = sparseId,
                FromName = "",
                ProtectedServerToken = protector.Protect("legacy-settings-test-token")
            };
            seed.WorkflowSettings.Add(sparseWorkflow);
            seed.PatronSettings.Add(sparsePatron);
            seed.EmailSettings.Add(sparseEmail);
            await seed.SaveChangesAsync();
            sparseWorkflowVersion = sparseWorkflow.RowVersion.ToArray();
            sparsePatronVersion = sparsePatron.RowVersion.ToArray();
            sparseEmailVersion = sparseEmail.RowVersion.ToArray();
            sparseSecret = sparseEmail.ProtectedServerToken!;
            await transaction.CommitAsync();
        }

        try
        {
            foreach (var scope in new[] { "system", inheritedId.ToString(), sparseId.ToString() })
            {
                using var before = await ReadLegacySettingsAsync(client, scope);
                var payload = LegacyNoEditPayload(before.RootElement, scope == "system");
                using var saved = await SaveLegacySettingsAsync(client, payload, HttpStatusCode.OK);
                Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
                using var after = await ReadLegacySettingsAsync(client, scope);
                Assert.AreEqual(before.RootElement.GetProperty("version").GetString(),
                    after.RootElement.GetProperty("version").GetString(), $"No-edit {scope} save changed the settings version.");
            }

            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual(0, await verify.WorkflowSettings.CountAsync(item => item.OrganizationId == inheritedId));
            Assert.AreEqual(0, await verify.PatronSettings.CountAsync(item => item.OrganizationId == inheritedId));
            Assert.AreEqual(0, await verify.EmailSettings.CountAsync(item => item.OrganizationId == inheritedId));
            var workflow = await verify.WorkflowSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == sparseId);
            var patron = await verify.PatronSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == sparseId);
            var email = await verify.EmailSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == sparseId);
            Assert.AreEqual(7, workflow.SuggestionLimit);
            Assert.IsNull(workflow.CommonAuthorsHelp);
            Assert.AreEqual("", patron.EbookMessage);
            Assert.IsNull(patron.EaudiobookMessage);
            Assert.AreEqual("", email.FromName);
            Assert.IsNull(email.FromAddress);
            Assert.AreEqual(sparseSecret, email.ProtectedServerToken);
            CollectionAssert.AreEqual(sparseWorkflowVersion, workflow.RowVersion);
            CollectionAssert.AreEqual(sparsePatronVersion, patron.RowVersion);
            CollectionAssert.AreEqual(sparseEmailVersion, email.RowVersion);
            var polaris = await verify.PolarisSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == 1);
            Assert.AreEqual(4701, polaris.SystemPolarisUserId);
            Assert.AreEqual("legacy-settings-test-api-key", protector.Unprotect(polaris.ProtectedApiKey!));
            Assert.AreEqual("legacy-settings-test-password", protector.Unprotect(polaris.ProtectedAdminPassword!));
        }
        finally
        {
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            var polaris = await cleanup.PolarisSettings.SingleAsync(item => item.OrganizationId == 1);
            polaris.ProtectedApiKey = originalApiKey;
            polaris.ProtectedAdminPassword = originalPassword;
            polaris.SystemPolarisUserId = originalPolarisUserId;
            cleanup.WorkflowSettings.RemoveRange(await cleanup.WorkflowSettings.Where(item => item.OrganizationId == sparseId).ToListAsync());
            cleanup.PatronSettings.RemoveRange(await cleanup.PatronSettings.Where(item => item.OrganizationId == sparseId).ToListAsync());
            cleanup.EmailSettings.RemoveRange(await cleanup.EmailSettings.Where(item => item.OrganizationId == sparseId).ToListAsync());
            cleanup.AdministrativeAudits.RemoveRange(await cleanup.AdministrativeAudits.Where(item =>
                item.OrganizationId == inheritedId || item.OrganizationId == sparseId).ToListAsync());
            await cleanup.SaveChangesAsync();
            cleanup.Organizations.RemoveRange(await cleanup.Organizations.Where(item => item.Id == inheritedId || item.Id == sparseId).ToListAsync());
            await cleanup.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task LegacySettingsExplicitEmptyLibrarySetsRemainEmptyAndVersionedOnNoEdit()
    {
        const int libraryId = 92344;
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        byte[] creatorVersion;
        byte[] codeVersion;
        byte[] publicationVersion;

        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            seed.Organizations.Add(new Organization
            {
                Id = libraryId, DisplayName = "Legacy empty-set settings", IsActive = true
            });
            await seed.SaveChangesAsync();
            seed.CommonCreatorSets.Add(new CommonCreatorSet { OrganizationId = libraryId });
            seed.PatronCodeEligibilitySets.Add(new PatronCodeEligibilitySet { OrganizationId = libraryId });
            seed.PublicationOptionSets.Add(new PublicationOptionSet { OrganizationId = libraryId });
            await seed.SaveChangesAsync();
            creatorVersion = (await seed.CommonCreatorSets.SingleAsync(item => item.OrganizationId == libraryId)).RowVersion.ToArray();
            codeVersion = (await seed.PatronCodeEligibilitySets.SingleAsync(item => item.OrganizationId == libraryId)).RowVersion.ToArray();
            publicationVersion = (await seed.PublicationOptionSets.SingleAsync(item => item.OrganizationId == libraryId)).RowVersion.ToArray();
        }

        try
        {
            using var before = await ReadLegacySettingsAsync(client, libraryId.ToString());
            var form = before.RootElement;
            Assert.AreEqual("", form.GetProperty("workflow").GetProperty("commonAuthorsList").GetString());
            Assert.AreEqual("", form.GetProperty("workflow").GetProperty("allowedPatronCodeIds").GetString());
            Assert.AreEqual(0, form.GetProperty("uiText").GetProperty("publicationOptions").GetArrayLength());
            var payload = LegacyNoEditPayload(form, false);
            using var saved = await SaveLegacySettingsAsync(client, payload, HttpStatusCode.OK);
            Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());
            using var after = await ReadLegacySettingsAsync(client, libraryId.ToString());
            Assert.AreEqual(form.GetProperty("version").GetString(),
                after.RootElement.GetProperty("version").GetString());

            await using var verify = await contextFactory.CreateDbContextAsync();
            CollectionAssert.AreEqual(creatorVersion,
                (await verify.CommonCreatorSets.AsNoTracking().SingleAsync(item => item.OrganizationId == libraryId)).RowVersion);
            CollectionAssert.AreEqual(codeVersion,
                (await verify.PatronCodeEligibilitySets.AsNoTracking().SingleAsync(item => item.OrganizationId == libraryId)).RowVersion);
            CollectionAssert.AreEqual(publicationVersion,
                (await verify.PublicationOptionSets.AsNoTracking().SingleAsync(item => item.OrganizationId == libraryId)).RowVersion);
            Assert.AreEqual(0, await verify.CommonCreatorTerms.CountAsync(item => item.OrganizationId == libraryId));
            Assert.AreEqual(0, await verify.PatronCodeEligibilityMembers.CountAsync(item => item.OrganizationId == libraryId));
            Assert.AreEqual(0, await verify.PublicationOptions.CountAsync(item => item.OrganizationId == libraryId));
        }
        finally
        {
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            cleanup.CommonCreatorSets.RemoveRange(await cleanup.CommonCreatorSets.Where(item => item.OrganizationId == libraryId).ToListAsync());
            cleanup.PatronCodeEligibilitySets.RemoveRange(await cleanup.PatronCodeEligibilitySets.Where(item => item.OrganizationId == libraryId).ToListAsync());
            cleanup.PublicationOptionSets.RemoveRange(await cleanup.PublicationOptionSets.Where(item => item.OrganizationId == libraryId).ToListAsync());
            cleanup.AdministrativeAudits.RemoveRange(await cleanup.AdministrativeAudits.Where(item => item.OrganizationId == libraryId).ToListAsync());
            await cleanup.SaveChangesAsync();
            cleanup.Organizations.RemoveRange(await cleanup.Organizations.Where(item => item.Id == libraryId).ToListAsync());
            await cleanup.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task LegacySettingsPartialEditsPreserveSiblingProviderDisabledRuleAndTemplateInheritance()
    {
        const int libraryId = 92342;
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var suffix = Guid.NewGuid().ToString("N");
        var activeKey = $"legacy_active_{suffix}";
        var disabledKey = $"legacy_disabled_{suffix}";
        var templateKey = $"rejection:legacy_{suffix}";
        long disabledRuleId = 0;
        byte[] disabledVersion = [];
        byte[] siblingVersion = [];
        long firstProviderId = 0;
        long siblingProviderId = 0;

        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                seed.Organizations.Add(new Organization { Id = libraryId, DisplayName = "Legacy edit settings", IsActive = true });
                await seed.SaveChangesAsync();
                var activeField = new PatronCustomField
                {
                    LibraryOrganizationId = libraryId, FieldKey = activeKey,
                    FieldType = "text", Label = "Editable", IsEnabled = true
                };
                var disabledField = new PatronCustomField
                {
                    LibraryOrganizationId = libraryId, FieldKey = disabledKey,
                    FieldType = "text", Label = "Hidden", IsEnabled = false
                };
                seed.PatronCustomFields.AddRange(activeField, disabledField);
                var source = new EmailTemplate
                {
                    OrganizationId = 1, TemplateKey = templateKey, DisplayName = "System name",
                    SubjectTemplate = "System subject", BodyTemplate = "System body"
                };
                seed.EmailTemplates.Add(source);
                await seed.SaveChangesAsync();
                var book = await seed.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
                var firstProvider = await seed.ExternalSearchProviders.SingleAsync(item => item.ProviderKey == "external_search_1");
                var siblingProvider = await seed.ExternalSearchProviders.SingleAsync(item => item.ProviderKey == "external_search_2");
                firstProviderId = firstProvider.Id;
                siblingProviderId = siblingProvider.Id;
                var seededDisabledRule = new MaterialFormatCustomFieldRule
                {
                    LibraryOrganizationId = libraryId, MaterialFormatId = book.Id,
                    PatronCustomFieldId = disabledField.Id, Mode = "hidden", LabelOverride = "Preserve hidden"
                };
                seed.MaterialFormatCustomFieldRules.AddRange(
                    new MaterialFormatCustomFieldRule
                    {
                        LibraryOrganizationId = libraryId, MaterialFormatId = book.Id,
                        PatronCustomFieldId = activeField.Id, Mode = "required", LabelOverride = "Editable label"
                    }, seededDisabledRule);
                seed.ExternalSearchProviderOverrides.AddRange(
                    new ExternalSearchProviderOverride
                    {
                        LibraryOrganizationId = libraryId,
                        ExternalSearchProviderId = firstProvider.Id,
                        Label = "First catalog"
                    },
                    new ExternalSearchProviderOverride
                    {
                        LibraryOrganizationId = libraryId,
                        ExternalSearchProviderId = siblingProvider.Id,
                        Label = "Sibling catalog"
                    });
                seed.EmailTemplates.Add(new EmailTemplate
                {
                    OrganizationId = libraryId, SourceTemplateId = source.Id, TemplateKey = templateKey,
                    DisplayName = "Original local name", SubjectTemplate = null, BodyTemplate = null
                });
                await seed.SaveChangesAsync();
                disabledRuleId = seededDisabledRule.Id;
                disabledVersion = seededDisabledRule.RowVersion.ToArray();
                siblingVersion = (await seed.ExternalSearchProviderOverrides.SingleAsync(item =>
                    item.LibraryOrganizationId == libraryId && item.ExternalSearchProviderId == siblingProvider.Id)).RowVersion.ToArray();
            }

            using var before = await ReadLegacySettingsAsync(client, libraryId.ToString());
            var payload = LegacyNoEditPayload(before.RootElement, system: false);
            ((JsonObject)payload["workflow"]!)["suggestionLimit"] = 14;
            var providers = (JsonArray)payload["providers"]!;
            var first = providers.OfType<JsonObject>().Single(item => (string?)item["key"] == "external_search_1");
            first["label"] = "Edited first catalog";
            var rules = (JsonObject)payload["formatRules"]!;
            var bookRules = (JsonObject)rules["book"]!;
            var customRules = (JsonObject)bookRules["customFields"]!;
            ((JsonObject)customRules[activeKey]!)["mode"] = "optional";
            var rejectionTemplates = (JsonArray)((JsonObject)payload["emails"]!)["rejection_templates"]!;
            var template = rejectionTemplates.OfType<JsonObject>().Single(item => (string?)item["templateKey"] == templateKey);
            template["name"] = "Edited local name";
            using var saved = await SaveLegacySettingsAsync(client, payload, HttpStatusCode.OK);
            Assert.AreEqual("saved", saved.RootElement.GetProperty("code").GetString());

            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.AreEqual(14, (await verify.WorkflowSettings.SingleAsync(item => item.OrganizationId == libraryId)).SuggestionLimit);
            var fields = await verify.PatronCustomFields.AsNoTracking().Where(item => item.LibraryOrganizationId == libraryId).ToListAsync();
            var activeFieldId = fields.Single(item => item.FieldKey == activeKey).Id;
            var disabledFieldId = fields.Single(item => item.FieldKey == disabledKey).Id;
            var activeRule = await verify.MaterialFormatCustomFieldRules.AsNoTracking().SingleAsync(item => item.PatronCustomFieldId == activeFieldId);
            var disabledRule = await verify.MaterialFormatCustomFieldRules.AsNoTracking().SingleAsync(item => item.PatronCustomFieldId == disabledFieldId);
            Assert.AreEqual("optional", activeRule.Mode);
            Assert.AreEqual(disabledRuleId, disabledRule.Id);
            Assert.AreEqual("hidden", disabledRule.Mode);
            Assert.AreEqual("Preserve hidden", disabledRule.LabelOverride);
            CollectionAssert.AreEqual(disabledVersion, disabledRule.RowVersion);
            var firstOverride = await verify.ExternalSearchProviderOverrides.AsNoTracking().SingleAsync(item =>
                item.LibraryOrganizationId == libraryId && item.ExternalSearchProviderId == firstProviderId);
            Assert.AreEqual("Edited first catalog", firstOverride.Label);
            var sibling = await verify.ExternalSearchProviderOverrides.AsNoTracking().SingleAsync(item =>
                item.LibraryOrganizationId == libraryId && item.ExternalSearchProviderId == siblingProviderId);
            Assert.AreEqual("Sibling catalog", sibling.Label);
            CollectionAssert.AreEqual(siblingVersion, sibling.RowVersion);
            var scopedTemplate = await verify.EmailTemplates.AsNoTracking().SingleAsync(item =>
                item.OrganizationId == libraryId && item.TemplateKey == templateKey);
            Assert.AreEqual("Edited local name", scopedTemplate.DisplayName);
            Assert.IsNull(scopedTemplate.SubjectTemplate);
            Assert.IsNull(scopedTemplate.BodyTemplate);
            using var after = await ReadLegacySettingsAsync(client, libraryId.ToString());
            var displayed = after.RootElement.GetProperty("templates").EnumerateArray().Single(item =>
                item.GetProperty("templateKey").GetString() == templateKey);
            Assert.AreEqual("System subject", displayed.GetProperty("subject").GetString());
            Assert.AreEqual("System body", displayed.GetProperty("body").GetString());
        }
        finally
        {
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            await cleanup.MaterialFormatCustomFieldRules.Where(item => item.LibraryOrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.PatronCustomFields.Where(item => item.LibraryOrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.ExternalSearchProviderOverrides.Where(item => item.LibraryOrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.WorkflowSettings.Where(item => item.OrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.EmailTemplates.Where(item => item.OrganizationId == libraryId && item.TemplateKey == templateKey).ExecuteDeleteAsync();
            await cleanup.EmailTemplates.Where(item => item.OrganizationId == 1 && item.TemplateKey == templateKey).ExecuteDeleteAsync();
            await cleanup.AdministrativeAudits.Where(item => item.OrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == libraryId).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    public async Task LegacySettingsRejectStaleMalformedAndOutOfScopeSaves()
    {
        const int libraryId = 92343;
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long? scopedStaffId = null;
        await using (var seed = await contextFactory.CreateDbContextAsync())
        {
            seed.Organizations.Add(new Organization { Id = libraryId, DisplayName = "Legacy stale settings", IsActive = true });
            await seed.SaveChangesAsync();
        }
        try
        {
            using var current = await ReadLegacySettingsAsync(client, libraryId.ToString());
            var valid = LegacyNoEditPayload(current.RootElement, system: false);

            var unsupported = (JsonObject)valid.DeepClone();
            unsupported["unexpectedSection"] = new JsonObject();
            using (var rejected = await SaveLegacySettingsAsync(client, unsupported, HttpStatusCode.BadRequest))
            {
                Assert.AreEqual("invalid_input", rejected.RootElement.GetProperty("code").GetString());
            }
            var malformed = (JsonObject)valid.DeepClone();
            malformed["workflow"] = new JsonArray();
            using (var rejected = await SaveLegacySettingsAsync(client, malformed, HttpStatusCode.BadRequest))
            {
                Assert.AreEqual("invalid_input", rejected.RootElement.GetProperty("code").GetString());
            }
            var systemOnly = (JsonObject)valid.DeepClone();
            systemOnly["polaris"] = new JsonObject { ["host"] = "forbidden.example.org" };
            using (var rejected = await SaveLegacySettingsAsync(client, systemOnly, HttpStatusCode.BadRequest))
            {
                Assert.AreEqual("invalid_input", rejected.RootElement.GetProperty("code").GetString());
            }

            await using (var change = await contextFactory.CreateDbContextAsync())
            {
                change.WorkflowSettings.Add(new WorkflowSettings
                {
                    OrganizationId = libraryId,
                    SuggestionLimitMessage = $"stale-probe-{Guid.NewGuid():N}"
                });
                await change.SaveChangesAsync();
            }
            using (var rejected = await SaveLegacySettingsAsync(client, valid, HttpStatusCode.Conflict))
            {
                Assert.AreEqual("stale_version", rejected.RootElement.GetProperty("code").GetString());
            }

            using var forbidden = factory.CreateClient();
            var staff = await factory.Services.GetRequiredService<Asap.Web.Features.Staff.StaffLifecycleService>().CreateAsync(
                actor,
                new Asap.Web.Features.Staff.StaffCreateInput($"legacy.scope.{Guid.NewGuid():N}@example.org", "staff", 2),
                CancellationToken.None);
            Assert.AreEqual("created", staff.Code);
            scopedStaffId = staff.User!.Id;
            AddTestingStaffHeaders(forbidden, staff.User!.Id, actor.EntraTenantId, staff.User.NormalizedUserPrincipalName!);
            using var denied = await forbidden.GetAsync("/api/asap/staff/legacy/settings?orgId=system");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode, await denied.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            await cleanup.WorkflowSettings.Where(item => item.OrganizationId == libraryId).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == libraryId).ExecuteDeleteAsync();
            if (scopedStaffId.HasValue)
            {
                await cleanup.StaffUsers.Where(item => item.Id == scopedStaffId.Value).ExecuteDeleteAsync();
            }
        }
    }

    private static async Task<JsonDocument> ReadLegacySettingsAsync(HttpClient client, string scope)
    {
        using var response = await client.GetAsync($"/api/asap/staff/legacy/settings?orgId={Uri.EscapeDataString(scope)}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> SaveLegacySettingsAsync(HttpClient client, JsonObject payload, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync("/api/asap/staff/legacy/settings", payload);
        Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonObject LegacyNoEditPayload(JsonElement displayed, bool system)
    {
        var root = JsonNode.Parse(displayed.GetRawText())!.AsObject();
        var payload = new JsonObject
        {
            ["orgId"] = root["orgId"]?.DeepClone(),
            ["version"] = root["version"]?.DeepClone(),
            ["workflow"] = LegacyPick(root["workflow"], LegacyWorkflowFields),
            ["ui_text"] = LegacyPick(root["uiText"], LegacyPatronFields)
        };
        if (!system)
        {
            ((JsonObject)payload["ui_text"]!).Remove("systemNotEnabledMessage");
            ((JsonObject)payload["ui_text"]!).Remove("misconfiguredMessage");
        }

        var emails = LegacyPick(root["emails"], ["fromAddress", "fromName"]);
        foreach (var key in new[] { "suggestion_submitted", "purchase_approved", "already_owned", "rejected", "hold_placed" })
        {
            if (root["emails"]?[key] is JsonObject value)
            {
                emails[key] = LegacyPick(value, ["subject", "body"]);
            }
        }
        var rejection = new JsonArray();
        foreach (var row in root["templates"]!.AsArray().OfType<JsonObject>())
        {
            if (((string?)row["templateKey"])?.StartsWith("rejection:", StringComparison.Ordinal) == true)
            {
                rejection.Add(LegacyPick(row, ["templateKey", "name", "subject", "body", "enabled"]));
            }
        }
        emails["rejection_templates"] = rejection;
        payload["emails"] = emails;

        var providers = new JsonArray();
        foreach (var row in root["providers"]!.AsArray())
        {
            providers.Add(LegacyPick(row, ["key", "isEnabled", "label", "urlTemplate"]));
        }
        payload["providers"] = providers;
        var formats = new JsonArray();
        foreach (var row in root["formats"]!.AsArray())
        {
            formats.Add(LegacyPick(row, LegacyFormatFields));
        }
        payload["formats"] = formats;
        payload["formatRules"] = root["uiText"]?["formatRules"]?.DeepClone();
        if (!system)
        {
            var claims = new JsonArray();
            foreach (var row in root["autoClaimRules"]!.AsArray().OfType<JsonObject>())
            {
                claims.Add(LegacyPick(row, ["format", "staffUserId"]));
            }
            payload["formatClaimRules"] = claims;
        }
        else
        {
            payload["polaris"] = LegacyPick(root["polaris"],
                ["host", "accessId", "staffDomain", "adminUser", "workstationId",
                 "systemPolarisUserId", "organizationIdForRequests", "pickupOrganizationId"]);
            var systemSettings = root["systemSettings"];
            foreach (var key in new[] { "staffUrl", "leapBibUrlPattern", "leapPatronUrlPattern",
                         "formatIconUrlPattern", "patronEmbedAllowedOrigins", "enabledLibraryOrgIds" })
            {
                if (systemSettings is JsonObject settings && settings.ContainsKey(key))
                {
                    payload[key] = key == "patronEmbedAllowedOrigins"
                        ? string.Join('\n', settings[key]!.AsArray().Select(item => (string?)item))
                        : settings[key]?.DeepClone();
                }
            }
        }
        return payload;
    }

    private static JsonObject LegacyPick(JsonNode? source, IEnumerable<string> keys)
    {
        var result = new JsonObject();
        if (source is not JsonObject values)
        {
            return result;
        }
        foreach (var key in keys)
        {
            if (values.ContainsKey(key))
            {
                result[key] = values[key]?.DeepClone();
            }
        }
        return result;
    }
}
