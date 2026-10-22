using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task LeapPatternValidationRejectsWholeMixedSaveAndPreservesOmittedLegacyValues()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var initial = await contexts.CreateDbContextAsync();
        var system = await initial.SystemSettings.SingleAsync();
        var originalBib = system.LeapBibUrlPattern;
        var originalPatron = system.LeapPatronUrlPattern;
        var key = "rejection:invalid_leap_" + Guid.NewGuid().ToString("N");
        try
        {
            using var before = await ReadSettingsDocumentAsync(client, "system");
            var auditBefore = await ReadAuditHighWatermarkAsync();
            foreach (var (field, pattern) in new[]
            {
                ("leapBibUrlPattern", "https://leap.example.org/no-token"),
                ("leapBibUrlPattern", "javascript:alert('{{bibid}}')"),
                ("leapBibUrlPattern", "https:leap.example.org/{{bibid}}"),
                ("leapBibUrlPattern", "https://user:secret@leap.example.org/{{bibid}}"),
                ("leapBibUrlPattern", "https://leap.example.org/{{bibid}}/{{unknown}}"),
                ("leapPatronUrlPattern", "https://leap.example.org/{{bibid}}"),
                ("leapPatronUrlPattern", "data:text/plain,{{patron-id}}"),
                ("leapPatronUrlPattern", "https://{{patron-id}}" + "\r\n" + "bad")
            })
            {
                using var rejected = await client.PostAsJsonAsync("/api/asap/staff/settings", new
                {
                    orgId = "system", version = before.RootElement.GetProperty("version").GetString(),
                    systemSettings = new Dictionary<string, object?> { [field] = pattern },
                    workflow = new { suggestionLimitMessage = "Forbidden partial mutation" },
                    templates = new[] { new { key, name = "Forbidden partial template", subject = "Subject", body = "Body" } }
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
                using var reloaded = await ReadSettingsDocumentAsync(client, "system");
                Assert.AreEqual(before.RootElement.GetProperty("version").GetString(), reloaded.RootElement.GetProperty("version").GetString());
                Assert.AreEqual(auditBefore, await ReadAuditHighWatermarkAsync());
                Assert.AreEqual(0, await initial.EmailTemplates.CountAsync(item => item.TemplateKey == key));
            }

            foreach (var (bib, patron) in new (string?, string?)[]
            {
                (" https://leap.example.org/#bib/{{bibid}}/{{bibid}} ", "http://leap.example.org/#patron/{{patron-id}}"),
                ("http://leap.example.org/{{bibid}}", "https://leap.example.org/{{patronId}}/{{patronId}}"),
                ("", "   "), (null, null)
            })
            {
                using var current = await ReadSettingsDocumentAsync(client, "system");
                using var saved = await SaveSettingsDocumentAsync(client, current.RootElement, "system",
                    new Dictionary<string, object?> { ["systemSettings"] = new { leapBibUrlPattern = bib, leapPatronUrlPattern = patron } });
                await using var read = await contexts.CreateDbContextAsync();
                var persisted = await read.SystemSettings.AsNoTracking().SingleAsync();
                Assert.AreEqual(string.IsNullOrWhiteSpace(bib) ? null : bib.Trim(), persisted.LeapBibUrlPattern);
                Assert.AreEqual(string.IsNullOrWhiteSpace(patron) ? null : patron.Trim(), persisted.LeapPatronUrlPattern);
            }

            // Invalid migrated patterns stay safe/unusable until explicitly edited;
            // an unrelated partial command must not rewrite legacy evidence.
            await using (var legacyWrite = await contexts.CreateDbContextAsync())
            {
                var legacyRow = await legacyWrite.SystemSettings.SingleAsync();
                legacyRow.LeapBibUrlPattern = "legacy invalid pattern";
                legacyRow.LeapPatronUrlPattern = "legacy patron pattern";
                await legacyWrite.SaveChangesAsync();
            }
            using var legacy = await ReadSettingsDocumentAsync(client, "system");
            using var omitted = await SaveSettingsDocumentAsync(client, legacy.RootElement, "system", new Dictionary<string, object?>());
            await using var legacyRead = await contexts.CreateDbContextAsync();
            var preserved = await legacyRead.SystemSettings.AsNoTracking().SingleAsync();
            Assert.AreEqual("legacy invalid pattern", preserved.LeapBibUrlPattern);
            Assert.AreEqual("legacy patron pattern", preserved.LeapPatronUrlPattern);
        }
        finally
        {
            await using var restore = await contexts.CreateDbContextAsync();
            var row = await restore.SystemSettings.SingleAsync();
            row.LeapBibUrlPattern = originalBib;
            row.LeapPatronUrlPattern = originalPatron;
            await restore.SaveChangesAsync();
        }
    }
}
