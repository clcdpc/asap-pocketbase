using System.Data;
using System.Collections.Concurrent;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Patron;

public sealed record PickupMutationReceipt(
    Guid OperationId,
    int? FromBranchId,
    string? FromBranchName,
    int ToBranchId,
    string ToBranchName,
    bool ConfirmedByRead = false);

public sealed class PickupMutationException(
    string code, Guid operationId, bool preferenceChanged, Exception? inner = null)
    : Exception("Pickup reconciliation is required before another preference write.", inner)
{
    public string Code { get; } = code;
    public Guid OperationId { get; } = operationId;
    public bool PickupPreferenceChanged { get; } = preferenceChanged;
}

public sealed class PickupMutationBlockedException(string code = "hold_operation_incomplete") : Exception
{
    public string Code { get; } = code;
}

/// <summary>
/// Journals only pickup preference writes. An uncertain dispatch is never repeated:
/// a retry may accept the desired live value, otherwise staff must reconcile it.
/// No connection or transaction remains open during a provider call.
/// </summary>
public sealed class PickupPreferenceMutationService(
    ExternalConfiguration configuration,
    IPatronProvider provider,
    TimeProvider timeProvider)
{
    private readonly string connectionString = configuration.ConnectionStrings.AsapDatabase!;
    private readonly ConcurrentDictionary<Guid, byte> activeDispatches = new();

    internal Func<CancellationToken, Task>? BeforeIntentValidationForTesting { get; set; }
    internal Func<CancellationToken, Task>? AfterInitialPendingReadForTesting { get; set; }

    public bool IsDispatchActive(Guid id) => activeDispatches.ContainsKey(id);

    public static async Task<AsapDbContext> BindContextAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            context.Database.SetDbConnection(connection, contextOwnsConnection: false);
            await context.Database.UseTransactionAsync(transaction, cancellationToken);
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    public async Task<PickupMutationReceipt?> ChangeAsync(
        PatronSnapshot patron,
        int organizationId,
        PickupBranch selected,
        string? fromBranchName,
        string origin,
        long? requestId,
        long? actorId,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task> validateIntentAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(validateIntentAsync);
        if (patron.PatronId <= 0 || organizationId <= 1 || selected.Id <= 1 || patron.PreferredPickupBranchId is <= 1)
        {
            throw new ArgumentException("A pickup write requires native patron, member-library and branch identities.");
        }

        var barcodeAliases = patron.KnownBarcodeAliases;
        var pending = await ReadPendingAsync(patron, cancellationToken);
        if (pending is null && AfterInitialPendingReadForTesting is { } afterInitialPendingRead)
        {
            await afterInitialPendingRead(cancellationToken);
        }
        if (pending is not null)
        {
            if (!pending.DispatchFinished || pending.PatronId != patron.PatronId || pending.OrganizationId != organizationId ||
                pending.Origin != origin || pending.RequestId != requestId ||
                pending.Receipt.ToBranchId != selected.Id || patron.PreferredPickupBranchId != selected.Id)
            {
                throw new PickupMutationException("pickup_reconciliation_required", pending.Receipt.OperationId,
                    pending.Confirmed);
            }

            if (!pending.Confirmed)
            {
                // Setting a preference has no distinct transaction identity to discover.
                // Observing the requested current value is sufficient to accept that
                // value locally, but is not proof that an uncertain dispatch succeeded.
                await ConfirmAsync(pending.Receipt.OperationId, byRead: true, cancellationToken);
            }
            return pending.Receipt with { ConfirmedByRead = !pending.Confirmed || pending.Receipt.ConfirmedByRead };
        }
        if (patron.PreferredPickupBranchId == selected.Id)
        {
            return null;
        }

        var receipt = new PickupMutationReceipt(Guid.NewGuid(), patron.PreferredPickupBranchId,
            fromBranchName, selected.Id, selected.Label);
        activeDispatches.TryAdd(receipt.OperationId, 0);
        try
        {
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);
                if (!await PatronMutationLock.TryAcquireAsync(
                        connection, transaction, patron.PatronId, barcodeAliases, cancellationToken))
                {
                    throw new PickupMutationBlockedException();
                }
                // Every new-intent authority gate follows the patron app lock, then
                // acquires organization/staff/request rows in the established order.
                if (BeforeIntentValidationForTesting is { } beforeIntentValidation)
                {
                    await beforeIntentValidation(cancellationToken);
                }
                await validateIntentAsync(connection, transaction, cancellationToken);
                // The initial read is only a fast recovery path. Another caller may
                // have committed its intent while this caller waited for the native
                // patron lock, so fence new dispatches against the journal again here.
                var pendingAfterLock = await ReadPendingAsync(
                    connection, transaction, patron, cancellationToken);
                if (pendingAfterLock is not null)
                {
                    throw new PickupMutationException("pickup_reconciliation_required",
                        pendingAfterLock.Receipt.OperationId, pendingAfterLock.Confirmed);
                }
                if (requestId is not null)
                {
                    await using var requestBarrier = new SqlCommand();
                    var aliases = AddBarcodeAliasParameters(requestBarrier, barcodeAliases);
                    requestBarrier.Connection = connection;
                    requestBarrier.Transaction = transaction;
                    requestBarrier.CommandText = $"""
                        SELECT COUNT(*) FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK)
                        WHERE [Id] = @requestId AND [LibraryOrganizationId] = @org
                          AND ([PatronIdSnapshot] = @nativePatronId OR
                               [PatronIdSnapshot] IS NULL AND [Barcode] IN ({aliases}))
                          AND [Status] IN (N'suggestion', N'outstanding_purchase', N'pending_hold');
                        """;
                    Add(requestBarrier, "@requestId", SqlDbType.BigInt, requestId);
                    Add(requestBarrier, "@nativePatronId", SqlDbType.Int, patron.PatronId);
                    Add(requestBarrier, "@org", SqlDbType.Int, organizationId);
                    if (Convert.ToInt32(await requestBarrier.ExecuteScalarAsync(cancellationToken)) != 1)
                    {
                        throw new PickupMutationBlockedException("pickup_read_only");
                    }
                }
                await using (var barrier = new SqlCommand())
                {
                    var aliases = AddBarcodeAliasParameters(barrier, barcodeAliases);
                    barrier.Connection = connection;
                    barrier.Transaction = transaction;
                    barrier.CommandText = $"""
                        SELECT COUNT(*) FROM [asap].[HoldPlacementOperation]
                        WHERE ([PatronIdSnapshot] = @nativePatronId OR [PatronBarcodeSnapshot] IN ({aliases}))
                          AND [CompletedUtc] IS NULL;
                        """;
                    Add(barrier, "@nativePatronId", SqlDbType.Int, patron.PatronId);
                    if (Convert.ToInt32(await barrier.ExecuteScalarAsync(cancellationToken)) != 0)
                    {
                        throw new PickupMutationBlockedException();
                    }
                }
                await using var command = new SqlCommand { Connection = connection, Transaction = transaction };
                command.CommandText = """
                    INSERT INTO [asap].[PickupPreferenceOperation]
                        ([Id], [Barcode], [PatronId], [LibraryOrganizationId], [TitleRequestId], [Origin],
                         [ActorStaffUserId], [FromPickupBranchId], [FromPickupBranchName],
                         [ToPickupBranchId], [ToPickupBranchName], [State], [DispatchStartedUtc], [ConfirmedByRead])
                    VALUES (@id, @barcode, @patronId, @organizationId, @requestId, @origin, @actorId,
                            @fromId, @fromName, @toId, @toName, 1, @now, 0);
                    """;
                Add(command, "@id", SqlDbType.UniqueIdentifier, receipt.OperationId);
                var operationBarcode = requestId is not null && patron.RequestedBarcode is { Length: > 0 } requestedBarcode &&
                    barcodeAliases.Contains(requestedBarcode, StringComparer.OrdinalIgnoreCase)
                    ? requestedBarcode
                    : patron.Barcode;
                Add(command, "@barcode", SqlDbType.NVarChar, operationBarcode, 50);
                Add(command, "@patronId", SqlDbType.Int, patron.PatronId);
                Add(command, "@organizationId", SqlDbType.Int, organizationId);
                Add(command, "@requestId", SqlDbType.BigInt, requestId);
                Add(command, "@origin", SqlDbType.NVarChar, origin, 32);
                Add(command, "@actorId", SqlDbType.BigInt, actorId);
                Add(command, "@fromId", SqlDbType.Int, receipt.FromBranchId);
                Add(command, "@fromName", SqlDbType.NVarChar, receipt.FromBranchName, 256);
                Add(command, "@toId", SqlDbType.Int, receipt.ToBranchId);
                Add(command, "@toName", SqlDbType.NVarChar, receipt.ToBranchName, 256);
                Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (SqlException exception) when (exception.Number is 2601 or 2627)
                {
                    using var conflictResolution = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await transaction.RollbackAsync(conflictResolution.Token);
                    var concurrent = await ReadPendingAsync(patron, conflictResolution.Token);
                    throw new PickupMutationException("pickup_reconciliation_required",
                        concurrent?.Receipt.OperationId ?? receipt.OperationId, concurrent?.Confirmed == true, exception);
                }
            }

            var dispatchFinishedRecorded = false;
            try
            {
                try
                {
                    await provider.UpdatePreferredPickupBranchAsync(patron.Barcode, selected.Id,
                        organizationId, cancellationToken);
                }
                catch (PolarisMutationNotDispatchedException exception)
                {
                    await RecordNoEffectAsync(receipt.OperationId, "local_not_dispatched");
                    dispatchFinishedRecorded = true;
                    exception.RethrowCause();
                    throw;
                }
                catch (PolarisPickupRejectedException exception)
                {
                    await RecordNoEffectAsync(receipt.OperationId,
                        $"documented_rejection_{exception.PapiErrorCode}");
                    dispatchFinishedRecorded = true;
                    throw;
                }
                catch (PolarisOperationalException exception)
                {
                    // A transport/provider failure does not prove the write had no effect.
                    throw new PickupMutationException("pickup_outcome_unconfirmed", receipt.OperationId, false, exception);
                }

                await RecordProviderSuccessAsync(receipt.OperationId);
                dispatchFinishedRecorded = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                if (!dispatchFinishedRecorded)
                {
                    // A separate bounded SQL-only token records an unknown invocation's
                    // end; it never authorizes another external provider mutation.
                    dispatchFinishedRecorded = await RecordDispatchFinishedAsync(receipt.OperationId);
                }
            }
            if (!dispatchFinishedRecorded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new PickupMutationException("pickup_changed_request_not_updated", receipt.OperationId, true);
            }
            return receipt;
        }
        finally
        {
            activeDispatches.TryRemove(receipt.OperationId, out _);
        }
    }

    public static async Task CompleteAsync(
        SqlConnection connection, SqlTransaction transaction,
        PickupMutationReceipt? receipt, long requestId, int nativePatronId, DateTime now,
        CancellationToken cancellationToken)
    {
        if (receipt is null)
        {
            return;
        }
        if (nativePatronId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativePatronId));
        }
        await using var command = new SqlCommand("""
            UPDATE [asap].[PickupPreferenceOperation]
            SET [State] = 3, [CompletedUtc] = @now, [TitleRequestId] = @requestId
            WHERE [Id] = @id AND [State] = 2
              AND [PatronId] = @nativePatronId
              AND ([TitleRequestId] IS NULL OR [TitleRequestId] = @requestId);
            """, connection, transaction);
        Add(command, "@id", SqlDbType.UniqueIdentifier, receipt.OperationId);
        Add(command, "@requestId", SqlDbType.BigInt, requestId);
        Add(command, "@nativePatronId", SqlDbType.Int, nativePatronId);
        Add(command, "@now", SqlDbType.DateTime2, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PickupMutationException("pickup_reconciliation_required", receipt.OperationId, true);
        }
    }

    public static async Task<bool> HasIncompleteAsync(
        AsapDbContext context, long requestId, CancellationToken cancellationToken) =>
        await context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value] FROM [asap].[PickupPreferenceOperation]
            WHERE [TitleRequestId] = {requestId} AND [CompletedUtc] IS NULL
            """).SingleAsync(cancellationToken) != 0;

    private async Task ConfirmAsync(Guid id, bool byRead, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [asap].[PickupPreferenceOperation]
            SET [State] = 2, [ProviderConfirmedUtc] = @now, [ConfirmedByRead] = @byRead,
                [ObservedPickupBranchId] = [ToPickupBranchId]
            WHERE [Id] = @id AND [State] = 1 AND [CompletedUtc] IS NULL
              AND [ProviderConfirmedUtc] IS NULL AND [DispatchFinishedUtc] IS NOT NULL;
            """;
        Add(command, "@id", SqlDbType.UniqueIdentifier, id);
        Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
        Add(command, "@byRead", SqlDbType.Bit, byRead);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PickupMutationException("pickup_reconciliation_required", id, false);
        }
    }

    private async Task RecordProviderSuccessAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new SqlCommand("""
                UPDATE [asap].[PickupPreferenceOperation]
                SET [State] = 2, [ProviderConfirmedUtc] = @now, [ConfirmedByRead] = 0,
                    [ObservedPickupBranchId] = [ToPickupBranchId], [DispatchFinishedUtc] = @now
                WHERE [Id] = @id AND [State] = 1 AND [CompletedUtc] IS NULL
                  AND [ProviderConfirmedUtc] IS NULL AND [DispatchFinishedUtc] IS NULL;
                """, connection);
            Add(command, "@id", SqlDbType.UniqueIdentifier, id);
            Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            if (await command.ExecuteNonQueryAsync(timeout.Token) != 1)
            {
                throw new PickupMutationException("pickup_reconciliation_required", id, true);
            }
        }
        catch (PickupMutationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            throw new PickupMutationException("pickup_changed_request_not_updated", id, true, exception);
        }
    }

    private async Task RecordNoEffectAsync(Guid id, string failureCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = new SqlCommand("""
                UPDATE [asap].[PickupPreferenceOperation]
                SET [State] = 4, [FailureCode] = @code, [CompletedUtc] = @now, [DispatchFinishedUtc] = @now
                WHERE [Id] = @id AND [State] = 1 AND [CompletedUtc] IS NULL
                  AND [ProviderConfirmedUtc] IS NULL AND [DispatchFinishedUtc] IS NULL;
                """, connection);
            Add(command, "@id", SqlDbType.UniqueIdentifier, id);
            Add(command, "@code", SqlDbType.NVarChar, failureCode, 80);
            Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            if (await command.ExecuteNonQueryAsync(timeout.Token) != 1)
            {
                throw new PickupMutationException("pickup_reconciliation_required", id, false);
            }
        }
        catch (PickupMutationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            throw new PickupMutationException("pickup_reconciliation_required", id, false, exception);
        }
    }

    private async Task<bool> RecordDispatchFinishedAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE [asap].[PickupPreferenceOperation] SET [DispatchFinishedUtc] = @now
                WHERE [Id] = @id AND [State] = 1 AND [CompletedUtc] IS NULL AND [DispatchFinishedUtc] IS NULL;
                """;
            Add(command, "@id", SqlDbType.UniqueIdentifier, id);
            Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            return await command.ExecuteNonQueryAsync(timeout.Token) == 1;
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            // Pre-dispatch intent survives; missing finish evidence deliberately
            // requires operator inspection instead of enabling another write.
            return false;
        }
    }

    private async Task<PendingPickup?> ReadPendingAsync(PatronSnapshot patron, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand();
        var aliases = AddBarcodeAliasParameters(command, patron.KnownBarcodeAliases);
        command.Connection = connection;
        command.CommandText = $"""
            SELECT [Id], [PatronId], [LibraryOrganizationId], [TitleRequestId], [Origin],
                   [FromPickupBranchId], [FromPickupBranchName], [ToPickupBranchId], [ToPickupBranchName], [State], [ConfirmedByRead], [DispatchFinishedUtc]
            FROM [asap].[PickupPreferenceOperation]
            WHERE ([PatronId] = @nativePatronId OR [Barcode] IN ({aliases})) AND [CompletedUtc] IS NULL;
            """;
        Add(command, "@nativePatronId", SqlDbType.Int, patron.PatronId);
        return await ReadPendingAsync(command, cancellationToken);
    }

    private async Task<PendingPickup?> ReadPendingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand();
        var aliases = AddBarcodeAliasParameters(command, patron.KnownBarcodeAliases);
        command.Connection = connection;
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT [Id], [PatronId], [LibraryOrganizationId], [TitleRequestId], [Origin],
                   [FromPickupBranchId], [FromPickupBranchName], [ToPickupBranchId], [ToPickupBranchName], [State], [ConfirmedByRead], [DispatchFinishedUtc]
            FROM [asap].[PickupPreferenceOperation] WITH (UPDLOCK, HOLDLOCK)
            WHERE ([PatronId] = @nativePatronId OR [Barcode] IN ({aliases})) AND [CompletedUtc] IS NULL;
            """;
        Add(command, "@nativePatronId", SqlDbType.Int, patron.PatronId);
        return await ReadPendingAsync(command, cancellationToken);
    }

    private static async Task<PendingPickup?> ReadPendingAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var pending = new PendingPickup(new PickupMutationReceipt(reader.GetGuid(0),
                reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7), reader.GetString(8), reader.GetBoolean(10)), reader.GetInt32(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4), reader.GetInt32(9) == 2, !reader.IsDBNull(11));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new PickupMutationException("pickup_identity_ambiguous", pending.Receipt.OperationId, false);
        }

        return pending;
    }

    private static string AddBarcodeAliasParameters(SqlCommand command, IReadOnlyList<string> aliases)
    {
        if (aliases.Count is < 1 or > 3)
        {
            throw new ArgumentException("A native patron identity requires one to three verified barcode aliases.", nameof(aliases));
        }

        var names = new string[aliases.Count];
        for (var index = 0; index < aliases.Count; index++)
        {
            names[index] = $"@barcodeAlias{index}";
            Add(command, names[index], SqlDbType.NVarChar, aliases[index], 50);
        }

        return string.Join(", ", names);
    }

    private static void Add(SqlCommand command, string name, SqlDbType type, object? value, int size = 0)
    {
        var parameter = size == 0 ? command.Parameters.Add(name, type) : command.Parameters.Add(name, type, size);
        parameter.Value = value ?? DBNull.Value;
    }

    private sealed record PendingPickup(PickupMutationReceipt Receipt, int PatronId, int OrganizationId,
        long? RequestId, string Origin, bool Confirmed, bool DispatchFinished);
}
