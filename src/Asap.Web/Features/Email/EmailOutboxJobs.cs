using System.Data;
using System.Runtime.ExceptionServices;
using Hangfire;
using Microsoft.Data.SqlClient;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
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
    DateTime DeadlineStartedUtc,
    Guid LeaseId,
    byte[] RowVersion);

public sealed class EmailOutboxJobs(
    ExternalConfiguration configuration,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailOutboxDispatcher dispatcher,
    EmailOutboxRuntimeOptions runtimeOptions,
    ILogger<EmailOutboxJobs> logger,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan SqlBookkeepingTimeout = TimeSpan.FromSeconds(5);
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

        EmailTransportReadiness readiness;
        try
        {
            readiness = await emailSender.CheckReadinessAsync(claim.OrganizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseCancelledPreSendClaimAsync(claim);
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseCancelledPreSendClaimAsync(claim);
            throw;
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Email outbox {OutboxId} readiness check failed ({FailureType}).",
                claim.Id, exception.GetType().Name);
            await ReleasePreSendFailureAsync(claim, "mail_readiness_unavailable", cancellationToken);
            return;
        }
        if (!readiness.IsConfigured)
        {
            await FailNotConfiguredAsync(claim);
            return;
        }

        if (timeProvider.GetUtcNow().UtcDateTime - claim.DeadlineStartedUtc > runtimeOptions.ProviderStartDeadline)
        {
            await RecordAmbiguousFailureAsync(
                claim,
                "provider_start_deadline_exceeded",
                "Transport did not start within the post-claim deadline.",
                cancellationToken);
            return;
        }

        var providerClaim = await TryBeginProviderSendAsync(claim, cancellationToken);
        if (providerClaim is null)
        {
            return;
        }
        claim = providerClaim;

        EmailSendResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(runtimeOptions.ProviderCallTimeout);
            result = await emailSender.SendAsync(
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
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RecordProviderFailurePreservingOriginalAsync(
                claim,
                "provider_timeout",
                exception.GetType().Name,
                exception);
            return;
        }
        // Once provider dispatch starts, any failure may follow an accepted external send.
        // Preserve ambiguous evidence and let the durable outbox recovery policy decide retry.
        catch (Exception exception) when (exception is not OperationCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Email outbox {OutboxId} delivery failed.", claim.Id);
            await RecordProviderFailurePreservingOriginalAsync(
                claim,
                "transport_failure",
                exception.GetType().Name,
                exception);
            return;
        }

        if (result.Outcome == EmailSendOutcome.NotConfigured)
        {
            await FailNotConfiguredAsync(claim);
            return;
        }
        if (string.IsNullOrWhiteSpace(result.ProviderMessageId))
        {
            throw new InvalidOperationException("A successful email transport result requires a provider message ID.");
        }

        await CompleteAsync(claim, result.ProviderMessageId);
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
            SET [Status] = CASE
                    WHEN [LastErrorCode] IN (N'pre_send_check_pending', N'provider_start_deadline_exceeded')
                        THEN N'pending'
                    ELSE N'failed' END,
                [NextAttemptUtc] = CASE
                    WHEN [LastErrorCode] IN (N'pre_send_check_pending', N'provider_start_deadline_exceeded')
                        THEN DATEADD(minute, 1, @now)
                    ELSE NULL END,
                [AttemptCount] = CASE
                    WHEN [LastErrorCode] IN (N'pre_send_check_pending', N'provider_start_deadline_exceeded')
                        THEN [AttemptCount] - 1
                    ELSE [AttemptCount] END,
                [SendingStartedUtc] = CASE
                    WHEN [LastErrorCode] IN (N'pre_send_check_pending', N'provider_start_deadline_exceeded')
                        THEN NULL
                    ELSE [SendingStartedUtc] END,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL,
                [LastErrorCode] = CASE
                    WHEN [LastErrorCode] = N'pre_send_check_pending' THEN N'mail_readiness_unavailable'
                    WHEN [LastErrorCode] IS NULL THEN N'ambiguous_expired_lease'
                    ELSE [LastErrorCode] END,
                [LastErrorDetail] = CASE
                    WHEN [LastErrorCode] = N'pre_send_check_pending'
                        THEN N'The email transport readiness check did not complete.'
                    WHEN [LastErrorCode] IS NULL
                        THEN N'The prior transport outcome is unknown because its lease expired.'
                    WHEN [LastErrorCode] = N'provider_start_deadline_exceeded'
                        THEN COALESCE([LastErrorDetail], N'Transport did not start within the post-claim deadline.')
                    ELSE [LastErrorDetail] END
            OUTPUT inserted.[Id], inserted.[Status]
            WHERE [Status] = N'sending'
              AND [LeaseExpiresUtc] <= SYSUTCDATETIME();
            """,
            connection))
        {
            reclaim.Parameters.Add("@now", SqlDbType.DateTime2).Value = timeProvider.GetUtcNow().UtcDateTime;
            await using var reader = await reclaim.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1) == "pending")
                {
                    ids.Add(reader.GetInt64(0));
                }
            }
        }

        await using (var due = new SqlCommand(
            """
            SELECT TOP (500) [Id]
            FROM [asap].[EmailOutbox] WITH (READPAST, READCOMMITTEDLOCK)
            WHERE [Status] = N'pending'
              AND ([NextAttemptUtc] IS NULL OR [NextAttemptUtc] <= @now)
            ORDER BY [CreatedUtc], [Id];
            """,
            connection))
        {
            due.Parameters.Add("@now", SqlDbType.DateTime2).Value = timeProvider.GetUtcNow().UtcDateTime;
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

    // Business timestamps and deadline duration use TimeProvider; durable lease start/expiry use SQL time.
    private async Task<ClaimedEmail?> TryClaimAsync(long outboxId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
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
                [LastAttemptUtc] = @now,
                [SendingStartedUtc] = SYSUTCDATETIME(),
                [LeaseId] = NEWID(),
                [LeaseExpiresUtc] = DATEADD(second, @leaseSeconds, SYSUTCDATETIME()),
                [LastErrorCode] = N'pre_send_check_pending',
                [LastErrorDetail] = NULL
            OUTPUT
                inserted.[Id], inserted.[OrganizationId], inserted.[BusinessKey],
                inserted.[DeliveryClass], inserted.[RecipientStaffUserId],
                inserted.[RecipientAuthenticationEmail],
                inserted.[AuthorizationOrganizationId], inserted.[RecipientAddressKind],
                inserted.[ToAddress], inserted.[FromAddress], inserted.[FromName],
                inserted.[Subject], inserted.[BodyText], inserted.[BodyHtml],
                inserted.[AttemptCount], inserted.[LeaseId],
                inserted.[RowVersion]
            WHERE [Id] = @id
              AND [Status] = N'pending'
              AND ([NextAttemptUtc] IS NULL OR [NextAttemptUtc] <= @now);
            """,
            connection,
            transaction);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = timeProvider.GetUtcNow().UtcDateTime;
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
                    NullableInt64(reader, 4),
                    NullableString(reader, 5),
                    NullableInt32(reader, 6),
                    NullableString(reader, 7),
                    reader.GetString(8),
                    reader.GetString(9),
                    NullableString(reader, 10),
                    reader.GetString(11),
                    NullableString(reader, 12),
                    NullableString(reader, 13),
                    reader.GetInt32(14),
                    now,
                    reader.GetGuid(15),
                    (byte[])reader[16]);
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
                END,
                staff.[UserPrincipalName], staff.[NormalizedUserPrincipalName],
                staff.[Role], staff.[OrganizationId], staff.[IsActive]
            FROM [asap].[StaffUser] AS staff
            INNER JOIN [asap].[Organization] AS target
                ON target.[Id] = @authorizationOrganizationId AND target.[IsActive] = 1
                AND (target.[Id] = 1 OR target.[Id] > 1 AND target.[OrganizationCodeId] = 2)
            INNER JOIN [asap].[Organization] AS owner
                ON owner.[Id] = staff.[OrganizationId] AND owner.[IsActive] = 1
                AND (owner.[Id] = 1 OR owner.[Id] > 1 AND owner.[OrganizationCodeId] = 2)
            WHERE staff.[Id] = @staffUserId;
            """,
            connection);
        command.Parameters.Add("@addressKind", SqlDbType.NVarChar, 32).Value = claim.RecipientAddressKind!;
        command.Parameters.Add("@staffUserId", SqlDbType.BigInt).Value = claim.RecipientStaffUserId.Value;
        command.Parameters.Add("@authorizationOrganizationId", SqlDbType.Int).Value =
            claim.AuthorizationOrganizationId.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "recipient_authorization_changed";
        }
        var row = new StaffUser
        {
            Id = claim.RecipientStaffUserId.Value,
            UserPrincipalName = NullableString(reader, 1),
            NormalizedUserPrincipalName = NullableString(reader, 2),
            Role = reader.GetString(3), OrganizationId = reader.GetInt32(4), IsActive = reader.GetBoolean(5)
        };
        return StaffEligibilityService.IsAssignmentEligible(row, claim.AuthorizationOrganizationId.Value) &&
               StaffEmail.MatchesAuthenticationEmail(row, claim.RecipientAuthenticationEmail) &&
               string.Equals(NullableString(reader, 0)?.Trim(), claim.ToAddress.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : "recipient_authorization_changed";
    }

    private Task CompleteAsync(
        ClaimedEmail claim,
        string providerMessageId) =>
        FinalizeKnownOutcomeAsync(
            claim,
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = N'sent',
                [ProviderMessageId] = @detail,
                [SentUtc] = @now,
                [NextAttemptUtc] = NULL,
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            providerMessageId,
            "the accepted provider result");

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
                [SuppressedUtc] = @now,
                [LastErrorCode] = NULL,
                [LastErrorDetail] = NULL,
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
        ClaimedEmail claim) =>
        FinalizeKnownOutcomeAsync(
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
            "mail_not_configured",
            "the definitive not-configured result");

    private async Task ReleaseCancelledPreSendClaimAsync(ClaimedEmail claim)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SqlBookkeepingTimeout);
            await ReleasePreSendFailureAsync(claim, "pre_send_cancelled", timeout.Token);
        }
        // Preserve caller cancellation; a failed release leaves the durable lease available to recovery.
        catch (Exception exception)
        {
            logger.LogWarning("Email outbox {OutboxId} could not release a cancelled pre-send claim ({FailureType}).",
                claim.Id, exception.GetType().Name);
        }
    }

    private async Task ReleasePreSendFailureAsync(
        ClaimedEmail claim,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE [asap].[EmailOutbox]
            SET [Status] = N'pending',
                [AttemptCount] = [AttemptCount] - 1,
                [NextAttemptUtc] = DATEADD(minute, 1, @now),
                [SendingStartedUtc] = NULL,
                [LeaseId] = NULL,
                [LeaseExpiresUtc] = NULL,
                [LastErrorCode] = @errorCode,
                [LastErrorDetail] = N'The email transport readiness check did not complete.'
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LastErrorCode] = N'pre_send_check_pending';
            """,
            connection);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = timeProvider.GetUtcNow().UtcDateTime;
        command.Parameters.Add("@errorCode", SqlDbType.NVarChar, 100).Value = errorCode;
        AddFence(command, claim);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> RecordAmbiguousFailureAsync(
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
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordProviderFailurePreservingOriginalAsync(
        ClaimedEmail claim,
        string errorCode,
        string errorDetail,
        Exception providerFailure)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SqlBookkeepingTimeout);
            var affectedRows = await RecordAmbiguousFailureAsync(
                claim,
                errorCode,
                errorDetail,
                timeout.Token);
            if (affectedRows != 1)
            {
                throw new InvalidOperationException(
                    $"Email outbox {claim.Id} could not record the ambiguous provider outcome because its lease or row version changed.");
            }
        }
        catch (Exception bookkeepingFailure)
        {
            logger.LogWarning(bookkeepingFailure,
                "Email outbox {OutboxId} could not record the ambiguous outcome after provider failure ({FailureType}).",
                claim.Id,
                providerFailure.GetType().Name);
            ExceptionDispatchInfo.Capture(providerFailure).Throw();
        }
    }

    private async Task<ClaimedEmail?> TryBeginProviderSendAsync(
        ClaimedEmail claim,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE [asap].[EmailOutbox]
            SET [LastErrorCode] = NULL,
                [LastErrorDetail] = NULL
            OUTPUT inserted.[RowVersion]
            WHERE [Id] = @id AND [Status] = N'sending'
              AND [LeaseId] = @leaseId AND [RowVersion] = @rowVersion
              AND [LastErrorCode] = N'pre_send_check_pending'
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            connection);
        AddFence(command, claim);
        var version = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        return version is null ? null : claim with { RowVersion = version };
    }

    private async Task FinalizeKnownOutcomeAsync(
        ClaimedEmail claim,
        string sql,
        string detail,
        string outcomeDescription)
    {
        using var timeout = new CancellationTokenSource(SqlBookkeepingTimeout);
        var affectedRows = await FinalizeAsync(claim, sql, detail, timeout.Token);
        if (affectedRows != 1)
        {
            throw new InvalidOperationException(
                $"Email outbox {claim.Id} could not record {outcomeDescription}; its live lease or row version changed.");
        }
    }

    private async Task<int> FinalizeAsync(
        ClaimedEmail claim,
        string sql,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = timeProvider.GetUtcNow().UtcDateTime;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 256).Value = detail;
        AddFence(command, claim);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
