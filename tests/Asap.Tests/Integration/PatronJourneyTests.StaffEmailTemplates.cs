using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffEmailTemplatesUseLibraryOverridesAndRejectInvalidOrUnauthorizedWrites()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var libraryId = await ReserveSettingsCourseLibraryIdAsync();
        var libraryScope = libraryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N");
        var customTemplateKey = $"rejection:staff_template_{suffix}";
        await UpsertTestOrganizationAsync(libraryId, "Staff template scope witness", "STW");
        await using (var setup = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync())
        {
            setup.EmailTemplates.Add(new EmailTemplate
            {
                OrganizationId = libraryId,
                TemplateKey = customTemplateKey,
                DisplayName = "Library owned rejection",
                SubjectTemplate = "Owned subject",
                BodyTemplate = "Owned body",
                IsCustom = true,
                IsHidden = false
            });
            await setup.SaveChangesAsync();
        }

        long systemTemplateId = 0;
        string? originalSystemBody = null;
        var auditBefore = await ReadAuditHighWatermarkAsync();
        StaffUser? deniedActor = null;
        try
        {
            using var initialSystem = await ReadSettingsDocumentAsync(client, "system");
            var systemTemplate = initialSystem.RootElement.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("templates").EnumerateArray().Single(item =>
                    item.GetProperty("templateKey").GetString() == StaffEmailTemplateCatalog.TitleAssignment);
            var templateId = systemTemplate.GetProperty("id").GetString()!;
            systemTemplateId = long.Parse(templateId, System.Globalization.CultureInfo.InvariantCulture);
            originalSystemBody = systemTemplate.GetProperty("body").GetString();
            var staffDefinition = initialSystem.RootElement.GetProperty("staffTemplateDefinitions").EnumerateArray()
                .Single(item => item.GetProperty("templateKey").GetString() == StaffEmailTemplateCatalog.TitleAssignment);
            var staffKeys = staffDefinition.GetProperty("placeholders").EnumerateArray()
                .Select(item => item.GetProperty("key").GetString()).ToArray();
            CollectionAssert.Contains(staffKeys, "recipientName");
            CollectionAssert.Contains(staffKeys, "requestUrl");
            CollectionAssert.DoesNotContain(staffKeys, "name");
            CollectionAssert.DoesNotContain(staffKeys, "taskId");
            CollectionAssert.Contains(initialSystem.RootElement.GetProperty("templatePlaceholders").EnumerateArray()
                .Select(item => item.GetString()).ToArray(), "name");

            using var initialLibrary = await ReadSettingsDocumentAsync(client, libraryScope);
            var initialLibraryOverrides = initialLibrary.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().ToArray();
            Assert.IsFalse(initialLibraryOverrides.Any(item => item.TryGetProperty("sourceTemplateId", out var id) &&
                id.GetString() == templateId));

            using (var pinned = await SaveSettingsDocumentAsync(client, initialLibrary.RootElement, libraryScope,
                       new Dictionary<string, object?>
                       {
                           ["templates"] = new object[]
                           {
                               new
                               {
                                   templateKey = StaffEmailTemplateCatalog.TitleAssignment,
                                   sourceTemplateId = templateId,
                                   subject = "Library notice for {{recipientName}}",
                                   body = (string?)null
                               }
                           }
                       }))
            {
                Assert.AreEqual("saved", pinned.RootElement.GetProperty("code").GetString());
            }

            using var pinnedLibrary = await ReadSettingsDocumentAsync(client, libraryScope);
            var pinnedOverride = pinnedLibrary.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().Single(item =>
                    item.GetProperty("sourceTemplateId").GetString() == templateId);
            Assert.AreEqual("Library notice for {{recipientName}}", pinnedOverride.GetProperty("subject").GetString());
            Assert.AreEqual(JsonValueKind.Null, pinnedOverride.GetProperty("body").ValueKind,
                "A null body inherits the system default while the subject remains pinned.");

            var libraryVersion = pinnedLibrary.RootElement.GetProperty("version").GetString()!;
            using (var stale = await client.PostAsJsonAsync("/api/asap/staff/settings", new
                   {
                       orgId = libraryScope,
                       version = initialLibrary.RootElement.GetProperty("version").GetString(),
                       templates = new[]
                       {
                           new { templateKey = StaffEmailTemplateCatalog.TitleAssignment, sourceTemplateId = templateId,
                               subject = "Stale library draft" }
                       }
                   }))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
                using var result = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
                Assert.AreEqual("stale_version", result.RootElement.GetProperty("code").GetString());
            }

            async Task AssertInvalidStaffTemplateAsync(object template, string expectedMessage)
            {
                using var response = await client.PostAsJsonAsync("/api/asap/staff/settings", new
                {
                    orgId = libraryScope,
                    version = libraryVersion,
                    templates = new[] { template }
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("settings_invalid", result.RootElement.GetProperty("code").GetString());
                StringAssert.Contains(result.RootElement.GetProperty("message").GetString()!, expectedMessage);
            }

            await AssertInvalidStaffTemplateAsync(new
            {
                templateKey = StaffEmailTemplateCatalog.TitleAssignment,
                sourceTemplateId = templateId,
                body = ""
            }, "subject and body are required");
            await AssertInvalidStaffTemplateAsync(new
            {
                templateKey = StaffEmailTemplateCatalog.TitleAssignment,
                sourceTemplateId = templateId,
                subject = "Hello {{name}}"
            }, "unsupported placeholder {{name}}");
            await AssertInvalidStaffTemplateAsync(new
            {
                templateKey = StaffEmailTemplateCatalog.TitleAssignment,
                libraryCustom = true,
                subject = "Custom staff subject",
                body = "Custom staff body"
            }, "Built-in staff templates cannot be saved as library-owned custom templates");
            using var unchangedLibrary = await ReadSettingsDocumentAsync(client, libraryScope);
            Assert.AreEqual(libraryVersion, unchangedLibrary.RootElement.GetProperty("version").GetString(),
                "Rejected blank and unsupported content must not mutate the accepted override.");

            var newSystemBody = originalSystemBody + "\nSystem version update {{requestUrl}}";
            using (var systemEdit = await SaveSettingsDocumentAsync(client, initialSystem.RootElement, "system",
                       new Dictionary<string, object?>
                       {
                           ["templates"] = new object[]
                           {
                               new { templateKey = StaffEmailTemplateCatalog.TitleAssignment, body = newSystemBody }
                           }
                       }))
            {
                Assert.AreEqual("saved", systemEdit.RootElement.GetProperty("code").GetString());
            }

            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using (var verify = await contexts.CreateDbContextAsync())
            {
                var effective = await StaffEmailTemplateCatalog.LoadEffectiveAsync(verify, libraryId,
                    StaffEmailTemplateCatalog.TitleAssignment, CancellationToken.None);
                Assert.IsNotNull(effective);
                Assert.AreEqual("Library notice for {{recipientName}}", effective!.Subject);
                Assert.AreEqual(newSystemBody, effective.Body,
                    "Null is inheritance; an override follows a later system-body edit.");
                var rendered = StaffEmailTemplateRenderer.RenderRequest(effective,
                    new StaffEmailRequestValues(libraryId, "https://staff.example.org/staff/", "Casey", "Morgan", "Casey",
                        "Template scope witness", "9007199254740993", null, "suggestion", null, "Example title", null, "Print"));
                Assert.AreEqual("Library notice for Casey", rendered.Subject);
                StringAssert.Contains(rendered.BodyText!, "System version update https://staff.example.org/staff/?request=9007199254740993&stage=suggestion&scope=" + libraryScope);
            }

            using var currentLibrary = await ReadSettingsDocumentAsync(client, libraryScope);
            using (var resetSubject = await SaveSettingsDocumentAsync(client, currentLibrary.RootElement, libraryScope,
                       new Dictionary<string, object?>
                       {
                           ["templates"] = new object[]
                           {
                               new { templateKey = StaffEmailTemplateCatalog.TitleAssignment,
                                   sourceTemplateId = templateId, subject = (string?)null }
                           }
                       }))
            {
                Assert.AreEqual("saved", resetSubject.RootElement.GetProperty("code").GetString());
            }
            using var resetSubjectLibrary = await ReadSettingsDocumentAsync(client, libraryScope);
            var subjectResetOverride = resetSubjectLibrary.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().Single(item => item.GetProperty("sourceTemplateId").GetString() == templateId);
            Assert.AreEqual(JsonValueKind.Null, subjectResetOverride.GetProperty("subject").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, subjectResetOverride.GetProperty("body").ValueKind);

            using (var resetTemplate = await SaveSettingsDocumentAsync(client, resetSubjectLibrary.RootElement, libraryScope,
                       new Dictionary<string, object?>
                       {
                           ["templates"] = new object[]
                           {
                               new { templateKey = StaffEmailTemplateCatalog.TitleAssignment,
                                   sourceTemplateId = templateId, reset = true }
                           }
                       }))
            {
                Assert.AreEqual("saved", resetTemplate.RootElement.GetProperty("code").GetString());
            }
            using var afterReset = await ReadSettingsDocumentAsync(client, libraryScope);
            var retainedLibraryRows = afterReset.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("templates").EnumerateArray().ToArray();
            Assert.IsFalse(retainedLibraryRows.Any(item => item.TryGetProperty("sourceTemplateId", out var id) && id.GetString() == templateId));
            Assert.IsTrue(retainedLibraryRows.Any(item => item.GetProperty("templateKey").GetString() == customTemplateKey &&
                item.GetProperty("isCustom").GetBoolean()), "Resetting the staff override preserves library-owned templates.");

            deniedActor = await CreateCorrectiveStaffAsync(actor, StaffRole.Staff, 2);
            using var deniedClient = factory.CreateClient();
            AddTestingStaffHeaders(deniedClient, deniedActor.Id, actor.EntraTenantId,
                deniedActor.NormalizedUserPrincipalName!);
            deniedClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(deniedClient));
            using var latestSystem = await ReadSettingsDocumentAsync(client, "system");
            using (var forbidden = await deniedClient.PostAsJsonAsync("/api/asap/staff/settings", new
                   {
                       orgId = "system",
                       version = latestSystem.RootElement.GetProperty("version").GetString(),
                       templates = new[]
                       {
                           new { templateKey = StaffEmailTemplateCatalog.TitleAssignment, subject = "Unauthorized edit" }
                       }
                   }))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
                using var result = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
                Assert.AreEqual("staff_scope_forbidden", result.RootElement.GetProperty("code").GetString());
            }
        }
        finally
        {
            if (deniedActor is not null)
            {
                await DeactivateCorrectiveStaffAsync(deniedActor.Id);
            }
            if (systemTemplateId > 0 && originalSystemBody is not null)
            {
                await using var restore = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
                var template = await restore.EmailTemplates.SingleAsync(item => item.Id == systemTemplateId);
                template.BodyTemplate = originalSystemBody;
                await restore.SaveChangesAsync();
            }
            await ExecuteNonQueryAsync("DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore; DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @library; DELETE FROM [asap].[Organization] WHERE [Id] = @library;",
                ("@auditBefore", auditBefore), ("@library", libraryId));
        }
    }
}
