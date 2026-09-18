using System.Data;
using Hangfire;
using Microsoft.Data.SqlClient;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Security;

namespace Asap.Web.Features.Email;

public sealed record EmailOutboxRuntimeOptions(
    TimeSpan ProviderStartDeadline,
    TimeSpan ProviderCallTimeout,
    TimeSpan LeaseDuration,
    int MaxAttempts)
{
    public static EmailOutboxRuntimeOptions Default { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), 5);
}

internal sealed record ClaimedEmail(
    long Id,
    int OrganizationId,
    string? BusinessKey,
    string DeliveryClass,
    string? DeliveryMode,
    long? RecipientStaffUserId,
    string? RecipientAuthenticationEmail,
    int? AuthorizationOrganizationId,
    string? RecipientAddressKind,
    string ToAddress,
    string FromAddress,
    string? FromName,
    string Subject,
    string? BodyText,
    string? BodyHtml,
    int AttemptCount,
    DateTime SendingStartedUtc,
    Guid LeaseId,
    byte[] RowVersion);

public sealed class EmailOutboxJobs(
    ExternalConfiguration configuration,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailOutboxDispatcher dispatcher,
    EmailOutboxRuntimeOptions runtimeOptions,
    ILogger<EmailOutboxJobs> logger)
{
    private readonly string connectionString = configuration.ConnectionStrings.AsapDatabase!;
    [AutomaticRetry(Attempts = 0)]
    [Queue("asap-email")]
    public async Task DeliverAsync(long outboxId, CancellationToken cancellationToken)
    {
        var claim = await TryClaimAsync(outboxId, cancellationToken);
        if (claim is null)
        {
            return;
        }

        var authorizationError = await ValidateCurrentRecipientAsync(claim, cancellationToken);
        if (authorizationError is not null)
        {
            await SuppressAsync(claim, authorizationError, cancellationToken);
            return;
        }

        if (!recipientDomainPolicy.IsAllowed(claim.ToAddress))
        {
            await SuppressAsync(claim, "recipient_domain_not_allowed", cancellationToken);
            return;
        }

        var readiness = await emailSender.CheckReadinessAsync(
            claim.OrganizationId,
            cancellationToken);
        if (!readiness.IsConfigured)
        {
            await FailNotConfiguredAsync(claim, readiness.Code ?? "mail_not_configured", cancellationToken);
            return;
        }

        if (claim.DeliveryMode is not null &&
            !string.Equals(claim.DeliveryMode, readiness.DeliveryMode, StringComparison.OrdinalIgnoreCase))
        {
            await FailNotConfiguredAsync(claim, "mail_transport_mode_changed", cancellationToken);
            return;
        }

        if (!await StillOwnsUnexpiredLeaseAsync(claim, cancellationToken) ||
            DateTime.UtcNow - claim.SendingStartedUtc > runtimeOptions.ProviderStartDeadline)
        {
            await RecordAmbiguousFailureAsync(
                claim,
                "provider_start_deadline_exceeded",
                "Transport did not start within the post-claim deadline.",
                cancellationToken);
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(runtimeOptions.ProviderCallTimeout);
            var result = await emailSender.SendAsync(
                new EmailEnvelope(
                    claim.Id,
                    claim.OrganizationId,
                    claim.BusinessKey,
                    claim.ToAddress,
                    claim.FromAddress,
                    claim.FromName,
                    claim.Subject,
                    claim.BodyText,
                    claim.BodyHtml),
                timeout.Token);
            if (result.Outcome == EmailSendOutcome.NotConfigured)
            {
                await FailNotConfiguredAsync(claim, result.ErrorCode ?? "mail_not_configured", cancellationToken);
                return;
            }
            if (result.Outcome == EmailSendOutcome.Rejected)
            {
                await FailProviderAsync(
                    claim,
                    result.ErrorCode ?? "provider_rejected",
                    cancellationToken);
                return;
            }
            if (string.IsNullOrWhiteSpace(result.ProviderMessageId))
            {
                throw new InvalidOperationException("A successful email transport result requires a provider message ID.");
            }
            await CompleteAsync(
                claim,
                result.ProviderMessageId,
                result.DeliveryMode ?? readiness.DeliveryMode,
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RecordAmbiguousFailureAsync(
                claim,
                "provider_timeout",
                exception.GetType().Name,
                cancellationToken);
        }
        catch (EmailTransportException exception) when (!exception.IsAmbiguous)
        {
            logger.LogWarning(
                "Email outbox {OutboxId} was rejected by the configured provider with code {ErrorCode}.",
                claim.Id,
                exception.Code);
            await FailProviderAsync(claim, exception.Code, cancellationToken);
        }
        catch (EmailTransportException exception)
        {
            logger.LogWarning(
                "Email outbox {OutboxId} has an ambiguous provider outcome with code {ErrorCode}.",
                claim.Id,
                exception.Code);
            await RecordAmbiguousFailureAsync(
                claim,
                exception.Code,
                exception.Message,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Email outbox {OutboxId} delivery failed.", claim.Id);
            await RecordAmbiguousFailureAsync(
                claim,
                "transport_failure",
                exception.GetType().Name,
                cancellationToken);
        }
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [Queue("asap-email")]
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var ids = new HashSet<long>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var reclaim = new SqlCommand(
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = CASE WHEN [AttemptCount] >= @maxAttempts THEN N'failed' ELSE N'pending' END,
                [NextAttemptUtc] = CASE WHEN [AttemptCount] >= @maxAttempts THEN NULL ELSE SYSUTCDATETIME() END,
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL,
                [LastErrorCode] = N'ambiguous_expired_lease',
                [LastErrorDetail] = N'The prior transport outcome is unknown because its lease expired.'
            OUTPUT inserted.[Id]
            WHERE [Status] = N'sending'
              AND [LeaseExpiresUtc] <= SYSUTCDATETIME();
            """,
            connection))
        {
            reclaim.Parameters.Add("@maxAttempts", SqlDbType.Int).Value = runtimeOptions.MaxAttempts;
            await using var reader = await reclaim.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        await using (var due = new SqlCommand(
            """
            SELECT TOP (500) [Id]
            FROM [asap].[EmailOutbox] WITH (READPAST, READCOMMITTEDLOCK)
            WHERE [Status] = N'pending'
              AND ([NextAttemptUtc] IS NULL OR [NextAttemptUtc] <= SYSUTCDATETIME())
            ORDER BY [CreatedUtc], [Id];
            """,
            connection))
        {
            await using var reader = await due.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            dispatcher.Enqueue(id);
        }
    }

    private async Task<ClaimedEmail?> TryClaimAsync(long outboxId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE [asap].[EmailOutbox] WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK)
            SET [Status] = N'sending',
                [AttemptCount] = [AttemptCount] + 1,
                [LastAttemptUtc] = SYSUTCDATETIME(),
                [SendingStartedUtc] = SYSUTCDATETIME(),
                [LeaseId] = NEWID(),
                [LeaseExpiresUtc] = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                [LastErrorCode] = NULL,
                [LastErrorDetail] = NULL
            OUTPUT
                inserted.[Id], inserted.[OrganizationId], inserted.[BusinessKey],
                inserted.[DeliveryClass], inserted.[DeliveryMode], inserted.[RecipientStaffUserId],
                inserted.[RecipientAuthenticationEmail],
                inserted.[AuthorizationOrganizationId], inserted.[RecipientAddressKind],
                inserted.[ToAddress], inserted.[FromAddress], inserted.[FromName],
                inserted.[Subject], inserted.[BodyText], inserted.[BodyHtml],
                inserted.[AttemptCount], inserted.[SendingStartedUtc], inserted.[LeaseId],
                inserted.[RowVersion]
            WHERE [Id] = @id
              AND [Status] = N'pending'
              AND ([NextAttemptUtc] IS NULL OR [NextAttemptUtc] <= SYSUTCDATETIME());
            """,
            connection,
            transaction);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = outboxId;
        command.Parameters.Add("@leaseSeconds", SqlDbType.Int).Value =
            Convert.ToInt32(runtimeOptions.LeaseDuration.TotalSeconds);
        ClaimedEmail? claim = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                claim = new ClaimedEmail(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    NullableString(reader, 2),
                    reader.GetString(3),
                    NullableString(reader, 4),
                    NullableInt64(reader, 5),
                    NullableString(reader, 6),
                    NullableInt32(reader, 7),
                    NullableString(reader, 8),
                    reader.GetString(9),
                    reader.GetString(10),
                    NullableString(reader, 11),
                    reader.GetString(12),
                    NullableString(reader, 13),
                    NullableString(reader, 14),
                    reader.GetInt32(15),
                    reader.GetDateTime(16),
                    reader.GetGuid(17),
                    (byte[])reader[18]);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    private async Task<string?> ValidateCurrentRecipientAsync(
        ClaimedEmail claim,
        CancellationToken cancellationToken)
    {
        if (claim.DeliveryClass != "staff_authorization_sensitive")
        {
            return null;
        }

        if (!claim.RecipientStaffUserId.HasValue ||
            string.IsNullOrWhiteSpace(claim.RecipientAuthenticationEmail) ||
            !claim.AuthorizationOrganizationId.HasValue)
        {
            return "recipient_authorization_changed";
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT
                CASE
                    WHEN @addressKind = N'weekly_summary' AND staff.[WeeklyActionSummaryEnabled] = 1
                        THEN COALESCE(NULLIF(LTRIM(RTRIM(staff.[WeeklyActionSummaryEmail])), N''), staff.[NotificationEmail])
                    WHEN @addressKind = N'notification_email' THEN staff.[NotificationEmail]
                    ELSE NULL
                END
            FROM [asap].[StaffUser] AS staff
            INNER JOIN [asap].[Organization] AS organization
                ON organization.[Id] = @authorizationOrganizationId
               AND organization.[IsActive] = 1
            WHERE staff.[Id] = @staffUserId
              AND staff.[NormalizedUserPrincipalName] = @authenticationEmail
              AND staff.[IsActive] = 1
              AND
              (
                  (staff.[Role] IN (N'staff', N'admin') AND staff.[OrganizationId] = @authorizationOrganizationId) OR
                  (staff.[Role] = N'super_admin' AND staff.[OrganizationId] = 1)
              );
            """,
            connection);
        command.Parameters.Add("@addressKind", SqlDbType.NVarChar, 32).Value = claim.RecipientAddressKind!;
        command.Parameters.Add("@staffUserId", SqlDbType.BigInt).Value = claim.RecipientStaffUserId.Value;
        command.Parameters.Add("@authenticationEmail", SqlDbType.NVarChar, 320).Value = claim.RecipientAuthenticationEmail;
        command.Parameters.Add("@authorizationOrganizationId", SqlDbType.Int).Value =
            claim.AuthorizationOrganizationId.Value;
        var address = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        return string.Equals(address?.Trim(), claim.ToAddress.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : "recipient_authorization_changed";
    }

    private Task CompleteAsync(
        ClaimedEmail claim,
        string providerMessageId,
        string deliveryMode,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            claim,
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = N'sent',
                [ProviderMessageId] = @detail,
                [DeliveryMode] = @deliveryMode,
                [SentUtc] = SYSUTCDATETIME(),
                [NextAttemptUtc] = NULL,
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            providerMessageId,
            cancellationToken,
            deliveryMode);

    private Task FailProviderAsync(
        ClaimedEmail claim,
        string errorCode,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            claim,
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = N'failed',
                [LastErrorCode] = @detail,
                [LastErrorDetail] = N'The configured email provider rejected the message.',
                [NextAttemptUtc] = NULL,
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            errorCode,
            cancellationToken);

    private Task SuppressAsync(
        ClaimedEmail claim,
        string reason,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            claim,
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = N'suppressed',
                [SuppressionReason] = @detail,
                [SuppressedUtc] = SYSUTCDATETIME(),
                [NextAttemptUtc] = NULL,
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            reason,
            cancellationToken);

    private Task FailNotConfiguredAsync(
        ClaimedEmail claim,
        string errorCode,
        CancellationToken cancellationToken) =>
        FinalizeAsync(
            claim,
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = N'failed',
                [LastErrorCode] = @detail,
                [LastErrorDetail] = N'The current email transport configuration is unavailable.',
                [NextAttemptUtc] = NULL,
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            errorCode,
            cancellationToken);

    private async Task RecordAmbiguousFailureAsync(
        ClaimedEmail claim,
        string errorCode,
        string errorDetail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE [asap].[EmailOutbox]
            SET [LastErrorCode] = @errorCode,
                [LastErrorDetail] = @errorDetail
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion;
            """,
            connection);
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        command.Parameters.Add("@errorDetail", SqlDbType.NVarChar, -1).Value = errorDetail;
        AddFence(command, claim);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> StillOwnsUnexpiredLeaseAsync(
        ClaimedEmail claim,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT COUNT_BIG(*)
            FROM [asap].[EmailOutbox]
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            connection);
        AddFence(command, claim);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task FinalizeAsync(
        ClaimedEmail claim,
        string sql,
        string detail,
        CancellationToken cancellationToken,
        string? deliveryMode = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 256).Value = detail;
        command.Parameters.Add("@deliveryMode", SqlDbType.NVarChar, 16).Value =
            (object?)deliveryMode ?? DBNull.Value;
        AddFence(command, claim);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFence(SqlCommand command, ClaimedEmail claim)
    {
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = claim.Id;
        command.Parameters.Add("@leaseId", SqlDbType.UniqueIdentifier).Value = claim.LeaseId;
        command.Parameters.Add("@rowVersion", SqlDbType.Timestamp, 8).Value = claim.RowVersion;
    }

    private static string? NullableString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableInt64(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? NullableInt32(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static Guid? NullableGuid(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
}
