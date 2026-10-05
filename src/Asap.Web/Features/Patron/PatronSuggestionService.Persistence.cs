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
    private async Task<(long RequestId, long? OutboxId, string EmailStatus, byte[] RowVersion, string? Identifier)> InsertStaffAsync(
        CurrentStaff actor,
        string barcode,
        PatronSnapshot patron,
        PickupBranch selectedBranch,
        StaffSuggestionInput input,
        ValidatedSuggestion preProviderSuggestion,
        EffectivePatronConfiguration configuration,
        AutoClaimCandidate? autoClaimCandidate,
        EmailTransportReadiness emailTransportReadiness,
        CreationActor creationActor,
        PickupMutationReceipt? pickupReceipt,
        CancellationToken cancellationToken)
    {
        if (contextFactory is null || staffEligibility is null)
        {
            throw new PatronFlowException(500, "Staff suggestion creation is not configured.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var databaseTransaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        var transaction = (SqlTransaction)databaseTransaction.GetDbTransaction();
        var lockedOrganizationIds = new HashSet<int>();
        foreach (var currentOrganizationId in new[]
                     { LibraryScope.SystemOrganizationId, configuration.OrganizationId, actor.OrganizationId, patron.PatronOrganizationId, patron.HomeLibraryOrganizationId }
                     .Where(item => item > 0)
                     .Distinct()
                     .Order())
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {currentOrganizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            var isTargetOrganization = currentOrganizationId == configuration.OrganizationId;
            var isPatronOrganization = currentOrganizationId == patron.PatronOrganizationId;
            var isPatronHomeOrganization = currentOrganizationId == patron.HomeLibraryOrganizationId;
            if (organization is null ||
                isTargetOrganization && !organization.IsActive ||
                isPatronHomeOrganization && !organization.IsActive)
            {
                throw new PatronFlowException(
                    409,
                    isPatronOrganization && organization is null
                        ? "The patron's registered organization is no longer available."
                        : isPatronHomeOrganization && !isTargetOrganization
                        ? "The patron's home library is no longer participating."
                        : configuration.SystemNotEnabledMessage,
                    new
                    {
                        code = isPatronOrganization && organization is null
                            ? "patron_organization_missing"
                            : isPatronHomeOrganization && !isTargetOrganization
                            ? "patron_home_library_inactive"
                            : "organization_inactive"
                    });
            }

            lockedOrganizationIds.Add(currentOrganizationId);
        }

        var staffResult = await staffEligibility.RevalidateLockedAsync(
            context,
            actor,
            configuration.OrganizationId,
            StaffRoleRequirement.Any,
            requireActorParticipation: true,
            lockedOrganizationIds,
            cancellationToken);
        RequireCurrentStaff(staffResult);
        var currentConfiguration = await configurationService.GetAsync(
                context,
                configuration.OrganizationId,
                cancellationToken)
            ?? throw new PatronFlowException(404, "The selected servicing library could not be determined.");
        if (!currentConfiguration.IsActive)
        {
            throw new PatronFlowException(409, currentConfiguration.SystemNotEnabledMessage);
        }

        EnforceStaffPatronEligibility(currentConfiguration, patron);
        var currentSuggestion = Validate(
            new PatronSuggestionInput(
                input.Format,
                input.Title,
                input.Author,
                input.Identifier,
                input.Publication,
                input.PreferredPickupBranchId,
                input.Autohold,
                input.CustomFields),
            currentConfiguration,
            allowInformationalMessage: true,
            forcedAutoHold: input.Autohold ?? true,
            exactPublicationDate: input.ExactPublicationDate,
            notes: input.Notes,
            verifiedBibId: preProviderSuggestion.VerifiedBibId);
        if (currentSuggestion.Format.Id != preProviderSuggestion.Format.Id)
        {
            throw FormatChanged();
        }

        var autoClaimTarget = await LockAutoClaimTargetAsync(
            connection,
            transaction,
            autoClaimCandidate,
            currentConfiguration.OrganizationId,
            cancellationToken);
        await LockAndValidateFormatAsync(
            connection,
            transaction,
            currentConfiguration.OrganizationId,
            currentSuggestion,
            cancellationToken);
        await EnforceDuplicateAsync(
            connection,
            transaction,
            barcode,
            currentSuggestion,
            currentConfiguration,
            cancellationToken);

        long requestId;
        await using (var insert = new SqlCommand(
            """
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [PatronOrganizationId], [StaffLibraryOrganizationIdCreatedBy], [Barcode], [Email],
                 [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                 [Title], [Author], [Identifier], [Publication], [ExactPublicationDate], [CustomFieldsJson], [AutoHold], [Notes],
                 [BibId], [BibIdStaffVerified], [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            OUTPUT inserted.[Id]
            VALUES
                (@libraryOrganizationId, @patronOrganizationId, @staffLibraryOrganizationIdCreatedBy, @barcode, @email,
                 @nameFirst, @nameLast, @patronCodeId, @patronCodeDescription,
                 @pickupBranchId, @pickupBranchName, @libraryName,
                 @title, @author, @identifier, @publication, @exactPublicationDate, @customFieldsJson, @autoHold, @notes,
                 @bibId, @bibIdStaffVerified, @materialFormatId, N'suggestion', @isbnCheckStatus, @now, @now);
            """,
            connection,
            transaction))
        {
            Add(insert, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            Add(insert, "@libraryOrganizationId", SqlDbType.Int, currentConfiguration.OrganizationId);
            Add(insert, "@patronOrganizationId", SqlDbType.Int, patron.PatronOrganizationId);
            Add(insert, "@staffLibraryOrganizationIdCreatedBy", SqlDbType.Int, creationActor.StaffLibraryOrganizationId);
            Add(insert, "@barcode", SqlDbType.NVarChar, barcode, 50);
            Add(insert, "@email", SqlDbType.NVarChar, patron.Email, 320);
            Add(insert, "@nameFirst", SqlDbType.NVarChar, patron.NameFirst, 256);
            Add(insert, "@nameLast", SqlDbType.NVarChar, patron.NameLast, 256);
            Add(insert, "@patronCodeId", SqlDbType.Int, patron.PatronCodeId);
            Add(insert, "@patronCodeDescription", SqlDbType.NVarChar, patron.PatronCodeDescription, 256);
            Add(insert, "@pickupBranchId", SqlDbType.Int, selectedBranch.Id);
            Add(insert, "@pickupBranchName", SqlDbType.NVarChar, selectedBranch.Label, 256);
            Add(insert, "@libraryName", SqlDbType.NVarChar, currentConfiguration.OrganizationName, 256);
            Add(insert, "@title", SqlDbType.NVarChar, currentSuggestion.Title, 500);
            Add(insert, "@author", SqlDbType.NVarChar, currentSuggestion.Author, 500);
            Add(insert, "@identifier", SqlDbType.NVarChar, currentSuggestion.Identifier, 100);
            Add(insert, "@publication", SqlDbType.NVarChar, currentSuggestion.Publication, 200);
            Add(insert, "@exactPublicationDate", SqlDbType.Date, currentSuggestion.ExactPublicationDate?.ToDateTime(TimeOnly.MinValue));
            Add(insert, "@customFieldsJson", SqlDbType.NVarChar, currentSuggestion.CustomFieldsJson, -1);
            Add(insert, "@autoHold", SqlDbType.Bit, currentSuggestion.AutoHold);
            Add(insert, "@notes", SqlDbType.NVarChar, currentSuggestion.Notes, -1);
            Add(insert, "@bibId", SqlDbType.Int, currentSuggestion.VerifiedBibId);
            Add(insert, "@bibIdStaffVerified", SqlDbType.Bit, currentSuggestion.VerifiedBibId is not null);
            Add(insert, "@materialFormatId", SqlDbType.BigInt, currentSuggestion.Format.Id);
            Add(insert, "@isbnCheckStatus", SqlDbType.NVarChar,
                currentSuggestion.Identifier is null ? "skipped_no_isbn" : "pending", 32);
            requestId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
        }

        await ApplyAutoClaimAsync(
            connection,
            transaction,
            requestId,
            currentConfiguration.OrganizationId,
            currentSuggestion.Format.Id,
            autoClaimCandidate,
            autoClaimTarget,
            cancellationToken);
        await ApplyCrossPatronDuplicateTagAsync(
            connection,
            transaction,
            requestId,
            barcode,
            currentConfiguration.OrganizationId,
            currentSuggestion.Identifier,
            cancellationToken);
        await InsertCreationEventAsync(
            connection,
            transaction,
            requestId,
            creationActor,
            currentConfiguration,
            patron,
            cancellationToken);
        var email = creationActor.EmailPatronConfirmation
            ? await InsertSubmissionEmailAsync(
                connection,
                transaction,
                requestId,
                patron,
                currentSuggestion,
                currentConfiguration,
                emailTransportReadiness,
                cancellationToken)
            : new SubmissionEmailResult(null, "not_requested");
        byte[] rowVersion;
        await using (var version = new SqlCommand(
            "SELECT [RowVersion] FROM [asap].[TitleRequest] WHERE [Id] = @requestId;",
            connection,
            transaction))
        {
            Add(version, "@requestId", SqlDbType.BigInt, requestId);
            rowVersion = (byte[])(await version.ExecuteScalarAsync(cancellationToken))!;
        }

        await PickupPreferenceMutationService.CompleteAsync(connection, transaction, pickupReceipt,
            requestId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
        return (requestId, email.OutboxId, email.Status, rowVersion, currentSuggestion.Identifier);
    }

    private static void RequireCurrentStaff(StaffEligibilityResult result)
    {
        if (result.Outcome == StaffEligibilityOutcome.Allowed)
        {
            return;
        }

        throw new PatronFlowException(
            result.Outcome == StaffEligibilityOutcome.InvalidIdentity ? 401 : 403,
            "Staff access is no longer available for this library.",
            new { code = result.Code, operationPhase = "rejected" });
    }

    private async Task<(long RequestId, long? OutboxId, string EmailStatus, byte[] RowVersion)> InsertAsync(
        string barcode,
        PatronSnapshot patron,
        PickupBranch selectedBranch,
        ValidatedSuggestion suggestion,
        EffectivePatronConfiguration configuration,
        AutoClaimCandidate? autoClaimCandidate,
        EmailTransportReadiness emailTransportReadiness,
        CreationActor creationActor,
        PickupMutationReceipt? pickupReceipt,
        bool enforcePatronLimit,
        bool sendSubmissionEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await LockAndValidateOrganizationAsync(
            connection,
            transaction,
            configuration,
            cancellationToken);
        var autoClaimTarget = await LockAutoClaimTargetAsync(
            connection,
            transaction,
            autoClaimCandidate,
            configuration.OrganizationId,
            cancellationToken);
        await LockAndValidateFormatAsync(
            connection,
            transaction,
            configuration.OrganizationId,
            suggestion,
            cancellationToken);
        if (enforcePatronLimit)
        {
            await EnforceLimitAsync(
                connection,
                transaction,
                barcode,
                configuration,
                cancellationToken);
        }
        await EnforceDuplicateAsync(
            connection,
            transaction,
            barcode,
            suggestion,
            configuration,
            cancellationToken);

        long requestId;
        await using (var insert = new SqlCommand(
            """
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [PatronOrganizationId], [StaffLibraryOrganizationIdCreatedBy], [Barcode], [Email],
                 [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                 [Title], [Author], [Identifier], [Publication], [ExactPublicationDate], [CustomFieldsJson], [AutoHold], [Notes],
                 [BibId], [BibIdStaffVerified],
                 [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            OUTPUT inserted.[Id]
            VALUES
                (@libraryOrganizationId, @patronOrganizationId, @staffLibraryOrganizationIdCreatedBy, @barcode, @email,
                 @nameFirst, @nameLast, @patronCodeId, @patronCodeDescription,
                 @pickupBranchId, @pickupBranchName, @libraryName,
                 @title, @author, @identifier, @publication, @exactPublicationDate, @customFieldsJson, @autoHold, @notes,
                 @bibId, @bibIdStaffVerified,
                 @materialFormatId, N'suggestion', @isbnCheckStatus, @now, @now);
            """,
            connection,
            transaction))
        {
            Add(insert, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            Add(insert, "@libraryOrganizationId", SqlDbType.Int, configuration.OrganizationId);
            Add(insert, "@patronOrganizationId", SqlDbType.Int, patron.PatronOrganizationId);
            Add(insert, "@staffLibraryOrganizationIdCreatedBy", SqlDbType.Int, creationActor.StaffLibraryOrganizationId);
            Add(insert, "@barcode", SqlDbType.NVarChar, barcode, 50);
            Add(insert, "@email", SqlDbType.NVarChar, patron.Email, 320);
            Add(insert, "@nameFirst", SqlDbType.NVarChar, patron.NameFirst, 256);
            Add(insert, "@nameLast", SqlDbType.NVarChar, patron.NameLast, 256);
            Add(insert, "@patronCodeId", SqlDbType.Int, patron.PatronCodeId);
            Add(insert, "@patronCodeDescription", SqlDbType.NVarChar, patron.PatronCodeDescription, 256);
            Add(insert, "@pickupBranchId", SqlDbType.Int, selectedBranch.Id);
            Add(insert, "@pickupBranchName", SqlDbType.NVarChar, selectedBranch.Label, 256);
            Add(insert, "@libraryName", SqlDbType.NVarChar, configuration.OrganizationName, 256);
            Add(insert, "@title", SqlDbType.NVarChar, suggestion.Title, 500);
            Add(insert, "@author", SqlDbType.NVarChar, suggestion.Author, 500);
            Add(insert, "@identifier", SqlDbType.NVarChar, suggestion.Identifier, 100);
            Add(insert, "@publication", SqlDbType.NVarChar, suggestion.Publication, 200);
            Add(insert, "@exactPublicationDate", SqlDbType.Date, suggestion.ExactPublicationDate?.ToDateTime(TimeOnly.MinValue));
            Add(insert, "@customFieldsJson", SqlDbType.NVarChar, suggestion.CustomFieldsJson, -1);
            Add(insert, "@autoHold", SqlDbType.Bit, suggestion.AutoHold);
            Add(insert, "@notes", SqlDbType.NVarChar, suggestion.Notes, -1);
            Add(insert, "@bibId", SqlDbType.Int, suggestion.VerifiedBibId);
            Add(insert, "@bibIdStaffVerified", SqlDbType.Bit, suggestion.VerifiedBibId is not null);
            Add(insert, "@materialFormatId", SqlDbType.BigInt, suggestion.Format.Id);
            Add(
                insert,
                "@isbnCheckStatus",
                SqlDbType.NVarChar,
                suggestion.Identifier is null ? "skipped_no_isbn" : "pending",
                32);
            requestId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
        }

        await ApplyAutoClaimAsync(
            connection,
            transaction,
            requestId,
            configuration.OrganizationId,
            suggestion.Format.Id,
            autoClaimCandidate,
            autoClaimTarget,
            cancellationToken);
        await ApplyCrossPatronDuplicateTagAsync(
            connection,
            transaction,
            requestId,
            barcode,
            configuration.OrganizationId,
            suggestion.Identifier,
            cancellationToken);
        await InsertCreationEventAsync(
            connection,
            transaction,
            requestId,
            creationActor,
            configuration,
            patron,
            cancellationToken);
        var email = sendSubmissionEmail
            ? await InsertSubmissionEmailAsync(
                connection,
                transaction,
                requestId,
                patron,
                suggestion,
                configuration,
                emailTransportReadiness,
                cancellationToken)
            : new SubmissionEmailResult(null, "not_requested");
        byte[] rowVersion;
        await using (var version = new SqlCommand(
            "SELECT [RowVersion] FROM [asap].[TitleRequest] WHERE [Id] = @requestId;",
            connection,
            transaction))
        {
            Add(version, "@requestId", SqlDbType.BigInt, requestId);
            rowVersion = (byte[])(await version.ExecuteScalarAsync(cancellationToken))!;
        }

        await PickupPreferenceMutationService.CompleteAsync(connection, transaction, pickupReceipt,
            requestId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (requestId, email.OutboxId, email.Status, rowVersion);
    }

    private static async Task LockAndValidateOrganizationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        foreach (var id in new[] { LibraryScope.SystemOrganizationId, configuration.OrganizationId }.Distinct().Order())
        {
            await using var command = new SqlCommand(
                "SELECT [IsActive] FROM [asap].[Organization] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id;",
                connection,
                transaction);
            Add(command, "@id", SqlDbType.Int, id);
            if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new PatronFlowException(403, configuration.SystemNotEnabledMessage);
            }
        }
    }

    private static async Task LockAndValidateFormatAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        ValidatedSuggestion suggestion,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT format.[OwnerOrganizationId], format.[Code],
                   COALESCE(formatOverride.[IsEnabled], format.[IsEnabled]) AS [IsEnabled],
                   COALESCE(formatOverride.[MessageBehavior], format.[MessageBehavior], N'none') AS [MessageBehavior],
                   COALESCE(formatOverride.[TitleMode], format.[TitleMode], N'required') AS [TitleMode],
                   COALESCE(formatOverride.[AuthorMode], format.[AuthorMode], N'optional') AS [AuthorMode],
                   COALESCE(formatOverride.[IdentifierMode], format.[IdentifierMode], N'optional') AS [IdentifierMode],
                   COALESCE(formatOverride.[PublicationMode], format.[PublicationMode], N'optional') AS [PublicationMode]
            FROM [asap].[MaterialFormat] AS format WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN [asap].[MaterialFormatOverride] AS formatOverride WITH (UPDLOCK, HOLDLOCK)
              ON formatOverride.[MaterialFormatId] = format.[Id]
             AND formatOverride.[LibraryOrganizationId] = @organizationId
            WHERE format.[Id] = @materialFormatId
              AND (format.[OwnerOrganizationId] = 1 OR format.[OwnerOrganizationId] = @organizationId);
            """,
            connection,
            transaction);
        Add(command, "@materialFormatId", SqlDbType.BigInt, suggestion.Format.Id);
        Add(command, "@organizationId", SqlDbType.Int, organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw FormatChanged();
        }

        var ownerOrganizationId = reader.GetInt32(0);
        var code = reader.GetString(1);
        var isEnabled = reader.GetBoolean(2);
        var messageBehavior = reader.GetString(3);
        var titleMode = reader.GetString(4);
        var authorMode = reader.GetString(5);
        var identifierMode = reader.GetString(6);
        var publicationMode = reader.GetString(7);
        if (ownerOrganizationId != 1 && ownerOrganizationId != organizationId ||
            !string.Equals(code, suggestion.Format.Code, StringComparison.Ordinal) ||
            !isEnabled ||
            !string.Equals(messageBehavior, suggestion.Format.MessageBehavior, StringComparison.Ordinal) ||
            !string.Equals(titleMode, suggestion.Format.Title.Mode, StringComparison.Ordinal) ||
            !string.Equals(authorMode, suggestion.Format.Author.Mode, StringComparison.Ordinal) ||
            !string.Equals(identifierMode, suggestion.Format.Identifier.Mode, StringComparison.Ordinal) ||
            !string.Equals(publicationMode, suggestion.Format.Publication.Mode, StringComparison.Ordinal))
        {
            throw FormatChanged();
        }

        await reader.CloseAsync();
        await using var rules = new SqlCommand(
            """
            SELECT field.[FieldKey], [formatRule].[Mode], [formatRule].[LabelOverride]
            FROM [asap].[MaterialFormatCustomFieldRule] AS [formatRule] WITH (UPDLOCK, HOLDLOCK)
            JOIN [asap].[PatronCustomField] AS field WITH (UPDLOCK, HOLDLOCK)
              ON field.[Id] = [formatRule].[PatronCustomFieldId]
             AND field.[LibraryOrganizationId] = [formatRule].[LibraryOrganizationId]
             AND field.[IsEnabled] = 1
            WHERE [formatRule].[LibraryOrganizationId] = @organizationId
              AND [formatRule].[MaterialFormatId] = @materialFormatId;
            """,
            connection,
            transaction);
        Add(rules, "@materialFormatId", SqlDbType.BigInt, suggestion.Format.Id);
        Add(rules, "@organizationId", SqlDbType.Int, organizationId);
        var currentRules = new Dictionary<string, (string Mode, string? Label)>(StringComparer.Ordinal);
        await using var ruleReader = await rules.ExecuteReaderAsync(cancellationToken);
        while (await ruleReader.ReadAsync(cancellationToken))
        {
            currentRules[ruleReader.GetString(0)] =
                (ruleReader.GetString(1), ruleReader.IsDBNull(2) ? null : ruleReader.GetString(2));
        }

        if (currentRules.Count != suggestion.Format.CustomFields.Count ||
            currentRules.Any(pair => !suggestion.Format.CustomFields.TryGetValue(pair.Key, out var captured) ||
                                     !string.Equals(captured.Mode, pair.Value.Mode, StringComparison.Ordinal)))
        {
            throw FormatChanged();
        }
    }

    private static PatronFlowException FormatChanged() =>
        new(
            409,
            "The selected material format changed while the suggestion was being submitted. Refresh the form and try again.",
            new { code = "material_format_changed" });

    private async Task EnforceLimitAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string barcode,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (@limit) [CreatedUtc]
            FROM [asap].[TitleRequest] WITH (UPDLOCK, HOLDLOCK)
            WHERE [LibraryOrganizationId] = @organizationId
              AND [Barcode] = @barcode
              AND [CreatedUtc] >= @cutoffUtc
            ORDER BY [CreatedUtc] DESC, [Id] DESC;
            """,
            connection,
            transaction);
        Add(command, "@limit", SqlDbType.Int, configuration.SuggestionLimit);
        Add(command, "@organizationId", SqlDbType.Int, configuration.OrganizationId);
        Add(command, "@barcode", SqlDbType.NVarChar, barcode, 50);
        Add(
            command,
            "@cutoffUtc",
            SqlDbType.DateTime2,
            ResolveBusinessCalendarDate(timeProvider.GetUtcNow(), -7).UtcDateTime);
        var dates = new List<DateTime>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dates.Add(reader.GetDateTime(0));
        }

        if (dates.Count < configuration.SuggestionLimit)
        {
            return;
        }

        var next = ResolveBusinessCalendarDate(
            new DateTimeOffset(DateTime.SpecifyKind(dates[^1], DateTimeKind.Utc)),
            7);
        var localNext = TimeZoneInfo.ConvertTime(next, businessTimeZone);
        var message = configuration.SuggestionLimitMessage.Replace(
            "{{next_available_date}}",
            localNext.ToString("dddd, MMMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US")),
            StringComparison.Ordinal);
        throw new PatronFlowException(
            406,
            message,
            new { code = "suggestion_limit_reached", message });
    }

    private DateTimeOffset ResolveBusinessCalendarDate(DateTimeOffset instant, int days)
    {
        var local = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(instant, businessTimeZone).DateTime.AddDays(days),
            DateTimeKind.Unspecified);
        if (businessTimeZone.IsInvalidTime(local))
        {
            var before = local.AddMinutes(-1);
            while (businessTimeZone.IsInvalidTime(before))
            {
                before = before.AddMinutes(-1);
            }

            var after = local.AddMinutes(1);
            while (businessTimeZone.IsInvalidTime(after))
            {
                after = after.AddMinutes(1);
            }

            local = local.Add(businessTimeZone.GetUtcOffset(after) - businessTimeZone.GetUtcOffset(before));
        }

        var offset = businessTimeZone.IsAmbiguousTime(local)
            ? businessTimeZone.GetAmbiguousTimeOffsets(local).Max()
            : businessTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private async Task EnforceDuplicateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string barcode,
        ValidatedSuggestion suggestion,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1)
                candidate.[Id], candidate.[CreatedUtc], candidate.[Status], candidate.[CloseReason],
                candidate.[Title], candidate.[Author], candidate.[FormatCode], candidate.[MatchType]
            FROM
            (
                SELECT request.[Id], request.[CreatedUtc], request.[Status], request.[CloseReason],
                       request.[Title], request.[Author], format.[Code] AS [FormatCode],
                       N'identifier' AS [MatchType], 0 AS [MatchPriority]
                FROM [asap].[TitleRequest] AS request WITH (UPDLOCK, HOLDLOCK)
                JOIN [asap].[MaterialFormat] AS format ON format.[Id] = request.[MaterialFormatId]
                WHERE request.[LibraryOrganizationId] = @organizationId
                  AND request.[Barcode] = @barcode
                  AND @identifier IS NOT NULL
                  AND request.[Identifier] = @identifier

                UNION ALL

                SELECT request.[Id], request.[CreatedUtc], request.[Status], request.[CloseReason],
                       request.[Title], request.[Author], format.[Code] AS [FormatCode],
                       N'bibid' AS [MatchType], 1 AS [MatchPriority]
                FROM [asap].[TitleRequest] AS request WITH (UPDLOCK, HOLDLOCK)
                JOIN [asap].[MaterialFormat] AS format ON format.[Id] = request.[MaterialFormatId]
                WHERE request.[LibraryOrganizationId] = @organizationId
                  AND request.[Barcode] = @barcode
                  AND @bibId IS NOT NULL
                  AND request.[BibId] = @bibId

                UNION ALL

                SELECT request.[Id], request.[CreatedUtc], request.[Status], request.[CloseReason],
                       request.[Title], request.[Author], format.[Code] AS [FormatCode],
                       N'title_format' AS [MatchType], 2 AS [MatchPriority]
                FROM [asap].[TitleRequest] AS request WITH (UPDLOCK, HOLDLOCK)
                JOIN [asap].[MaterialFormat] AS format ON format.[Id] = request.[MaterialFormatId]
                WHERE request.[LibraryOrganizationId] = @organizationId
                  AND request.[Barcode] = @barcode
                  AND request.[Title] = @title
                  AND request.[MaterialFormatId] = @materialFormatId
            ) AS candidate
            ORDER BY candidate.[MatchPriority], candidate.[CreatedUtc] DESC, candidate.[Id] DESC;
            """,
            connection,
            transaction);
        Add(command, "@organizationId", SqlDbType.Int, configuration.OrganizationId);
        Add(command, "@barcode", SqlDbType.NVarChar, barcode, 50);
        Add(command, "@identifier", SqlDbType.NVarChar, suggestion.Identifier, 100);
        Add(command, "@bibId", SqlDbType.Int, suggestion.VerifiedBibId);
        Add(command, "@title", SqlDbType.NVarChar, suggestion.Title, 500);
        Add(command, "@materialFormatId", SqlDbType.BigInt, suggestion.Format.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        var id = reader.GetInt64(0);
        var created = reader.GetDateTime(1);
        var status = reader.GetString(2);
        var closeReason = reader.IsDBNull(3) ? null : reader.GetString(3);
        var title = reader.GetString(4);
        var author = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        var formatCode = reader.GetString(6);
        var matchType = reader.GetString(7);
        var statusKey = status == "closed" && closeReason is not null ? closeReason : status;
        var displayStatus = configuration.DuplicateStatusLabels.TryGetValue(statusKey, out var label)
            ? label
            : configuration.DuplicateStatusLabels.GetValueOrDefault(status, "Submitted");
        var localCreated = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(created, DateTimeKind.Utc),
            businessTimeZone);
        var formatLabel = configuration.Formats
            .SingleOrDefault(item => item.Code == formatCode)?.Label ?? formatCode;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["duplicate_date"] = localCreated.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US")),
            ["duplicate_status"] = displayStatus,
            ["duplicate_title"] = title,
            ["duplicate_author"] = author,
            ["duplicate_format"] = formatLabel,
            ["duplicate_match_type"] = matchType switch
            {
                "identifier" => "identifier number",
                "bibid" => "catalog BIB",
                _ => "title and format"
            },
            ["title"] = title,
            ["author"] = author,
            ["format"] = formatLabel
        };
        var conflictMessage = DuplicatePlaceholderRegex().Replace(
            configuration.AlreadySubmittedMessage,
            match => replacements.TryGetValue(match.Groups[1].Value, out var value)
                ? WebUtility.HtmlEncode(value)
                : match.Value);
        var message = matchType switch
        {
            "identifier" => "This patron already has a suggestion for this identifier number.",
            "bibid" => "This patron already has a suggestion for this catalog BIB.",
            _ => "This patron already has this suggestion."
        };
        throw new PatronFlowException(
            409,
            message,
            new PatronSuggestionDuplicateConflict(
                message,
                "Already Submitted",
                conflictMessage,
                new PatronSuggestionDuplicate(
                    id,
                    created,
                    status,
                    closeReason ?? string.Empty,
                    title,
                    author,
                    formatCode,
                    matchType)));
    }

    private async Task<AutoClaimCandidate?> FindAutoClaimCandidateAsync(
        int organizationId,
        long materialFormatId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) [Id], [StaffUserId]
            FROM [asap].[FormatAutoClaimRule]
            WHERE [LibraryOrganizationId] = @organizationId
              AND [MaterialFormatId] = @materialFormatId
              AND [IsActive] = 1
            ORDER BY [Id];
            """,
            connection);
        Add(command, "@organizationId", SqlDbType.Int, organizationId);
        Add(command, "@materialFormatId", SqlDbType.BigInt, materialFormatId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AutoClaimCandidate(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private async Task<LockedAutoClaimTarget?> LockAutoClaimTargetAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        AutoClaimCandidate? candidate,
        int organizationId,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
        {
            return null;
        }

        await using var command = new SqlCommand(
            """
            SELECT [DisplayName], [UserPrincipalName], [NormalizedUserPrincipalName], [Role], [OrganizationId], [IsActive]
            FROM [asap].[StaffUser] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @staffId;
            """,
            connection,
            transaction);
        Add(command, "@staffId", SqlDbType.BigInt, candidate.StaffUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var row = new StaffUser
        {
            Id = candidate.StaffUserId,
            DisplayName = reader.IsDBNull(0) ? null : reader.GetString(0),
            UserPrincipalName = reader.IsDBNull(1) ? null : reader.GetString(1),
            NormalizedUserPrincipalName = reader.IsDBNull(2) ? null : reader.GetString(2),
            Role = reader.GetString(3), OrganizationId = reader.GetInt32(4), IsActive = reader.GetBoolean(5)
        };
        return StaffEligibilityService.IsAssignmentEligible(row, organizationId)
            ? new LockedAutoClaimTarget(row.Id, row.DisplayName ?? row.UserPrincipalName ?? "Staff")
            : null;
    }

    private async Task ApplyAutoClaimAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long requestId,
        int organizationId,
        long materialFormatId,
        AutoClaimCandidate? candidate,
        LockedAutoClaimTarget? target,
        CancellationToken cancellationToken)
    {
        long? currentRuleId = null;
        long? currentStaffId = null;
        await using (var rule = new SqlCommand(
            """
            SELECT TOP (1) [Id], [StaffUserId]
            FROM [asap].[FormatAutoClaimRule] WITH (UPDLOCK, HOLDLOCK)
            WHERE [LibraryOrganizationId] = @organizationId
              AND [MaterialFormatId] = @materialFormatId
              AND [IsActive] = 1
            ORDER BY [Id];
            """,
            connection,
            transaction))
        {
            Add(rule, "@organizationId", SqlDbType.Int, organizationId);
            Add(rule, "@materialFormatId", SqlDbType.BigInt, materialFormatId);
            await using var reader = await rule.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                currentRuleId = reader.GetInt64(0);
                currentStaffId = reader.GetInt64(1);
            }
        }

        if (currentRuleId != candidate?.RuleId ||
            currentStaffId != candidate?.StaffUserId)
        {
            throw new AutoClaimCandidateChangedException();
        }

        if (candidate is null)
        {
            return;
        }

        if (target is null)
        {
            await using var skipped = new SqlCommand(
                """
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [ActorType], [ActorName],
                     [Message], [MetadataJson], [CreatedUtc])
                VALUES
                    (@requestId, N'claim_auto_skipped', N'suggestion', N'system', N'system',
                     N'Auto-claim skipped because the configured claimant is unavailable or outside this library.',
                     @metadataJson, @now);
                """,
                connection,
                transaction);
            Add(skipped, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
            Add(skipped, "@requestId", SqlDbType.BigInt, requestId);
            Add(
                skipped,
                "@metadataJson",
                SqlDbType.NVarChar,
                JsonSerializer.Serialize(new
                {
                    trigger = "submission",
                    ruleId = candidate.RuleId,
                    formatId = materialFormatId
                }));
            await skipped.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var update = new SqlCommand(
            """
            UPDATE [asap].[TitleRequest] WITH (UPDLOCK, HOLDLOCK)
            SET [ClaimedByStaffUserId] = @staffId,
                [ClaimedByDisplayName] = @displayName,
                [ClaimedAtUtc] = @now,
                [ClaimType] = N'automatic_format_rule',
                [ClaimRuleId] = @ruleId,
                [UpdatedUtc] = @now
            WHERE [Id] = @requestId;
            """,
            connection,
            transaction);
        Add(update, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
        Add(update, "@staffId", SqlDbType.BigInt, target.StaffUserId);
        Add(update, "@displayName", SqlDbType.NVarChar, target.DisplayName, 256);
        Add(update, "@ruleId", SqlDbType.BigInt, candidate.RuleId);
        Add(update, "@requestId", SqlDbType.BigInt, requestId);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await using var assigned = new SqlCommand(
            """
            INSERT INTO [asap].[TitleRequestEvent]
                ([TitleRequestId], [EventType], [Status], [ActorType], [ActorName],
                 [Message], [MetadataJson], [CreatedUtc])
            VALUES
                (@requestId, N'claim_auto_assigned', N'suggestion', N'system', N'system',
                 @message, @metadataJson, @now);
            """,
            connection,
            transaction);
        Add(assigned, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
        Add(assigned, "@requestId", SqlDbType.BigInt, requestId);
        Add(
            assigned,
            "@message",
            SqlDbType.NVarChar,
            $"Auto-claimed by {target.DisplayName} because this format is assigned to {target.DisplayName} for this library.");
        Add(
            assigned,
            "@metadataJson",
            SqlDbType.NVarChar,
            JsonSerializer.Serialize(new
            {
                trigger = "submission",
                ruleId = candidate.RuleId,
                formatId = materialFormatId
            }));
        await assigned.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ApplyCrossPatronDuplicateTagAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long requestId,
        string barcode,
        int organizationId,
        string? identifier,
        CancellationToken cancellationToken)
    {
        if (identifier is null)
        {
            return;
        }

        await using var command = new SqlCommand(
            """
            IF EXISTS
            (
                SELECT 1
                FROM [asap].[TitleRequest] WITH (UPDLOCK, HOLDLOCK)
                WHERE [LibraryOrganizationId] = @organizationId
                  AND [Identifier] = @identifier
                  AND [Barcode] <> @barcode
                  AND [Id] <> @requestId
            )
            BEGIN
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
                SELECT @requestId, tag.[Id]
                FROM [asap].[WorkflowTag] AS tag
                WHERE tag.[Code] = N'duplicate_suggestion'
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM [asap].[TitleRequestWorkflowTag]
                      WHERE [TitleRequestId] = @requestId AND [WorkflowTagId] = tag.[Id]
                  );

                UPDATE [asap].[TitleRequest]
                SET [Notes] =
                        CASE WHEN NULLIF([Notes], N'') IS NULL THEN @note
                             ELSE [Notes] + CHAR(13) + CHAR(10) + @note END,
                    [UpdatedUtc] = @now
                WHERE [Id] = @requestId;
            END;
            """,
            connection,
            transaction);
        Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
        Add(command, "@organizationId", SqlDbType.Int, organizationId);
        Add(command, "@identifier", SqlDbType.NVarChar, identifier, 100);
        Add(command, "@barcode", SqlDbType.NVarChar, barcode, 50);
        Add(command, "@requestId", SqlDbType.BigInt, requestId);
        Add(
            command,
            "@note",
            SqlDbType.NVarChar,
            "Tagged as a duplicate suggestion because another patron has a suggestion with the same identifier number.",
            -1);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertCreationEventAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long requestId,
        CreationActor actor,
        EffectivePatronConfiguration configuration,
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        var message = actor.ActorType == "staff"
            ? $"Suggestion created on behalf of patron by {actor.ActorName ?? "staff"}."
            : "Suggestion submitted by patron.";
        var metadata = JsonSerializer.Serialize(new
        {
            servicingLibraryOrganizationId = configuration.OrganizationId,
            patronOrganizationId = patron.PatronOrganizationId,
            homeLibraryOrganizationId = patron.HomeLibraryOrganizationId,
            emailPatronConfirmation = actor.EmailPatronConfirmation,
            pickupPreferenceChanged = actor.PickupPreferenceChanged,
            pickupPreferenceReconciled = actor.PickupPreferenceReconciled
        });
        await using var command = new SqlCommand(
            """
            INSERT INTO [asap].[TitleRequestEvent]
                ([TitleRequestId], [EventType], [Status], [ActorType], [StaffUserId], [ActorName], [Message], [MetadataJson], [CreatedUtc])
            VALUES
                (@requestId, N'created', N'suggestion', @actorType, @staffUserId, @actorName, @message, @metadataJson, @now);
            """,
            connection,
            transaction);
        Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
        Add(command, "@requestId", SqlDbType.BigInt, requestId);
        Add(command, "@actorType", SqlDbType.NVarChar, actor.ActorType, 16);
        Add(command, "@staffUserId", SqlDbType.BigInt, actor.StaffUserId);
        Add(command, "@actorName", SqlDbType.NVarChar, actor.ActorName, 256);
        Add(command, "@message", SqlDbType.NVarChar, message, -1);
        Add(command, "@metadataJson", SqlDbType.NVarChar, metadata, -1);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SubmissionEmailResult> InsertSubmissionEmailAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long requestId,
        PatronSnapshot patron,
        ValidatedSuggestion suggestion,
        EffectivePatronConfiguration configuration,
        EmailTransportReadiness emailTransportReadiness,
        CancellationToken cancellationToken)
    {
        var suppressionReason = Missing(
            patron.Email,
            configuration.Email.FromAddress,
            configuration.SubmissionTemplate,
            recipientDomainPolicy,
            emailTransportReadiness);
        var status = suppressionReason is null ? "pending" : "suppressed";
        var rendered = configuration.SubmissionTemplate is null
            ? null
            : PatronEmailTemplateRenderer.Render(
                configuration.SubmissionTemplate,
                patron,
                suggestion.Title,
                suggestion.Author,
                suggestion.Format.Label,
                patron.Barcode);

        await using var command = new SqlCommand(
            """
            INSERT INTO [asap].[EmailOutbox]
                ([OrganizationId], [BusinessKey], [DeliveryClass], [ToAddress], [FromAddress],
                 [FromName], [Subject], [BodyText], [BodyHtml], [Status], [SuppressionReason],
                 [NextAttemptUtc], [CreatedUtc], [SuppressedUtc])
            OUTPUT inserted.[Id]
            VALUES
                (@organizationId, @businessKey, N'business_event', @toAddress, @fromAddress,
                 @fromName, @subject, @bodyText, @bodyHtml, @status, @suppressionReason,
                 CASE WHEN @status = N'pending' THEN @now ELSE NULL END,
                 @now,
                 CASE WHEN @status = N'suppressed' THEN @now ELSE NULL END);
            """,
            connection,
            transaction);
        Add(command, "@now", SqlDbType.DateTime2, timeProvider.GetUtcNow().UtcDateTime);
        Add(command, "@organizationId", SqlDbType.Int, configuration.OrganizationId);
        Add(command, "@businessKey", SqlDbType.NVarChar, $"patron-submission:{requestId}", 450);
        Add(command, "@toAddress", SqlDbType.NVarChar, patron.Email, 320);
        Add(command, "@fromAddress", SqlDbType.NVarChar, configuration.Email.FromAddress, 320);
        Add(command, "@fromName", SqlDbType.NVarChar, configuration.Email.FromName, 256);
        Add(command, "@subject", SqlDbType.NVarChar, rendered?.Subject, -1);
        Add(command, "@bodyText", SqlDbType.NVarChar, rendered?.BodyText, -1);
        Add(command, "@bodyHtml", SqlDbType.NVarChar, rendered?.BodyHtml, -1);
        Add(command, "@status", SqlDbType.NVarChar, status, 16);
        Add(command, "@suppressionReason", SqlDbType.NVarChar, suppressionReason, 100);
        var outboxId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return new SubmissionEmailResult(
            status == "pending" ? outboxId : null,
            status == "pending" ? "queued" : "suppressed");
    }

}
