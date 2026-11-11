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
    [DataRow("additionalCopy", "inactive")]
    [DataRow("additionalCopy", "email")]
    [DataRow("additionalCopy", "library")]
    [DataRow("additionalCopy", "request")]
    [DataRow("additionalCopy", "other_actor")]
    [DataRow("purchase", "inactive")]
    [DataRow("purchase", "email")]
    [DataRow("purchase", "library")]
    [DataRow("purchase", "request")]
    [DataRow("purchase", "other_actor")]
    public async Task PolarisQueueReplayReauthorizesAfterLockWaitAndChecksOriginalActor(string action, string mutation)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        if (action == "purchase")
        {
            await SetSavedPurchaseBibAsync(fixture, autoHold: false);
        }
        var input = action == "purchase" ? PurchaseTaskCommand(fixture.Source, false) : QueueCommand(fixture.Source, action);
        var accepted = await QueueService().ActionAsync(fixture.Actor, fixture.Source.Id, input, CancellationToken.None);
        Assert.AreEqual("updated", accepted.Code);
        var providerCalls = fixture.Provider.Calls.Count;
        var replayActor = fixture.Actor;
        if (mutation == "other_actor")
        {
            var staff = await CreateCorrectiveStaffAsync(await ReadConfiguredSuperAdminAsync(), StaffRole.Staff, 2);
            replayActor = await ReadCorrectiveStaffAsync(staff);
        }
        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        var sessionId = Convert.ToInt32(await ExecuteScalarAsync(blocker, "SELECT @@SPID;"));
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using var command = new SqlCommand("SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = 2;", blocker, transaction);
        await command.ExecuteScalarAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = QueueService().ActionAsync(replayActor, fixture.Source.Id, input, cancellation.Token);
        try
        {
            await WaitForBlockedSessionAsync(sessionId);
            var sql = mutation switch
            {
                "inactive" => "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
                "email" => "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = CONCAT(N'changed-replay-', CONVERT(nvarchar(20), @id), N'@example.org'), [NormalizedUserPrincipalName] = UPPER(CONCAT(N'changed-replay-', CONVERT(nvarchar(20), @id), N'@example.org')) WHERE [Id] = @id;",
                "library" => "UPDATE [asap].[StaffUser] SET [OrganizationId] = 101 WHERE [Id] = @id;",
                "request" => "UPDATE [asap].[TitleRequest] SET [LibraryOrganizationId] = 101 WHERE [Id] = @id;",
                _ => null
            };
            if (sql is not null)
            {
                await using var change = new SqlCommand(sql, blocker, transaction);
                change.Parameters.AddWithValue("@id", mutation == "request" ? fixture.Source.Id : fixture.Actor.Id);
                await change.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            var replay = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(mutation == "other_actor" ? "operation_conflict" : mutation == "request" ? "not_found" : "staff_scope_forbidden", replay.Code);
            Assert.AreEqual(providerCalls, fixture.Provider.Calls.Count, "Receipt replay must reauthorize without returning to Polaris.");
            await using var verify = await fixture.Contexts.CreateDbContextAsync();
            Assert.AreEqual(1, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id && item.EventType == "status_changed"));
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id && item.EventType == "polaris_queue_completed"));
            Assert.AreEqual(action == "purchase" ? "closed" : "pending_hold",
                await verify.TitleRequests.Where(item => item.Id == fixture.Source.Id).Select(item => item.Status).SingleAsync());
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
                // Ensure a failed ordering assertion does not leave a blocked task.
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolarisQueuePostCommitDispatchOutcomeReplaysWithoutAnotherDurableEffect(bool cancelAfterCommit)
    {
        await using var fixture = await SeedPolarisQueueAsync();
        using var cancellation = new CancellationTokenSource();
        var dispatch = new QueueDispatchFailure(cancellation, cancelAfterCommit);
        var service = ActivatorUtilities.CreateInstance<TitleRequestMutationService>(factory!.Services, dispatch);
        var input = QueueCommand(fixture.Source, "additionalCopy", true);
        var accepted = await service.ActionAsync(fixture.Actor, fixture.Source.Id, input, cancellation.Token);
        Assert.AreEqual("updated", accepted.Code);
        Assert.AreEqual(cancelAfterCommit ? "queued" : "dispatch_failed", accepted.NotificationStatus);
        Assert.AreEqual(cancelAfterCommit ? null : "queue_unavailable", accepted.NotificationReason);
        Assert.AreEqual(1, dispatch.Calls);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var version = await context.TitleRequests.Where(item => item.Id == fixture.Source.Id).Select(item => item.RowVersion).SingleAsync();
        var events = await context.TitleRequestEvents.Where(item => item.TitleRequestId == fixture.Source.Id).CountAsync();
        var replay = await service.ActionAsync(fixture.Actor, fixture.Source.Id, input, CancellationToken.None);
        Assert.AreEqual("updated", replay.Code);
        Assert.IsTrue(replay.Replayed);
        Assert.AreEqual("queued", replay.NotificationStatus, "The receipt returns the durable accepted outcome, before optional dispatch.");
        Assert.AreEqual(1, dispatch.Calls);
        Assert.AreEqual(1, await context.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == fixture.Source.Id));
        Assert.AreEqual(events, await context.TitleRequestEvents.CountAsync(item => item.TitleRequestId == fixture.Source.Id));
        Assert.AreEqual(1, await context.EmailOutbox.CountAsync(item => item.TitleRequestId == fixture.Source.Id));
        CollectionAssert.AreEqual(version, await context.TitleRequests.Where(item => item.Id == fixture.Source.Id).Select(item => item.RowVersion).SingleAsync());
    }

    private sealed class QueueDispatchFailure(CancellationTokenSource cancellation, bool cancelAfterCommit) : IEmailOutboxDispatcher
    {
        public int Calls { get; private set; }
        public void Enqueue(long outboxId)
        {
            Calls++;
            if (cancelAfterCommit)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw new InvalidOperationException("Immediate dispatch unavailable after commit.");
        }
    }
}
