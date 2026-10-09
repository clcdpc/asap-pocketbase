using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed record PatronSuggestionInput(
    string? Format,
    string? Title,
    string? Author,
    string? Isbn,
    string? Publication,
    int? PreferredPickupBranchId,
    bool? Autohold,
    IReadOnlyDictionary<string, string?>? CustomFields);

public sealed record PatronSuggestionResult(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id,
    string SuccessTitle,
    string SuccessMessage,
    string NotificationStatus = "queued",
    int? LibraryOrganizationId = null);

public sealed record PatronSuggestionDuplicate(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Id,
    DateTime Created,
    string Status,
    string CloseReason,
    string Title,
    string Author,
    string Format,
    string MatchType);

public sealed record PatronSuggestionDuplicateConflict(
    string Message,
    string ConflictTitle,
    string ConflictMessage,
    PatronSuggestionDuplicate Duplicate);

public sealed record PatronSuggestionPickupChangedFailure(
    string Message,
    PatronSuggestionDuplicateConflict? DuplicateConflict = null,
    Guid? OperationId = null)
{
    public string Code => "request_not_created_pickup_changed";
    public bool PickupPreferenceChanged => true;
}

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
    string? Notes,
    int? VerifiedBibId);

internal sealed record CreationActor(
    string ActorType,
    long? StaffUserId,
    string? ActorName,
    int? StaffLibraryOrganizationId,
    bool EmailPatronConfirmation,
    bool PickupPreferenceChanged,
    bool PickupPreferenceReconciled = false);

internal sealed record SubmissionEmailResult(long? OutboxId, string Status);

