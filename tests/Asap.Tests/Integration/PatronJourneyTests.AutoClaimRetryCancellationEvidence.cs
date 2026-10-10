using System.Data;
using System.Diagnostics;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public Task AutoClaimPublicSqlDiagnosticSeamProducesFiveConflicts() =>
        RunAutoClaimRetryCancellationCaseAsync("public", "uncanceled_fifth");

    [TestMethod]
    [DataRow("public", "canceled_first")]
    [DataRow("public", "canceled_fifth")]
    [DataRow("direct_staff", "canceled_first")]
    [DataRow("direct_staff", "canceled_fifth")]
    [DataRow("direct_staff", "uncanceled_fifth")]
    [DataRow("staff_wrapper", "canceled_first")]
    [DataRow("staff_wrapper", "canceled_fifth")]
    [DataRow("staff_wrapper", "uncanceled_fifth")]
    public Task AutoClaimRetryCancellationPreservesBoundaryOutcome(
        string entryPath,
        string boundary) =>
        RunAutoClaimRetryCancellationCaseAsync(entryPath, boundary);

    private async Task RunAutoClaimRetryCancellationCaseAsync(string entryPath, string boundary)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var barcode = $"autoclaim-retry-{Guid.NewGuid():N}";
        var title = $"Auto-claim retry cancellation {entryPath} {boundary} {Guid.NewGuid():N}";
        provider.AddPatron(
            new PatronSnapshot(
                870_000_000,
                barcode,
                "autoclaim@example.org",
                "Auto",
                "Claim",
                1,
                "Adult",
                101,
                2,
                "Test Library",
                101),
            [new PickupBranch(101, "Main Library")],
            2);

        AutoClaimRetryFixture? fixture = null;
        PatronSessionContext? session = null;
        AutoClaimRetryDiagnosticObserver? observer = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            fixture = await SeedAutoClaimRetryFixtureAsync(Guid.NewGuid().ToString("N"));
            if (entryPath == "public")
            {
                session = await IssueTestPatronSessionAsync(barcode);
            }
            var mutationBaseline = await ReadAutoClaimMutationCountsAsync();

            observer = new AutoClaimRetryDiagnosticObserver(
                databaseConnectionString,
                fixture.RuleId,
                fixture.FormatId,
                fixture.FirstStaffId,
                fixture.SecondStaffId,
                cancellation,
                boundary);

            var suggestionService = factory.Services.GetRequiredService<PatronSuggestionService>();
            var staffInput = new StaffSuggestionInput(
                2,
                barcode,
                "book",
                title,
                "Auto Claim Author",
                null,
                "Coming soon",
                null,
                null,
                101,
                101,
                false,
                false,
                new Dictionary<string, string?>(),
                CurrentPreferredPickupBranchObservedAtLoad: true);
            Task<PatronSuggestionResult> submission = entryPath switch
            {
                "public" => suggestionService.CreateAsync(session!, Suggestion(title), cancellation.Token),
                "direct_staff" => suggestionService.CreateForStaffAsync(actor, 2, staffInput, cancellation.Token),
                "staff_wrapper" => factory.Services.GetRequiredService<StaffSuggestionService>()
                    .CreateAsync(actor, staffInput, cancellation.Token),
                _ => throw new AssertFailedException($"Unknown AutoClaim entry path: {entryPath}.")
            };

            Exception? observedOutcome = null;
            try
            {
                await submission;
            }
            catch (Exception exception)
            {
                observedOutcome = exception;
            }

            Assert.IsNull(observer.Failure, observer.Failure?.ToString() ?? "The SQL diagnostic observer failed.");
            var expectedAttempts = boundary == "canceled_first" ? 1 : 5;
            Assert.AreEqual(expectedAttempts, observer.CandidateQueryCount);
            Assert.AreEqual(expectedAttempts, observer.ApplyQueryCount);
            Assert.AreEqual(expectedAttempts, observer.RuleUpdateCount);
            if (boundary == "uncanceled_fifth")
            {
                Assert.AreEqual(0, observer.ClosedApplyConnectionCount);
                Assert.IsFalse(cancellation.IsCancellationRequested);
            }
            else
            {
                Assert.AreEqual(1, observer.ClosedApplyConnectionCount,
                    "Cancellation must be raised by the selected Apply connection closing after its transaction unwinds.");
                Assert.IsTrue(cancellation.IsCancellationRequested);
            }

            if (boundary == "canceled_first")
            {
                Assert.IsInstanceOfType<OperationCanceledException>(observedOutcome);
            }
            else
            {
                var patronFailure = entryPath == "staff_wrapper"
                    ? (observedOutcome as StaffSuggestionException)?.InnerException as PatronFlowException
                    : observedOutcome as PatronFlowException;
                Assert.IsNotNull(patronFailure, $"Expected the fifth real candidate conflict, got {observedOutcome?.GetType().Name ?? "success"}.");
                Assert.AreEqual(409, patronFailure.StatusCode);
                Assert.IsInstanceOfType<AutoClaimCandidateChangedException>(patronFailure.InnerException);

                if (entryPath == "staff_wrapper")
                {
                    var staffFailure = observedOutcome as StaffSuggestionException;
                    Assert.IsNotNull(staffFailure);
                    Assert.AreEqual(409, staffFailure.StatusCode);
                    Assert.AreSame(patronFailure, staffFailure.InnerException);
                }
            }

            await AssertAutoClaimRetryHasNoAcceptedMutationAsync(title, mutationBaseline);
            Assert.AreEqual(0, provider.Calls.Count(call =>
                    call.Operation == TestingPolarisOperation.PickupUpdate &&
                    string.Equals(call.Key, barcode, StringComparison.Ordinal)),
                "The selected branch matches the provider's current branch, so the retry witness performs no pickup PUT.");
        }
        finally
        {
            observer?.Dispose();
            await DeletePostCommitSuggestionAsync(title, session?.Id);
            if (fixture is not null)
            {
                await RestoreAutoClaimRetryFixtureAsync(fixture);
            }
        }
    }

    private async Task AssertAutoClaimRetryHasNoAcceptedMutationAsync(
        string title,
        AutoClaimMutationCounts expectedCounts)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var requestIds = await context.TitleRequests.AsNoTracking()
            .Where(item => item.Title == title)
            .Select(item => item.Id)
            .ToArrayAsync();
        Assert.AreEqual(0, requestIds.Length, "A mismatched auto-claim candidate must roll the attempted request back.");
        Assert.AreEqual(expectedCounts.EventCount,
            await context.TitleRequestEvents.AsNoTracking().CountAsync(),
            "A rolled-back retry must not leave an accepted request event.");
        Assert.AreEqual(expectedCounts.OutboxCount,
            await context.EmailOutbox.AsNoTracking().CountAsync(),
            "A rolled-back retry must not leave a submission outbox row.");
    }

    private async Task<AutoClaimMutationCounts> ReadAutoClaimMutationCountsAsync()
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        return new AutoClaimMutationCounts(
            await context.TitleRequestEvents.AsNoTracking().CountAsync(),
            await context.EmailOutbox.AsNoTracking().CountAsync());
    }

    private static async Task<AutoClaimRetryFixture> SeedAutoClaimRetryFixtureAsync(string suffix)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var firstObjectId = Guid.NewGuid();
        var secondObjectId = Guid.NewGuid();
        var firstUpn = $"autoclaim-{suffix}-a@example.org";
        var secondUpn = $"autoclaim-{suffix}-b@example.org";
        var priorRules = new List<AutoClaimPriorRule>();

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        long formatId;
        await using (var format = new SqlCommand(
                         "SELECT [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book';",
                         connection,
                         transaction))
        {
            formatId = Convert.ToInt64(await format.ExecuteScalarAsync());
        }

        await using (var existing = new SqlCommand(
                         "SELECT [Id], [DeactivatedUtc] FROM [asap].[FormatAutoClaimRule] " +
                         "WHERE [LibraryOrganizationId] = 2 AND [MaterialFormatId] = @formatId AND [IsActive] = 1;",
                         connection,
                         transaction))
        {
            existing.Parameters.AddWithValue("@formatId", formatId);
            await using var reader = await existing.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                priorRules.Add(new AutoClaimPriorRule(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1)));
            }
        }

        await using (var deactivate = new SqlCommand(
                         "UPDATE [asap].[FormatAutoClaimRule] " +
                         "SET [IsActive] = 0, [DeactivatedUtc] = COALESCE([DeactivatedUtc], SYSUTCDATETIME()) " +
                         "WHERE [LibraryOrganizationId] = 2 AND [MaterialFormatId] = @formatId AND [IsActive] = 1;",
                         connection,
                         transaction))
        {
            deactivate.Parameters.AddWithValue("@formatId", formatId);
            await deactivate.ExecuteNonQueryAsync();
        }

        long firstStaffId;
        long secondStaffId;
        long ruleId;
        await using (var seed = new SqlCommand(
                         "DECLARE @firstStaffId bigint; " +
                         "INSERT INTO [asap].[StaffUser] " +
                         "([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName], " +
                         "[DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive]) " +
                         "VALUES (@tenantId, @firstObjectId, @firstUpn, UPPER(@firstUpn), @firstUpn, @firstUpn, N'staff', 2, 1); " +
                         "SET @firstStaffId = CONVERT(bigint, SCOPE_IDENTITY()); " +
                         "DECLARE @secondStaffId bigint; " +
                         "INSERT INTO [asap].[StaffUser] " +
                         "([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName], " +
                         "[DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive]) " +
                         "VALUES (@tenantId, @secondObjectId, @secondUpn, UPPER(@secondUpn), @secondUpn, @secondUpn, N'staff', 2, 1); " +
                         "SET @secondStaffId = CONVERT(bigint, SCOPE_IDENTITY()); " +
                         "INSERT INTO [asap].[FormatAutoClaimRule] " +
                         "([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc]) " +
                         "VALUES (2, @formatId, @firstStaffId, 1, SYSUTCDATETIME()); " +
                         "DECLARE @ruleId bigint = CONVERT(bigint, SCOPE_IDENTITY()); " +
                         "SELECT @firstStaffId, @secondStaffId, @ruleId;",
                         connection,
                         transaction))
        {
            seed.Parameters.AddWithValue("@tenantId", tenantId);
            seed.Parameters.AddWithValue("@firstObjectId", firstObjectId);
            seed.Parameters.AddWithValue("@secondObjectId", secondObjectId);
            seed.Parameters.AddWithValue("@firstUpn", firstUpn);
            seed.Parameters.AddWithValue("@secondUpn", secondUpn);
            seed.Parameters.AddWithValue("@formatId", formatId);
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            firstStaffId = reader.GetInt64(0);
            secondStaffId = reader.GetInt64(1);
            ruleId = reader.GetInt64(2);
        }

        await transaction.CommitAsync();
        return new AutoClaimRetryFixture(formatId, ruleId, firstStaffId, secondStaffId, priorRules);
    }

    private static async Task RestoreAutoClaimRetryFixtureAsync(AutoClaimRetryFixture fixture)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var remove = new SqlCommand(
                         "DELETE FROM [asap].[FormatAutoClaimRule] WHERE [Id] = @ruleId; " +
                         "DELETE FROM [asap].[StaffUser] WHERE [Id] IN (@firstStaffId, @secondStaffId);",
                         connection,
                         transaction))
        {
            remove.Parameters.AddWithValue("@ruleId", fixture.RuleId);
            remove.Parameters.AddWithValue("@firstStaffId", fixture.FirstStaffId);
            remove.Parameters.AddWithValue("@secondStaffId", fixture.SecondStaffId);
            await remove.ExecuteNonQueryAsync();
        }

        foreach (var priorRule in fixture.PriorRules)
        {
            await using var restore = new SqlCommand(
                "UPDATE [asap].[FormatAutoClaimRule] SET [IsActive] = 1, [DeactivatedUtc] = @deactivatedUtc " +
                "WHERE [Id] = @ruleId;",
                connection,
                transaction);
            restore.Parameters.AddWithValue("@ruleId", priorRule.Id);
            restore.Parameters.AddWithValue("@deactivatedUtc", (object?)priorRule.DeactivatedUtc ?? DBNull.Value);
            await restore.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private sealed record AutoClaimPriorRule(long Id, DateTime? DeactivatedUtc);

    private sealed record AutoClaimRetryFixture(
        long FormatId,
        long RuleId,
        long FirstStaffId,
        long SecondStaffId,
        IReadOnlyList<AutoClaimPriorRule> PriorRules);

    private sealed record AutoClaimMutationCounts(int EventCount, int OutboxCount);

    private sealed class AutoClaimRetryDiagnosticObserver :
        IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>,
        IDisposable
    {
        private const string ListenerName = "SqlClientDiagnosticListener";
        private const string EventName = "Microsoft.Data.SqlClient.WriteCommandAfter";
        private const string CandidateCommand =
            "SELECT TOP (1) [Id], [StaffUserId] FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @organizationId AND [MaterialFormatId] = @materialFormatId AND [IsActive] = 1 ORDER BY [Id];";
        private const string ApplyCommand =
            "SELECT TOP (1) [Id], [StaffUserId] FROM [asap].[FormatAutoClaimRule] WITH (UPDLOCK, HOLDLOCK) WHERE [LibraryOrganizationId] = @organizationId AND [MaterialFormatId] = @materialFormatId AND [IsActive] = 1 ORDER BY [Id];";
        private readonly string connectionString;
        private readonly string dataSource;
        private readonly string database;
        private readonly long ruleId;
        private readonly long formatId;
        private readonly long firstStaffId;
        private readonly long secondStaffId;
        private readonly CancellationTokenSource cancellation;
        private readonly string boundary;
        private readonly IDisposable allListenersSubscription;
        private readonly List<IDisposable> listenerSubscriptions = [];
        private readonly List<Exception> failures = [];
        private readonly object gate = new();
        private long currentStaffId;
        private int candidateQueryCount;
        private int applyQueryCount;
        private int ruleUpdateCount;
        private int closedApplyConnectionCount;

        public AutoClaimRetryDiagnosticObserver(
            string connectionString,
            long ruleId,
            long formatId,
            long firstStaffId,
            long secondStaffId,
            CancellationTokenSource cancellation,
            string boundary)
        {
            this.connectionString = connectionString;
            var builder = new SqlConnectionStringBuilder(connectionString);
            dataSource = builder.DataSource;
            database = builder.InitialCatalog;
            this.ruleId = ruleId;
            this.formatId = formatId;
            this.firstStaffId = firstStaffId;
            this.secondStaffId = secondStaffId;
            this.cancellation = cancellation;
            this.boundary = boundary;
            currentStaffId = firstStaffId;
            allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int CandidateQueryCount => Volatile.Read(ref candidateQueryCount);
        public int ApplyQueryCount => Volatile.Read(ref applyQueryCount);
        public int RuleUpdateCount => Volatile.Read(ref ruleUpdateCount);
        public int ClosedApplyConnectionCount => Volatile.Read(ref closedApplyConnectionCount);
        public Exception? Failure
        {
            get
            {
                lock (gate)
                {
                    return failures.FirstOrDefault();
                }
            }
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == ListenerName)
            {
                lock (gate)
                {
                    listenerSubscriptions.Add(listener.Subscribe(this, eventName => eventName == EventName));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> diagnosticEvent)
        {
            if (diagnosticEvent.Key != EventName || diagnosticEvent.Value is not SqlClientCommandAfter completed)
            {
                return;
            }

            try
            {
                HandleCommand(completed.Command);
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => RecordFailure(error);

        public void Dispose()
        {
            allListenersSubscription.Dispose();
            lock (gate)
            {
                foreach (var subscription in listenerSubscriptions)
                {
                    subscription.Dispose();
                }
                listenerSubscriptions.Clear();
            }
        }

        private void HandleCommand(SqlCommand command)
        {
            var connection = command.Connection;
            if (connection is null ||
                !string.Equals(connection.DataSource, dataSource, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(connection.Database, database, StringComparison.OrdinalIgnoreCase) ||
                !HasFixtureParameters(command))
            {
                return;
            }

            var normalized = NormalizeCommand(command.CommandText);
            if (normalized == NormalizeCommand(CandidateCommand))
            {
                if (command.Transaction is not null)
                {
                    RecordFailure(new InvalidOperationException("The candidate query unexpectedly ran in a transaction."));
                    return;
                }

                Interlocked.Increment(ref candidateQueryCount);
                FlipActiveRuleStaff();
                return;
            }

            if (normalized != NormalizeCommand(ApplyCommand))
            {
                return;
            }

            if (command.Transaction is null)
            {
                RecordFailure(new InvalidOperationException("The Apply query did not carry its owning SQL transaction."));
                return;
            }

            var applyNumber = Interlocked.Increment(ref applyQueryCount);
            var cancelAt = boundary switch
            {
                "canceled_first" => 1,
                "canceled_fifth" => 5,
                _ => 0
            };
            if (applyNumber == cancelAt)
            {
                if (connection.State != ConnectionState.Open)
                {
                    RecordFailure(new InvalidOperationException("The selected Apply connection was not open when its query completed."));
                    return;
                }

                connection.StateChange += (_, change) =>
                {
                    if (change.CurrentState == ConnectionState.Closed &&
                        Interlocked.Exchange(ref closedApplyConnectionCount, 1) == 0)
                    {
                        try
                        {
                            cancellation.Cancel();
                        }
                        catch (Exception exception)
                        {
                            RecordFailure(exception);
                        }
                    }
                };
            }
        }

        private bool HasFixtureParameters(SqlCommand command)
        {
            if (!command.Parameters.Contains("@organizationId") ||
                !command.Parameters.Contains("@materialFormatId"))
            {
                return false;
            }

            return Convert.ToInt32(command.Parameters["@organizationId"].Value) == 2 &&
                   Convert.ToInt64(command.Parameters["@materialFormatId"].Value) == formatId;
        }

        private void FlipActiveRuleStaff()
        {
            var expected = Interlocked.Read(ref currentStaffId);
            var replacement = expected == firstStaffId ? secondStaffId : firstStaffId;
            using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = 5
            }.ConnectionString);
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandTimeout = 5;
            update.CommandText =
                "UPDATE [asap].[FormatAutoClaimRule] SET [StaffUserId] = @replacement " +
                "WHERE [Id] = @ruleId AND [StaffUserId] = @expected AND [IsActive] = 1;";
            update.Parameters.AddWithValue("@replacement", replacement);
            update.Parameters.AddWithValue("@ruleId", ruleId);
            update.Parameters.AddWithValue("@expected", expected);
            var affected = update.ExecuteNonQuery();
            if (affected != 1)
            {
                throw new InvalidOperationException($"The fixture rule flip affected {affected} rows; expected exactly one.");
            }

            Interlocked.Exchange(ref currentStaffId, replacement);
            Interlocked.Increment(ref ruleUpdateCount);
        }

        private void RecordFailure(Exception exception)
        {
            lock (gate)
            {
                failures.Add(exception);
            }
        }

        private static string NormalizeCommand(string commandText) =>
            string.Join(' ', commandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
