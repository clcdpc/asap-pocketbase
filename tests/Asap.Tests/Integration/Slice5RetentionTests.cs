using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task RetentionUsesSetBasedSqlWithExactBoundariesCountsAndNoRepeatedWrites()
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-90);
        var prefix = $"retention:{Guid.NewGuid():N}";
        var sessions = new List<PatronSession>();
        var mail = new List<EmailOutbox>();
        foreach (var offset in new[] { -1, 0, 1 })
        {
            sessions.Add(Session(now.AddTicks(offset), null));
            sessions.Add(Session(now.AddDays(1), now.AddDays(-1).AddTicks(offset)));
            mail.Add(NewCleanupOutbox(prefix, $"sent-{offset}", cutoff.AddDays(-1), status: "sent", sentUtc: cutoff.AddTicks(offset)));
            mail.Add(NewCleanupOutbox(prefix, $"suppressed-{offset}", cutoff.AddDays(-1), status: "suppressed",
                suppressedUtc: cutoff.AddTicks(offset), suppressionReason: "retention_evidence"));
        }
        foreach (var state in new[] { "pending", "sending", "failed" })
        {
            mail.Add(NewCleanupOutbox(prefix, state, cutoff.AddDays(-1), status: state, sending: state == "sending",
                errorCode: state == "failed" ? "provider_outcome_uncertain" : null));
        }
        foreach (var payload in new[] { "subject", "text", "html", "empty-string", "cleared" })
        {
            var row = NewCleanupOutbox(prefix, payload, cutoff.AddDays(-1), status: "sent", sentUtc: cutoff.AddTicks(-1));
            row.Subject = payload == "subject" ? "remaining" : payload == "empty-string" ? "" : null;
            row.BodyText = payload == "text" ? "remaining" : null;
            row.BodyHtml = payload == "html" ? "remaining" : null;
            mail.Add(row);
        }
        foreach (var row in mail)
        {
            row.AttemptCount = 3;
            row.ProviderMessageId = $"provider:{row.BusinessKey}";
            row.LastErrorDetail = "Retained provider/recovery evidence";
            row.LastAttemptUtc = cutoff.AddDays(-2);
            row.IntendedDeliveryMode = "postmark";
            row.AttemptDeliveryMode = "postmark";
        }
        await using (var seed = await contexts.CreateDbContextAsync())
        {
            seed.PatronSessions.AddRange(sessions);
            seed.EmailOutbox.AddRange(mail);
            await seed.SaveChangesAsync();
            seed.ChangeTracker.Clear();
            mail = await seed.EmailOutbox.AsNoTracking().Where(item => item.BusinessKey!.StartsWith(prefix)).ToListAsync();
        }
        var metadata = mail.ToDictionary(item => item.Id, RetentionMetadata);
        var beforeVersions = mail.ToDictionary(item => item.Id, item => item.RowVersion.ToArray());
        var eligibleMail = mail.Where(item => (item.Status == "sent" && item.SentUtc < cutoff ||
                                               item.Status == "suppressed" && item.SuppressedUtc < cutoff) &&
                                              (item.Subject != null || item.BodyText != null || item.BodyHtml != null))
            .Select(item => item.Id).ToHashSet();
        var probe = new RetentionSqlProbe();
        var measuredContexts = new SettingsReadContextFactory(databaseConnectionString, probe);
        var service = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(factory.Services, measuredContexts);
        try
        {
            Assert.AreEqual(4, await service.CleanupSessionsAsync());
            Assert.AreEqual(6, await service.CleanupEmailPayloadsAsync());
            await using var firstPass = await contexts.CreateDbContextAsync();
            var sessionIds = sessions.Select(item => item.Id).ToArray();
            var remaining = await firstPass.PatronSessions.AsNoTracking().Where(item => sessionIds.Contains(item.Id)).ToArrayAsync();
            CollectionAssert.AreEquivalent(sessions.Where(item => item.ExpiresUtc > now &&
                    (item.RevokedUtc == null || item.RevokedUtc > now.AddDays(-1))).Select(item => item.Id).ToArray(),
                remaining.Select(item => item.Id).ToArray());
            var firstRows = await firstPass.EmailOutbox.AsNoTracking().Where(item => item.BusinessKey!.StartsWith(prefix)).ToArrayAsync();
            foreach (var row in firstRows)
            {
                Assert.AreEqual(metadata[row.Id], RetentionMetadata(row), "All operational metadata must survive payload cleanup.");
                if (eligibleMail.Contains(row.Id))
                {
                    Assert.IsNull(row.Subject);
                    Assert.IsNull(row.BodyText);
                    Assert.IsNull(row.BodyHtml);
                    CollectionAssert.AreNotEqual(beforeVersions[row.Id], row.RowVersion);
                }
                else
                {
                    CollectionAssert.AreEqual(beforeVersions[row.Id], row.RowVersion, "Ineligible and fully cleared rows must never be updated.");
                    var original = mail.Single(item => item.Id == row.Id);
                    Assert.AreEqual(original.Subject, row.Subject);
                    Assert.AreEqual(original.BodyText, row.BodyText);
                    Assert.AreEqual(original.BodyHtml, row.BodyHtml);
                }
            }
            var firstVersions = firstRows.ToDictionary(item => item.Id, item => item.RowVersion);
            Assert.AreEqual(0, await service.CleanupSessionsAsync());
            Assert.AreEqual(0, await service.CleanupEmailPayloadsAsync());
            await using var secondPass = await contexts.CreateDbContextAsync();
            var secondRows = await secondPass.EmailOutbox.AsNoTracking().Where(item => item.BusinessKey!.StartsWith(prefix)).ToArrayAsync();
            foreach (var row in secondRows)
            {
                CollectionAssert.AreEqual(firstVersions[row.Id], row.RowVersion, "The second pass must issue no writes to cleared payloads.");
                Assert.AreEqual(metadata[row.Id], RetentionMetadata(row));
            }
            Assert.AreEqual(0, probe.ReaderCommands);
            Assert.AreEqual(0, probe.MaterializedEntities);
            Assert.HasCount(4, probe.Mutations);
            CollectionAssert.AreEqual(new[] { 4, 6, 0, 0 }, probe.Mutations.Select(item => item.Count).ToArray());
            StringAssert.Contains(probe.Mutations[0].Sql, "DELETE FROM");
            StringAssert.Contains(probe.Mutations[1].Sql, "UPDATE");
            foreach (var field in new[] { "Subject", "BodyText", "BodyHtml" })
            {
                StringAssert.Contains(probe.Mutations[1].Sql, $"[{field}] IS NOT NULL");
            }
            TestContext.WriteLine("Two retention passes: four set-based nonquery commands, zero reader commands/entities; affected counts 4, 6, 0, 0.");
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.EmailOutbox.Where(item => item.BusinessKey!.StartsWith(prefix)).ExecuteDeleteAsync();
            var sessionIds = sessions.Select(item => item.Id).ToArray();
            await cleanup.PatronSessions.Where(item => sessionIds.Contains(item.Id)).ExecuteDeleteAsync();
        }

        PatronSession Session(DateTime expiresUtc, DateTime? revokedUtc) => new()
        {
            TokenHash = RandomNumberGenerator.GetBytes(32), Barcode = "retention-boundary", EffectiveOrganizationId = 2,
            CreatedUtc = now.AddDays(-2), ExpiresUtc = expiresUtc, RevokedUtc = revokedUtc
        };
    }

    private static string RetentionMetadata(EmailOutbox row)
    {
        var values = JsonSerializer.SerializeToNode(row)!.AsObject();
        foreach (var name in new[] { nameof(row.Subject), nameof(row.BodyText), nameof(row.BodyHtml), nameof(row.RowVersion) })
        {
            values.Remove(name);
        }
        return values.ToJsonString();
    }

    private sealed class RetentionSqlProbe : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int ReaderCommands { get; private set; }
        public int MaterializedEntities { get; private set; }
        public List<(string Sql, int Count)> Mutations { get; } = [];

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            MaterializedEntities++;
            return entity;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            Mutations.Add((command.CommandText, result));
            return ValueTask.FromResult(result);
        }
    }
}
