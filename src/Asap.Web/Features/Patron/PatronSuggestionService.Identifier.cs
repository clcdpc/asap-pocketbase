using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Patron;

public sealed partial class PatronSuggestionService
{
    public async Task<IdentifierLookupOutcome> ProcessIdentifierLookupAsync(
        long requestId,
        string identifier,
        int organizationId,
        byte[] expectedRowVersion,
        CancellationToken cancellationToken) =>
        (await ProcessIdentifierLookupCoreAsync(
            requestId,
            identifier,
            organizationId,
            expectedRowVersion,
            queueName: null,
            queueScope: null,
            expectedQueueVersion: null,
            queueCreatedUtc: null,
            queueItemId: null,
            cancellationToken)).Outcome;

    internal Task<IdentifierLookupProcessingResult> ProcessIdentifierLookupForQueueAsync(
        long requestId,
        string identifier,
        int organizationId,
        byte[] expectedRowVersion,
        string queueName,
        int queueScope,
        byte[] expectedQueueVersion,
        DateTime queueCreatedUtc,
        long queueItemId,
        CancellationToken cancellationToken) =>
        ProcessIdentifierLookupCoreAsync(
            requestId,
            identifier,
            organizationId,
            expectedRowVersion,
            queueName,
            queueScope,
            expectedQueueVersion,
            queueCreatedUtc,
            queueItemId,
            cancellationToken);

