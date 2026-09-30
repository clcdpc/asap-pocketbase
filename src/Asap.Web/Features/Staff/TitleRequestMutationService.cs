using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record VersionInput(string? Version, string? ActorVersion = null);
public sealed record AssignTitleRequestInput(string? Version,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? AssigneeId);

public sealed class TitleRequestActionInput
{
    public string? Version { get; init; }
    public string? Action { get; init; }
    public string? Status { get; init; }
    public string? Title { get; init; }
    public string? Author { get; init; }
    public JsonElement Identifier { get; init; }
    public string? Publication { get; init; }
    public JsonElement ExactPublicationDate { get; init; }
    public JsonElement CustomFields { get; init; }
    public JsonElement Autohold { get; init; }
    private int? bibid;
    private int? staffSelectedBibId;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Bibid
    {
        get => bibid;
        init
        {
            bibid = value;
            BibidSupplied = true;
        }
    }

    [JsonIgnore]
    public bool BibidSupplied { get; private set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? StaffSelectedBibId
    {
        get => staffSelectedBibId;
        init
        {
            staffSelectedBibId = value;
            StaffSelectedBibIdSupplied = true;
        }
    }

    [JsonIgnore]
    public bool StaffSelectedBibIdSupplied { get; private set; }
    public string? Notes { get; init; }
    public string? Format { get; init; }
    public bool EmailPurchaseReminder { get; init; }
    public string? RejectionTemplateId { get; init; }
}

public sealed record TitleRequestMutationResult(
    string Code,
    long? RequestId = null,
    IReadOnlyList<long>? DispatchOutboxIds = null,
    string? FinalStatus = null,
    string? NotificationStatus = null,
    string? NotificationReason = null,
    string? PatronNotificationStatus = null,
    string? PatronNotificationReason = null,
    TitleRequestDuplicateConflict? Duplicate = null);

public sealed record TitleRequestDuplicateConflict(long Id, string Title, string Status, int BibId, string MatchType);

public sealed class TitleRequestMutationService(
    IDbContextFactory<AsapDbContext> contextFactory,
    ExternalConfiguration configuration,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailOutboxDispatcher outboxDispatcher,
    IStaffPolarisProvider staffPolarisProvider,
    IPatronProvider patronProvider,
    PatronConfigurationService patronConfigurations,
    IIdentifierLookupDispatcher identifierLookupDispatcher,
    ILogger<TitleRequestMutationService> logger)
{
    private sealed record ActionClaimRuleSnapshot(long FormatId, long? RuleId, long? StaffUserId);
    private static readonly string[] IdentifierDerivedTagCodes =
        ["polaris_bib_found", "polaris_bib_not_found", "polaris_multiple_matches"];
    private const string InterruptedIdentifierResult =
        "Identifier processing was not completed before this request left suggestions.";
    private readonly HashSet<Guid> allowedTenantIds = configuration.Authentication.Entra.AllowedTenantIds!
        .Select(Guid.Parse)
        .ToHashSet();

    public async Task<TitleRequestMutationResult> ClaimAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        bool unclaim,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new TitleRequestMutationResult("invalid_version");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockForMutationAsync(context, actor, requestId, [actor.Id], cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion))
        {
            return new TitleRequestMutationResult("stale_version");
        }
        if (request.Status == "closed")
        {
            return new TitleRequestMutationResult("request_not_open");
        }

