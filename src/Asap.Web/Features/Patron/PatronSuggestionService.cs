using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Security;

namespace Asap.Web.Features.Patron;

public sealed record PatronSuggestionInput(
    string? Format,
    string? Title,
    string? Author,
    string? Isbn,
    string? Publication,
    int? PreferredPickupBranchId,
    bool? Autohold,
    IReadOnlyDictionary<string, string?>? CustomFields);

public sealed record PatronSuggestionResult(long Id, string SuccessTitle, string SuccessMessage);

public sealed class PatronFlowException(
    int statusCode,
    string message,
    object? response = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
    public object? Response { get; } = response;
}

internal sealed record ValidatedSuggestion(
    EffectiveFormatRule Format,
    string Title,
    string? Author,
    string? Identifier,
    string? Publication,
    bool AutoHold,
    string? CustomFieldsJson,
    DateOnly? ExactPublicationDate,
    string? Notes);

internal sealed record CreationActor(
    string ActorType,
    long? StaffUserId,
    string? ActorName,
    int? StaffLibraryOrganizationId,
    bool EmailPatronConfirmation,
    bool PickupPreferenceChanged);

internal sealed record AutoClaimCandidate(long RuleId, long StaffUserId);

internal sealed record LockedAutoClaimTarget(long StaffUserId, string DisplayName);

internal sealed class AutoClaimCandidateChangedException : Exception;

internal sealed record IdentifierLookupProcessingResult(
    IdentifierLookupOutcome Outcome,
    byte[]? QueueProgressVersion = null);