    private async Task<IdentifierLookupProcessingResult> ProcessIdentifierLookupCoreAsync(
        long requestId,
        string identifier,
        int organizationId,
        byte[] expectedRowVersion,
        string? queueName,
        int? queueScope,
        byte[]? expectedQueueVersion,
        DateTime? queueCreatedUtc,
        long? queueItemId,
        CancellationToken cancellationToken)
    {
        IdentifierLookupResult result;
        try
        {
            result = await patronProvider.LookupIdentifierAsync(identifier, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (exception is PolarisOperationalException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            logger.LogWarning(
                exception,
                "Immediate identifier lookup failed for title request {TitleRequestId}.",
                requestId);
            result = new IdentifierLookupResult(
                IdentifierLookupOutcome.OperationalFailure,
                ErrorCode: "polaris_identifier_lookup_failed");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (result.Outcome == IdentifierLookupOutcome.OperationalFailure)
        {
            logger.LogError(
                "Operational identifier lookup failure for title request {TitleRequestId}: {ErrorCode}",
                requestId,
                result.ErrorCode ?? "polaris_search_operational_failure");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await using (var organization = new SqlCommand(
            """
            SELECT CONVERT(bit, CASE
                WHEN [Id] > 1 AND [OrganizationCodeId] = 2 AND [IsActive] = 1 THEN 1
                ELSE 0 END)
            FROM [asap].[Organization] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @organizationId;
            """,
            connection,
            transaction))
        {
            Add(organization, "@organizationId", SqlDbType.Int, organizationId);
            if (await organization.ExecuteScalarAsync(cancellationToken) is not true)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new IdentifierLookupProcessingResult(result.Outcome);
            }
        }

        string currentTitle = null!;
        string? currentAuthor = null;
        int? currentBibId = null;
        var bibIdStaffVerified = false;
        var requestFound = false;
        await using (var request = new SqlCommand(
            $"""
            SELECT request.[Title], request.[Author], request.[BibId], request.[BibIdStaffVerified]
            FROM [asap].[TitleRequest] AS request WITH (UPDLOCK, HOLDLOCK)
            WHERE request.[Id] = @requestId
              AND request.[LibraryOrganizationId] = @organizationId
              AND request.[Identifier] = @identifier
              AND request.[IsbnCheckStatus] = N'pending'
              AND [RowVersion] = @rowVersion
            {IdentifierMutationBarrierPredicate};
            """,
            connection,
            transaction))
        {
            Add(request, "@requestId", SqlDbType.BigInt, requestId);
            Add(request, "@organizationId", SqlDbType.Int, organizationId);
            Add(request, "@identifier", SqlDbType.NVarChar, identifier, 100);
            Add(request, "@rowVersion", SqlDbType.Timestamp, expectedRowVersion, 8);
            await using var reader = await request.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                requestFound = true;
                currentTitle = reader.GetString(0);
                currentAuthor = reader.IsDBNull(1) ? null : reader.GetString(1);
                currentBibId = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                bibIdStaffVerified = reader.GetBoolean(3);
            }
        }
        if (!requestFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new IdentifierLookupProcessingResult(result.Outcome);
        }

        if (queueName is not null)
        {
            await using var progress = new SqlCommand(
                "SELECT [RowVersion] FROM [asap].[QueueProgress] WITH (UPDLOCK, HOLDLOCK) WHERE [QueueName] = @queueName AND [ScopeOrganizationId] = @scope;",
                connection,
                transaction);
            Add(progress, "@queueName", SqlDbType.NVarChar, queueName, 64);
            Add(progress, "@scope", SqlDbType.Int, queueScope);
            var currentQueueVersion = await progress.ExecuteScalarAsync(cancellationToken);
            if (currentQueueVersion is not byte[] currentVersion || expectedQueueVersion is null ||
                !currentVersion.SequenceEqual(expectedQueueVersion))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new IdentifierLookupProcessingResult(result.Outcome);
            }
        }

        var status = result.Outcome switch
        {
            IdentifierLookupOutcome.Found when result.BibId is > 0 => "found",
            IdentifierLookupOutcome.DefinitiveNotFound => "not_found",
            IdentifierLookupOutcome.TransientFailure => "pending",
            _ => null
        };
        var retryIncrement = result.Outcome == IdentifierLookupOutcome.TransientFailure ? 1 : 0;
        var reconciledTitle = status == "found" && !bibIdStaffVerified
            ? MergeCatalogValue(result.CatalogTitle, currentTitle)
            : currentTitle;
        var reconciledAuthor = status == "found" && !bibIdStaffVerified
            ? MergeCatalogValue(result.CatalogAuthor, currentAuthor)
            : currentAuthor;
        var note = status switch
        {
            "found" when bibIdStaffVerified =>
                $"Identifier number verification found a Polaris bibliographic match (BIB ID {result.BibId}); staff-selected BIB ID {currentBibId} was retained.",
            "found" => $"Identifier number verification found a Polaris bibliographic match (BIB ID {result.BibId}).",
            "not_found" when result.FilteredByMaterialType =>
                $"Identifier number verification completed: only e-materials found matching identifier {identifier}; these cannot be placed on hold in Polaris.",
            "not_found" => "Identifier number verification completed: no Polaris bibliographic match found.",
            _ => null
        };
        if (result.MultipleMatches)
        {
            note = JoinNote(
                note,
                bibIdStaffVerified
                    ? "Identifier number search found multiple Polaris matches; the staff-selected BIB was retained."
                    : "Identifier number search found multiple Polaris matches; ASAP used the first result by publication date descending.");
        }

        var affected = 0;
        await using (var update = new SqlCommand(
            $"""
            UPDATE request
            SET [IsbnCheckStatus] =
                    CASE
                        WHEN @status IS NULL THEN [IsbnCheckStatus]
                        WHEN @retryIncrement = 1 AND [IsbnCheckRetryCount] + 1 >= 5 THEN N'error_max_retries'
                        ELSE @status
                    END,
                [BibId] = CASE
                              WHEN @status IN (N'found', N'not_found') AND request.[BibIdStaffVerified] = 0
                                  THEN @bibId
                              ELSE request.[BibId]
                          END,
                [Title] = CASE WHEN @status = N'found' THEN @title ELSE [Title] END,
                [Author] = CASE WHEN @status = N'found' THEN @author ELSE [Author] END,
                [IsbnCheckResult] =
                    CASE
                        WHEN @status = N'found' THEN N'Polaris bibliographic match found.'
                        WHEN @status = N'not_found' THEN N'No Polaris bibliographic match found.'
                        ELSE [IsbnCheckResult]
                    END,
                [IsbnCheckRetryCount] = [IsbnCheckRetryCount] + @retryIncrement,
                [IsbnCheckLastErrorCode] = @errorCode,
                [Notes] =
                    CASE WHEN @note IS NULL THEN [Notes]
                         WHEN NULLIF([Notes], N'') IS NULL THEN @note
                         ELSE [Notes] + CHAR(13) + CHAR(10) + @note END,
                [LastCheckedUtc] = @now,
                [UpdatedUtc] = @now
            FROM [asap].[TitleRequest] AS request
            WHERE request.[Id] = @requestId
              AND request.[LibraryOrganizationId] = @organizationId
              AND request.[Identifier] = @identifier
              AND request.[IsbnCheckStatus] = N'pending'
              AND request.[RowVersion] = @rowVersion
            {IdentifierMutationBarrierPredicate};
            """,
            connection,
            transaction))
        {
            Add(update, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            Add(update, "@status", SqlDbType.NVarChar, status, 32);
            Add(update, "@retryIncrement", SqlDbType.Int, retryIncrement);
            Add(update, "@bibId", SqlDbType.Int, result.BibId);
            Add(update, "@title", SqlDbType.NVarChar, reconciledTitle, 500);
            Add(update, "@author", SqlDbType.NVarChar, reconciledAuthor, 500);
            Add(update, "@errorCode", SqlDbType.NVarChar, result.ErrorCode, 100);
            Add(update, "@note", SqlDbType.NVarChar, note, -1);
            Add(update, "@requestId", SqlDbType.BigInt, requestId);
            Add(update, "@organizationId", SqlDbType.Int, organizationId);
            Add(update, "@identifier", SqlDbType.NVarChar, identifier, 100);
            Add(update, "@rowVersion", SqlDbType.Timestamp, expectedRowVersion, 8);
            affected = await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new IdentifierLookupProcessingResult(result.Outcome);
        }

        if (status == "found" || status == "not_found" && !result.FilteredByMaterialType)
        {
            await using var tag = new SqlCommand(
                """
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
                SELECT @requestId, tag.[Id]
                FROM [asap].[WorkflowTag] AS tag
                WHERE tag.[Code] = @tagCode
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM [asap].[TitleRequestWorkflowTag]
                      WHERE [TitleRequestId] = @requestId
                        AND [WorkflowTagId] = tag.[Id]
                  );
                """,
                connection,
                transaction);
            Add(tag, "@requestId", SqlDbType.BigInt, requestId);
            Add(
                tag,
                "@tagCode",
                SqlDbType.NVarChar,
                status == "found" ? "polaris_bib_found" : "polaris_bib_not_found",
                100);
            await tag.ExecuteNonQueryAsync(cancellationToken);
        }

        if (result.MultipleMatches)
        {
            await using var multiple = new SqlCommand(
                """
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
                SELECT @requestId, tag.[Id]
                FROM [asap].[WorkflowTag] AS tag
                WHERE tag.[Code] = N'polaris_multiple_matches'
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM [asap].[TitleRequestWorkflowTag]
                      WHERE [TitleRequestId] = @requestId
                        AND [WorkflowTagId] = tag.[Id]
                  );
                """,
                connection,
                transaction);
            Add(multiple, "@requestId", SqlDbType.BigInt, requestId);
            await multiple.ExecuteNonQueryAsync(cancellationToken);
        }

        byte[]? queueProgressVersion = null;
        if (queueName is not null)
        {
            await using var progress = new SqlCommand(
                """
                UPDATE [asap].[QueueProgress]
                SET [LastCreatedUtc] = @createdUtc,
                    [LastItemId] = @itemId,
                    [LastOutcomeItemId] = @itemId,
                    [LastOutcomeCode] = @outcome,
                    [LastOutcomeUtc] = @now,
                    [UpdatedUtc] = @now
                OUTPUT inserted.[RowVersion]
                WHERE [QueueName] = @queueName
                  AND [ScopeOrganizationId] = @scope
                  AND [RowVersion] = @rowVersion;
                """,
                connection,
                transaction);
            Add(progress, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            Add(progress, "@createdUtc", SqlDbType.DateTime2, queueCreatedUtc);
            Add(progress, "@itemId", SqlDbType.BigInt, queueItemId);
            Add(
                progress,
                "@outcome",
                SqlDbType.NVarChar,
                result.Outcome == IdentifierLookupOutcome.OperationalFailure ? "operational_failure" : "processed",
                64);
            Add(progress, "@queueName", SqlDbType.NVarChar, queueName, 64);
            Add(progress, "@scope", SqlDbType.Int, queueScope);
            Add(progress, "@rowVersion", SqlDbType.Timestamp, expectedQueueVersion, 8);
            var updatedQueueVersion = await progress.ExecuteScalarAsync(cancellationToken);
            if (updatedQueueVersion is not byte[])
            {
                await transaction.RollbackAsync(cancellationToken);
                return new IdentifierLookupProcessingResult(result.Outcome);
            }

            queueProgressVersion = (byte[])updatedQueueVersion;
        }

        await transaction.CommitAsync(cancellationToken);
        return new IdentifierLookupProcessingResult(result.Outcome, queueProgressVersion);
    }

}
