using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task AdministrationAuditBrowserPreservesSectionsFiltersScopesDraftsAndApprovedLayouts()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedAdministrationAuditAsync(actor.Id);
        var admin = await CreateCorrectiveStaffAsync(actor, StaffRole.Admin, AuditLibrary);
        var ordinary = await ReadCorrectiveStaffAsync(admin);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using (var context = await contexts.CreateDbContextAsync())
        {
            // 268 stored fixture entries + the real staff-create audit + six
            // settings entries make 275: first AND last pages contain 25 rows.
            // Paging focus must survive collapsing and restoring a full page.
            context.AdministrativeAudits.AddRange(Enumerable.Range(0, 6).Select(index => new AdministrativeAudit
            {
                ActorStaffUserId = actor.Id, ActorName = actor.DisplayName, OrganizationId = AuditLibrary,
                Action = "library_settings_updated", TargetType = "Configuration", TargetId = $"focus-layout-{index}",
                CreatedUtc = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index)
            }));
            context.QueueProgress.Add(new QueueProgress { QueueName = QueueNames.IdentifierProcessing, ScopeOrganizationId = AuditLibrary,
                LastOutcomeCode = "cycle_complete", UpdatedUtc = new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc) });
            foreach (var status in new[] { "failed", "sent", "suppressed" })
            {
                context.EmailOutbox.Add(new EmailOutbox
                {
                    OrganizationId = AuditLibrary, DeliveryClass = status == "suppressed" ? "business_event" : "operational_test", Status = status,
                    ToAddress = "audit-fixture@example.org", FromAddress = "asap@example.org", Subject = "Synthetic audit browser fixture",
                    BodyText = "No delivery is performed.", LastErrorCode = status == "failed" ? "mail_not_configured" : null,
                    SuppressionReason = status == "suppressed" ? "recipient_domain_not_allowed" : null,
                    AttemptDeliveryMode = status == "sent" ? "postmark" : null,
                    CreatedUtc = new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc),
                    SentUtc = status == "sent" ? new DateTime(2026, 11, 3, 14, 1, 0, DateTimeKind.Utc) : null,
                    SuppressedUtc = status == "suppressed" ? new DateTime(2026, 11, 3, 14, 1, 0, DateTimeKind.Utc) : null
                });
            }
            await context.SaveChangesAsync();
        }
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var ciResults = Environment.GetEnvironmentVariable("ASAP_CI_BROWSER_RESULTS");
        var artifacts = Path.Combine(string.IsNullOrWhiteSpace(ciResults) ? Path.Combine(root, ".artifacts", "browser")
            : Path.GetDirectoryName(Path.GetFullPath(ciResults))!, $"operations-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        try
        {
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "administration-audit.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                AuditLibrary.ToString(CultureInfo.InvariantCulture), actor.Id.ToString(CultureInfo.InvariantCulture),
                actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                ordinary.Id.ToString(CultureInfo.InvariantCulture), ordinary.EntraTenantId.ToString(), ordinary.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(26, report.RootElement.GetProperty("states").GetArrayLength());
            Assert.AreEqual(4, report.RootElement.GetProperty("queueScrolling").GetArrayLength());
            Assert.AreEqual(22, report.RootElement.GetProperty("focus").GetArrayLength());
            Assert.IsTrue(report.RootElement.GetProperty("focus").EnumerateArray().All(item => item.GetProperty("visible").GetBoolean()));
            Assert.AreEqual(0, report.RootElement.GetProperty("writes").GetInt32());
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
            Assert.HasCount(0, ((RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>()).Envelopes);
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(admin.Id);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.EmailOutbox.Where(item => item.OrganizationId == AuditLibrary).ExecuteDeleteAsync();
            await cleanup.QueueProgress.Where(item => item.ScopeOrganizationId == AuditLibrary).ExecuteDeleteAsync();
            await CleanupAdministrationAuditAsync();
        }
    }
}
