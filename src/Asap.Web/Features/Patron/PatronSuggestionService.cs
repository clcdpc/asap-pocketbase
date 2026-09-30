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
    long Id,
    string SuccessTitle,
    string SuccessMessage,
    string NotificationStatus = "queued",
    int? LibraryOrganizationId = null);

public sealed record PatronSuggestionDuplicate(
    long Id,
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
            pickupBranches = await patronProvider.GetPickupBranchesAsync(patron, session.EffectiveOrganizationId, cancellationToken);
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

        var suggestion = Validate(input, configuration);
        EmailTransportReadiness emailTransportReadiness;
        try
        {
            emailTransportReadiness = await emailSender.CheckReadinessAsync(
                configuration.OrganizationId,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(502, "Email readiness could not be checked. Please try again.",
                new { code = "notification_dependency_unavailable" }, exception);
        }
        var pickupReceipt = await ChangePickupForSuggestionAsync(patron, configuration.OrganizationId,
            selectedBranch, pickupBranches, "patron_suggestion", null, cancellationToken);
        long requestId = 0;
        long? outboxId = null;
        var notificationStatus = "queued";
        byte[] expectedRowVersion = [];
        try
        {
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var autoClaimCandidate = await FindAutoClaimCandidateAsync(
                    configuration.OrganizationId,
                    suggestion.Format.Id,
                    cancellationToken);
                try
                {
                    (requestId, outboxId, notificationStatus, expectedRowVersion) = await InsertAsync(
                        session.Barcode,
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
            throw PickupChangedCreationFailure(exception, pickupReceipt);
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
                selectedBranch, pickupBranches, "staff_suggestion", actor.Id, cancellationToken);
            pickupUpdated = pickupReceipt is not null;
            pickupChanged = pickupUpdated;
            if (pickupUpdated)
            {
                // Refresh the snapshot after the external mutation so the persisted patron context
                // is not merely the browser's earlier lookup row.
                patron = await RefreshStaffPatronAsync(barcode, organizationId, cancellationToken);
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
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PatronFlowException(502, "Email readiness could not be checked. Please try again.",
                    new { code = "notification_dependency_unavailable" }, exception);
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
                        barcode,
                        patron,
                        selectedBranch,
                        input,
                        prepared.Suggestion with { VerifiedBibId = verifiedBibId },
                        prepared.Configuration,
                        autoClaimCandidate,
                        emailReadiness,
                        creationActor,
                        pickupReceipt,
                        cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (pickupUpdated && !cancellationToken.IsCancellationRequested &&
                                         exception is PatronFlowException or SqlException or DbUpdateException or PickupMutationException)
        {
            throw PickupChangedCreationFailure(exception, pickupReceipt!);
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

        if (persistedIdentifier is not null)
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
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
        CancellationToken cancellationToken)
    {
        try
        {
            return await pickupMutations.ChangeAsync(patron, organizationId, selected,
                branches.SingleOrDefault(branch => branch.Id == patron.PreferredPickupBranchId)?.Label,
                origin, null, actorId, cancellationToken);
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

    private static PatronFlowException PickupChangedCreationFailure(Exception exception, PickupMutationReceipt receipt)
    {
        var flowException = exception as PatronFlowException;
        var message = "The suggestion was not confirmed, but the patron's preferred pickup location was changed successfully. " +
                      (flowException?.Message ?? "Reload the live patron and request state before retrying.");
        return new PatronFlowException(flowException?.StatusCode ?? 502, message,
            new PatronSuggestionPickupChangedFailure(message,
                flowException?.Response as PatronSuggestionDuplicateConflict, receipt.OperationId), exception);
    }

    private async Task<PatronSnapshot> RefreshStaffPatronAsync(
        string barcode,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            return await patronProvider.RefreshAsync(barcode, organizationId, cancellationToken);
        }
        catch (PolarisOperationalException exception)
        {
            if (exception.Code is "polaris_patron_not_found" or "polaris_patron_invalid_barcode")
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(
                502,
                "Current patron information could not be loaded from Polaris. Please try again.",
                new { code = "polaris_unavailable" },
                exception);
        }
    }

    private async Task<IReadOnlyList<PickupBranch>> LoadPickupBranchesAsync(
        PatronSnapshot patron,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            return await patronProvider.GetPickupBranchesAsync(patron, organizationId, cancellationToken);
        }
        catch (PolarisOperationalException exception)
        {
            throw new PatronFlowException(
                502,
                "Eligible pickup locations could not be loaded from Polaris.",
                new { code = "pickup_branches_unavailable" },
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(
                502,
                "Eligible pickup locations could not be loaded from Polaris.",
                new { code = "pickup_branches_unavailable" },
                exception);
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
        }
        catch (PolarisOperationalException exception)
        {
            throw new PatronFlowException(
                502,
                "Catalog lookup is temporarily unavailable.",
                new { code = "bib_validation_unavailable" },
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PatronFlowException(
                502,
                "Catalog lookup is temporarily unavailable.",
                new { code = "bib_validation_unavailable" },
                exception);
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
                isTargetOrganization && !organization.IsActive ||
                isPatronHomeOrganization && !organization.IsActive)
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
            barcode,
            suggestion,
            configuration,
            cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);
        return (configuration, suggestion);
    }

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
                     { configuration.OrganizationId, actor.OrganizationId, patron.PatronOrganizationId, patron.HomeLibraryOrganizationId }
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
                 @bibId, @bibIdStaffVerified, @materialFormatId, N'suggestion', @isbnCheckStatus, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection,
            transaction))
        {
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
            pickupPreferenceChanged = actor.PickupPreferenceChanged,
            pickupPreferenceReconciled = actor.PickupPreferenceReconciled
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
        return new SubmissionEmailResult(
            status == "pending" ? outboxId : null,
            status == "pending" ? "queued" : "suppressed");
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
            result = await patronProvider.LookupIdentifierAsync(identifier, organizationId, cancellationToken);
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
        string? notes = null,
        int? verifiedBibId = null)
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
            format.Publication.Mode == "hidden" ? null : exactPublicationDate,
            cleanedNotes,
            verifiedBibId);
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
                    throw new PatronFlowException(400, $"{rule.Label ?? definition.Label} is required.");
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
                        throw new PatronFlowException(400, $"{rule.Label ?? definition.Label} is required.");
                    }

                    continue;
                }

                snapshot[definition.Key] = new
                {
                    label = rule.Label ?? definition.Label,
                    type = definition.Type,
                    value = option.Key,
                    displayValue = option.Label
                };
                continue;
            }

            var maxLength = definition.Type == "textarea" ? 2000 : 250;
            snapshot[definition.Key] = new
            {
                label = rule.Label ?? definition.Label,
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
            !patron.PatronCodeId.HasValue ||
            configuration.AllowedPatronCodeIds.Contains(patron.PatronCodeId.Value))
        {
            return;
        }

        throw new PatronFlowException(
            403,
            configuration.PatronCodeEligibilityMessage,
            new { code = "patron_ineligible" });
    }

    private static void EnforceStaffPatronEligibility(
        EffectivePatronConfiguration configuration,
        PatronSnapshot patron)
    {
        if (patron.PatronOrganizationId <= 1 || patron.HomeLibraryOrganizationId <= 1 ||
            !configuration.AllowAnyRegisteredCardLogin &&
            patron.HomeLibraryOrganizationId != configuration.OrganizationId)
        {
            throw new PatronFlowException(
                403,
                "This patron is not eligible for the selected servicing library.",
                new { code = "patron_library_forbidden" });
        }

        EnforcePatronCodeEligibility(configuration, patron);
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