public sealed partial class PatronSuggestionService(
    ExternalConfiguration externalConfiguration,
    PatronConfigurationService configurationService,
    IPatronProvider patronProvider,
    IEmailOutboxDispatcher outboxDispatcher,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    TimeProvider timeProvider,
    ILogger<PatronSuggestionService> logger)
{
    private static readonly CreationActor PatronActor = new(
        "patron",
        null,
        null,
        null,
        true,
        false);

    private const string IdentifierMutationBarrierPredicate = """
              AND [Status] = N'suggestion'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [asap].[HoldPlacementOperation] AS incomplete
                  WHERE incomplete.[TitleRequestId] = request.[Id]
                    AND incomplete.[CompletedUtc] IS NULL
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [asap].[HoldPlacementOperation] AS successful
                  WHERE successful.[TitleRequestId] = request.[Id]
                    AND successful.[State] = N'succeeded'
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [asap].[TitleRequestEvent] AS legacy
                  WHERE legacy.[TitleRequestId] = request.[Id]
                    AND ISJSON(legacy.[MetadataJson]) = 1
                    AND JSON_VALUE(legacy.[MetadataJson], '$.legacyBibProtection') = N'true'
              )
        """;

    private readonly string connectionString = externalConfiguration.ConnectionStrings.AsapDatabase!;
    private readonly TimeZoneInfo businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        externalConfiguration.Application.BusinessTimeZone!);

    public async Task<PatronSuggestionResult> CreateAsync(
        PatronSessionContext session,
        PatronSuggestionInput input,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationService.GetAsync(
            session.EffectiveOrganizationId,
            cancellationToken)
            ?? throw new PatronFlowException(403, "Your library could not be determined.");
        if (!configuration.IsActive)
        {
            throw new PatronFlowException(403, configuration.SystemNotEnabledMessage);
        }

        PatronSnapshot patron;
        IReadOnlyList<PickupBranch> pickupBranches;
        try
        {
            patron = await patronProvider.RefreshAsync(session.Barcode, cancellationToken);
            pickupBranches = await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
        }
        catch (PolarisOperationalException exception)
        {
            throw new PatronFlowException(
                502,
                "Current patron information could not be loaded from Polaris. Please try again.",
                innerException: exception);
        }

        EnforcePatronCodeEligibility(configuration, patron);
        var selectedBranch = pickupBranches.SingleOrDefault(
            branch => branch.Id == input.PreferredPickupBranchId);
        if (selectedBranch is null)
        {
            throw new PatronFlowException(400, "Choose a valid preferred pickup location.");
        }

        if (patron.PreferredPickupBranchId != selectedBranch.Id)
        {
            try
            {
                await patronProvider.UpdatePreferredPickupBranchAsync(
                    session.Barcode,
                    selectedBranch.Id,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new PatronFlowException(
                    502,
                    "Your preferred pickup location could not be updated in Polaris. Please try again.",
                    innerException: exception);
            }
        }

        var suggestion = Validate(input, configuration);
        var emailTransportReadiness = await emailSender.CheckReadinessAsync(
            configuration.OrganizationId,
            cancellationToken);
        long requestId = 0;
        long? outboxId = null;
        byte[] expectedRowVersion = [];
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var autoClaimCandidate = await FindAutoClaimCandidateAsync(
                configuration.OrganizationId,
                suggestion.Format.Id,
                cancellationToken);
            try
            {
                (requestId, outboxId, expectedRowVersion) = await InsertAsync(
                    session.Barcode,
                    patron,
                    selectedBranch,
                    suggestion,
                    configuration,
                    autoClaimCandidate,
                    emailTransportReadiness,
                    PatronActor,
                    enforcePatronLimit: true,
                    sendSubmissionEmail: true,
                    cancellationToken: cancellationToken);
                break;
            }
            catch (AutoClaimCandidateChangedException) when (attempt < 5)
            {
                continue;
            }
            catch (AutoClaimCandidateChangedException exception)
            {
                throw new PatronFlowException(
                    409,
                    "The automatic assignment changed while the suggestion was submitted. Please try again.",
                    innerException: exception);
            }
        }

        if (outboxId.HasValue)
        {
            try
            {
                outboxDispatcher.Enqueue(outboxId.Value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Submission email outbox {OutboxId} will be recovered by the sweeper.",
                    outboxId.Value);
            }
        }

        if (suggestion.Identifier is not null)
        {
            await ProcessIdentifierLookupAsync(
                requestId,
                suggestion.Identifier,
                configuration.OrganizationId,
                expectedRowVersion,
                cancellationToken);
        }

        return new PatronSuggestionResult(
            requestId,
            configuration.SuccessTitle,
            configuration.SuccessMessage);
    }

    public async Task<PatronSuggestionResult> CreateForStaffAsync(
        CurrentStaff actor,
        int organizationId,
        StaffSuggestionInput input,
        CancellationToken cancellationToken)
    {
        var configuration = await configurationService.GetAsync(organizationId, cancellationToken)
            ?? throw new PatronFlowException(403, "The selected servicing library could not be determined.");
        if (!configuration.IsActive)
        {
            throw new PatronFlowException(403, configuration.SystemNotEnabledMessage);
        }

        var barcode = Clean(input.Barcode)
            ?? throw new PatronFlowException(400, "Verify a patron before submitting the suggestion.");
        PatronSnapshot patron;
        IReadOnlyList<PickupBranch> pickupBranches;
        try
        {
            patron = await patronProvider.RefreshAsync(barcode, cancellationToken);
            pickupBranches = await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
        }
        catch (PolarisOperationalException exception)
        {
            throw new PatronFlowException(
                502,
                "Current patron information could not be loaded from Polaris. Please try again.",
                innerException: exception);
        }

        if (!configuration.AllowAnyRegisteredCardLogin &&
            patron.HomeLibraryOrganizationId != organizationId)
        {
            throw new PatronFlowException(
                403,
                "This patron is not eligible for the selected servicing library.",
                new { code = "patron_library_forbidden" });
        }

        var selectedBranch = pickupBranches.SingleOrDefault(
            branch => branch.Id == input.PreferredPickupBranchId);
        if (selectedBranch is null)
        {
            throw new PatronFlowException(400, "Choose a valid preferred pickup location.");
        }

        if (input.CurrentPreferredPickupBranchIdAtLoad.HasValue &&
            input.CurrentPreferredPickupBranchIdAtLoad != patron.PreferredPickupBranchId &&
            selectedBranch.Id == input.CurrentPreferredPickupBranchIdAtLoad)
        {
            throw new PatronFlowException(
                409,
                "The patron's preferred pickup location changed. Refresh the patron and review the current selection.",
                new { code = "pickup_changed_since_load" });
        }

        var suggestion = Validate(
            new PatronSuggestionInput(
                input.Format,
                input.Title,
                input.Author,
                input.Identifier,
                input.Publication,
                input.PreferredPickupBranchId,
                input.Autohold,
                input.CustomFields),
            configuration,
            allowInformationalMessage: true,
            forcedAutoHold: input.Autohold,
            exactPublicationDate: input.ExactPublicationDate,
            notes: input.Notes);
        var emailTransportReadiness = input.EmailPatronConfirmation
            ? await emailSender.CheckReadinessAsync(organizationId, cancellationToken)
            : EmailTransportReadiness.NotConfigured;
        var pickupPreferenceChanged = patron.PreferredPickupBranchId != selectedBranch.Id;
        if (pickupPreferenceChanged)
        {
            try
            {
                await patronProvider.UpdatePreferredPickupBranchAsync(
                    barcode,
                    selectedBranch.Id,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new PatronFlowException(
                    502,
                    "The patron's preferred pickup location could not be updated in Polaris. The suggestion was not created.",
                    new { code = "pickup_update_failed" },
                    exception);
            }
        }

        var creationActor = new CreationActor(
            "staff",
            actor.Id,
            actor.DisplayName ?? actor.UserPrincipalName ?? actor.AuthenticationEmail,
            organizationId,
            input.EmailPatronConfirmation,
            pickupPreferenceChanged);

        long requestId = 0;
        long? outboxId = null;
        byte[] expectedRowVersion = [];
        try
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var autoClaimCandidate = await FindAutoClaimCandidateAsync(
                    organizationId,
                    suggestion.Format.Id,
                    cancellationToken);
                try
                {
                    (requestId, outboxId, expectedRowVersion) = await InsertAsync(
                        barcode,
                        patron,
                        selectedBranch,
                        suggestion,
                        configuration,
                        autoClaimCandidate,
                        emailTransportReadiness,
                        creationActor,
                        enforcePatronLimit: false,
                        sendSubmissionEmail: input.EmailPatronConfirmation,
                        cancellationToken: cancellationToken);
                    break;
                }
                catch (AutoClaimCandidateChangedException) when (attempt < 5)
                {
                    continue;
                }
                catch (AutoClaimCandidateChangedException exception)
                {
                    throw new PatronFlowException(
                        409,
                        "The automatic assignment changed while the suggestion was submitted. Please try again.",
                        innerException: exception);
                }
            }
        }
        catch (PatronFlowException exception) when (pickupPreferenceChanged)
        {
            throw new PatronFlowException(
                exception.StatusCode,
                $"The suggestion was not created, but the patron's preferred pickup location was changed successfully. {exception.Message}",
                new
                {
                    code = "request_not_created_pickup_changed",
                    message = $"The suggestion was not created, but the patron's preferred pickup location was changed successfully. {exception.Message}",
                    originalResponse = exception.Response
                },
                exception);
        }
        catch (Exception exception) when (pickupPreferenceChanged && exception is not OperationCanceledException)
        {
            throw new PatronFlowException(
                500,
                "The suggestion was not created, but the patron's preferred pickup location was changed successfully. Try submitting again.",
                new { code = "request_not_created_pickup_changed" },
                exception);
        }

        if (outboxId.HasValue)
        {
            try
            {
                outboxDispatcher.Enqueue(outboxId.Value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Staff-created submission email outbox {OutboxId} will be recovered by the sweeper.",
                    outboxId.Value);
            }
        }

        if (suggestion.Identifier is not null)
        {
            await ProcessIdentifierLookupAsync(
                requestId,
                suggestion.Identifier,
                organizationId,
                expectedRowVersion,
                cancellationToken);
        }

        return new PatronSuggestionResult(
            requestId,
            configuration.SuccessTitle,
            configuration.SuccessMessage);
    }

    private async Task<(long RequestId, long? OutboxId, byte[] RowVersion)> InsertAsync(
        string barcode,
        PatronSnapshot patron,
        PickupBranch selectedBranch,
        ValidatedSuggestion suggestion,
        EffectivePatronConfiguration configuration,
        AutoClaimCandidate? autoClaimCandidate,
        EmailTransportReadiness emailTransportReadiness,
        CreationActor creationActor,
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
                 [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            OUTPUT inserted.[Id]
            VALUES
                (@libraryOrganizationId, @patronOrganizationId, @staffLibraryOrganizationIdCreatedBy, @barcode, @email,
                 @nameFirst, @nameLast, @patronCodeId, @patronCodeDescription,
                 @pickupBranchId, @pickupBranchName, @libraryName,
                 @title, @author, @identifier, @publication, @exactPublicationDate, @customFieldsJson, @autoHold, @notes,
                 @materialFormatId, N'suggestion', @isbnCheckStatus, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection,
            transaction))
        {
            Add(insert, "@libraryOrganizationId", SqlDbType.Int, configuration.OrganizationId);
            Add(insert, "@patronOrganizationId", SqlDbType.Int, patron.PatronOrganizationId);
            Add(insert, "@staffLibraryOrganizationIdCreatedBy", SqlDbType.Int, creationActor.StaffLibraryOrganizationId);
            Add(insert, "@barcode", SqlDbType.NVarChar, barcode, 50);
            Add(insert, "@email", SqlDbType.NVarChar, patron.Email, 320);
            Add(insert, "@nameFirst", SqlDbType.NVarChar, patron.NameFirst, 256);
            Add(insert, "@nameLast", SqlDbType.NVarChar, patron.NameLast, 256);
            Add(insert, "@patronCodeId", SqlDbType.NVarChar, patron.PatronCodeId, 100);
            Add(insert, "@patronCodeDescription", SqlDbType.NVarChar, patron.PatronCodeDescription, 256);
            Add(insert, "@pickupBranchId", SqlDbType.Int, selectedBranch.Id);
            Add(insert, "@pickupBranchName", SqlDbType.NVarChar, selectedBranch.Label, 256);
            Add(insert, "@libraryName", SqlDbType.NVarChar, configuration.OrganizationName, 256);
            Add(insert, "@title", SqlDbType.NVarChar, suggestion.Title, 500);
            Add(insert, "@author", SqlDbType.NVarChar, suggestion.Author, 500);
            Add(insert, "@identifier", SqlDbType.NVarChar, suggestion.Identifier, 100);
            Add(insert, "@publication", SqlDbType.NVarChar, suggestion.Publication, 200);
            Add(
                insert,
                "@exactPublicationDate",
                SqlDbType.Date,
                suggestion.ExactPublicationDate?.ToDateTime(TimeOnly.MinValue));
            Add(insert, "@customFieldsJson", SqlDbType.NVarChar, suggestion.CustomFieldsJson, -1);
            Add(insert, "@autoHold", SqlDbType.Bit, suggestion.AutoHold);
            Add(insert, "@notes", SqlDbType.NVarChar, suggestion.Notes, -1);
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
        var outboxId = sendSubmissionEmail
            ? await InsertSubmissionEmailAsync(
                connection,
                transaction,
                requestId,
                patron,
                suggestion,
                configuration,
                emailTransportReadiness,
                cancellationToken)
            : null;
        byte[] rowVersion;
        await using (var version = new SqlCommand(
            "SELECT [RowVersion] FROM [asap].[TitleRequest] WHERE [Id] = @requestId;",
            connection,
            transaction))
        {
            Add(version, "@requestId", SqlDbType.BigInt, requestId);
            rowVersion = (byte[])(await version.ExecuteScalarAsync(cancellationToken))!;
        }

        await transaction.CommitAsync(cancellationToken);
        return (requestId, outboxId, rowVersion);
    }

    private static async Task LockAndValidateOrganizationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT [IsActive] FROM [asap].[Organization] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @id;",
            connection,
            transaction);
        Add(command, "@id", SqlDbType.Int, configuration.OrganizationId);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new PatronFlowException(403, configuration.SystemNotEnabledMessage);
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
        throw new PatronFlowException(406, message);
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
                       N'title_format' AS [MatchType], 1 AS [MatchPriority]
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
            ["duplicate_match_type"] = matchType == "identifier" ? "identifier number" : "title and format",
            ["title"] = title,
            ["author"] = author,
            ["format"] = formatLabel
        };
        var conflictMessage = DuplicatePlaceholderRegex().Replace(
            configuration.AlreadySubmittedMessage,
            match => replacements.TryGetValue(match.Groups[1].Value, out var value)
                ? WebUtility.HtmlEncode(value)
                : match.Value);
        var message = matchType == "identifier"
            ? "This patron already has a suggestion for this identifier number."
            : "This patron already has this suggestion.";
        throw new PatronFlowException(
            409,
            message,
            new
            {
                message,
                conflictTitle = "Already Submitted",
                conflictMessage,
                duplicate = new
                {
                    id,
                    created,
                    status,
                    closeReason = closeReason ?? string.Empty,
                    title,
                    author,
                    format = formatCode,
                    matchType
                }
            });
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
            SELECT [DisplayName], [UserPrincipalName]
            FROM [asap].[StaffUser] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Id] = @staffId
              AND [IsActive] = 1
              AND NULLIF(LTRIM(RTRIM([UserPrincipalName])), N'') IS NOT NULL
              AND [NormalizedUserPrincipalName] = UPPER(LTRIM(RTRIM([UserPrincipalName])))
              AND
              (
                  ([Role] IN (N'staff', N'admin') AND [OrganizationId] = @organizationId) OR
                  ([Role] = N'super_admin' AND [OrganizationId] = 1)
              );
            """,
            connection,
            transaction);
        Add(command, "@staffId", SqlDbType.BigInt, candidate.StaffUserId);
        Add(command, "@organizationId", SqlDbType.Int, organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var displayName = reader.IsDBNull(0) ? null : reader.GetString(0);
        var userPrincipalName = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new LockedAutoClaimTarget(
            candidate.StaffUserId,
            displayName ?? userPrincipalName ?? "Staff");
    }

    private static async Task ApplyAutoClaimAsync(
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
                     @metadataJson, SYSUTCDATETIME());
                """,
                connection,
                transaction);
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
                [ClaimedAtUtc] = SYSUTCDATETIME(),
                [ClaimType] = N'automatic_format_rule',
                [ClaimRuleId] = @ruleId,
                [UpdatedUtc] = SYSUTCDATETIME()
            WHERE [Id] = @requestId;
            """,
            connection,
            transaction);
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
                 @message, @metadataJson, SYSUTCDATETIME());
            """,
            connection,
            transaction);
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

    private static async Task ApplyCrossPatronDuplicateTagAsync(
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
                    [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [Id] = @requestId;
            END;
            """,
            connection,
            transaction);
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

    private static async Task InsertCreationEventAsync(
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
            pickupPreferenceChanged = actor.PickupPreferenceChanged
        });
        await using var command = new SqlCommand(
            """
            INSERT INTO [asap].[TitleRequestEvent]
                ([TitleRequestId], [EventType], [Status], [ActorType], [StaffUserId], [ActorName], [Message], [MetadataJson], [CreatedUtc])
            VALUES
                (@requestId, N'created', N'suggestion', @actorType, @staffUserId, @actorName, @message, @metadataJson, SYSUTCDATETIME());
            """,
            connection,
            transaction);
        Add(command, "@requestId", SqlDbType.BigInt, requestId);
        Add(command, "@actorType", SqlDbType.NVarChar, actor.ActorType, 16);
        Add(command, "@staffUserId", SqlDbType.BigInt, actor.StaffUserId);
        Add(command, "@actorName", SqlDbType.NVarChar, actor.ActorName, 256);
        Add(command, "@message", SqlDbType.NVarChar, message, -1);
        Add(command, "@metadataJson", SqlDbType.NVarChar, metadata, -1);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long?> InsertSubmissionEmailAsync(
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
                 CASE WHEN @status = N'pending' THEN SYSUTCDATETIME() ELSE NULL END,
                 SYSUTCDATETIME(),
                 CASE WHEN @status = N'suppressed' THEN SYSUTCDATETIME() ELSE NULL END);
            """,
            connection,
            transaction);
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
        return status == "pending" ? outboxId : null;
    }

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
            result = await patronProvider.LookupIdentifierAsync(identifier, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Immediate identifier lookup failed for title request {TitleRequestId}.",
                requestId);
            result = new IdentifierLookupResult(
                IdentifierLookupOutcome.OperationalFailure,
                ErrorCode: "polaris_identifier_lookup_failed");
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
            SELECT [IsActive]
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
        var requestFound = false;
        await using (var request = new SqlCommand(
            $"""
            SELECT request.[Title], request.[Author]
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
            IdentifierLookupOutcome.Found when !string.IsNullOrWhiteSpace(result.BibId) => "found",
            IdentifierLookupOutcome.DefinitiveNotFound => "not_found",
            IdentifierLookupOutcome.TransientFailure => "pending",
            _ => null
        };
        var retryIncrement = result.Outcome == IdentifierLookupOutcome.TransientFailure ? 1 : 0;
        var reconciledTitle = status == "found"
            ? MergeCatalogValue(result.CatalogTitle, currentTitle)
            : currentTitle;
        var reconciledAuthor = status == "found"
            ? MergeCatalogValue(result.CatalogAuthor, currentAuthor)
            : currentAuthor;
        var note = status switch
        {
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
                "Identifier number search found multiple Polaris matches; ASAP used the first result by publication date descending.");
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
                [BibId] = CASE WHEN @status IN (N'found', N'not_found') THEN @bibId ELSE [BibId] END,
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
                [LastCheckedUtc] = SYSUTCDATETIME(),
                [UpdatedUtc] = SYSUTCDATETIME()
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
            Add(update, "@status", SqlDbType.NVarChar, status, 32);
            Add(update, "@retryIncrement", SqlDbType.Int, retryIncrement);
            Add(update, "@bibId", SqlDbType.NVarChar, result.BibId, 100);
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
                    [LastOutcomeUtc] = SYSUTCDATETIME(),
                    [UpdatedUtc] = SYSUTCDATETIME()
                OUTPUT inserted.[RowVersion]
                WHERE [QueueName] = @queueName
                  AND [ScopeOrganizationId] = @scope
                  AND [RowVersion] = @rowVersion;
                """,
                connection,
                transaction);
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

    private static ValidatedSuggestion Validate(
        PatronSuggestionInput input,
        EffectivePatronConfiguration configuration,
        bool allowInformationalMessage = false,
        bool? forcedAutoHold = null,
        DateOnly? exactPublicationDate = null,
        string? notes = null)
    {
        var formatCode = Clean(input.Format) ?? "book";
        var format = configuration.Formats.SingleOrDefault(item =>
            item.IsEnabled && string.Equals(item.Code, formatCode, StringComparison.Ordinal));
        if (format is null)
        {
            throw new PatronFlowException(400, "Choose a valid material format.");
        }

        if (!allowInformationalMessage &&
            !string.Equals(format.MessageBehavior, "none", StringComparison.Ordinal))
        {
            var message = format.MessageBehavior switch
            {
                "ebookMessage" => configuration.EbookMessage,
                "eaudiobookMessage" => configuration.EaudiobookMessage,
                _ => format.Message
            };
            throw new PatronFlowException(
                400,
                string.IsNullOrWhiteSpace(message)
                    ? "This format is informational only and cannot be submitted from the patron form."
                    : message);
        }

        var title = ValidateField(input.Title, format.Title, 500, forceRequired: true);
        var author = ValidateField(input.Author, format.Author, 500);
        var identifier = ValidateField(input.Isbn, format.Identifier, 100);
        var publication = ValidateField(input.Publication, format.Publication, 200);
        if (publication is not null &&
            !configuration.PublicationOptions.Contains(publication, StringComparer.Ordinal))
        {
            throw new PatronFlowException(400, $"{format.Publication.Label} is invalid.");
        }

        var customFields = ValidateCustomFields(input.CustomFields, format, configuration.CustomFields);
        var cleanedNotes = Clean(notes);
        if (cleanedNotes?.Length > 10000)
        {
            throw new PatronFlowException(400, "Notes cannot exceed 10000 characters.");
        }

        return new ValidatedSuggestion(
            format,
            TitleCase(title!),
            author,
            identifier,
            publication,
            forcedAutoHold ?? (configuration.AllowPatronAutoholdOptOut ? input.Autohold ?? true : true),
            customFields,
            exactPublicationDate,
            cleanedNotes);
    }

    private static string? ValidateField(
        string? raw,
        EffectiveFieldRule rule,
        int maxLength,
        bool forceRequired = false)
    {
        if (rule.Mode == "hidden" && !forceRequired)
        {
            return null;
        }

        var value = Clean(raw);
        if (value?.Length > maxLength)
        {
            throw new PatronFlowException(400, $"{rule.Label} cannot exceed {maxLength} characters.");
        }

        if ((forceRequired || rule.Mode == "required") && value is null)
        {
            throw new PatronFlowException(400, $"{rule.Label} is required.");
        }

        return value;
    }

    private static string? ValidateCustomFields(
        IReadOnlyDictionary<string, string?>? submitted,
        EffectiveFormatRule format,
        IReadOnlyList<EffectiveCustomField> definitions)
    {
        var snapshot = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!format.CustomFields.TryGetValue(definition.Key, out var rule) || rule.Mode == "hidden")
            {
                continue;
            }

            string? raw = null;
            submitted?.TryGetValue(definition.Key, out raw);
            var value = Clean(raw);
            if (value is null)
            {
                if (rule.Mode == "required")
                {
                    throw new PatronFlowException(400, $"{definition.Label} is required.");
                }

                continue;
            }

            if (definition.Type == "select")
            {
                var option = definition.Options.SingleOrDefault(item =>
                    string.Equals(item.Key, value, StringComparison.Ordinal) ||
                    string.Equals(item.Label, value, StringComparison.Ordinal));
                if (option is null)
                {
                    if (rule.Mode == "required")
                    {
                        throw new PatronFlowException(400, $"{definition.Label} is required.");
                    }

                    continue;
                }

                snapshot[definition.Key] = new
                {
                    label = definition.Label,
                    type = definition.Type,
                    value = option.Key,
                    displayValue = option.Label
                };
                continue;
            }

            var maxLength = definition.Type == "textarea" ? 2000 : 250;
            snapshot[definition.Key] = new
            {
                label = definition.Label,
                type = definition.Type,
                value = value[..Math.Min(value.Length, maxLength)]
            };
        }

        return snapshot.Count == 0 ? null : JsonSerializer.Serialize(snapshot);
    }

    private static void EnforcePatronCodeEligibility(
        EffectivePatronConfiguration configuration,
        PatronSnapshot patron)
    {
        if (!configuration.PatronCodeEligibilityEnabled ||
            configuration.AllowedPatronCodeIds.Count == 0 ||
            string.IsNullOrWhiteSpace(patron.PatronCodeId) ||
            configuration.AllowedPatronCodeIds.Contains(patron.PatronCodeId))
        {
            return;
        }

        throw new PatronFlowException(403, configuration.PatronCodeEligibilityMessage);
    }

    private static string? Missing(
        string? recipient,
        string? sender,
        EffectiveEmailTemplate? template,
        RecipientDomainPolicy recipientDomainPolicy,
        EmailTransportReadiness emailTransportReadiness)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return "recipient_missing";
        }

        if (string.IsNullOrWhiteSpace(sender))
        {
            return "sender_missing";
        }

        if (template is null)
        {
            return "template_missing";
        }

        if (!recipientDomainPolicy.IsAllowed(recipient))
        {
            return "recipient_domain_not_allowed";
        }

        if (!emailTransportReadiness.IsConfigured)
        {
            return "mail_not_configured";
        }

        return null;
    }

    private static string ParticipationMessage(EffectivePatronConfiguration configuration) =>
        $"The {configuration.OrganizationName} suggestion service is not currently enabled.";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string MergeCatalogValue(string? catalogValue, string? originalValue)
    {
        var catalog = Clean(catalogValue);
        var original = Clean(originalValue);
        if (catalog is null)
        {
            return original ?? string.Empty;
        }

        if (original is null || string.Equals(original, catalog, StringComparison.Ordinal))
        {
            return catalog;
        }

        if (original.StartsWith(catalog + " (", StringComparison.Ordinal))
        {
            return original;
        }

        var oldBase = Regex.Replace(original, @"\s+\([^()]*\)\s*$", string.Empty).Trim();
        return oldBase.Length > 0 &&
               (string.Equals(oldBase, catalog, StringComparison.Ordinal) ||
                oldBase.StartsWith(catalog, StringComparison.Ordinal) ||
                catalog.StartsWith(oldBase, StringComparison.Ordinal))
            ? original
            : $"{catalog} ({original})";
    }

    private static string? JoinNote(string? first, string second) =>
        first is null ? second : first + Environment.NewLine + second;

    [GeneratedRegex(@"\w\S*", RegexOptions.ECMAScript | RegexOptions.CultureInvariant)]
    private static partial Regex TitleWordRegex();

    [GeneratedRegex("{{(\\w+)}}", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicatePlaceholderRegex();

    internal static string TitleCase(string value) =>
        TitleWordRegex().Replace(
            value.Trim(),
            match => char.ToUpperInvariant(match.Value[0]) +
                     match.Value[1..].ToLowerInvariant());

    private static void Add(
        SqlCommand command,
        string name,
        SqlDbType type,
        object? value,
        int size = 0)
    {
        var parameter = size == 0
            ? command.Parameters.Add(name, type)
            : command.Parameters.Add(name, type, size);
        parameter.Value = value ?? DBNull.Value;
    }
}