internal sealed record CurrentPatronSubmissionPolicy(
    int SuggestionLimit,
    string SuggestionLimitMessage,
    bool AllowAnyRegisteredCardLogin,
    bool PatronCodeEligibilityEnabled,
    string PatronCodeEligibilityMessage,
    int AllowedPatronCodeCount,
    bool PatronCodeAllowed);

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
    ILogger<PatronSuggestionService> logger,
    IDbContextFactory<AsapDbContext>? contextFactory = null,
    StaffEligibilityService? staffEligibility = null,
    IStaffPolarisProvider? staffPolaris = null,
    PickupPreferenceMutationService? pickupMutationService = null)
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
                  SELECT 1 FROM [asap].[PickupPreferenceOperation] AS pickup
                  WHERE pickup.[TitleRequestId] = request.[Id] AND pickup.[CompletedUtc] IS NULL
              )
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
              AND request.[LegacyHoldProtected] = 0
        """;

    private readonly string connectionString = externalConfiguration.ConnectionStrings.AsapDatabase!;
    private readonly PickupPreferenceMutationService pickupMutations = pickupMutationService ??
        new PickupPreferenceMutationService(externalConfiguration, patronProvider, timeProvider);
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
            patron = await patronProvider.RefreshAsync(session.Barcode, session.EffectiveOrganizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session.NativePatronId <= 0 || patron.PatronId != session.NativePatronId ||
                !patron.KnownBarcodeAliases.Contains(session.Barcode, StringComparer.OrdinalIgnoreCase))
            {
                throw new PatronFlowException(401, "Your patron identity is no longer valid.",
                    new { code = "patron_session_invalid" });
            }
            pickupBranches = await patronProvider.GetPickupBranchesAsync(patron, session.EffectiveOrganizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (PatronFlowException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(
                502,
                "Current patron information could not be loaded from Polaris. Please try again.",
                innerException: exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        EnforcePatronCodeEligibility(configuration, patron);
        var selectedBranch = pickupBranches.SingleOrDefault(
            branch => branch.Id == input.PreferredPickupBranchId);
        if (selectedBranch is null)
        {
            throw new PatronFlowException(400, "Choose a valid preferred pickup location.");
        }

        var suggestion = Validate(input, configuration);
        EmailTransportReadiness emailTransportReadiness;
        try
        {
            emailTransportReadiness = await emailSender.CheckReadinessAsync(
                configuration.OrganizationId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(502, "Email readiness could not be checked. Please try again.",
                new { code = "notification_dependency_unavailable" }, exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        var pickupReceipt = await ChangePickupForSuggestionAsync(patron, configuration.OrganizationId,
            selectedBranch, pickupBranches, "patron_suggestion", null,
            (connection, transaction, token) => ValidatePublicPickupIntentAsync(
                session, patron, connection, transaction, token),
            cancellationToken);
        long requestId = 0;
        long? outboxId = null;
        var notificationStatus = "queued";
        byte[] expectedRowVersion = [];
        try
        {
            try
            {
                // Bind final acceptance to current Polaris identity and home data, then
                // recheck policy under SQL locks in InsertAsync. If pickup already changed,
                // failures here must retain that accepted external effect as partial truth.
                patron = await patronProvider.RefreshAsync(session.Barcode, session.EffectiveOrganizationId, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (patron.PatronId != session.NativePatronId ||
                    !patron.KnownBarcodeAliases.Contains(session.Barcode, StringComparer.OrdinalIgnoreCase))
                {
                    throw new PatronFlowException(401, "Your patron identity is no longer valid.",
                        new { code = "patron_session_invalid" });
                }
            }
            catch (PatronFlowException)
            {
                throw;
            }
            catch (Exception exception) when (exception is PolarisOperationalException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new PatronFlowException(502,
                    "Current patron information could not be loaded from Polaris. Please try again.",
                    new { code = "patron_identity_unavailable" }, exception);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var autoClaimCandidate = await FindAutoClaimCandidateAsync(
                    configuration.OrganizationId,
                    suggestion.Format.Id,
                    cancellationToken);
                try
                {
                    (requestId, outboxId, notificationStatus, expectedRowVersion) = await InsertAsync(
                        session,
                        patron,
                        selectedBranch,
                        suggestion,
                        configuration,
                        autoClaimCandidate,
                        emailTransportReadiness,
                        PatronActor with { PickupPreferenceChanged = pickupReceipt is { ConfirmedByRead: false },
                            PickupPreferenceReconciled = pickupReceipt is { ConfirmedByRead: true } },
                        pickupReceipt,
                        enforcePatronLimit: true,
                        sendSubmissionEmail: true,
                        cancellationToken);
                    break;
                }
                catch (AutoClaimCandidateChangedException) when (attempt < 5)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception exception) when (pickupReceipt is not null &&
                                         exception is PatronFlowException or SqlException or PickupMutationException)
        {
            throw PickupChangedCreationFailure(exception, pickupReceipt, statusCodeOverride: 409);
        }

        if (outboxId.HasValue && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                outboxDispatcher.Enqueue(outboxId.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The accepted request and pending outbox remain recoverable by the sweeper.
            }
            // The request/outbox are committed; the sweeper recovers immediate enqueue failures.
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Submission email outbox {OutboxId} will be recovered by the sweeper.",
                    outboxId.Value);
                notificationStatus = "dispatch_failed";
            }
        }

        if (suggestion.Identifier is not null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessIdentifierLookupAsync(
                    requestId,
                    suggestion.Identifier,
                    configuration.OrganizationId,
                    expectedRowVersion,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Accepted patron suggestion {TitleRequestId}; immediate identifier processing did not complete and will be recovered by the recurring processor.",
                    requestId);
            }
        }

        return new PatronSuggestionResult(
            requestId,
            configuration.SuccessTitle,
            configuration.SuccessMessage,
            notificationStatus);
    }

    public async Task<PatronSuggestionResult> CreateForStaffAsync(
        CurrentStaff actor,
        int organizationId,
        StaffSuggestionInput input,
        CancellationToken cancellationToken)
    {
        if (contextFactory is null || staffEligibility is null)
        {
            throw new PatronFlowException(500, "Staff suggestion creation is not configured.");
        }

        var configuration = await configurationService.GetAsync(organizationId, cancellationToken)
            ?? throw new PatronFlowException(404, "The selected servicing library could not be determined.");
        if (!configuration.IsActive)
        {
            throw new PatronFlowException(409, configuration.SystemNotEnabledMessage);
        }

        var barcode = Clean(input.Barcode)
            ?? throw new PatronFlowException(400, "Verify a patron before submitting the suggestion.");
        var patron = await RefreshStaffPatronAsync(barcode, organizationId, cancellationToken);
        RequireStaffPatronIdentity(patron, expectedPatronId: null, requestedBarcode: barcode);
        var verifiedPatronId = patron.PatronId;
        EnforceStaffPatronEligibility(configuration, patron);
        var pickupBranches = await LoadPickupBranchesAsync(patron, organizationId, cancellationToken);
        var selectedBranch = pickupBranches.SingleOrDefault(
            branch => branch.Id == input.PreferredPickupBranchId);
        if (selectedBranch is null)
        {
            throw new PatronFlowException(400, "Choose a valid preferred pickup location.");
        }

        if ((input.CurrentPreferredPickupBranchObservedAtLoad == true ||
             input.CurrentPreferredPickupBranchIdAtLoad.HasValue) &&
            input.CurrentPreferredPickupBranchIdAtLoad != patron.PreferredPickupBranchId &&
            selectedBranch.Id != patron.PreferredPickupBranchId)
        {
            throw new PatronFlowException(
                409,
                "The patron's preferred pickup location changed. Refresh the patron and review the current selection.",
                new { code = "pickup_changed_since_load" });
        }

        var verifiedBibId = await ValidateStaffBibAsync(input.VerifiedBibId, organizationId, cancellationToken);
        var prepared = await PrepareStaffSuggestionMutationAsync(
            actor,
            organizationId,
            barcode,
            patron,
            input,
            verifiedBibId,
            cancellationToken);
        var pickupChanged = patron.PreferredPickupBranchId != selectedBranch.Id;
        var pickupUpdated = false;
        PickupMutationReceipt? pickupReceipt = null;
        long requestId = 0;
        long? outboxId = null;
        string? persistedIdentifier = null;
        var notificationStatus = input.EmailPatronConfirmation ? "suppressed" : "not_requested";
        byte[] expectedRowVersion = [];
        try
        {
            pickupReceipt = await ChangePickupForSuggestionAsync(patron, organizationId,
                selectedBranch, pickupBranches, "staff_suggestion", actor.Id,
                (connection, transaction, token) => ValidateStaffPickupIntentAsync(
                    actor, organizationId, patron, connection, transaction, token),
                cancellationToken);
            pickupUpdated = pickupReceipt is not null;
            pickupChanged = pickupUpdated;
            if (pickupUpdated)
            {
                // Refresh the snapshot after the external mutation so the persisted patron context
                // is not merely the browser's earlier lookup row.
                var refreshedPatron = await RefreshStaffPatronAsync(barcode, organizationId, cancellationToken);
                RequireStaffPatronIdentity(refreshedPatron, verifiedPatronId, barcode);
                patron = refreshedPatron;
                EnforceStaffPatronEligibility(prepared.Configuration, patron);
                pickupBranches = await LoadPickupBranchesAsync(patron, organizationId, cancellationToken);
                selectedBranch = pickupBranches.SingleOrDefault(branch => branch.Id == input.PreferredPickupBranchId)
                    ?? throw new PatronFlowException(
                        409,
                        "The pickup location changed while the suggestion was being submitted. Refresh the patron and try again.",
                        new { code = "pickup_changed_during_submit" });
            }

            // Keep the DB-only authorization/configuration gate immediately adjacent to the
            // provider mutation. Email readiness is local persistence preparation and does not
            // belong between that gate and the external call.
            EmailTransportReadiness emailReadiness;
            try
            {
                emailReadiness = input.EmailPatronConfirmation
                    ? await emailSender.CheckReadinessAsync(organizationId, cancellationToken)
                    : EmailTransportReadiness.NotConfigured;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new PatronFlowException(502, "Email readiness could not be checked. Please try again.",
                    new { code = "notification_dependency_unavailable" }, exception);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            var creationActor = new CreationActor(
                "staff",
                actor.Id,
                actor.DisplayName ?? actor.UserPrincipalName ?? actor.AuthenticationEmail,
                organizationId,
                input.EmailPatronConfirmation,
                pickupChanged && pickupReceipt?.ConfirmedByRead != true,
                pickupReceipt?.ConfirmedByRead == true);
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var autoClaimCandidate = await FindAutoClaimCandidateAsync(
                    organizationId,
                    prepared.Suggestion.Format.Id,
                    cancellationToken);
                try
                {
                    (requestId, outboxId, notificationStatus, expectedRowVersion, persistedIdentifier) = await InsertStaffAsync(
                        actor,
                        patron,
                        selectedBranch,
                        input,
                        prepared.Suggestion with { VerifiedBibId = verifiedBibId },
                        prepared.Configuration,
                        autoClaimCandidate,
                        emailReadiness,
                        creationActor,
                        pickupReceipt,
                        verifiedPatronId,
                        cancellationToken);
                    break;
                }
                catch (AutoClaimCandidateChangedException) when (attempt < 5)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (pickupUpdated && !cancellationToken.IsCancellationRequested &&
                                         exception is PatronFlowException or SqlException or DbUpdateException or PickupMutationException)
        {
            throw PickupChangedCreationFailure(exception, pickupReceipt!);
        }

        if (outboxId.HasValue && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                outboxDispatcher.Enqueue(outboxId.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The accepted request and pending outbox remain recoverable by the sweeper.
            }
            // Preserve the committed suggestion and durable outbox; the sweeper owns dispatch recovery.
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Staff-created submission email outbox {OutboxId} will be recovered by the sweeper.",
                    outboxId.Value);
                notificationStatus = "dispatch_failed";
            }
        }

        if (persistedIdentifier is not null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessIdentifierLookupAsync(
                    requestId,
                    persistedIdentifier,
                    organizationId,
                    expectedRowVersion,
                    cancellationToken);
            }
            // Suggestion acceptance is committed; recurring identifier processing recovers this optional follow-up.
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Staff-created suggestion {TitleRequestId} was saved, but immediate identifier processing did not complete.",
                    requestId);
            }
        }

        return new PatronSuggestionResult(
            requestId,
            prepared.Configuration.SuccessTitle,
            prepared.Configuration.SuccessMessage,
            notificationStatus,
            organizationId);
    }

    private async Task<PickupMutationReceipt?> ChangePickupForSuggestionAsync(
        PatronSnapshot patron, int organizationId, PickupBranch selected,
        IReadOnlyList<PickupBranch> branches, string origin, long? actorId,
        Func<SqlConnection, SqlTransaction, CancellationToken, Task> validateIntentAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await pickupMutations.ChangeAsync(patron, organizationId, selected,
                branches.SingleOrDefault(branch => branch.Id == patron.PreferredPickupBranchId)?.Label,
                origin, null, actorId, validateIntentAsync, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return receipt;
        }
        catch (PickupMutationException exception)
        {
            throw new PatronFlowException(409,
                "The pickup outcome is durably recorded. Reload the patron's live preference before retrying; an uncertain write will not be repeated.",
                new { exception.Code, exception.OperationId, exception.PickupPreferenceChanged,
                    outcomeUnconfirmed = !exception.PickupPreferenceChanged }, exception);
        }
        catch (PickupMutationBlockedException exception)
        {
            throw new PatronFlowException(409, "A patron write is already in progress. Reload before continuing.",
                new { exception.Code }, exception);
        }
    }

    private async Task ValidatePublicPickupIntentAsync(
        PatronSessionContext session,
        PatronSnapshot patron,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await LockAndValidateParticipatingLibraryAsync(
            connection, transaction, session.EffectiveOrganizationId,
            patron.HomeLibraryOrganizationId, cancellationToken);
        await EnsureCurrentPatronSessionAsync(connection, transaction, session, cancellationToken);

        var policy = await LoadCurrentPatronSubmissionPolicyAsync(
            connection, transaction, session.EffectiveOrganizationId, patron.PatronCodeId, cancellationToken);
        EnforcePatronCodeEligibility(policy, patron);
        EnforceCurrentPatronScope(session, patron, policy);
        await EnforceLimitAsync(connection, transaction, patron.PatronId, patron.KnownBarcodeAliases,
            session.EffectiveOrganizationId, policy, cancellationToken);
    }

    private async Task ValidateStaffPickupIntentAsync(
        CurrentStaff actor,
        int organizationId,
        PatronSnapshot patron,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (contextFactory is null || staffEligibility is null)
        {
            throw new PatronFlowException(500, "Staff suggestion creation is not configured.");
        }

        await using var context = await PickupPreferenceMutationService.BindContextAsync(
            contextFactory, connection, transaction, cancellationToken);
        var organizationIds = new[]
        {
            LibraryScope.SystemOrganizationId,
            organizationId,
            actor.OrganizationId,
            patron.HomeLibraryOrganizationId
        }.Where(item => item > 0).Distinct().ToArray();
        if (!await StaffEligibilityService.LockOrganizationsAsync(context, organizationIds, cancellationToken))
        {
            throw new PatronFlowException(409, "The selected servicing library is not participating.",
                new { code = "organization_inactive" });
        }

        if (context.Organizations.Local.SingleOrDefault(item => item.Id == organizationId) is not { } target ||
            !OrganizationAuthority.IsActiveLibrary(target))
        {
            throw new PatronFlowException(409, "The selected servicing library is not participating.",
                new { code = "organization_inactive" });
        }

        var staffResult = await staffEligibility.RevalidateLockedAsync(
            context, actor, organizationId, StaffRoleRequirement.Any,
            requireActorParticipation: true, organizationIds.ToHashSet(), cancellationToken);
        RequireCurrentStaff(staffResult);

        var configuration = await configurationService.GetAsync(context, organizationId, cancellationToken)
            ?? throw new PatronFlowException(404, "The selected servicing library could not be determined.");
        if (!configuration.IsActive)
        {
            throw new PatronFlowException(409, configuration.SystemNotEnabledMessage);
        }
        if (patron.HomeLibraryOrganizationId <= 1 ||
            context.Organizations.Local.SingleOrDefault(item => item.Id == patron.HomeLibraryOrganizationId) is not { } home ||
            !OrganizationAuthority.IsActiveLibrary(home))
        {
            throw new PatronFlowException(403, "The patron's home library is no longer participating.",
                new { code = "patron_home_library_inactive" });
        }
        EnforceStaffPatronEligibility(configuration, patron);
    }

    private static void RequireStaffPatronIdentity(
        PatronSnapshot patron,
        int? expectedPatronId,
        string requestedBarcode)
    {
        if (patron.PatronId <= 0 || expectedPatronId.HasValue && patron.PatronId != expectedPatronId.Value ||
            !patron.KnownBarcodeAliases.Contains(requestedBarcode, StringComparer.OrdinalIgnoreCase))
        {
            throw new PatronFlowException(409,
                "The verified patron identity changed while the suggestion was being submitted.",
                new { code = "patron_identity_changed" });
        }
    }

    private static async Task LockAndValidateParticipatingLibraryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int organizationId,
        int homeOrganizationId,
        CancellationToken cancellationToken)
    {
        foreach (var id in new[] { LibraryScope.SystemOrganizationId, organizationId, homeOrganizationId }.Distinct().Order())
        {
            await using var command = new SqlCommand("""
                SELECT [Id], [OrganizationCodeId], [IsActive]
                FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK)
                WHERE [Id] = @id;
                """, connection, transaction);
            command.Parameters.Add("@id", System.Data.SqlDbType.Int).Value = id;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new PatronFlowException(403, "Your library could not be determined.",
                    new { code = "organization_inactive" });
            }
            var active = reader.GetBoolean(2);
            var codeId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            await reader.CloseAsync();
            if (id == LibraryScope.SystemOrganizationId && !active ||
                id == organizationId &&
                (!active || codeId != OrganizationAuthority.LibraryOrganizationCodeId) ||
                id == homeOrganizationId && codeId != OrganizationAuthority.LibraryOrganizationCodeId)
            {
                throw new PatronFlowException(403, "Your library is no longer participating.",
                    new { code = "organization_inactive" });
            }
        }
    }

    private static PatronFlowException PickupChangedCreationFailure(
        Exception exception, PickupMutationReceipt receipt, int? statusCodeOverride = null)
    {
        var flowException = exception as PatronFlowException;
        var message = "The suggestion was not confirmed, but the patron's preferred pickup location was changed successfully. " +
                      (flowException?.Message ?? "Reload the live patron and request state before retrying.");
        return new PatronFlowException(statusCodeOverride ?? flowException?.StatusCode ?? 502, message,
            new PatronSuggestionPickupChangedFailure(message,
                flowException?.Response as PatronSuggestionDuplicateConflict, receipt.OperationId), exception);
    }

    private async Task<PatronSnapshot> RefreshStaffPatronAsync(
        string barcode,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var patron = await patronProvider.RefreshAsync(barcode, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return patron;
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (exception is PolarisOperationalException { Code: "polaris_patron_not_found" or "polaris_patron_invalid_barcode" })
            {
                throw new PatronFlowException(
                    404,
                    "That patron could not be found in Polaris.",
                    new { code = "patron_not_found", status = "not_found" },
                    exception);
            }

            throw new PatronFlowException(
                502,
                "Current patron information could not be loaded from Polaris. Please try again.",
                new { code = "polaris_unavailable" },
                exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private async Task<IReadOnlyList<PickupBranch>> LoadPickupBranchesAsync(
        PatronSnapshot patron,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var branches = await patronProvider.GetPickupBranchesAsync(patron, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return branches;
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(
                502,
                "Eligible pickup locations could not be loaded from Polaris.",
                new { code = "pickup_branches_unavailable" },
                exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private async Task<int?> ValidateStaffBibAsync(
        int? bibId,
        int organizationId, CancellationToken cancellationToken)
    {
        if (bibId is null)
        {
            return null;
        }

        if (bibId <= 0)
        {
            throw new PatronFlowException(
                400,
                "The selected Polaris BIB is invalid.",
                new { code = "invalid_bib" });
        }

        if (staffPolaris is null)
        {
            throw new PatronFlowException(500, "Polaris BIB verification is not configured.");
        }

        BibValidationResult result;
        try
        {
            result = await staffPolaris.ValidateBibAsync(bibId.Value, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(
                502,
                "Catalog lookup is temporarily unavailable.",
                new { code = "bib_validation_unavailable" },
                exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        if (!result.IsValid)
        {
            throw new PatronFlowException(
                404,
                "The selected Polaris BIB could not be found.",
                new { code = "bib_not_found" });
        }

        return bibId;
    }

    private async Task<(EffectivePatronConfiguration Configuration, ValidatedSuggestion Suggestion)>
        PrepareStaffSuggestionMutationAsync(
        CurrentStaff actor,
        int organizationId,
        string barcode,
        PatronSnapshot patron,
        StaffSuggestionInput input,
        int? verifiedBibId,
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
        var lockedOrganizationIds = new HashSet<int>();
        foreach (var currentOrganizationId in new[]
                     { organizationId, actor.OrganizationId, patron.PatronOrganizationId, patron.HomeLibraryOrganizationId }
                     .Where(item => item > 0)
                     .Distinct()
                     .Order())
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {currentOrganizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            var isTargetOrganization = currentOrganizationId == organizationId;
            var isPatronOrganization = currentOrganizationId == patron.PatronOrganizationId;
            var isPatronHomeOrganization = currentOrganizationId == patron.HomeLibraryOrganizationId;
            if (organization is null ||
                isTargetOrganization && !OrganizationAuthority.IsActiveLibrary(organization) ||
                isPatronHomeOrganization && !OrganizationAuthority.IsActiveLibrary(organization))
            {
                throw new PatronFlowException(
                    409,
                    isPatronOrganization && organization is null
                        ? "The patron's registered organization is no longer available."
                        : isPatronHomeOrganization && !isTargetOrganization
                        ? "The patron's home library is no longer participating."
                        : "The selected servicing library is not participating.",
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
            organizationId,
            StaffRoleRequirement.Any,
            requireActorParticipation: true,
            lockedOrganizationIds,
            cancellationToken);
        RequireCurrentStaff(staffResult);

        var configuration = await configurationService.GetAsync(context, organizationId, cancellationToken)
            ?? throw new PatronFlowException(404, "The selected servicing library could not be determined.");
        if (!configuration.IsActive)
        {
            throw new PatronFlowException(409, configuration.SystemNotEnabledMessage);
        }

        EnforceStaffPatronEligibility(configuration, patron);
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
            forcedAutoHold: input.Autohold ?? true,
            exactPublicationDate: input.ExactPublicationDate,
            notes: input.Notes,
            verifiedBibId: verifiedBibId);
        var connection = (SqlConnection)context.Database.GetDbConnection();
        var transaction = (SqlTransaction)databaseTransaction.GetDbTransaction();
        await LockAndValidateFormatAsync(
            connection,
            transaction,
            configuration.OrganizationId,
            suggestion,
            cancellationToken);
        await EnforceDuplicateAsync(
            connection,
            transaction,
            patron.PatronId,
            patron.KnownBarcodeAliases,
            suggestion,
            configuration,
            cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
        return (configuration, suggestion);
    }

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
