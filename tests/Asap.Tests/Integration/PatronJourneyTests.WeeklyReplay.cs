using System.Data.Common;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WeeklyCommittedReplaySkipsReadinessWritesAndDispatchEvenDuringOutage(bool forced)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            Guid? run = forced ? Guid.NewGuid() : null;
            await QueueWeeklyRecipientsAsync(run);
            var before = JsonSerializer.Serialize(await ReadWeeklyRecipientRowsAsync(id));
            var sender = new CountingWeeklyReadinessFailure();
            var dispatch = new RecordingOutboxDispatcher();
            var probe = new WeeklySummarySqlProbe();
            var service = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(factory!.Services,
                new WeeklySummaryContextFactory(databaseConnectionString, probe), sender, dispatch);

            var replay = await service.SendWeeklyStaffSummaryAsync(run, WeeklyRecipientLibrary, CancellationToken.None);
            Assert.AreEqual("completed", replay.Code);
            Assert.AreEqual(run, replay.ManualRunId);
            Assert.AreEqual(1, replay.Visited);
            Assert.AreEqual(0, replay.Changed);
            Assert.AreEqual(1, replay.Skipped);
            Assert.AreEqual(0, sender.ReadinessCalls);
            Assert.HasCount(0, dispatch.EnqueuedIds);
            Assert.AreEqual(before, JsonSerializer.Serialize(await ReadWeeklyRecipientRowsAsync(id)));
            Assert.IsFalse(probe.Commands.Any(sql => sql.Contains("INSERT ", StringComparison.Ordinal) ||
                sql.Contains("UPDATE ", StringComparison.Ordinal) || sql.Contains("DELETE ", StringComparison.Ordinal)));

            var next = await service.SendWeeklyStaffSummaryAsync(Guid.NewGuid(), WeeklyRecipientLibrary, CancellationToken.None);
            Assert.AreEqual("operational_failure", next.Code, "A new occurrence still reports a readiness outage honestly.");
            Assert.AreEqual(0, next.Changed);
            Assert.AreEqual(1, sender.ReadinessCalls);
            Assert.HasCount(0, dispatch.EnqueuedIds);
            Assert.AreEqual(before, JsonSerializer.Serialize(await ReadWeeklyRecipientRowsAsync(id)));
        });
    }

    [TestMethod]
    [DataRow("recipient")]
    [DataRow("actor")]
    [DataRow("organization")]
    [DataRow("removed")]
    [DataRow("cancel")]
    public async Task WeeklyReplayRechecksCurrentAuthorityAndOccurrenceAfterOrganizationLockWait(string mutation)
    {
        await WithWeeklyRecipientsAsync("primary@example.org", "additional@example.org", true, async id =>
        {
            var initiatingAdmin = await CreateCorrectiveStaffAsync(await ReadConfiguredSuperAdminAsync(), StaffRole.Admin, WeeklyRecipientLibrary);
            try
            {
                var actor = await ReadCorrectiveStaffAsync(initiatingAdmin);
                await QueueWeeklyRecipientsAsync();
                var before = JsonSerializer.Serialize(await ReadWeeklyRecipientRowsAsync(id));
                var sender = new CountingWeeklyReadinessFailure();
                var dispatch = new RecordingOutboxDispatcher();
                var probe = new WeeklySummarySqlProbe();
                var service = ActivatorUtilities.CreateInstance<WorkflowProcessingService>(factory!.Services,
                    new WeeklySummaryContextFactory(databaseConnectionString, probe), sender, dispatch);
                await using var blocker = new SqlConnection(databaseConnectionString);
                await blocker.OpenAsync();
                var sessionId = Convert.ToInt32(await ExecuteScalarAsync(blocker, "SELECT @@SPID;"));
                await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
                await using var command = new SqlCommand(
                    "SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @scope;", blocker, transaction);
                command.Parameters.AddWithValue("@scope", WeeklyRecipientLibrary);
                await command.ExecuteScalarAsync();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var pending = service.SendWeeklyStaffSummaryAsync(null, WeeklyRecipientLibrary, cancellation.Token,
                    new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId));
                try
                {
                    await WaitForBlockedSessionAsync(sessionId);
                    Assert.AreEqual(1, probe.OccurrenceReads, "The unlocked prefilter ran before waiting for locked authority.");
                    if (mutation == "cancel")
                    {
                        cancellation.Cancel();
                        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                    }
                    else
                    {
                        var sql = mutation switch
                        {
                            "recipient" => "UPDATE [asap].[StaffUser] SET [WeeklyActionSummaryEnabled] = 0 WHERE [Id] = @id;",
                            "actor" => "UPDATE [asap].[StaffUser] SET [Role] = N'staff' WHERE [Id] = @actor;",
                            "organization" => "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @scope;",
                            "removed" => "DELETE FROM [asap].[EmailOutbox] WHERE [RecipientStaffUserId] = @id;",
                            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
                        };
                        await using var change = new SqlCommand(sql, blocker, transaction);
                        change.Parameters.AddWithValue("@id", id);
                        change.Parameters.AddWithValue("@actor", actor.Id);
                        change.Parameters.AddWithValue("@scope", WeeklyRecipientLibrary);
                        await change.ExecuteNonQueryAsync();
                        await transaction.CommitAsync();
                        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                        Assert.AreEqual(mutation == "removed" ? "operational_failure" : "completed", result.Code);
                        Assert.AreEqual(0, result.Changed);
                        Assert.AreEqual(mutation == "removed" ? 2 : 1, probe.OccurrenceReads,
                            "Changed authority must reject replay before the final occurrence check.");
                    }
                    Assert.AreEqual(mutation == "removed" ? 1 : 0, sender.ReadinessCalls);
                    Assert.HasCount(0, dispatch.EnqueuedIds);
                    if (mutation == "removed")
                    {
                        Assert.HasCount(0, await ReadWeeklyRecipientRowsAsync(id), "Removed intent cannot be recreated without readiness.");
                    }
                    else
                    {
                        Assert.AreEqual(before, JsonSerializer.Serialize(await ReadWeeklyRecipientRowsAsync(id)));
                    }
                }
                finally
                {
                    if (transaction.Connection is not null)
                    {
                        await transaction.RollbackAsync();
                    }
                    cancellation.Cancel();
                    try
                    {
                        await pending;
                    }
                    catch (OperationCanceledException)
                    {
                        // Drain the cancelled SQL operation before fixture cleanup.
                    }
                }
            }
            finally
            {
                await DeactivateCorrectiveStaffAsync(initiatingAdmin.Id);
            }
        });
    }

    private sealed class CountingWeeklyReadinessFailure : IEmailSender
    {
        public int ReadinessCalls { get; private set; }
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadinessCalls++;
            throw new EmailOperationalException("Deterministic transport outage.");
        }
        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Weekly preparation must not deliver email directly.");
    }

    private sealed class WeeklySummaryContextFactory(string connectionString, WeeklySummarySqlProbe probe) : IDbContextFactory<AsapDbContext>
    {
        public AsapDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AsapDbContext>()
            .UseSqlServer(connectionString).AddInterceptors(probe).Options);
    }

    private sealed class WeeklySummarySqlProbe : DbCommandInterceptor, IMaterializationInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<(string Sql, string[] Columns, int ReadCalls)> Reads { get; } = [];
        public int FullRequestEntities { get; private set; }
        public int OccurrenceReads => Commands.Count(sql => sql.Contains("[asap].[EmailOutbox]", StringComparison.Ordinal) &&
            sql.Contains("[BusinessKey] =", StringComparison.Ordinal));
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Reads.Add((command.CommandText, Enumerable.Range(0, eventData.DataReader.FieldCount)
                .Select(eventData.DataReader.GetName).ToArray(), eventData.ReadCount));
            return ValueTask.FromResult(result);
        }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is TitleRequest or AdditionalCopyRequest)
            {
                FullRequestEntities++;
            }
            return entity;
        }
    }
}
