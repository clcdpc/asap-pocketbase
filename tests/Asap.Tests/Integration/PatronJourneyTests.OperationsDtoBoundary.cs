using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task OperationsSqlReadDtosPreserveUtcTicksAndExactQueueIdentities()
    {
        const int scope = 382820;
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedOperationsBoundaryAsync(scope);
        try
        {
            var before = await ReadOperationsBoundaryStateAsync(scope);
            Assert.IsTrue(before.Queues.All(item => item.UpdatedUtc.Kind == DateTimeKind.Unspecified));
            Assert.IsTrue(before.Emails.All(item => item.CreatedUtc.Kind == DateTimeKind.Unspecified));

            using var queuesResponse = await client.GetAsync($"/api/asap/staff/workflow/queues?organizationId={scope}");
            Assert.AreEqual(HttpStatusCode.OK, queuesResponse.StatusCode);
            using var queues = JsonDocument.Parse(await queuesResponse.Content.ReadAsStringAsync());
            Assert.AreEqual(JsonValueKind.Number, queues.RootElement.GetProperty("scopeOrganizationId").ValueKind,
                "Native Polaris Int32 organization identifiers remain JSON numbers.");
            var queueRows = queues.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.HasCount(before.Queues.Length, queueRows);
            foreach (var stored in before.Queues)
            {
                var row = queueRows.Single(item => item.GetProperty("queueName").GetString() == stored.QueueName);
                Assert.AreEqual(scope, row.GetProperty("scopeOrganizationId").GetInt32());
                AssertOperationsUtc(row, "lastCreatedUtc", stored.LastCreatedUtc);
                AssertOperationsUtc(row, "lastOutcomeUtc", stored.LastOutcomeUtc);
                AssertOperationsUtc(row, "updatedUtc", stored.UpdatedUtc);
                AssertOperationsIdentity(row, "lastItemId", stored.LastItemId);
                AssertOperationsIdentity(row, "cycleMaxId", stored.CycleMaxId);
                AssertOperationsIdentity(row, "lastOutcomeItemId", stored.LastOutcomeItemId);
                Assert.AreEqual(Convert.ToBase64String(stored.RowVersion), row.GetProperty("version").GetString());
            }

            using var emailResponse = await client.GetAsync($"/api/asap/staff/email-operations?organizationId={scope}");
            Assert.AreEqual(HttpStatusCode.OK, emailResponse.StatusCode);
            using var emails = JsonDocument.Parse(await emailResponse.Content.ReadAsStringAsync());
            var emailRows = emails.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.HasCount(before.Emails.Length, emailRows);
            foreach (var stored in before.Emails)
            {
                var row = emailRows.Single(item => item.GetProperty("id").GetString() == stored.Id.ToString(CultureInfo.InvariantCulture));
                AssertOperationsUtc(row, "createdUtc", stored.CreatedUtc);
                AssertOperationsUtc(row, "sentUtc", stored.SentUtc);
                Assert.AreEqual(scope, row.GetProperty("organizationId").GetInt32());
                Assert.AreEqual(Convert.ToBase64String(stored.RowVersion), row.GetProperty("version").GetString());
                Assert.AreEqual(stored.Status == "failed", row.GetProperty("canRetry").GetBoolean());
            }
            CollectionAssert.AreEqual(before.Emails.OrderByDescending(item => item.Status == "failed")
                    .ThenByDescending(item => item.CreatedUtc).Select(item => item.Id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                emailRows.Select(item => item.GetProperty("id").GetString()).ToArray(),
                "UTC presentation must preserve failed-first, newest-first ordering.");

            using var failedResponse = await client.GetAsync($"/api/asap/staff/email-operations?organizationId={scope}&status=failed");
            using var failed = JsonDocument.Parse(await failedResponse.Content.ReadAsStringAsync());
            Assert.HasCount(1, failed.RootElement.GetProperty("items").EnumerateArray());
            Assert.AreEqual("failed", failed.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
            using var otherScopeResponse = await client.GetAsync("/api/asap/staff/email-operations?organizationId=2");
            using var otherScope = JsonDocument.Parse(await otherScopeResponse.Content.ReadAsStringAsync());
            Assert.IsTrue(otherScope.RootElement.GetProperty("items").EnumerateArray()
                .All(item => item.GetProperty("organizationId").GetInt32() == 2));

            var snapshots = factory.Services.GetRequiredService<QueueProgressService>();
            var maximum = await snapshots.GetSnapshotAsync(QueueNames.PurchasePromotion, scope, CancellationToken.None);
            Assert.AreEqual(DateTimeKind.Utc, maximum!.LastCreatedUtc!.Value.Kind);
            Assert.AreEqual(long.MaxValue.ToString(CultureInfo.InvariantCulture), maximum.CycleMaxId);
            Assert.IsTrue(QueueProgressService.IsAfter(before.Queues[0].UpdatedUtc, long.MaxValue,
                before.Queues[0].UpdatedUtc, long.MaxValue - 1));
            Assert.IsFalse(QueueProgressService.IsInCycle(long.MaxValue, long.MaxValue - 1),
                "Worker cursor comparisons continue to use exact SQL long arithmetic.");

            var after = await ReadOperationsBoundaryStateAsync(scope);
            Assert.AreEqual(before.Snapshot, after.Snapshot,
                "Read DTO normalization must not change stored ticks, cursor values, leases or rowversions.");
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
            Assert.HasCount(0, ((RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>()).Envelopes);
        }
        finally
        {
            await CleanupOperationsBoundaryAsync(scope);
        }
    }

    [TestMethod]
    public async Task OperationsBrowserLocalizesSqlUtcAndPreservesQueueIdentitiesAcrossScopes()
    {
        const int scope = 382821;
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await SeedOperationsBoundaryAsync(scope);
        try
        {
            var before = await ReadOperationsBoundaryStateAsync(scope);
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"operations-dto-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "operations-dto-boundary.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                scope.ToString(CultureInfo.InvariantCulture), actor.Id.ToString(CultureInfo.InvariantCulture),
                actor.EntraTenantId.ToString(), actor.AuthenticationEmail);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(4, report.RootElement.GetProperty("states").GetArrayLength());
            var after = await ReadOperationsBoundaryStateAsync(scope);
            Assert.AreEqual(before.Snapshot, after.Snapshot, "Local rendering, scope changes and reloads never write timestamps or cursor state.");
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
            Assert.HasCount(0, ((RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>()).Envelopes);
        }
        finally
        {
            await CleanupOperationsBoundaryAsync(scope);
        }
    }

    private async Task SeedOperationsBoundaryAsync(int scope)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        Assert.IsFalse(await context.Organizations.AnyAsync(item => item.Id == scope));
        context.Organizations.Add(new Organization
        {
            Id = scope, DisplayName = "Operations UTC and bigint fixture", Abbreviation = "OUTC",
            OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true
        });
        await context.SaveChangesAsync();
        var springBefore = OperationsSqlClock("2026-03-08T06:59:00.1234567");
        var springAfter = OperationsSqlClock("2026-03-08T07:01:00.1234567");
        var fallBefore = OperationsSqlClock("2026-11-01T05:59:00.1234567");
        var fallAfter = OperationsSqlClock("2026-11-01T06:01:00.1234567");
        var winter = OperationsSqlClock("2026-01-15T15:00:00.7654321");
        var summer = OperationsSqlClock("2026-07-15T15:00:00.7654321");
        context.QueueProgress.AddRange(
            new QueueProgress
            {
                QueueName = QueueNames.IdentifierProcessing, ScopeOrganizationId = scope,
                LastCreatedUtc = springBefore, LastItemId = 9_007_199_254_740_993,
                CycleMaxId = 9_007_199_254_740_995, LastOutcomeItemId = 9_007_199_254_740_997,
                LastOutcomeCode = "processed", LastOutcomeUtc = summer, UpdatedUtc = springBefore
            },
            new QueueProgress
            {
                QueueName = QueueNames.PurchasePromotion, ScopeOrganizationId = scope,
                LastCreatedUtc = springAfter, LastItemId = long.MaxValue,
                CycleMaxId = long.MaxValue, LastOutcomeItemId = long.MaxValue,
                LastOutcomeCode = "processed", LastOutcomeUtc = fallAfter, UpdatedUtc = springAfter
            },
            new QueueProgress
            {
                QueueName = QueueNames.HoldPlacement, ScopeOrganizationId = scope,
                LastCreatedUtc = winter, LastItemId = 0, CycleMaxId = 0, LastOutcomeItemId = 0,
                LastOutcomeCode = "cycle_complete", LastOutcomeUtc = winter, UpdatedUtc = winter
            },
            new QueueProgress
            {
                QueueName = QueueNames.FulfillmentTracking, ScopeOrganizationId = scope,
                LastCreatedUtc = fallBefore, LastItemId = 1, CycleMaxId = null,
                LastOutcomeCode = "cycle_complete", UpdatedUtc = fallBefore
            },
            new QueueProgress
            {
                QueueName = QueueNames.OutstandingTimeout, ScopeOrganizationId = scope,
                LastCreatedUtc = fallAfter, LastItemId = 2, CycleMaxId = null,
                LastOutcomeCode = "waiting", UpdatedUtc = fallAfter
            },
            new QueueProgress
            {
                QueueName = QueueNames.PendingHoldTimeout, ScopeOrganizationId = scope, UpdatedUtc = summer
            });
        context.EmailOutbox.AddRange(
            new EmailOutbox
            {
                OrganizationId = scope, DeliveryClass = "operational_test", Status = "failed",
                ToAddress = "operations-fixture@example.org", FromAddress = "asap@example.org",
                Subject = "Synthetic Operations fixture", BodyText = "No delivery is performed.",
                LastErrorCode = "mail_not_configured", CreatedUtc = winter
            },
            new EmailOutbox
            {
                OrganizationId = scope, DeliveryClass = "operational_test", Status = "sent",
                ToAddress = "operations-fixture@example.org", FromAddress = "asap@example.org",
                Subject = "Synthetic Operations fixture", BodyText = "No delivery is performed.",
                ProviderMessageId = "synthetic-operations-fixture", CreatedUtc = summer, SentUtc = fallAfter
            },
            new EmailOutbox
            {
                OrganizationId = scope, DeliveryClass = "operational_test", Status = "pending",
                ToAddress = "operations-fixture@example.org", FromAddress = "asap@example.org",
                Subject = "Synthetic Operations fixture", BodyText = "No delivery is performed.", CreatedUtc = springAfter
            });
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<(QueueProgress[] Queues, EmailOutbox[] Emails, string Snapshot)> ReadOperationsBoundaryStateAsync(int scope)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var queues = await context.QueueProgress.AsNoTracking().Where(item => item.ScopeOrganizationId == scope)
            .OrderBy(item => item.QueueName).ToArrayAsync();
        var emails = await context.EmailOutbox.AsNoTracking().Where(item => item.OrganizationId == scope)
            .OrderBy(item => item.Id).ToArrayAsync();
        return (queues, emails, JsonSerializer.Serialize(new { queues, emails }));
    }

    private async Task CleanupOperationsBoundaryAsync(int scope)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        await context.EmailOutbox.Where(item => item.OrganizationId == scope).ExecuteDeleteAsync();
        await context.QueueProgress.Where(item => item.ScopeOrganizationId == scope).ExecuteDeleteAsync();
        await context.Organizations.Where(item => item.Id == scope).ExecuteDeleteAsync();
    }

    private static DateTime OperationsSqlClock(string value) => DateTime.ParseExact(value,
        "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static void AssertOperationsUtc(JsonElement row, string property, DateTime? stored)
    {
        var actual = row.GetProperty(property);
        if (!stored.HasValue)
        {
            Assert.AreEqual(JsonValueKind.Null, actual.ValueKind, property);
            return;
        }
        var value = actual.GetString()!;
        Assert.IsTrue(value.EndsWith('Z'), $"{property}: {value}");
        var parsed = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.AreEqual(DateTimeKind.Utc, parsed.Kind, property);
        Assert.AreEqual(stored.Value.Ticks, parsed.Ticks, $"{property} must preserve all datetime2(7) ticks.");
    }

    private static void AssertOperationsIdentity(JsonElement row, string property, long? stored)
    {
        var actual = row.GetProperty(property);
        Assert.AreEqual(stored.HasValue ? JsonValueKind.String : JsonValueKind.Null, actual.ValueKind, property);
        if (stored.HasValue)
        {
            Assert.AreEqual(stored.Value.ToString(CultureInfo.InvariantCulture), actual.GetString(), property);
        }
    }
}