        if (unclaim)
        {
            if (!request.ClaimedByStaffUserId.HasValue)
            {
                return new TitleRequestMutationResult("claim_conflict");
            }
            if (request.ClaimedByStaffUserId != actor.Id)
            {
                return new TitleRequestMutationResult("claim_forbidden");
            }
            request.ClaimedByStaffUserId = null;
            request.ClaimedByDisplayName = null;
            request.ClaimedAtUtc = null;
            request.ClaimType = null;
            request.ClaimRuleId = null;
            AddEvent(context, request, actor, "claim_manual_cleared", "Manual claim cleared.");
        }
        else
        {
            if (request.ClaimedByStaffUserId.HasValue && request.ClaimedByStaffUserId != actor.Id)
            {
                return new TitleRequestMutationResult("claim_conflict");
            }
            SetManualClaim(request, locked.Staff[actor.Id]);
            AddEvent(context, request, actor, "claim_manual_assigned", "Request manually claimed.");
        }
        request.UpdatedUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TitleRequestMutationResult("updated", request.Id);
    }

    public async Task<TitleRequestMutationResult> ClearClaimAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new TitleRequestMutationResult("invalid_version");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockForMutationAsync(context, actor, requestId, [actor.Id], cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion))
        {
            return new TitleRequestMutationResult("stale_version");
        }
        if (locked.Staff[actor.Id].Role is not ("admin" or "super_admin"))
        {
            return new TitleRequestMutationResult("claim_forbidden");
        }
        if (request.Status == "closed" || !request.ClaimedByStaffUserId.HasValue ||
            request.ClaimedByStaffUserId == actor.Id)
        {
            return new TitleRequestMutationResult("claim_conflict");
        }
        var previousClaimantId = request.ClaimedByStaffUserId.Value;
        var previousClaimantName = request.ClaimedByDisplayName;
        var previousClaimedAt = request.ClaimedAtUtc;
        var previousClaimType = request.ClaimType;
        var previousClaimRuleId = request.ClaimRuleId;
        request.ClaimedByStaffUserId = null;
        request.ClaimedByDisplayName = null;
        request.ClaimedAtUtc = null;
        request.ClaimType = null;
        request.ClaimRuleId = null;
        request.UpdatedUtc = DateTime.UtcNow;
        AddEvent(context, request, actor, "claim_admin_cleared",
            $"Administrator cleared {previousClaimantName ?? previousClaimantId.ToString(CultureInfo.InvariantCulture)}'s claim.",
            new
            {
                previousClaimantId = previousClaimantId.ToString(CultureInfo.InvariantCulture),
                previousClaimantName,
                previousClaimedAt,
                previousClaimType,
                previousClaimRuleId = previousClaimRuleId?.ToString(CultureInfo.InvariantCulture)
            });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TitleRequestMutationResult("updated", request.Id);
    }

    public async Task<TitleRequestMutationResult> AssignAsync(
        CurrentStaff actor,
        long requestId,
        AssignTitleRequestInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion) || !input.AssigneeId.HasValue)
        {
            return new TitleRequestMutationResult("invalid_assignment");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var notificationRequest = await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId)
            .Select(item => new { item.LibraryOrganizationId, item.RowVersion }).SingleOrDefaultAsync(cancellationToken);
        if (notificationRequest is null || !TitleRequestViewService.CanAccess(actor, notificationRequest.LibraryOrganizationId))
        {
            return new TitleRequestMutationResult("not_found");
        }
        if (!notificationRequest.RowVersion.SequenceEqual(expectedVersion))
        {
            return new TitleRequestMutationResult("stale_version");
        }
        var currentActor = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (currentActor is null || !IsSameCurrentActor(actor, currentActor) ||
            !IsEligibleForLibrary(currentActor, notificationRequest.LibraryOrganizationId))
        {
            return new TitleRequestMutationResult("staff_scope_forbidden");
        }
        EmailTransportReadiness readiness;
        try
        {
            readiness = await emailSender.CheckReadinessAsync(notificationRequest.LibraryOrganizationId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new TitleRequestMutationResult("notification_dependency_unavailable");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var staffIds = new[] { actor.Id, input.AssigneeId.Value }.Distinct().Order().ToArray();
        var locked = await LockForMutationAsync(context, actor, requestId, staffIds, cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion) || request.LibraryOrganizationId != notificationRequest.LibraryOrganizationId)
        {
            return new TitleRequestMutationResult("stale_version");
        }
        if (request.Status == "closed")
        {
            return new TitleRequestMutationResult("request_not_open");
        }
        var assignee = locked.Staff[input.AssigneeId.Value];
        if (!IsEligibleForLibrary(assignee, request.LibraryOrganizationId))
        {
            return new TitleRequestMutationResult("assignee_ineligible");
        }

        SetManualClaim(request, assignee);
        request.UpdatedUtc = DateTime.UtcNow;
        AddEvent(context, request, actor, "claim_manual_assigned", $"Claim transferred to {DisplayName(assignee)}.");
        var outbox = await AddStaffNotificationAsync(
            context,
            assignee,
            request,
            $"title-assignment:{request.Id}:{Convert.ToHexString(expectedVersion)}:{assignee.Id}",
            "ASAP request assigned",
            $"{request.Title} has been assigned to you.",
            readiness.IsConfigured,
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var notificationStatus = outbox?.Status == "pending" ? "queued" : "suppressed";
        var notificationReason = outbox?.SuppressionReason ?? (outbox is null ? "recipient_missing" : null);
        try
        {
            Dispatch(outbox);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Assignment notification dispatch failed after request {RequestId} committed", request.Id);
            notificationStatus = "dispatch_failed";
            notificationReason = "queue_unavailable";
        }
        return new TitleRequestMutationResult("updated", request.Id, outbox is null ? [] : [outbox.Id],
            NotificationStatus: notificationStatus, NotificationReason: notificationReason);
    }

    public async Task<TitleRequestMutationResult> ActionAsync(
        CurrentStaff actor,
        long requestId,
        TitleRequestActionInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new TitleRequestMutationResult("invalid_version");
        }

        if (input.Action is null || !string.Equals(input.Action, Clean(input.Action), StringComparison.Ordinal))
        {
            return new TitleRequestMutationResult("invalid_action");
        }

        DateOnly? proposedExactDate = null;
        if (input.ExactPublicationDate.ValueKind == JsonValueKind.String)
        {
            if (!DateOnly.TryParseExact(input.ExactPublicationDate.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                return new TitleRequestMutationResult("invalid_exact_publication_date");
            }
            proposedExactDate = parsedDate;
        }
        else if (input.ExactPublicationDate.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return new TitleRequestMutationResult("invalid_exact_publication_date");
        }

        var bibPreflight = await PreflightExplicitBibAsync(
            actor,
            requestId,
            expectedVersion,
            input,
            cancellationToken);
        if (bibPreflight is not null)
        {
            return new TitleRequestMutationResult(bibPreflight);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var notificationRequest = await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId)
            .Select(item => new { item.LibraryOrganizationId, item.Barcode, item.Status, item.Identifier, item.BibId,
                item.MaterialFormatId, item.ClaimedByStaffUserId, item.ClaimType, item.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (notificationRequest is null ||
            !TitleRequestViewService.CanAccess(actor, notificationRequest.LibraryOrganizationId))
        {
            return new TitleRequestMutationResult("not_found");
        }
        var actionActor = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (actionActor is null || !IsSameCurrentActor(actor, actionActor) ||
            !IsEligibleForLibrary(actionActor, notificationRequest.LibraryOrganizationId))
        {
            return new TitleRequestMutationResult("staff_scope_forbidden");
        }
        if (!notificationRequest.RowVersion.SequenceEqual(expectedVersion))
        {
            return new TitleRequestMutationResult("stale_version");
        }
        ActionClaimRuleSnapshot? claimRuleSnapshot = null;
        if (notificationRequest.ClaimedByStaffUserId == actor.Id &&
            notificationRequest.ClaimType == "automatic_format_rule" &&
            !string.IsNullOrWhiteSpace(input.Format))
        {
            var proposedFormatId = await ResolveFormatIdAsync(context, notificationRequest.LibraryOrganizationId,
                input.Format, cancellationToken);
            if (proposedFormatId.HasValue && proposedFormatId.Value != notificationRequest.MaterialFormatId)
            {
                var rule = await context.FormatAutoClaimRules.AsNoTracking()
                    .Where(item => item.LibraryOrganizationId == notificationRequest.LibraryOrganizationId &&
                        item.MaterialFormatId == proposedFormatId.Value && item.IsActive)
                    .OrderBy(item => item.Id)
                    .Select(item => new { item.Id, item.StaffUserId })
                    .FirstOrDefaultAsync(cancellationToken);
                claimRuleSnapshot = new ActionClaimRuleSnapshot(proposedFormatId.Value, rule?.Id, rule?.StaffUserId);
            }
        }
        var previewIdentifier = Clean(notificationRequest.Identifier);
        var previewBib = notificationRequest.BibId;
        var previewIdentifierChanged = IsSupplied(input.Identifier) &&
            !string.Equals(Clean(ElementString(input.Identifier)), previewIdentifier, StringComparison.Ordinal);
        var previewBibSupplied = input.BibidSupplied;
        var previewProposedBib = previewBibSupplied ? input.Bibid : previewBib;
        var previewBibChanged = previewBibSupplied &&
            previewProposedBib != previewBib;
        var previewSelectedBibId = input.StaffSelectedBibIdSupplied
            ? input.StaffSelectedBibId : null;
        var previewStaffSelectedBib = previewSelectedBibId is not null &&
            previewSelectedBibId == previewProposedBib;
        var notificationTargetStatus = ResolveStatus(input.Action, input.Status, notificationRequest.Status,
            ProjectBibAfterMutation(previewIdentifierChanged, previewBibChanged,
                previewStaffSelectedBib, previewProposedBib));
        var notificationRequested = input.Action is "reject" or "alreadyOwn" ||
            input.Action == "purchase" && notificationTargetStatus == "outstanding_purchase";
        PatronSnapshot? currentPatron = null;
        if (input.Action is "reject" or "alreadyOwn" ||
            input.Action == "purchase" && notificationTargetStatus == "outstanding_purchase")
        {
            try
            {
                var refreshed = await patronProvider.RefreshAsync(notificationRequest.Barcode, notificationRequest.LibraryOrganizationId, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (refreshed is not null &&
                    string.Equals(refreshed.Barcode, notificationRequest.Barcode, StringComparison.Ordinal))
                {
                    currentPatron = refreshed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception,
                    "Patron refresh failed before action email for request {RequestId}.", requestId);
            }
        }
        var transportConfigured = false;
        if (notificationRequested)
        {
            try
            {
                transportConfigured = (await emailSender.CheckReadinessAsync(
                    notificationRequest.LibraryOrganizationId, cancellationToken)).IsConfigured;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return new TitleRequestMutationResult("notification_dependency_unavailable");
            }
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var staffIds = claimRuleSnapshot?.StaffUserId is long candidateId
            ? new[] { actor.Id, candidateId }
            : [actor.Id];
        var locked = await LockForMutationAsync(context, actor, requestId, staffIds, cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion) ||
            request.LibraryOrganizationId != notificationRequest.LibraryOrganizationId)
        {
            return new TitleRequestMutationResult("stale_version");
        }

        var requestEvents = await context.TitleRequestEvents
            .Where(item => item.TitleRequestId == request.Id)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (await PickupPreferenceMutationService.HasIncompleteAsync(context, request.Id, cancellationToken))
        {
            return new TitleRequestMutationResult("pickup_reconciliation_required");
        }
        var incompleteOperation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
            cancellationToken);
        var successfulOperation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.State == "succeeded",
            cancellationToken);
        var placedProtection = successfulOperation || TitleRequestViewService.HasLegacyPlacedProtection(requestEvents);
        var capabilities = TitleRequestCapabilityPolicy.Evaluate(request, incompleteOperation, placedProtection);
        if (input.Action == "reopen" && placedProtection)
        {
            return new TitleRequestMutationResult("hold_history_retained");
        }

        var identifierSupplied = IsSupplied(input.Identifier);
        var proposedIdentifier = identifierSupplied ? Clean(ElementString(input.Identifier)) : Clean(request.Identifier);
        var identifierChanged = identifierSupplied &&
                                !string.Equals(Clean(proposedIdentifier), Clean(request.Identifier), StringComparison.Ordinal);
        var bibSupplied = input.BibidSupplied;
        var proposedBib = bibSupplied ? input.Bibid : request.BibId;
        var bibChanged = bibSupplied &&
                         proposedBib != request.BibId;
        var selectedBibId = input.StaffSelectedBibIdSupplied
            ? input.StaffSelectedBibId
            : null;
        var staffSelectedBib = selectedBibId is not null &&
                               selectedBibId == proposedBib;
        var bibAfterMutation = ProjectBibAfterMutation(identifierChanged, bibChanged,
            staffSelectedBib, proposedBib);
        var autoHoldSupplied = input.Autohold.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var proposedAutoHold = autoHoldSupplied ? input.Autohold.GetBoolean() : request.AutoHold;
        var requestedTarget = ResolveStatus(input.Action, input.Status, request.Status, bibAfterMutation);
        var targetStatus = ResolveBibTargetStatus(input.Action, request.Status, requestedTarget,
            bibAfterMutation, proposedAutoHold, bibSupplied);
        if (targetStatus is null)
        {
            return new TitleRequestMutationResult("invalid_transition");
        }
        var statusChanged = targetStatus != request.Status;
        var autoHoldOptOut = targetStatus == "closed" &&
            (requestedTarget == "pending_hold" ||
             input.Action == "edit" && request.Status == "outstanding_purchase" &&
             bibSupplied && bibAfterMutation is not null);

        if (incompleteOperation && (identifierChanged || bibChanged || statusChanged || proposedAutoHold != request.AutoHold))
        {
            return new TitleRequestMutationResult("hold_operation_incomplete");
        }
        if (identifierChanged && !capabilities.CanEditIdentifier || bibChanged && !capabilities.CanChangeBib)
        {
            return new TitleRequestMutationResult(capabilities.BlockingReason ?? "identifier_locked_by_stage");
        }
        if ((targetStatus == "hold_placed" || targetStatus == "closed" && !autoHoldOptOut) &&
            (identifierChanged || bibChanged))
        {
            return new TitleRequestMutationResult("identifier_locked_by_stage");
        }
        if (requestedTarget == "pending_hold" && statusChanged && bibAfterMutation is null)
        {
            return new TitleRequestMutationResult("bib_required");
        }
        if (requestedTarget == "pending_hold" && statusChanged &&
            !request.BibIdStaffVerified && !bibChanged && !staffSelectedBib)
        {
            return new TitleRequestMutationResult("bib_unverified");
        }
        if (bibChanged && proposedBib is not null && proposedBib <= 0)
        {
            return new TitleRequestMutationResult("invalid_bib");
        }
        if (targetStatus == "pending_hold" && bibAfterMutation is not null &&
            (request.Status != "pending_hold" || !request.AutoHold || bibChanged))
        {
            // LockForMutationAsync holds the library Organization row until commit. Patron
            // creation and automatic promotion take the same lock before writing requests.
            var otherOpenRequests = await context.TitleRequests.AsNoTracking().Where(item =>
                item.LibraryOrganizationId == request.LibraryOrganizationId &&
                item.Barcode == request.Barcode && item.BibId != null &&
                item.Id != request.Id && item.Status != "closed")
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Title, item.Status, item.BibId })
                .ToListAsync(cancellationToken);
            var duplicate = otherOpenRequests.FirstOrDefault(other => other.BibId == bibAfterMutation);
            if (duplicate is not null)
            {
                return new TitleRequestMutationResult("duplicate_open_request", Duplicate:
                    new TitleRequestDuplicateConflict(duplicate.Id, duplicate.Title, duplicate.Status,
                        duplicate.BibId!.Value, "bibid"));
            }
        }

        ResolvedRejectionTemplate? rejectionTemplate = null;
        long? rejectionTemplateId = null;
        if (input.Action == "reject")
        {
            var templates = await context.EmailTemplates.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[EmailTemplate] WITH (UPDLOCK,HOLDLOCK) WHERE [OrganizationId] = 1 OR [OrganizationId] = {request.LibraryOrganizationId}")
                .ToListAsync(cancellationToken);
            if (input.RejectionTemplateId is { Length: > 0 })
            {
                if (!long.TryParse(input.RejectionTemplateId, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var parsedId) || parsedId <= 0)
                {
                    return new TitleRequestMutationResult("invalid_rejection_template");
                }
                rejectionTemplate = RejectionTemplatePolicy.Resolve(
                    templates, request.LibraryOrganizationId, parsedId);
                if (rejectionTemplate is null)
                {
                    return new TitleRequestMutationResult("invalid_rejection_template");
                }
                rejectionTemplateId = parsedId;
            }
            else
            {
                rejectionTemplate = RejectionTemplatePolicy.Default(
                    templates, request.LibraryOrganizationId);
            }
        }
        else if (input.Action != "reject" && input.RejectionTemplateId is { Length: > 0 })
        {
            return new TitleRequestMutationResult("invalid_rejection_template");
        }

        var previousTitle = request.Title;
        var previousAuthor = request.Author;
        var previousIdentifier = request.Identifier;
        var previousBib = request.BibId;
        var previousBibStaffVerified = request.BibIdStaffVerified;
        var previousPublication = request.Publication;
        var previousExactDate = request.ExactPublicationDate;
        var previousFormatId = request.MaterialFormatId;
        var previousNotes = request.Notes;
        var previousAutohold = request.AutoHold;
        var previousCustomFields = request.CustomFieldsJson;

        if (identifierChanged)
        {
            request.Identifier = Clean(proposedIdentifier);
        }
        if (identifierChanged || bibChanged)
        {
            request.BibId = null;
            request.BibIdStaffVerified = false;
            InvalidateIdentifierCheckState(request);
            await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
        }
        if (bibChanged || staffSelectedBib)
        {
            request.BibId = proposedBib;
            request.BibIdStaffVerified = request.BibId is not null;
        }

        if (input.Title is not null)
        {
            request.Title = input.Title.Trim();
        }
        if (input.Author is not null)
        {
            request.Author = Clean(input.Author);
        }
        if (input.Publication is not null)
        {
            request.Publication = Clean(input.Publication);
        }
        if (input.Notes is not null)
        {
            request.Notes = input.Notes;
        }
        if (IsSupplied(input.ExactPublicationDate))
        {
            request.ExactPublicationDate = proposedExactDate;
        }
        if (autoHoldSupplied)
        {
            request.AutoHold = proposedAutoHold;
        }
        if (!string.IsNullOrWhiteSpace(input.Format))
        {
            var currentCode = await context.MaterialFormats.AsNoTracking()
                .Where(item => item.Id == request.MaterialFormatId)
                .Select(item => item.Code)
                .SingleAsync(cancellationToken);
            if (!string.Equals(currentCode, input.Format.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var formatId = await ResolveFormatIdAsync(context, request.LibraryOrganizationId, input.Format, cancellationToken);
                if (!formatId.HasValue)
                {
                    return new TitleRequestMutationResult("invalid_format");
                }
                request.MaterialFormatId = formatId.Value;
            }
        }
        var customFieldsChanged = false;
        if (input.CustomFields.ValueKind != JsonValueKind.Undefined ||
            previousFormatId != request.MaterialFormatId)
        {
            JsonElement submittedFields = input.CustomFields;
            if (submittedFields.ValueKind == JsonValueKind.Undefined)
            {
                try
                {
                    using var storedFields = JsonDocument.Parse(
                        string.IsNullOrWhiteSpace(previousCustomFields) ? "{}" : previousCustomFields);
                    submittedFields = storedFields.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return new TitleRequestMutationResult("invalid_custom_fields");
                }
            }
            if (submittedFields.ValueKind != JsonValueKind.Object)
            {
                return new TitleRequestMutationResult("invalid_custom_fields");
            }
            var customFields = await MergeCustomFieldsAsync(context, request, submittedFields, cancellationToken);
            if (customFields.Error is not null)
            {
                return new TitleRequestMutationResult(customFields.Error);
            }
            customFieldsChanged = !string.Equals(previousCustomFields, customFields.Json, StringComparison.Ordinal);
            if (customFieldsChanged)
            {
                request.CustomFieldsJson = customFields.Json;
            }
        }

        var now = DateTime.UtcNow;
        request.Status = targetStatus;
        request.CloseReason = targetStatus == "closed"
            ? autoHoldOptOut ? "purchased_no_hold"
                : statusChanged ? ResolveCloseReason(input.Action) : request.CloseReason
            : null;
        request.UpdatedUtc = now;
        if (targetStatus != "suggestion" && request.IsbnCheckStatus == "pending")
        {
            await ResolveUnreachableIdentifierCheckAsync(context, request, cancellationToken);
        }
        else if (input.Action == "reopen" && targetStatus == "suggestion" &&
            request.IsbnCheckStatus is null &&
            request.IsbnCheckResult == InterruptedIdentifierResult)
        {
            InvalidateIdentifierCheckState(request);
            await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
        }
        if (statusChanged)
        {
            AddEvent(context, request, actor, "status_changed", $"Moved to {targetStatus}.", new
            {
                fromStatus = locked.OriginalStatus,
                toStatus = targetStatus,
                action = Clean(input.Action)
            });
        }
        if (input.Action == "edit")
        {
            var changed = new List<string>();
            if (previousTitle != request.Title)
            {
                changed.Add("title");
            }
            if (previousAuthor != request.Author)
            {
                changed.Add("author");
            }
            if (previousIdentifier != request.Identifier)
            {
                changed.Add("identifier");
            }
            if (previousBib != request.BibId)
            {
                changed.Add("BIB ID");
            }
            if (previousBibStaffVerified != request.BibIdStaffVerified)
            {
                changed.Add("BIB verification");
            }
            if (previousPublication != request.Publication)
            {
                changed.Add("publication");
            }
            if (previousExactDate != request.ExactPublicationDate)
            {
                changed.Add("exact publication date");
            }
            if (previousFormatId != request.MaterialFormatId)
            {
                changed.Add("format");
            }
            if (previousNotes != request.Notes)
            {
                changed.Add("notes");
            }
            if (previousAutohold != request.AutoHold)
            {
                changed.Add("automatic hold");
            }
            if (customFieldsChanged)
            {
                changed.Add("custom fields");
            }
            if (changed.Count > 0)
            {
                AddEvent(context, request, actor, "request_edited", $"Updated {string.Join(", ", changed)}.");
            }
        }
        if (autoHoldOptOut)
        {
            AddEvent(context, request, actor, "autohold_opt_out",
                input.Action == "alreadyOwn"
                    ? "Closed without hold because Already Own was selected and automatic hold placement was disabled."
                    : "Closed without hold because a BIB ID was supplied and automatic hold placement was disabled.");
        }
        if (rejectionTemplateId.HasValue)
        {
            AddEvent(context, request, actor, "rejection_template_selected",
                $"Selected rejection template: {rejectionTemplate!.Name}.",
                new { templateId = rejectionTemplateId.Value.ToString(CultureInfo.InvariantCulture) });
        }
        else if (input.Action == "reject")
        {
            AddEvent(context, request, actor, "rejection_template_default",
                "Used the default rejection email.");
        }
        var claimError = await ApplyActionClaimAsync(context, request, actor, locked.Staff,
            previousFormatId != request.MaterialFormatId, claimRuleSnapshot, cancellationToken);
        if (claimError is not null)
        {
            return new TitleRequestMutationResult(claimError);
        }

        var outboxIds = new List<long>();
        string? notificationStatus = null;
        string? notificationReason = null;
        string? patronNotificationStatus = null;
        string? patronNotificationReason = null;
        long? patronNotificationOutboxId = null;
        if (input.EmailPurchaseReminder && input.Action == "purchase" && targetStatus == "outstanding_purchase")
        {
            var outbox = await AddStaffNotificationAsync(
                context,
                locked.Staff[actor.Id],
                request,
                $"purchase-reminder:{request.Id}:{Convert.ToHexString(expectedVersion)}",
                "ASAP purchase reminder",
                $"Purchase requested for {request.Title}.",
                transportConfigured,
                cancellationToken);
            notificationStatus = outbox?.Status == "pending" ? "queued" : "suppressed";
            notificationReason = outbox?.SuppressionReason ?? (outbox is null ? "recipient_missing" : null);
            if (outbox?.Status == "pending")
            {
                outboxIds.Add(outbox.Id);
            }
        }
        else if (input.Action == "purchase")
        {
            notificationStatus = input.EmailPurchaseReminder ? "not_applicable" : "not_requested";
        }
        if (rejectionTemplate is not null)
        {
            var outbox = await AddRejectionEmailAsync(context, request, currentPatron, rejectionTemplate,
                expectedVersion, transportConfigured, cancellationToken);
            notificationStatus = outbox.Status == "pending" ? "queued" : "suppressed";
            notificationReason = outbox.SuppressionReason;
            if (outbox.Status == "pending")
            {
                outboxIds.Add(outbox.Id);
            }
        }
        if (input.Action == "purchase" && targetStatus == "outstanding_purchase" ||
            input.Action == "alreadyOwn")
        {
            var templateKey = input.Action == "purchase" ? "purchase_approved" : "already_owned";
            var outbox = await AddPatronActionEmailAsync(context, request, currentPatron, templateKey,
                expectedVersion, transportConfigured, cancellationToken);
            if (outbox.Status == "pending")
            {
                outboxIds.Add(outbox.Id);
            }
            if (input.Action == "alreadyOwn")
            {
                notificationStatus = outbox.Status == "pending" ? "queued" : "suppressed";
                notificationReason = outbox.SuppressionReason;
            }
            else
            {
                patronNotificationStatus = outbox.Status == "pending" ? "queued" : "suppressed";
                patronNotificationReason = outbox.SuppressionReason;
                patronNotificationOutboxId = outbox.Status == "pending" ? outbox.Id : null;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var outboxId in outboxIds)
        {
            try
            {
                outboxDispatcher.Enqueue(outboxId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification dispatch failed after request {RequestId} committed", request.Id);
                if (patronNotificationOutboxId == outboxId)
                {
                    patronNotificationStatus = "dispatch_failed";
                    patronNotificationReason = "queue_unavailable";
                }
                else
                {
                    notificationStatus = "dispatch_failed";
                    notificationReason = "queue_unavailable";
                }
            }
        }
        return new TitleRequestMutationResult("updated", request.Id, outboxIds,
            targetStatus, notificationStatus, notificationReason,
            patronNotificationStatus, patronNotificationReason);
    }

    private async Task<string?> PreflightExplicitBibAsync(
        CurrentStaff actor,
        long requestId,
        byte[] expectedVersion,
        TitleRequestActionInput input,
        CancellationToken cancellationToken)
    {
        var bibSupplied = input.BibidSupplied;
        var selectedBibId = input.StaffSelectedBibIdSupplied
            ? input.StaffSelectedBibId
            : null;
        if (input.StaffSelectedBibIdSupplied &&
            (!bibSupplied || selectedBibId is null || selectedBibId <= 0))
        {
            return "invalid_bib";
        }
        var mayEnterPendingHold = input.Action is "alreadyOwn" or "catalogFound" or "purchase" ||
                                  input.Status == "pending_hold";
        if (!bibSupplied && !mayEnterPendingHold)
        {
            return null;
        }
        var proposedBib = bibSupplied ? input.Bibid : null;
        if (proposedBib is not null && proposedBib <= 0)
        {
            return "invalid_bib";
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null || !TitleRequestViewService.CanAccess(actor, request.LibraryOrganizationId))
        {
            return "not_found";
        }
        var currentActor = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (currentActor is null || !IsSameCurrentActor(actor, currentActor) ||
            !IsEligibleForLibrary(currentActor, request.LibraryOrganizationId))
        {
            return "staff_scope_forbidden";
        }
        if (!request.RowVersion.SequenceEqual(expectedVersion))
        {
            return "stale_version";
        }

        if (!bibSupplied)
        {
            proposedBib = request.BibId;
        }
        if (selectedBibId is not null &&
            selectedBibId != proposedBib)
        {
            return "invalid_bib";
        }
        if (proposedBib is not null && proposedBib <= 0)
        {
            return "invalid_bib";
        }

        if (!await context.Organizations.AsNoTracking()
                .AnyAsync(item => item.Id == request.LibraryOrganizationId && item.IsActive, cancellationToken))
        {
            return "organization_inactive";
        }

        var identifierSupplied = IsSupplied(input.Identifier);
        var proposedIdentifier = identifierSupplied ? Clean(ElementString(input.Identifier)) : Clean(request.Identifier);
        var identifierChanged = identifierSupplied &&
                                !string.Equals(proposedIdentifier, Clean(request.Identifier), StringComparison.Ordinal);
        var bibChanged = proposedBib != request.BibId;
        var explicitlySuppliedBib = bibChanged || selectedBibId is not null;
        var bibAfterMutation = identifierChanged
            ? explicitlySuppliedBib ? proposedBib : null
            : proposedBib;
        var targetStatus = ResolveStatus(input.Action, input.Status, request.Status, bibAfterMutation);
        var enteringPendingHold = targetStatus == "pending_hold" && request.Status != "pending_hold";
        var staffSelectedBib = selectedBibId is not null;
        if (!bibChanged && !identifierChanged && !enteringPendingHold && !staffSelectedBib)
        {
            return null;
        }

        var incompleteOperation = await context.HoldPlacementOperations.AsNoTracking()
            .AnyAsync(item => item.TitleRequestId == request.Id && item.CompletedUtc == null, cancellationToken);
        if (incompleteOperation)
        {
            return "hold_operation_incomplete";
        }
        var successfulOperation = await context.HoldPlacementOperations.AsNoTracking()
            .AnyAsync(item => item.TitleRequestId == request.Id && item.State == "succeeded", cancellationToken);
        var events = await context.TitleRequestEvents.AsNoTracking()
            .Where(item => item.TitleRequestId == request.Id)
            .ToListAsync(cancellationToken);
        var capability = TitleRequestCapabilityPolicy.Evaluate(
            request,
            incompleteOperation,
            successfulOperation || TitleRequestViewService.HasLegacyPlacedProtection(events));
        if (identifierChanged && !capability.CanEditIdentifier || bibChanged && !capability.CanChangeBib)
        {
            return capability.BlockingReason ?? "identifier_locked_by_stage";
        }
        if (targetStatus is null)
        {
            return "invalid_transition";
        }
        if (targetStatus == "pending_hold" && enteringPendingHold && bibAfterMutation is null)
        {
            return "bib_required";
        }
        if (targetStatus is "hold_placed" or "closed")
        {
            return "identifier_locked_by_stage";
        }
        if (proposedBib is null)
        {
            return null;
        }
        if (!bibChanged && !enteringPendingHold && !staffSelectedBib)
        {
            return null;
        }

        try
        {
            var result = await staffPolarisProvider.ValidateBibAsync(proposedBib.Value, request.LibraryOrganizationId, cancellationToken);
            return result.IsValid ? null : "bib_not_found";
        }
        catch (PolarisOperationalException)
        {
            return "bib_validation_unavailable";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "bib_validation_unavailable";
        }
    }

    public async Task<TitleRequestMutationResult> RetryIdentifierAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new TitleRequestMutationResult("invalid_version");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockForMutationAsync(context, actor, requestId, [actor.Id], cancellationToken);
        if (locked.Code != "locked") return new TitleRequestMutationResult(locked.Code);
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion)) return new TitleRequestMutationResult("stale_version");
        var operation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
            cancellationToken);
        var protection = await context.HoldPlacementOperations.AnyAsync(
                             item => item.TitleRequestId == request.Id && item.State == "succeeded",
                             cancellationToken) ||
                         TitleRequestViewService.HasLegacyPlacedProtection(await context.TitleRequestEvents
                             .Where(item => item.TitleRequestId == request.Id)
                             .ToListAsync(cancellationToken));
        var capability = TitleRequestCapabilityPolicy.Evaluate(request, operation, protection);
        if (!capability.CanRetryIdentifierCheck)
        {
            return new TitleRequestMutationResult(capability.BlockingReason ?? "identifier_retry_not_allowed");
        }
        request.IsbnCheckStatus = "pending";
        request.IsbnCheckRetryCount = 0;
        request.IsbnCheckLastErrorCode = null;
        request.IsbnCheckResult = null;
        request.LastCheckedUtc = null;
        request.UpdatedUtc = DateTime.UtcNow;
        AddEvent(context, request, actor, "identifier_retry_requested", "Identifier check queued for retry.");
        await context.SaveChangesAsync(cancellationToken);
        var processingVersion = request.RowVersion.ToArray();
        var identifier = request.Identifier!;
        var organizationId = request.LibraryOrganizationId;
        await transaction.CommitAsync(cancellationToken);
        try
        {
            identifierLookupDispatcher.Enqueue(request.Id, identifier, organizationId, processingVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Identifier retry job enqueue failed for title request {TitleRequestId}; the recurring processor remains the recovery path.",
                request.Id);
        }
        return new TitleRequestMutationResult("updated", request.Id);
    }

    public async Task<TitleRequestMutationResult> DeleteClosedAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion) || actor.Role is not ("admin" or "super_admin"))
        {
            return new TitleRequestMutationResult("delete_forbidden");
        }
        if (!StaffVersion.TryDecode(input.ActorVersion, out var expectedActorVersion))
        {
            return new TitleRequestMutationResult("actor_changed_since_preview");
        }
        if (!actor.RowVersion.SequenceEqual(expectedActorVersion))
        {
            return new TitleRequestMutationResult("actor_changed_since_preview");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockForMutationAsync(context, actor, requestId, [actor.Id], cancellationToken);
        if (locked.Code != "locked") return new TitleRequestMutationResult(locked.Code);
        var request = locked.Request!;
        if (locked.Staff[actor.Id].Role is not ("admin" or "super_admin")) return new TitleRequestMutationResult("delete_forbidden");
        if (!locked.Staff[actor.Id].RowVersion.SequenceEqual(expectedActorVersion))
        {
            return new TitleRequestMutationResult("actor_changed_since_preview");
        }
        if (!request.RowVersion.SequenceEqual(expectedVersion)) return new TitleRequestMutationResult("stale_version");
        if (request.Status != "closed") return new TitleRequestMutationResult("request_not_closed");
        if (await PickupPreferenceMutationService.HasIncompleteAsync(context, request.Id, cancellationToken))
        {
            return new TitleRequestMutationResult("pickup_reconciliation_required");
        }
        if (await context.HoldPlacementOperations.AnyAsync(item => item.TitleRequestId == request.Id, cancellationToken))
        {
            return new TitleRequestMutationResult("hold_history_retained");
        }
        context.DeletedRequestAudits.Add(new DeletedRequestAudit
        {
            RequestType = "title_request",
            OriginalRequestKey = request.LegacyId ?? request.Id.ToString(),
            LibraryOrganizationId = request.LibraryOrganizationId,
            Title = request.Title,
            Author = request.Author,
            Identifier = request.Identifier,
            BibId = request.BibId,
            Status = request.Status,
            CloseReason = request.CloseReason,
            MaskedBarcode = MaskBarcode(request.Barcode),
            CreatedUtc = request.CreatedUtc,
            DeletedUtc = DateTime.UtcNow,
            DeletedByStaffUserId = actor.Id,
            DeletedByDisplayName = actor.DisplayName
        });
        context.TitleRequests.Remove(request);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TitleRequestMutationResult("deleted", requestId);
    }

    private async Task<LockedMutation> LockForMutationAsync(
        AsapDbContext context,
        CurrentStaff actor,
        long requestId,
        IReadOnlyList<long> staffIds,
        CancellationToken cancellationToken)
    {
        var snapshot = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (snapshot is null || !TitleRequestViewService.CanAccess(actor, snapshot.LibraryOrganizationId))
        {
            return new LockedMutation("not_found");
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {snapshot.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (organization?.IsActive != true)
        {
            return new LockedMutation("organization_inactive");
        }
        var staff = new Dictionary<long, StaffUser>();
        foreach (var staffId in staffIds.Distinct().Order())
        {
            var row = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {staffId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                return new LockedMutation("staff_not_found");
            }
            staff[staffId] = row;
        }
        if (!staff.TryGetValue(actor.Id, out var lockedActor) ||
            !IsSameCurrentActor(actor, lockedActor) ||
            !IsEligibleForLibrary(lockedActor, snapshot.LibraryOrganizationId))
        {
            return new LockedMutation("staff_scope_forbidden");
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null || request.LibraryOrganizationId != snapshot.LibraryOrganizationId)
        {
            return new LockedMutation("not_found");
        }
        return new LockedMutation("locked", request, staff, request.Status);
    }

    private bool IsSameCurrentActor(CurrentStaff ticket, StaffUser row) =>
        row.IsActive && allowedTenantIds.Contains(ticket.EntraTenantId) &&
        StaffEmail.MatchesAuthenticationEmail(row, ticket.AuthenticationEmail);

    private bool IsEligibleForLibrary(StaffUser row, int organizationId) =>
        row.IsActive && StaffEmail.IsValidAuthenticationEmail(row) &&
        (row.Role == "super_admin" && row.OrganizationId == 1 ||
         row.Role is "staff" or "admin" && row.OrganizationId == organizationId);

    private async Task<string?> ApplyActionClaimAsync(
        AsapDbContext context,
        TitleRequest request,
        CurrentStaff actor,
        IReadOnlyDictionary<long, StaffUser> lockedStaff,
        bool formatChanged,
        ActionClaimRuleSnapshot? preflightRule,
        CancellationToken cancellationToken)
    {
        if (request.ClaimedByStaffUserId != actor.Id)
        {
            var wasClaimed = request.ClaimedByStaffUserId.HasValue;
            SetManualClaim(request, lockedStaff[actor.Id]);
            AddEvent(context, request, actor,
                wasClaimed ? "claim_manual_transferred" : "claim_manual_assigned",
                $"Claim set to {DisplayName(lockedStaff[actor.Id])} after staff action.");
            return null;
        }

        if (!formatChanged || request.ClaimType != "automatic_format_rule")
        {
            return null;
        }

        if (preflightRule?.FormatId != request.MaterialFormatId)
        {
            return "claim_rule_changed";
        }
        var rule = await context.FormatAutoClaimRules.FromSqlInterpolated(
                $"SELECT * FROM [asap].[FormatAutoClaimRule] WITH (UPDLOCK,HOLDLOCK) WHERE [LibraryOrganizationId] = {request.LibraryOrganizationId} AND [MaterialFormatId] = {request.MaterialFormatId} AND [IsActive] = 1")
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (rule?.Id != preflightRule.RuleId || rule?.StaffUserId != preflightRule.StaffUserId)
        {
            return "claim_rule_changed";
        }

        if (rule?.StaffUserId is long staffId && lockedStaff.TryGetValue(staffId, out var assignee) &&
            IsEligibleForLibrary(assignee, request.LibraryOrganizationId))
        {
            request.ClaimedByStaffUserId = assignee.Id;
            request.ClaimedByDisplayName = DisplayName(assignee);
            request.ClaimedAtUtc = DateTime.UtcNow;
            request.ClaimType = "automatic_format_rule";
            request.ClaimRuleId = rule.Id;
            AddEvent(context, request, actor, "claim_auto_reassigned",
                $"Format rule reassigned this request to {DisplayName(assignee)}.");
            return null;
        }

        if (rule is not null)
        {
            AddEvent(context, request, actor, "claim_auto_skipped",
                "Auto-claim skipped because the configured claimant is unavailable or outside this library.");
            return null;
        }

        request.ClaimedByStaffUserId = null;
        request.ClaimedByDisplayName = null;
        request.ClaimedAtUtc = null;
        request.ClaimType = null;
        request.ClaimRuleId = null;
        AddEvent(context, request, actor, "claim_auto_cleared",
            "Auto-claim cleared because no rule is configured for the new format.");
        return null;
    }

    private static void SetManualClaim(TitleRequest request, StaffUser staff)
    {
        request.ClaimedByStaffUserId = staff.Id;
        request.ClaimedByDisplayName = DisplayName(staff);
        request.ClaimedAtUtc = DateTime.UtcNow;
        request.ClaimType = "manual";
        request.ClaimRuleId = null;
    }

    private static void AddEvent(
        AsapDbContext context,
        TitleRequest request,
        CurrentStaff actor,
        string eventType,
        string message,
        object? metadata = null) =>
        context.TitleRequestEvents.Add(new TitleRequestEvent
        {
            TitleRequestId = request.Id,
            EventType = eventType,
            Status = request.Status,
            CloseReason = request.CloseReason,
            ActorType = "staff",
            StaffUserId = actor.Id,
            ActorName = actor.DisplayName ?? actor.UserPrincipalName,
            Message = message,
            MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata),
            CreatedUtc = DateTime.UtcNow
        });

    private async Task<EmailOutbox?> AddStaffNotificationAsync(
        AsapDbContext context,
        StaffUser recipient,
        TitleRequest request,
        string businessKey,
        string subject,
        string body,
        bool transportConfigured,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipient.NotificationEmail))
        {
            return null;
        }
        var system = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId, cancellationToken);
        var fromAddress = Clean(library?.FromAddress) ?? Clean(system?.FromAddress);
        var fromName = Clean(library?.FromName) ?? Clean(system?.FromName);
        string? suppressionReason = null;
        if (!StaffEmail.TryNormalize(recipient.NotificationEmail, out var toAddress) || toAddress is null)
        {
            suppressionReason = "recipient_invalid";
        }
        else if (!recipientDomainPolicy.IsAllowed(toAddress))
        {
            suppressionReason = "recipient_domain_not_allowed";
        }
        else if (fromAddress is null)
        {
            suppressionReason = "sender_missing";
        }
        else if (!transportConfigured)
        {
            suppressionReason = "mail_not_configured";
        }
        var now = DateTime.UtcNow;
        var outbox = new EmailOutbox
        {
            OrganizationId = request.LibraryOrganizationId,
            BusinessKey = businessKey,
            DeliveryClass = "staff_authorization_sensitive",
            RecipientStaffUserId = recipient.Id,
            RecipientAuthenticationEmail = recipient.NormalizedUserPrincipalName,
            AuthorizationOrganizationId = request.LibraryOrganizationId,
            RecipientAddressKind = "notification_email",
            ToAddress = toAddress,
            FromAddress = fromAddress,
            FromName = fromName,
            Subject = subject,
            BodyText = body,
            Status = suppressionReason is null ? "pending" : "suppressed",
            SuppressionReason = suppressionReason,
            NextAttemptUtc = suppressionReason is null ? now : null,
            CreatedUtc = now,
            SuppressedUtc = suppressionReason is null ? null : now
        };
        context.EmailOutbox.Add(outbox);
        await context.SaveChangesAsync(cancellationToken);
        return outbox;
    }

    private async Task<EmailOutbox> AddPatronActionEmailAsync(
        AsapDbContext context,
        TitleRequest request,
        PatronSnapshot? currentPatron,
        string templateKey,
        byte[] expectedVersion,
        bool transportConfigured,
        CancellationToken cancellationToken)
    {
        var systemTemplate = await context.EmailTemplates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1 && item.TemplateKey == templateKey,
                cancellationToken);
        var libraryTemplate = systemTemplate is null ? null : await context.EmailTemplates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId &&
                item.SourceTemplateId == systemTemplate.Id, cancellationToken);
        var (defaultSubject, defaultBody) = templateKey == "purchase_approved"
            ? ("Update on your suggestion: {{title}}",
                "Hello {{name}},\n\nYour suggestion for {{title}} has been approved for purchase.")
            : ("Already in our collection: {{title}}",
                "Hello {{name}},\n\n{{title}} is already in our collection.");
        var template = new EffectiveEmailTemplate(templateKey,
            Clean(libraryTemplate?.SubjectTemplate) ?? Clean(systemTemplate?.SubjectTemplate) ?? defaultSubject,
            Clean(libraryTemplate?.BodyTemplate) ?? Clean(systemTemplate?.BodyTemplate) ?? defaultBody);
        var formatLabel = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.Id == request.MaterialFormatId)
            .Select(item => item.Label)
            .SingleAsync(cancellationToken);
        var patron = currentPatron ?? new PatronSnapshot(0, request.Barcode, null,
            request.NameFirst, request.NameLast, request.PatronCodeId,
            request.PatronCodeDescription, request.PatronOrganizationId ?? 0,
            request.LibraryOrganizationId, request.LibraryNameSnapshot ?? string.Empty,
            request.PreferredPickupBranchId);
        var rendered = PatronEmailTemplateRenderer.Render(template, patron, request.Title,
            request.Author, formatLabel, request.Barcode);
        var systemEmail = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var libraryEmail = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId, cancellationToken);
        var fromAddress = Clean(libraryEmail?.FromAddress) ?? Clean(systemEmail?.FromAddress);
        var fromName = Clean(libraryEmail?.FromName) ?? Clean(systemEmail?.FromName);
        var toAddress = Clean(currentPatron?.Email);
        string? suppressionReason = currentPatron is null ? "patron_refresh_unavailable" :
            systemTemplate?.IsHidden == true || libraryTemplate?.IsHidden == true
                ? "template_hidden" : null;
        if (suppressionReason is null && (toAddress is null || !MailAddress.TryCreate(toAddress, out var parsed) ||
            parsed.Address != toAddress))
        {
            suppressionReason = "recipient_invalid";
        }
        else if (suppressionReason is null && !recipientDomainPolicy.IsAllowed(toAddress!))
        {
            suppressionReason = "recipient_domain_not_allowed";
        }
        else if (suppressionReason is null && fromAddress is null)
        {
            suppressionReason = "sender_missing";
        }
        else if (suppressionReason is null && !transportConfigured)
        {
            suppressionReason = "mail_not_configured";
        }
        var now = DateTime.UtcNow;
        var outbox = new EmailOutbox
        {
            OrganizationId = request.LibraryOrganizationId,
            BusinessKey = $"staff-patron-action:{templateKey}:{request.Id}:{Convert.ToHexString(expectedVersion)}",
            DeliveryClass = "business_event",
            ToAddress = toAddress,
            FromAddress = fromAddress,
            FromName = fromName,
            Subject = rendered.Subject,
            BodyText = rendered.BodyText,
            BodyHtml = rendered.BodyHtml,
            Status = suppressionReason is null ? "pending" : "suppressed",
            SuppressionReason = suppressionReason,
            NextAttemptUtc = suppressionReason is null ? now : null,
            CreatedUtc = now,
            SuppressedUtc = suppressionReason is null ? null : now
        };
        context.EmailOutbox.Add(outbox);
        await context.SaveChangesAsync(cancellationToken);
        return outbox;
    }

    private async Task<EmailOutbox> AddRejectionEmailAsync(
        AsapDbContext context,
        TitleRequest request,
        PatronSnapshot? currentPatron,
        ResolvedRejectionTemplate template,
        byte[] expectedVersion,
        bool transportConfigured,
        CancellationToken cancellationToken)
    {
        var formatLabel = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.Id == request.MaterialFormatId)
            .Select(item => item.Label)
            .SingleAsync(cancellationToken);
        var patron = currentPatron ?? new PatronSnapshot(0, request.Barcode, null,
            request.NameFirst, request.NameLast, request.PatronCodeId,
            request.PatronCodeDescription, request.PatronOrganizationId ?? 0,
            request.LibraryOrganizationId, request.LibraryNameSnapshot ?? string.Empty,
            request.PreferredPickupBranchId);
        var rendered = PatronEmailTemplateRenderer.Render(
            new EffectiveEmailTemplate("rejected", template.Subject, template.Body),
            patron, request.Title, request.Author, formatLabel, request.Barcode);
        var system = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId, cancellationToken);
        var fromAddress = Clean(library?.FromAddress) ?? Clean(system?.FromAddress);
        var fromName = Clean(library?.FromName) ?? Clean(system?.FromName);
        var toAddress = Clean(currentPatron?.Email);
        string? suppressionReason = currentPatron is null ? "patron_refresh_unavailable" :
            template.IsHidden ? "template_hidden" : null;
        if (suppressionReason is null && (toAddress is null || !MailAddress.TryCreate(toAddress, out var parsed) ||
            parsed.Address != toAddress))
        {
            suppressionReason = "recipient_invalid";
        }
        else if (suppressionReason is null && !recipientDomainPolicy.IsAllowed(toAddress!))
        {
            suppressionReason = "recipient_domain_not_allowed";
        }
        else if (suppressionReason is null && fromAddress is null)
        {
            suppressionReason = "sender_missing";
        }
        else if (suppressionReason is null && !transportConfigured)
        {
            suppressionReason = "mail_not_configured";
        }
        var now = DateTime.UtcNow;
        var outbox = new EmailOutbox
        {
            OrganizationId = request.LibraryOrganizationId,
            BusinessKey = $"rejection:{request.Id}:{Convert.ToHexString(expectedVersion)}",
            DeliveryClass = "business_event",
            ToAddress = toAddress,
            FromAddress = fromAddress,
            FromName = fromName,
            Subject = rendered.Subject,
            BodyText = rendered.BodyText,
            BodyHtml = rendered.BodyHtml,
            Status = suppressionReason is null ? "pending" : "suppressed",
            SuppressionReason = suppressionReason,
            NextAttemptUtc = suppressionReason is null ? now : null,
            CreatedUtc = now,
            SuppressedUtc = suppressionReason is null ? null : now
        };
        context.EmailOutbox.Add(outbox);
        await context.SaveChangesAsync(cancellationToken);
        return outbox;
    }

    private void Dispatch(EmailOutbox? outbox)
    {
        if (outbox?.Status == "pending") outboxDispatcher.Enqueue(outbox.Id);
    }

    private static async Task RemoveIdentifierTagsAsync(
        AsapDbContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        var links = await (
                from link in context.TitleRequestWorkflowTags
                join tag in context.WorkflowTags on link.WorkflowTagId equals tag.Id
                where link.TitleRequestId == requestId && IdentifierDerivedTagCodes.Contains(tag.Code)
                select link)
            .ToListAsync(cancellationToken);
        context.TitleRequestWorkflowTags.RemoveRange(links);
    }

    private static void InvalidateIdentifierCheckState(TitleRequest request)
    {
        request.IsbnCheckResult = null;
        request.IsbnCheckRetryCount = 0;
        request.IsbnCheckLastErrorCode = null;
        request.LastCheckedUtc = null;
        request.IsbnCheckStatus = request.Identifier is null ? "skipped_no_isbn" : "pending";
    }

    private static async Task ResolveUnreachableIdentifierCheckAsync(
        AsapDbContext context,
        TitleRequest request,
        CancellationToken cancellationToken)
    {
        request.IsbnCheckStatus = request.Identifier is null ? "skipped_no_isbn" : null;
        request.IsbnCheckResult = request.Identifier is null ? null : InterruptedIdentifierResult;
        request.IsbnCheckRetryCount = 0;
        request.IsbnCheckLastErrorCode = null;
        request.LastCheckedUtc = null;
        await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
    }

    private static async Task<long?> ResolveFormatIdAsync(
        AsapDbContext context,
        int libraryOrganizationId,
        string code,
        CancellationToken cancellationToken) =>
        await context.MaterialFormats.AsNoTracking()
            .Where(item => item.Code == code.Trim() &&
                           (item.OwnerOrganizationId == 1 || item.OwnerOrganizationId == libraryOrganizationId))
            .OrderByDescending(item => item.OwnerOrganizationId == libraryOrganizationId)
            .Select(item => (long?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<(string? Error, string? Json)> MergeCustomFieldsAsync(
        AsapDbContext context,
        TitleRequest request,
        JsonElement submitted,
        CancellationToken cancellationToken)
    {
        var configuration = await patronConfigurations.GetAsync(
            context, request.LibraryOrganizationId, cancellationToken);
        var format = configuration?.Formats.FirstOrDefault(item => item.Id == request.MaterialFormatId);
        if (format is null)
        {
            return submitted.EnumerateObject().Any()
                ? ("invalid_custom_fields", null)
                : (null, request.CustomFieldsJson);
        }

        Dictionary<string, JsonElement> merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(request.CustomFieldsJson)
                ? new(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.CustomFieldsJson)
                  ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            merged = new(StringComparer.Ordinal);
        }

        foreach (var field in format.CustomFields)
        {
            if (field.Value.Mode == "required" &&
                configuration!.CustomFields.Any(item => item.Key == field.Key) &&
                !submitted.TryGetProperty(field.Key, out _))
            {
                return ("invalid_custom_fields", null);
            }
        }

        foreach (var property in submitted.EnumerateObject())
        {
            var definition = configuration!.CustomFields.FirstOrDefault(item => item.Key == property.Name);
            if (definition is null || !format.CustomFields.TryGetValue(property.Name, out var rule) ||
                rule.Mode == "hidden")
            {
                continue;
            }
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Object) ||
                property.Value.ValueKind == JsonValueKind.Object &&
                (!property.Value.TryGetProperty("value", out var nestedValue) ||
                 nestedValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            {
                return ("invalid_custom_fields", null);
            }
            var raw = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Object when property.Value.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String => value.GetString(),
                _ => null
            };
            var cleaned = Clean(raw);
            if (cleaned is null)
            {
                if (rule.Mode == "required")
                {
                    return ("invalid_custom_fields", null);
                }
                merged.Remove(property.Name);
                continue;
            }
            if (definition.Type == "select")
            {
                var option = definition.Options.FirstOrDefault(item => item.Key == cleaned || item.Label == cleaned);
                if (option is null)
                {
                    return ("invalid_custom_fields", null);
                }
                merged[property.Name] = JsonSerializer.SerializeToElement(new
                {
                    label = rule.Label ?? definition.Label,
                    type = definition.Type,
                    value = option.Key,
                    displayValue = option.Label
                });
            }
            else
            {
                var maxLength = definition.Type == "textarea" ? 2000 : 250;
                merged[property.Name] = JsonSerializer.SerializeToElement(new
                {
                    label = rule.Label ?? definition.Label,
                    type = definition.Type,
                    value = cleaned[..Math.Min(cleaned.Length, maxLength)]
                });
            }
        }

        return (null, merged.Count == 0 ? null : JsonSerializer.Serialize(merged));
    }

    private static string? ResolveStatus(string? actionValue, string? requestedStatus, string current, int? bib)
    {
        var action = Clean(actionValue);
        var requested = Clean(requestedStatus);
        var target = action switch
        {
            "edit" when requested is null || requested == current => current,
            "purchase" when current == "suggestion" =>
                bib is null ? "outstanding_purchase" : "pending_hold",
            "alreadyOwn" when current == "suggestion" => "pending_hold",
            "catalogFound" when current is "suggestion" or "outstanding_purchase" => "pending_hold",
            "reject" or "silentClose" when current == "suggestion" => "closed",
            "closeDuplicate" when current != "closed" => "closed",
            "close" when current == "hold_placed" => "closed",
            "reopen" when current == "closed" => "suggestion",
            _ => null
        };
        return target is not null && (requested is null || requested == target) ? target : null;
    }

    private static string? ResolveBibTargetStatus(string? action, string current, string? requestedTarget,
        int? bib, bool autoHold, bool bibSupplied)
    {
        if (requestedTarget is null)
        {
            return null;
        }
        var targetsHold = requestedTarget == "pending_hold" ||
            action == "edit" && current == "outstanding_purchase" && bibSupplied && bib is not null;
        if (!targetsHold)
        {
            return requestedTarget;
        }
        return autoHold ? "pending_hold" : "closed";
    }

    private static string ResolveCloseReason(string? action) => action switch
    {
        "reject" => "rejected",
        "silentClose" => "Silently Closed",
        "closeDuplicate" => "duplicate_hold",
        _ => "manual"
    };

    private static bool IsSupplied(JsonElement value) => value.ValueKind != JsonValueKind.Undefined;
    private static int? ProjectBibAfterMutation(
        bool identifierChanged,
        bool bibChanged,
        bool staffSelectedBib,
        int? proposedBib) => identifierChanged && !bibChanged && !staffSelectedBib ? null : proposedBib;
    private static string? ElementString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : value.ToString();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string DisplayName(StaffUser value) =>
        Clean(value.DisplayName) ?? Clean(value.UserPrincipalName) ?? "Staff";
    private static string MaskBarcode(string value) => value.Length <= 4 ? new string('*', value.Length) : $"***{value[^4..]}";

    private sealed record LockedMutation(
        string Code,
        TitleRequest? Request = null,
        IReadOnlyDictionary<long, StaffUser>? StaffValues = null,
        string? OriginalStatus = null)
    {
        public IReadOnlyDictionary<long, StaffUser> Staff => StaffValues ?? new Dictionary<long, StaffUser>();
    }
}
