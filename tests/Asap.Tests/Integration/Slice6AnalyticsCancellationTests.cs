using System.Data;
using System.Diagnostics;
using Asap.Web.Features.Analytics;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnalyticsBlockedReadsPropagateCancellationAndPreserveSubsequentReports(bool throughHttp)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (organizationA, organizationB) = await AllocateAnalyticsOrganizationsAsync();
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var abortedRequest = new TaskCompletionSource<AnalyticsCanceledRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new AnalyticsCancellationObservationFilter(abortedRequest))));
        using var client = observedFactory.CreateClient();
        AddTestingStaffHeaders(client, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.AuthenticationEmail);
        var path = $"/api/asap/staff/analytics?scope={organizationA}&range=custom&startDate=2026-09-01&endDate=2026-09-30";

        try
        {
            await SeedAnalyticsFixtureAsync(organizationA, organizationB, suffix);
            using var before = await ReadAnalyticsReportAsync(client, path);
            AssertAnalyticsSummary(before.RootElement, new(7, 8, 3, 2, 1.5));
            var beforeState = await ReadAnalyticsDurableStateAsync();
            var beforeVersions = await ReadAnalyticsPersistedVersionsAsync();
            var provider = observedFactory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            var sender = (RecordingEmailSender)observedFactory.Services.GetRequiredService<IEmailSender>();
            var providerCalls = provider.Calls.Count;
            var emails = sender.Envelopes.Count;
            var dispatches = dispatcher!.EnqueuedIds.Count;

            await using var blocker = new SqlConnection(databaseConnectionString);
            await blocker.OpenAsync();
            await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
            await using var command = blocker.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT @@SPID;";
            var blockerId = Convert.ToInt32(await command.ExecuteScalarAsync());
            // A rolled-back fixture-only column holds Sch-M even when the database uses snapshot reads.
            command.CommandText = "ALTER TABLE [asap].[TitleRequest] ADD [AnalyticsCancellationBlocker] bit NULL;";
            await command.ExecuteNonQueryAsync();

            using var cancellation = new CancellationTokenSource();
            Task? pending = null;
            Exception? error;
            AnalyticsCanceledRequest? server = null;
            TaskStatus status;
            var elapsed = Stopwatch.StartNew();
            try
            {
                pending = throughHttp
                    ? client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellation.Token)
                    : observedFactory.Services.GetRequiredService<AnalyticsService>().GetAsync(
                        superAdmin, LibraryScope.ForLibrary(organizationA), "custom", "2026-09-01", "2026-09-30", cancellation.Token);
                var session = await WaitForBlockedAnalyticsAggregateAsync(blockerId);
                elapsed.Restart();
                cancellation.Cancel();
                error = await CaptureAnalyticsReadErrorAsync(pending);
                await WaitForAnalyticsAggregateToDisappearAsync(session);
                if (throughHttp)
                {
                    server = await abortedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                status = pending.Status;

                command.CommandText =
                    """
                    SELECT COUNT(*) FROM sys.dm_tran_locks
                    WHERE [request_session_id] = @@SPID AND [resource_database_id] = DB_ID()
                      AND [resource_type] = N'OBJECT' AND [resource_associated_entity_id] = OBJECT_ID(N'asap.TitleRequest')
                      AND [request_mode] = N'Sch-M' AND [request_status] = N'GRANT';
                    """;
                Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync()),
                    "The canceled aggregate must disappear while its blocker is still held.");
                TestContext.WriteLine($"Analytics cancellation: boundary={(throughHttp ? "HTTP" : "service")}; " +
                    $"blockedSession={session}; task={status}; error={error?.GetType().Name}; " +
                    $"SQL disappeared before lock release in {elapsed.Elapsed.TotalMilliseconds:F3}ms.");
            }
            finally
            {
                cancellation.Cancel();
                await transaction.RollbackAsync();
                if (pending is not null)
                {
                    await CaptureAnalyticsReadErrorAsync(pending);
                }
            }

            // Verify recovery in this same database before a classification assertion can stop the test.
            using var after = await ReadAnalyticsReportAsync(client, path);
            AssertAnalyticsMetricsEqual(before.RootElement, after.RootElement);
            Assert.AreEqual(beforeState, await ReadAnalyticsDurableStateAsync());
            Assert.AreEqual(beforeVersions.Rows, (await ReadAnalyticsPersistedVersionsAsync()).Rows);
            Assert.AreEqual(providerCalls, provider.Calls.Count);
            Assert.AreEqual(emails, sender.Envelopes.Count);
            Assert.AreEqual(dispatches, dispatcher.EnqueuedIds.Count);
            TestContext.WriteLine("Subsequent report succeeded; persisted versions, schema, data/event/outbox counters and provider/email activity are unchanged.");

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.AreEqual(TaskStatus.Canceled, status);
            Assert.IsInstanceOfType<OperationCanceledException>(error);
            if (throughHttp)
            {
                Assert.IsNotNull(server);
                Assert.IsTrue(server.Token.IsCancellationRequested);
                Assert.IsFalse(server.ResponseStarted, "Cancellation cannot deliver a successful or validation response.");
                Assert.IsInstanceOfType<OperationCanceledException>(server.Error);
                Assert.AreEqual(server.Token, ((OperationCanceledException)server.Error).CancellationToken);
                Assert.IsInstanceOfType<SqlException>(server.Error.InnerException);
            }
            else
            {
                Assert.AreEqual(cancellation.Token, ((OperationCanceledException)error).CancellationToken);
                Assert.IsInstanceOfType<SqlException>(error.InnerException);
            }
        }
        finally
        {
            await CleanupAnalyticsFixtureAsync(organizationA, organizationB, 0, suffix);
        }
    }

    private static async Task<Exception?> CaptureAnalyticsReadErrorAsync(Task pending)
    {
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(8));
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private async Task<int> WaitForBlockedAnalyticsAggregateAsync(int blockerId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TOP (1) CONVERT(int, requests.[session_id]), requests.[wait_type]
            FROM sys.dm_exec_requests AS requests
            CROSS APPLY sys.dm_exec_sql_text(requests.[sql_handle]) AS sql
            WHERE requests.[database_id] = DB_ID() AND requests.[blocking_session_id] = @blocker
              AND CHARINDEX(N'ScopedTitleRequests', sql.[text]) > 0
              AND CHARINDEX(N'@rangeEndExclusiveUtc', sql.[text]) > 0;
            """;
        command.Parameters.Add("@blocker", SqlDbType.Int).Value = blockerId;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
        {
            await using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    TestContext.WriteLine($"Observed Analytics SQL session {reader.GetInt32(0)} blocked by {blockerId}: {reader.GetString(1)}.");
                    return reader.GetInt32(0);
                }
            }
            await Task.Delay(20);
        }
        Assert.Fail("The actual Analytics aggregate did not reach the fixture's SQL blocker.");
        return 0;
    }

    private static async Task WaitForAnalyticsAggregateToDisappearAsync(int session)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM sys.dm_exec_requests AS requests
            CROSS APPLY sys.dm_exec_sql_text(requests.[sql_handle]) AS sql
            WHERE requests.[database_id] = DB_ID() AND requests.[session_id] = @session
              AND CHARINDEX(N'ScopedTitleRequests', sql.[text]) > 0
              AND CHARINDEX(N'@rangeEndExclusiveUtc', sql.[text]) > 0;
            """;
        command.Parameters.Add("@session", SqlDbType.Int).Value = session;
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
            {
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("The canceled Analytics command is still waiting behind the fixture's SQL blocker.");
    }

    private sealed record AnalyticsCanceledRequest(CancellationToken Token, bool ResponseStarted, Exception? Error);

    private sealed class AnalyticsCancellationObservationFilter(
        TaskCompletionSource<AnalyticsCanceledRequest> abortedRequest) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext context, RequestDelegate nextRequest) =>
            {
                Exception? error = null;
                try
                {
                    await nextRequest(context);
                }
                catch (Exception observedError)
                {
                    error = observedError;
                    throw;
                }
                finally
                {
                    if (context.Request.Path == "/api/asap/staff/analytics" && context.RequestAborted.IsCancellationRequested)
                    {
                        abortedRequest.TrySetResult(new AnalyticsCanceledRequest(
                            context.RequestAborted, context.Response.HasStarted, error));
                    }
                }
            });
            next(app);
        };
    }
}
