using System.Data;
using System.Globalization;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Features.Email;

// Request mail only: the outbox owns intent and delivery events own immutable
// local transitions. ProviderEventId remains reserved for actual provider evidence.
internal static class RequestEmailEvidence
{
    internal static readonly IReadOnlyDictionary<string, string> MessageKinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["confirmation"] = "Patron confirmation",
        ["assignment"] = "Staff assignment notification",
        ["purchase_reminder"] = "Purchase reminder",
        ["purchase_approved"] = "Purchase approved notification",
        ["already_owned"] = "Already owned notification",
        ["rejection"] = "Patron rejection notification",
        ["hold_placed"] = "Hold placed notification",
        ["timeout"] = "Request timeout notification",
        ["additional_copy_reminder"] = "Additional-copy reminder",
        ["additional_copy_assignment"] = "Additional-copy assignment notification",
        ["new_patron_request"] = "New patron purchase request notification"
    };

    internal sealed record LegacyIdentity(long Id, bool IsCopy, string MessageKind);

    internal static LegacyIdentity? ParseLegacyKey(string? key)
    {
        var parts = key?.Split(':');
        if (parts is null)
        {
            return null;
        }
        var idIndex = parts[0] is "staff-patron-action" or "timeout" ? 2 : 1;
        if (parts.Length <= idIndex || !CanonicalId(parts[idIndex], out var id))
        {
            return null;
        }
        var kind = parts[0] switch
        {
            "patron-submission" when parts.Length == 2 => "confirmation",
            "title-assignment" when parts.Length == 4 && Version(parts[2]) && CanonicalId(parts[3], out _) => "assignment",
            "purchase-reminder" when parts.Length == 3 && Version(parts[2]) => "purchase_reminder",
            "staff-patron-action" when parts.Length == 4 && parts[1] is "purchase_approved" or "already_owned" && Version(parts[3]) => parts[1],
            "rejection" when parts.Length == 3 && Version(parts[2]) => "rejection",
            "title-hold-placed" when parts.Length == 3 && CanonicalId(parts[2], out var attempt) && attempt <= int.MaxValue => "hold_placed",
            "timeout" when parts.Length == 3 && parts[1] == "OutstandingTimeout" => "timeout",
            "additional-copy-reminder" when parts.Length == 2 => "additional_copy_reminder",
            "additional-copy-assignment" when parts.Length == 4 && Version(parts[2]) && CanonicalId(parts[3], out _) => "additional_copy_assignment",
            _ => null
        };
        return kind is null ? null : new LegacyIdentity(id, kind.StartsWith("additional_copy_", StringComparison.Ordinal), kind);
    }

    private static bool CanonicalId(string value, out long id) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 &&
        id.ToString(CultureInfo.InvariantCulture) == value;

    private static bool Version(string value) =>
        value.Length == 16 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static void AddInitial(AsapDbContext context, EmailOutbox outbox)
    {
        if (outbox.MessageKind is null)
        {
            return;
        }
        context.EmailDeliveryEvents.Add(new EmailDeliveryEvent
        {
            Outbox = outbox,
            LocalEventKey = "initial",
            EventType = outbox.Status == "suppressed" ? "suppressed" : "queued",
            ReceivedUtc = outbox.SuppressedUtc ?? outbox.CreatedUtc,
            TimestampKind = "transition",
            ReasonCode = outbox.SuppressionReason
        });
    }

    internal static Task AddInitialAsync(SqlConnection connection, SqlTransaction transaction, long id,
        string status, string? reason, DateTime now, CancellationToken cancellationToken) =>
        AppendAsync(connection, transaction, id, "initial", status == "suppressed" ? "suppressed" : "queued",
            reason, null, now, cancellationToken);

    // Called under the same row lock and transaction as the subsequent fenced
    // update. A zero-row update rolls this back too. Old snapshots are summaries,
    // never an invented reconstruction of AttemptCount historical attempts.
    internal static async Task<bool> PreserveSnapshotAsync(SqlConnection connection, SqlTransaction transaction,
        long id, CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand("""
            SELECT [OrganizationId], [BusinessKey], [MessageKind], [DeliveryClass], [RecipientAddressKind],
                   [Status], [CreatedUtc], [SentUtc], [SuppressedUtc], [LastAttemptUtc],
                   [LastErrorCode], [SuppressionReason], [AttemptDeliveryMode], [ProviderMessageId]
            FROM [asap].[EmailOutbox] WITH (UPDLOCK, ROWLOCK)
            WHERE [Id] = @id;
            """, connection, transaction);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        string? Text(SqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        int organizationId;
        string? businessKey;
        string? kind;
        string status;
        string? reason;
        string? mode;
        DateTime timestamp;
        string timestampKind;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(3) == "operational_test" ||
                Text(reader, 4)?.StartsWith("weekly_summary", StringComparison.Ordinal) == true)
            {
                return false;
            }
            organizationId = reader.GetInt32(0);
            businessKey = Text(reader, 1);
            kind = Text(reader, 2);
            status = reader.GetString(5);
            var timeIndex = status == "sent" && !reader.IsDBNull(7) ? 7 :
                status == "suppressed" && !reader.IsDBNull(8) ? 8 : !reader.IsDBNull(9) ? 9 : 6;
            timestamp = reader.GetDateTime(timeIndex);
            timestampKind = timeIndex switch { 7 => "sent", 8 => "suppressed", 9 => "last_attempt", _ => "created" };
            reason = Text(reader, status == "suppressed" ? 11 : 10);
            mode = Text(reader, 12) ?? (Text(reader, 13)?.StartsWith("file:", StringComparison.Ordinal) == true ? "file" : null);
        }
        if (kind is not null && !MessageKinds.ContainsKey(kind))
        {
            return false;
        }
        if (kind is null)
        {
            var identity = ParseLegacyKey(businessKey);
            if (identity is null)
            {
                return false;
            }
            await using var verify = new SqlCommand(identity.IsCopy ? """
                SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] AS copy
                INNER JOIN [asap].[TitleRequest] AS request ON request.[Id] = copy.[SourceTitleRequestId]
                WHERE copy.[Id] = @owner AND copy.[LibraryOrganizationId] = @organization
                  AND request.[LibraryOrganizationId] = @organization;
                """ : """
                SELECT COUNT(*) FROM [asap].[TitleRequest]
                WHERE [Id] = @owner AND [LibraryOrganizationId] = @organization;
                """, connection, transaction);
            verify.Parameters.Add("@owner", SqlDbType.BigInt).Value = identity.Id;
            verify.Parameters.Add("@organization", SqlDbType.Int).Value = organizationId;
            if (Convert.ToInt32(await verify.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                return false;
            }
        }
        await using var save = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] = @id AND [LocalEventKey] IS NOT NULL)
                INSERT INTO [asap].[EmailDeliveryEvent]
                    ([EmailOutboxId], [LocalEventKey], [EventType], [ReceivedUtc], [ReasonCode], [DeliveryMode], [TimestampKind])
                VALUES (@id, N'recorded-summary', @type, @time, @reason, @mode, @timeKind);
            """, connection, transaction);
        save.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        save.Parameters.Add("@type", SqlDbType.NVarChar, 100).Value = $"recorded_{status}";
        save.Parameters.Add("@time", SqlDbType.DateTime2).Value = timestamp;
        save.Parameters.Add("@reason", SqlDbType.NVarChar, 100).Value = (object?)reason ?? DBNull.Value;
        save.Parameters.Add("@mode", SqlDbType.NVarChar, 32).Value = (object?)mode ?? DBNull.Value;
        save.Parameters.Add("@timeKind", SqlDbType.NVarChar, 32).Value = timestampKind;
        await save.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    internal static async Task AppendAsync(SqlConnection connection, SqlTransaction transaction, long id,
        string key, string eventType, string? reason, string? mode, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO [asap].[EmailDeliveryEvent]
                ([EmailOutboxId], [LocalEventKey], [EventType], [ReceivedUtc], [ReasonCode], [DeliveryMode], [TimestampKind])
            VALUES (@id, @key, @type, @now, @reason, @mode, N'transition');
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@key", SqlDbType.NVarChar, 100).Value = key;
        command.Parameters.Add("@type", SqlDbType.NVarChar, 100).Value = eventType;
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = now;
        command.Parameters.Add("@reason", SqlDbType.NVarChar, 100).Value = (object?)reason ?? DBNull.Value;
        command.Parameters.Add("@mode", SqlDbType.NVarChar, 32).Value = (object?)mode ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
