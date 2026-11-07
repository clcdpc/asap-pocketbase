using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task EmailHistoryRetryBrowserPreservesFocusAcrossPendingCompletionAndMovedHistory()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedEmailHistoryAsync(actor);
        try
        {
            var original = (await ReadEmailHistoryAsync(EmailHistoryLibrary))
                .Where(item => item.Status == "failed" && item.LastErrorCode == "mail_not_configured")
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).Take(3).ToArray();
            Assert.HasCount(3, original);
            var ids = original.Select(item => item.Id).ToArray();
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var configuredArtifacts = Environment.GetEnvironmentVariable("ASAP_EMAIL_RETRY_BROWSER_ARTIFACTS");
            var artifacts = string.IsNullOrWhiteSpace(configuredArtifacts)
                ? Path.Combine(root, ".artifacts", "browser", $"email-retry-focus-{Guid.NewGuid():N}")
                : Path.GetFullPath(configuredArtifacts);
            Directory.CreateDirectory(artifacts);
            TestContext.WriteLine($"Email retry focus evidence: {artifacts}");
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "email-retry-focus.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                EmailHistoryLibrary.ToString(CultureInfo.InvariantCulture),
                string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))),
                actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(0, report.RootElement.GetProperty("violations").GetArrayLength());
            Assert.AreEqual(3, report.RootElement.GetProperty("writes").GetArrayLength());
            Assert.AreEqual(3, report.RootElement.GetProperty("scenarios").GetArrayLength());
            var current = (await ReadEmailHistoryAsync(EmailHistoryLibrary)).Where(item => ids.Contains(item.Id)).ToArray();
            foreach (var row in current)
            {
                var before = original.Single(item => item.Id == row.Id);
                Assert.AreEqual("pending", row.Status);
                Assert.IsNull(row.LastErrorCode);
                Assert.AreEqual(before.AttemptCount, row.AttemptCount);
                Assert.IsFalse(row.RowVersion.SequenceEqual(before.RowVersion));
            }
            CollectionAssert.AreEquivalent(ids, dispatcher!.EnqueuedIds.ToArray(), "Each explicit retry queues its original persisted operation once.");
            Assert.HasCount(0, ((RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>()).Envelopes,
                "The fixture records dispatch intent but runs no email provider worker.");
        }
        finally
        {
            await CleanupEmailHistoryAsync();
        }
    }

    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task EmailHistoryBrowserPagesRealSqlScopesFiltersAndReadOnlyLayouts()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        await SeedEmailHistoryAsync(superAdmin);
        var libraryAdminRecord = await CreateCorrectiveStaffAsync(superAdmin, StaffRole.Admin, EmailHistoryLibrary);
        var libraryAdmin = await ReadCorrectiveStaffAsync(libraryAdminRecord);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var configuredArtifacts = Environment.GetEnvironmentVariable("ASAP_EMAIL_HISTORY_BROWSER_ARTIFACTS");
        var artifacts = string.IsNullOrWhiteSpace(configuredArtifacts)
            ? Path.Combine(root, ".artifacts", "browser", $"email-history-{Guid.NewGuid():N}")
            : Path.GetFullPath(configuredArtifacts);
        Directory.CreateDirectory(artifacts);
        TestContext.WriteLine($"Email history browser evidence: {artifacts}");
        var baselineKeys = new[] { "i459-browser-unrelated:system", "i459-browser-unrelated:library", "i459-browser-unrelated:branch" };

        async Task<string> SnapshotAsync()
        {
            await using var context = await contexts.CreateDbContextAsync();
            var rows = await context.EmailOutbox.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.OrganizationId, row.Status, row.AttemptCount, row.RowVersion }).ToArrayAsync();
            return JsonSerializer.Serialize(rows);
        }

        async Task<(long AllCount, long SystemCount, long SystemBusinessEventCount)> CountHistoryAsync()
        {
            // This independent SQL oracle includes prior tests' legitimate rows;
            // neither the HTTP response nor the feature's seed defines the total.
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT_BIG(*),
                    COUNT_BIG(CASE WHEN email.[OrganizationId] = 1 THEN 1 END),
                    COUNT_BIG(CASE WHEN email.[OrganizationId] = 1 AND email.[DeliveryClass] = N'business_event' THEN 1 END)
                FROM [asap].[EmailOutbox] AS email
                INNER JOIN [asap].[Organization] AS organization ON organization.[Id] = email.[OrganizationId]
                WHERE organization.[Id] = 1 OR organization.[OrganizationCodeId] = 2;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        try
        {
            var originalCounts = await CountHistoryAsync();
            var unrelated = new[] { 1, 2, EmailHistoryOtherLibrary + 1 }.Select((scope, index) => new EmailOutbox
            {
                OrganizationId = scope, BusinessKey = baselineKeys[index], DeliveryClass = "business_event", Status = "sent",
                ToAddress = "unrelated-history@example.org", FromAddress = "sender@example.org",
                Subject = "Unrelated history baseline", BodyText = "Retained read-only browser witness.",
                AttemptCount = 1, AttemptDeliveryMode = "file", ProviderMessageId = $"file:{baselineKeys[index]}",
                CreatedUtc = new DateTime(2028, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                SentUtc = new DateTime(2028, 1, 1, 12, 0, 1, DateTimeKind.Utc)
            }).ToArray();
            await using (var seed = await contexts.CreateDbContextAsync())
            {
                Assert.AreEqual(2, (await seed.Organizations.SingleAsync(row => row.Id == 2)).OrganizationCodeId);
                Assert.AreEqual(3, (await seed.Organizations.SingleAsync(row => row.Id == EmailHistoryOtherLibrary + 1)).OrganizationCodeId);
                Assert.IsFalse(await seed.EmailOutbox.AnyAsync(row => baselineKeys.Contains(row.BusinessKey!)));
                seed.EmailOutbox.AddRange(unrelated);
                await seed.SaveChangesAsync();
            }
            var expected = await CountHistoryAsync();
            Assert.AreEqual(originalCounts.AllCount + 2, expected.AllCount, "All history includes System and other libraries, excluding branch records.");
            Assert.AreEqual(originalCounts.SystemCount + 1, expected.SystemCount);
            Assert.AreEqual(originalCounts.SystemBusinessEventCount + 1, expected.SystemBusinessEventCount);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "history-expectations.json"), JsonSerializer.Serialize(new
            {
                allCount = expected.AllCount, systemCount = expected.SystemCount, systemBusinessEventCount = expected.SystemBusinessEventCount,
                baseline = unrelated.Select(row => new { reference = row.Id.ToString(CultureInfo.InvariantCulture),
                    organizationId = row.OrganizationId, count = row.OrganizationId == EmailHistoryOtherLibrary + 1 ? 0 : 1 }).ToArray()
            }));
            Assert.IsGreaterThan(500, (await ReadEmailHistoryAsync(EmailHistoryLibrary)).Length);
            Assert.HasCount(60, await ReadEmailHistoryAsync(EmailHistoryOtherLibrary));
            var before = await SnapshotAsync();
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "email-history.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                EmailHistoryLibrary.ToString(CultureInfo.InvariantCulture),
                EmailHistoryOtherLibrary.ToString(CultureInfo.InvariantCulture),
                superAdmin.Id.ToString(CultureInfo.InvariantCulture), superAdmin.EntraTenantId.ToString(), superAdmin.AuthenticationEmail,
                libraryAdmin.Id.ToString(CultureInfo.InvariantCulture), libraryAdmin.EntraTenantId.ToString(), libraryAdmin.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            var result = report.RootElement;
            Assert.AreEqual(0, result.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, result.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(0, result.GetProperty("writes").GetInt32());
            Assert.AreEqual(5, result.GetProperty("states").GetArrayLength());
            Assert.AreEqual(0, result.GetProperty("states").EnumerateArray().Sum(item => item.GetProperty("violations").GetArrayLength()));
            Assert.IsTrue(result.GetProperty("states").EnumerateArray().All(item =>
                item.GetProperty("layout").GetProperty("scrollWidth").GetInt32() <= item.GetProperty("layout").GetProperty("width").GetInt32() &&
                item.GetProperty("layout").GetProperty("imagesLoaded").GetBoolean()));
            foreach (var state in result.GetProperty("states").EnumerateArray())
            {
                var label = state.GetProperty("layout").GetProperty("statusLabel");
                Assert.AreEqual("failed", label.GetProperty("value").GetString());
                Assert.AreEqual("Failed or unconfirmed", label.GetProperty("text").GetString());
                Assert.IsTrue(label.GetProperty("textWidth").GetDouble() <= label.GetProperty("availableWidth").GetDouble(),
                    $"The selected failed filter must fit its rendered control: {state.GetProperty("name").GetString()}.");
            }
            Assert.IsTrue(result.GetProperty("emailReads").GetArrayLength() > 0);
            Assert.AreEqual(1, result.GetProperty("focus").GetArrayLength());
            var races = result.GetProperty("races");
            Assert.IsTrue(races.GetProperty("failureBackedByRealRead").GetBoolean());
            Assert.IsTrue(races.GetProperty("failureRetryViaMainRefresh").GetBoolean());
            Assert.IsTrue(races.GetProperty("delayedScopeResponseRetired").GetBoolean());
            Assert.IsTrue(races.GetProperty("delayedIdentityResponseRetired").GetBoolean());
            Assert.IsTrue(races.GetProperty("directScopeDenied").GetBoolean());
            Assert.IsTrue(races.GetProperty("unrelatedBaselineScopeVerified").GetBoolean());
            Assert.AreEqual(2, result.GetProperty("nativeZoom").GetProperty("zoom").GetInt32());
            var nativeCapture = result.GetProperty("states").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "email-history-native-200").GetProperty("capture");
            Assert.AreEqual("cdp-viewport", nativeCapture.GetProperty("method").GetString());
            Assert.IsTrue(nativeCapture.GetProperty("width").GetInt32() > nativeCapture.GetProperty("cssWidth").GetInt32() * 1.9,
                "The actual PNG must contain the full physical viewport at native 200% zoom.");
            Assert.AreEqual(before, await SnapshotAsync(), "History browsing must not change rows, status, attempts or rowversions.");
            AssertEmailHistoryReadOnly();
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(libraryAdminRecord.Id);
            await using (var cleanup = await contexts.CreateDbContextAsync())
            {
                await cleanup.EmailOutbox.Where(row => baselineKeys.Contains(row.BusinessKey!)).ExecuteDeleteAsync();
            }
            await CleanupEmailHistoryAsync();
        }
    }
}
