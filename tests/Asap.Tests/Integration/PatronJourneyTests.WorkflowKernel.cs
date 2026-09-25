using System.Data;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Asap.Web.Infrastructure.Security;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task BackgroundHoldAcquisitionRechecksQueueFenceAfterOrganizationLock()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));

        var (requestId, requestVersion) = await SeedPendingHoldRequestAsync("queue-fence-race");
        var expectedProgressVersion = await ResetAndReadQueueVersionAsync(QueueNames.HoldPlacement, 2);

        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        var blockerSessionId = Convert.ToInt32(await ExecuteScalarAsync(blocker, "SELECT @@SPID;"));
        await using var blockerTransaction = (SqlTransaction)await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using (var lockOrganization = new SqlCommand(
                   "SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = 2;",
                   blocker,
                   blockerTransaction))
        {
            Assert.AreEqual(2, Convert.ToInt32(await lockOrganization.ExecuteScalarAsync()));
        }

        var placement = holdFactory.Services.GetRequiredService<HoldPlacementService>();
        var acquisition = placement.PlaceBackgroundAsync(
            requestId,
            requestVersion,
            QueueNames.HoldPlacement,
            2,
            expectedProgressVersion,
            CancellationToken.None);
        await WaitForBlockedSessionAsync(blockerSessionId);

        var changedProgressVersion = await AdvanceQueueVersionAsync(QueueNames.HoldPlacement, 2);
        CollectionAssert.AreNotEqual(expectedProgressVersion, changedProgressVersion);

        await blockerTransaction.CommitAsync();
        var result = await acquisition.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("stale_progress_fence", result.Code);
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(0, await CountForRequestAsync(
            "[asap].[HoldPlacementOperation]",
            "[TitleRequestId]",
            requestId));
        Assert.AreEqual("pending_hold", await ReadStringAsync(
            "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
            "@id",
            requestId));
    }

    [TestMethod]
    public async Task ReopenedLegacyProtectedPendingIdentifierCannotApplyConflictingLookup()
    {
        var provider = new ConflictingIdentifierProvider();
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        var service = new PatronSuggestionService(
            configuration,
            factory!.Services.GetRequiredService<PatronConfigurationService>(),
            provider,
            dispatcher!,
            new RecordingEmailSender(),
            new RecipientDomainPolicy(configuration),
            timeProvider!,
            NullLogger<PatronSuggestionService>.Instance,
            factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            factory.Services.GetRequiredService<StaffEligibilityService>());

        var identifier = $"978{Random.Shared.NextInt64(1000000000, 9999999999)}";
        long requestId;
        byte[] requestVersion;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                     [Status], [CloseReason], [BibId], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, @barcode, N'Reopened protected title', @identifier, 1, @formatId,
                        N'closed', N'hold_cancelled', NULL, N'not_found', DATEADD(day, -2, SYSUTCDATETIME()),
                        DATEADD(day, -1, SYSUTCDATETIME()));
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [ActorType], [Message], [MetadataJson], [CreatedUtc])
                VALUES (@requestId, N'legacy_status_imported', N'closed', N'system', N'Imported placement evidence.',
                        N'{"legacyBibProtection":true,"legacyBibId":null}', DATEADD(day, -1, SYSUTCDATETIME()));
                UPDATE [asap].[TitleRequest]
                SET [Status] = N'suggestion', [CloseReason] = NULL, [IsbnCheckStatus] = N'pending',
                    [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [Id] = @requestId;
                SELECT @requestId, [RowVersion]
                FROM [asap].[TitleRequest]
                WHERE [Id] = @requestId;
                """;
            seed.Parameters.AddWithValue("@barcode", $"2000000000{Random.Shared.Next(100000, 999999)}");
            seed.Parameters.AddWithValue("@identifier", identifier);
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            requestId = reader.GetInt64(0);
            requestVersion = (byte[])reader[1];
        }

        try
        {
            var outcome = await service.ProcessIdentifierLookupAsync(
                requestId,
                identifier,
                2,
                requestVersion,
                CancellationToken.None);

            Assert.AreEqual(IdentifierLookupOutcome.Found, outcome);
            Assert.AreEqual(1, provider.LookupCount);
            Assert.AreEqual("suggestion", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                requestId));
            Assert.AreEqual("pending", await ReadStringAsync(
                "SELECT [IsbnCheckStatus] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                requestId));
            Assert.IsNull(await ReadNullableStringAsync(
                "SELECT [BibId] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                requestId));
            Assert.AreEqual(identifier, await ReadStringAsync(
                "SELECT [Identifier] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                requestId));
        }
        finally
        {
            await DeleteRequestAsync(requestId);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FulfillmentClosesExactTerminalHoldDespiteHistoricalSameBibInEitherOrder(bool historicalFirst)
    {
        var requestBarcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Holds = historicalFirst
                ? [
                    new PolarisHoldSnapshot(100, 9902, 3, "Expired", 101),
                    new PolarisHoldSnapshot(200, 9902, 3, "Expired", 101)
                ]
                : [
                    new PolarisHoldSnapshot(200, 9902, 3, "Expired", 101),
                    new PolarisHoldSnapshot(100, 9902, 3, "Expired", 101)
                ]
        };
        await using var evidenceFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            $"fulfillment-historical-{historicalFirst}",
            requestBarcode,
            "9902",
            holdRequestId: "200");
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, 2, seeded.RequestId);
        try
        {
            var result = await evidenceFactory.Services
                .GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);

            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("closed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                seeded.RequestId));
            Assert.AreEqual(1, provider.HoldReadBarcodes.Count(item => item == requestBarcode));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    [TestMethod]
    public async Task FulfillmentKeepsTrackedHoldOpenWhenHistoricalSameBibHoldIsTerminal()
    {
        var requestBarcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Holds = [
                new PolarisHoldSnapshot(200, 9903, 1, "Active", 101),
                new PolarisHoldSnapshot(100, 9903, 3, "Expired", 101)
            ]
        };
        await using var evidenceFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-active-tracked",
            requestBarcode,
            "9903",
            holdRequestId: "200");
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, 2, seeded.RequestId);
        try
        {
            var result = await evidenceFactory.Services
                .GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);

            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                seeded.RequestId));
            Assert.IsNull(await ReadNullableStringAsync(
                "SELECT [LastErrorCode] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;",
                "@id",
                seeded.OperationId));
            Assert.AreEqual(1, provider.HoldReadBarcodes.Count(item => item == requestBarcode));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    [TestMethod]
    public async Task FulfillmentPreservesExactTrackedIdConflict()
    {
        var requestBarcode = $"2000000000{Random.Shared.Next(100000, 999999)}";
        var provider = new FulfillmentEvidenceProvider
        {
            Holds = [
                new PolarisHoldSnapshot(200, 9904, 3, "Expired", 101),
                new PolarisHoldSnapshot(200, 9904, 1, "Active", 101)
            ]
        };
        await using var evidenceFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var seeded = await SeedCompletedHoldIdentityAsync(
            "fulfillment-exact-id-conflict",
            requestBarcode,
            "9904",
            holdRequestId: "200");
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, 2, seeded.RequestId);
        try
        {
            var result = await evidenceFactory.Services
                .GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);

            Assert.AreEqual("completed", result.Code);
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                seeded.RequestId));
            Assert.AreEqual("hold_identity_ambiguous", await ReadStringAsync(
                "SELECT [LastErrorCode] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;",
                "@id",
                seeded.OperationId));
        }
        finally
        {
            await DeleteRequestAsync(seeded.RequestId);
        }
    }

    [TestMethod]
    public async Task WorkflowStopsAfterOperationalFailureBeforeLaterFulfillmentPhase()
    {
        var provider = new FulfillmentEvidenceProvider
        {
            HoldReadException = new PolarisOperationalException(
                "testing_workflow_provider_failure",
                "Synthetic provider read failure.")
        };
        await using var workflowFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));

        var pending = await SeedPendingHoldRequestAsync("workflow-phase-stop");
        var placed = await SeedCompletedHoldIdentityAsync(
            "workflow-phase-stop-later",
            $"2000000000{Random.Shared.Next(100000, 999999)}",
            "9910",
            holdRequestId: "300");
        await PrepareSingleItemCycleAsync(QueueNames.HoldPlacement, 2, pending.RequestId);
        await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, 2, placed.RequestId);
        try
        {
            var result = await workflowFactory.Services
                .GetRequiredService<WorkflowProcessingService>()
                .ProcessWorkflowAsync(2, CancellationToken.None);

            Assert.AreEqual("operational_failure", result.Code);
            Assert.AreEqual("pending_hold", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                pending.RequestId));
            Assert.AreEqual("hold_placed", await ReadStringAsync(
                "SELECT [Status] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                "@id",
                placed.RequestId));
            Assert.AreEqual(1, provider.HoldReadCount);
            Assert.AreEqual(0, provider.CheckoutReadCount,
                "The later fulfillment phase must not run after an operational failure.");
        }
        finally
        {
            await DeleteRequestAsync(pending.RequestId);
            await DeleteRequestAsync(placed.RequestId);
        }
    }

    private static async Task PrepareSingleItemCycleAsync(string queueName, int scope, long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF NOT EXISTS
            (
                SELECT 1 FROM [asap].[QueueProgress]
                WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope
            )
            BEGIN
                INSERT INTO [asap].[QueueProgress] ([QueueName], [ScopeOrganizationId], [UpdatedUtc])
                VALUES (@queueName, @scope, SYSUTCDATETIME());
            END;
            DECLARE @candidateUtc datetime2(7) = DATEADD(second, -1, SYSUTCDATETIME());
            UPDATE [asap].[TitleRequest]
            SET [CreatedUtc] = @candidateUtc,
                [UpdatedUtc] = @candidateUtc
            WHERE [Id] = @requestId;
            UPDATE [asap].[QueueProgress]
            SET [CycleMaxId] = @requestId,
                [LastCreatedUtc] = DATEADD(nanosecond, -100,
                    (SELECT [CreatedUtc] FROM [asap].[TitleRequest] WHERE [Id] = @requestId)),
                [LastItemId] = 0,
                [LastOutcomeItemId] = NULL,
                [LastOutcomeCode] = N'test_cycle_started',
                [LastOutcomeUtc] = SYSUTCDATETIME(),
                [UpdatedUtc] = SYSUTCDATETIME()
            WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope;
            """;
        command.Parameters.AddWithValue("@queueName", queueName);
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@requestId", requestId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(long RequestId, byte[] RowVersion)> SeedPendingHoldRequestAsync(string key, int scope = 2)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [CreatedUtc], [UpdatedUtc])
            VALUES (@scope, @barcode, @title, 1, @formatId, N'pending_hold', N'99001',
                    DATEADD(day, -1, SYSUTCDATETIME()), SYSUTCDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS bigint), [RowVersion]
            FROM [asap].[TitleRequest]
            WHERE [Id] = SCOPE_IDENTITY();
            """;
        command.Parameters.AddWithValue("@barcode", $"2000000000{Random.Shared.Next(100000, 999999)}");
        command.Parameters.AddWithValue("@title", $"Pending hold {key} {Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@scope", scope);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return (reader.GetInt64(0), (byte[])reader[1]);
    }

    private static async Task<byte[]> ResetAndReadQueueVersionAsync(string queueName, int scope)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF NOT EXISTS
            (
                SELECT 1 FROM [asap].[QueueProgress]
                WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope
            )
            BEGIN
                INSERT INTO [asap].[QueueProgress] ([QueueName], [ScopeOrganizationId], [UpdatedUtc])
                VALUES (@queueName, @scope, SYSUTCDATETIME());
            END;
            UPDATE [asap].[QueueProgress]
            SET [CycleMaxId] = NULL, [LastCreatedUtc] = NULL, [LastItemId] = NULL,
                [LastOutcomeItemId] = NULL, [LastOutcomeCode] = NULL, [LastOutcomeUtc] = NULL,
                [UpdatedUtc] = SYSUTCDATETIME()
            WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope;
            SELECT [RowVersion]
            FROM [asap].[QueueProgress]
            WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope;
            """;
        command.Parameters.AddWithValue("@queueName", queueName);
        command.Parameters.AddWithValue("@scope", scope);
        return await command.ExecuteScalarAsync() as byte[]
            ?? throw new AssertFailedException("Queue progress rowversion was not returned.");
    }

    private static async Task<byte[]> AdvanceQueueVersionAsync(string queueName, int scope)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE [asap].[QueueProgress]
            SET [LastOutcomeCode] = N'race_fence_changed', [UpdatedUtc] = SYSUTCDATETIME()
            WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope;
            SELECT [RowVersion]
            FROM [asap].[QueueProgress]
            WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope;
            """;
        command.Parameters.AddWithValue("@queueName", queueName);
        command.Parameters.AddWithValue("@scope", scope);
        return await command.ExecuteScalarAsync() as byte[]
            ?? throw new AssertFailedException("Queue progress rowversion was not returned.");
    }

    private static async Task WaitForBlockedSessionAsync(int blockerSessionId)
    {
        await using var observer = new SqlConnection(databaseConnectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = observer.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND blocking_session_id = @spid;";
            command.Parameters.AddWithValue("@spid", blockerSessionId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(25);
        }

        Assert.Fail($"No SQL request became blocked by session {blockerSessionId}.");
    }

    private static async Task<int> CountForRequestAsync(string table, string column, long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @id;";
        command.Parameters.AddWithValue("@id", requestId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<object?> ExecuteScalarAsync(SqlConnection connection, string commandText)
    {
        await using var command = new SqlCommand(commandText, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string> ReadStringAsync(string commandText, string parameterName, long parameterValue)
    {
        var value = await ReadNullableStringAsync(commandText, parameterName, parameterValue);
        return value ?? throw new AssertFailedException("Expected a non-null string value.");
    }

    private static async Task<string?> ReadNullableStringAsync(
        string commandText,
        string parameterName,
        long parameterValue)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(commandText, connection);
        command.Parameters.AddWithValue(parameterName, parameterValue);
        return await command.ExecuteScalarAsync() is { } value && value is not DBNull
            ? (string)value
            : null;
    }

    private static async Task DeleteRequestAsync(long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id;
            DELETE FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] = @id;
            DELETE FROM [asap].[HoldPlacementOperation] WHERE [TitleRequestId] = @id;
            DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;
            """;
        command.Parameters.AddWithValue("@id", requestId);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ConflictingIdentifierProvider : IPatronProvider
    {
        public int LookupCount { get; private set; }

        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PatronSnapshot> RefreshAsync(
            string barcode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            return Task.FromResult(new IdentifierLookupResult(
                IdentifierLookupOutcome.Found,
                BibId: "9999",
                CatalogTitle: "Conflicting catalog title"));
        }
    }

    private sealed class FulfillmentEvidenceProvider : IStaffPolarisProvider
    {
        public IReadOnlyList<PolarisHoldSnapshot> Holds { get; set; } = [];
        public IReadOnlyList<PolarisCheckoutSnapshot> Checkouts { get; set; } = [];
        public Exception? HoldReadException { get; set; }
        public Exception? CheckoutReadException { get; set; }
        public List<string> HoldReadBarcodes { get; } = [];
        public int HoldReadCount { get; private set; }
        public int CheckoutReadCount { get; private set; }
        public TaskCompletionSource CheckoutReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<PolarisCheckoutSnapshot>>? PendingCheckoutRead { get; private set; }

        public void BlockCheckoutRead() => PendingCheckoutRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBlockedCheckout(IReadOnlyList<PolarisCheckoutSnapshot> checkouts) =>
            PendingCheckoutRead!.TrySetResult(checkouts);

        public Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken) =>
            Task.FromResult(new BibValidationResult(true));

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HoldReadCount++;
            HoldReadBarcodes.Add(barcode);
            if (HoldReadException is not null) throw HoldReadException;
            return Task.FromResult(Holds);
        }

        public Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(
            string barcode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckoutReadCount++;
            if (CheckoutReadException is not null) throw CheckoutReadException;
            if (PendingCheckoutRead is not null)
            {
                CheckoutReadStarted.TrySetResult();
                return PendingCheckoutRead.Task;
            }
            return Task.FromResult(Checkouts);
        }

        public Task<HoldProviderResult> CreateHoldAsync(
            HoldCreateCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HoldProviderResult> ReplyToHoldAsync(
            HoldReplyCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
