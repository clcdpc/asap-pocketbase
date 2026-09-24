using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Features.Staff;

public sealed record VersionInput(string? Version);
public sealed record AssignTitleRequestInput(string? Version, long? AssigneeId);

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
    public JsonElement Bibid { get; init; }
    public string? Notes { get; init; }
    public string? Format { get; init; }
    public bool EmailPurchaseReminder { get; init; }
    public string? RejectionTemplateId { get; init; }
}

public sealed record TitleRequestMutationResult(
    string Code,
    long? RequestId = null,
    IReadOnlyList<long>? DispatchOutboxIds = null,
    long? AdditionalCopyRequestId = null,
    bool ReminderRequested = false,
    bool ReminderQueued = false,
    string? ReminderSkippedReason = null);

public sealed class TitleRequestMutationService(
    IDbContextFactory<AsapDbContext> contextFactory,
    ExternalConfiguration configuration,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailOutboxDispatcher outboxDispatcher,
    IStaffPolarisProvider staffPolarisProvider,
    IPatronProvider patronProvider,
    PatronConfigurationService patronConfigurations,
    AdditionalCopyService additionalCopies,
    IIdentifierLookupDispatcher identifierLookupDispatcher,
    ILogger<TitleRequestMutationService> logger)
{
    private static readonly string[] IdentifierDerivedTagCodes =
        ["polaris_bib_found", "polaris_bib_not_found", "polaris_multiple_matches"];
    private const string InterruptedIdentifierResult =
        "Identifier processing was not completed before this request left suggestions.";
    private sealed record ExplicitBibPreflight(string? Error = null, BibValidationResult? ValidatedBib = null);
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
            if (request.ClaimedByStaffUserId.HasValue &&
                request.ClaimedByStaffUserId != actor.Id &&
                locked.Staff[actor.Id].Role == "staff")
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
        var notificationOrganizationId = await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId)
            .Select(item => (int?)item.LibraryOrganizationId).SingleOrDefaultAsync(cancellationToken);
        if (!notificationOrganizationId.HasValue || !TitleRequestViewService.CanAccess(actor, notificationOrganizationId.Value))
            return new TitleRequestMutationResult("not_found");
        var readiness = await emailSender.CheckReadinessAsync(notificationOrganizationId.Value, cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var staffIds = new[] { actor.Id, input.AssigneeId.Value }.Distinct().Order().ToArray();
        var locked = await LockForMutationAsync(context, actor, requestId, staffIds, cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion) || request.LibraryOrganizationId != notificationOrganizationId)
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
        Dispatch(outbox);
        return new TitleRequestMutationResult("updated", request.Id, outbox is null ? [] : [outbox.Id]);
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

        DateOnly? exactPublicationDate = null;
        if (input.ExactPublicationDate.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(input.ExactPublicationDate.GetString(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            exactPublicationDate = parsedDate;
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
        if (bibPreflight.Error is not null)
        {
            return new TitleRequestMutationResult(bibPreflight.Error);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var actionSnapshot = await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId)
            .Select(item => new
            {
                item.LibraryOrganizationId, item.Barcode, item.RowVersion, item.MaterialFormatId,
                item.ClaimType, item.ClaimedByStaffUserId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (actionSnapshot is null || !TitleRequestViewService.CanAccess(actor, actionSnapshot.LibraryOrganizationId))
            return new TitleRequestMutationResult("not_found");
        if (!actionSnapshot.RowVersion.SequenceEqual(expectedVersion))
        {
            return new TitleRequestMutationResult("stale_version");
        }
        PatronSnapshot? currentPatron = null;
        if (input.Action is "reject" or "alreadyOwn" or "purchase")
        {
            try
            {
                currentPatron = await patronProvider.RefreshAsync(actionSnapshot.Barcode, cancellationToken);
            }
            catch (PolarisOperationalException exception)
            {
                logger.LogWarning(exception, "Patron refresh failed before staff action email for request {RequestId}.", requestId);
            }
        }
        FormatAutoClaimRule? preflightClaimRule = null;
        if (actionSnapshot.ClaimType == "automatic_format_rule" &&
            actionSnapshot.ClaimedByStaffUserId == actor.Id &&
            !string.IsNullOrWhiteSpace(input.Format))
        {
            var proposedFormatId = await ResolveFormatIdAsync(context, actionSnapshot.LibraryOrganizationId,
                input.Format, cancellationToken);
            if (proposedFormatId.HasValue && proposedFormatId != actionSnapshot.MaterialFormatId)
            {
                preflightClaimRule = await context.FormatAutoClaimRules.AsNoTracking()
                    .Where(item => item.LibraryOrganizationId == actionSnapshot.LibraryOrganizationId &&
                                   item.MaterialFormatId == proposedFormatId && item.IsActive)
                    .OrderBy(item => item.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }
        var readiness = await emailSender.CheckReadinessAsync(actionSnapshot.LibraryOrganizationId, cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var staffIds = preflightClaimRule?.StaffUserId is long targetStaffId
            ? new[] { actor.Id, targetStaffId }
            : [actor.Id];
        var locked = await LockForMutationAsync(context, actor, requestId, staffIds, cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion) || request.LibraryOrganizationId != actionSnapshot.LibraryOrganizationId)
        {
            return new TitleRequestMutationResult("stale_version");
        }

        var requestEvents = await context.TitleRequestEvents
            .Where(item => item.TitleRequestId == request.Id)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var incompleteOperation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
            cancellationToken);
        var successfulOperation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.State == "succeeded",
            cancellationToken);
        var placedProtection = successfulOperation || TitleRequestViewService.HasLegacyPlacedProtection(requestEvents);
        var capabilities = TitleRequestCapabilityPolicy.Evaluate(request, incompleteOperation, placedProtection);

        var identifierSupplied = IsSupplied(input.Identifier);
        var validatedIdentifier = Clean(bibPreflight.ValidatedBib?.Identifier);
        var selectedCatalogAction = input.Action is "additionalCopy" or "catalogFound" or "purchase" or "alreadyOwn";
        var proposedIdentifier = validatedIdentifier ??
            (selectedCatalogAction && bibPreflight.ValidatedBib is not null
                ? Clean(request.Identifier)
                : identifierSupplied ? Clean(ElementString(input.Identifier)) : Clean(request.Identifier));
        var identifierChanged = !string.Equals(proposedIdentifier, Clean(request.Identifier), StringComparison.Ordinal);
        var bibSupplied = IsSupplied(input.Bibid);
        var proposedBib = bibSupplied ? Clean(ElementString(input.Bibid)) : Clean(request.BibId);
        var bibChanged = bibSupplied &&
                         !string.Equals(Clean(proposedBib), Clean(request.BibId), StringComparison.Ordinal);
        // The edit form submits the existing BIB with every save. Changing only the
        // identifier must clear that BIB rather than silently reattaching it.
        var applySuppliedBib = bibSupplied && (!identifierChanged || bibChanged || selectedCatalogAction);
        if (identifierChanged && !applySuppliedBib && bibPreflight.ValidatedBib is null)
        {
            proposedBib = null;
        }
        var autoHoldSupplied = input.Autohold.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var proposedAutoHold = input.Action == "additionalCopy"
            ? true
            : autoHoldSupplied ? input.Autohold.GetBoolean() : request.AutoHold;
        var requestedTarget = ResolveStatus(input.Action, input.Status, request.Status, proposedBib);
        var targetStatus = ResolveBibTargetStatus(input.Action, request.Status, requestedTarget,
            proposedBib, proposedAutoHold, bibSupplied);
        if (targetStatus is null)
        {
            return new TitleRequestMutationResult("invalid_transition");
        }
        var statusChanged = targetStatus != request.Status;
        var autoHoldOptOut = targetStatus == "closed" &&
            (requestedTarget == "pending_hold" ||
             input.Action == "edit" && request.Status == "outstanding_purchase" &&
             bibSupplied && proposedBib is not null);

        if (incompleteOperation && (identifierChanged || bibChanged || statusChanged || proposedAutoHold != request.AutoHold))
        {
            return new TitleRequestMutationResult("hold_operation_incomplete");
        }
        if (identifierChanged && !capabilities.CanEditIdentifier || bibChanged && !capabilities.CanChangeBib)
        {
            return new TitleRequestMutationResult(capabilities.BlockingReason ?? "identifier_locked_by_stage");
        }
        if (targetStatus is "hold_placed" or "closed" && !autoHoldOptOut &&
            (identifierChanged || bibChanged))
        {
            return new TitleRequestMutationResult("identifier_locked_by_stage");
        }
        if (targetStatus == "pending_hold" && string.IsNullOrWhiteSpace(proposedBib))
        {
            return new TitleRequestMutationResult("bib_required");
        }
        if (targetStatus == "pending_hold" && identifierChanged && !bibSupplied &&
            bibPreflight.ValidatedBib is null)
        {
            // Changing the identifier clears the old BIB. A pending hold needs a
            // selected, validated BIB to replace it in the same action.
            return new TitleRequestMutationResult("bib_required");
        }
        if (bibChanged && proposedBib is not null && !IsPositiveInteger(proposedBib))
        {
            return new TitleRequestMutationResult("invalid_bib");
        }
        if (targetStatus == "pending_hold" &&
            (request.Status != "pending_hold" || !request.AutoHold || bibChanged) && proposedBib is not null)
        {
            // Serialize actions for this patron before checking the competing BIB. The
            // transaction-owned lock also protects the empty-result case.
            await LockPatronBibTargetAsync(context, request, cancellationToken);
            var duplicate = await context.TitleRequests.AsNoTracking().AnyAsync(item =>
                item.LibraryOrganizationId == request.LibraryOrganizationId &&
                item.Barcode == request.Barcode && item.BibId == proposedBib &&
                item.Id != request.Id && item.Status != "closed", cancellationToken);
            if (duplicate)
            {
                return new TitleRequestMutationResult("duplicate_open_request");
            }
        }

        if (identifierChanged)
        {
            request.Identifier = Clean(proposedIdentifier);
            request.BibId = null;
            request.IsbnCheckResult = null;
            request.IsbnCheckRetryCount = 0;
            request.IsbnCheckLastErrorCode = null;
            request.LastCheckedUtc = null;
            request.IsbnCheckStatus = request.Identifier is null ? "skipped_no_isbn" : "pending";
            if (bibPreflight.ValidatedBib is null)
            {
                await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
            }
        }
        if (applySuppliedBib || bibPreflight.ValidatedBib is not null)
        {
            request.BibId = Clean(proposedBib);
        }
        if (bibChanged && request.BibId is null && request.IsbnCheckStatus == "found")
        {
            request.IsbnCheckStatus = request.Identifier is null ? "skipped_no_isbn" : "not_found";
            request.IsbnCheckResult = request.Identifier is null
                ? null
                : "Selected BIB cleared by staff; the identifier match is no longer verified.";
            request.IsbnCheckRetryCount = 0;
            request.IsbnCheckLastErrorCode = null;
            request.LastCheckedUtc = null;
            await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
        }
        if (bibPreflight.ValidatedBib is not null)
        {
            await ReconcileExplicitPolarisIdentifierAsync(
                context, request, validatedIdentifier, cancellationToken);
        }
        else if (targetStatus != "suggestion" && request.IsbnCheckStatus == "pending")
        {
            await ResolveUnreachableIdentifierCheckAsync(context, request, cancellationToken);
        }
        else if (input.Action == "reopen" && targetStatus == "suggestion" &&
                 request.IsbnCheckStatus == "not_found" &&
                 request.IsbnCheckResult == InterruptedIdentifierResult)
        {
            request.IsbnCheckStatus = request.Identifier is null ? "skipped_no_isbn" : "pending";
            request.IsbnCheckResult = null;
            request.IsbnCheckRetryCount = 0;
            request.IsbnCheckLastErrorCode = null;
            request.LastCheckedUtc = null;
            await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
        }

        if (input.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(input.Title))
            {
                return new TitleRequestMutationResult("title_required");
            }
            request.Title = input.Title.Trim();
        }
        if (input.Author is not null) request.Author = Clean(input.Author);
        if (input.Publication is not null && input.Action != "additionalCopy")
        {
            request.Publication = Clean(input.Publication);
        }
        if (input.Notes is not null) request.Notes = input.Notes;
        if (IsSupplied(input.ExactPublicationDate) && input.Action != "additionalCopy")
        {
            request.ExactPublicationDate = exactPublicationDate;
        }
        if (input.Action == "additionalCopy")
        {
            request.AutoHold = true;
        }
        else if (autoHoldSupplied)
        {
            request.AutoHold = proposedAutoHold;
        }
        var originalFormatId = request.MaterialFormatId;
        if (!string.IsNullOrWhiteSpace(input.Format))
        {
            var formatId = await ResolveFormatIdAsync(context, request.LibraryOrganizationId, input.Format, cancellationToken);
            if (!formatId.HasValue)
            {
                return new TitleRequestMutationResult("invalid_format");
            }
            request.MaterialFormatId = formatId.Value;
        }
        if (input.CustomFields.ValueKind == JsonValueKind.Object)
        {
            var customFields = await MergeCustomFieldsAsync(context, request, input.CustomFields, cancellationToken);
            if (customFields.Error is not null)
            {
                return new TitleRequestMutationResult(customFields.Error);
            }
            request.CustomFieldsJson = customFields.Json;
        }

        var now = DateTime.UtcNow;
        request.Status = targetStatus;
        request.CloseReason = targetStatus == "closed"
            ? autoHoldOptOut ? "purchased_no_hold" : ResolveCloseReason(input.Action)
            : null;
        request.UpdatedUtc = now;
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
            AddEvent(context, request, actor, "request_edited", "Request details updated.");
        }
        if (autoHoldOptOut)
        {
            AddEvent(context, request, actor, "autohold_opt_out",
                input.Action == "alreadyOwn"
                    ? "Closed without hold because Already Own was selected and the patron opted out of automatic hold placement."
                    : "Closed without hold because a BIB ID was supplied and the patron opted out of automatic hold placement.");
        }
        var claimError = await ApplyActionClaimAsync(context, request, actor, locked.Staff,
            originalFormatId != request.MaterialFormatId, preflightClaimRule, cancellationToken);
        if (claimError is not null)
        {
            return new TitleRequestMutationResult(claimError);
        }

        var outboxIds = new List<long>();
        var patronEmail = await AddPatronActionEmailAsync(context, request, locked.OriginalStatus!,
            input, expectedVersion, currentPatron, readiness.IsConfigured, cancellationToken);
        if (patronEmail.Error is not null)
        {
            return new TitleRequestMutationResult(patronEmail.Error);
        }
        AdditionalCopyMutationResult? copyResult = null;
        if (input.Action == "additionalCopy")
        {
            copyResult = await additionalCopies.CreateFromLockedSourceAsync(
                context, request, locked.Staff[actor.Id], locked.Staff[actor.Id],
                input.EmailPurchaseReminder, readiness.IsConfigured, cancellationToken);
            if (copyResult.DispatchOutboxId.HasValue)
            {
                outboxIds.Add(copyResult.DispatchOutboxId.Value);
            }
        }
        var reminderRequested = input.EmailPurchaseReminder && input.Action == "purchase";
        var reminderQueued = false;
        if (reminderRequested && targetStatus == "outstanding_purchase")
        {
            var outbox = await AddStaffNotificationAsync(
                context,
                locked.Staff[actor.Id],
                request,
                $"purchase-reminder:{request.Id}:{Convert.ToHexString(expectedVersion)}",
                "ASAP purchase reminder",
                $"Purchase requested for {request.Title}.",
                readiness.IsConfigured,
                cancellationToken);
            if (outbox?.Status == "pending")
            {
                outboxIds.Add(outbox.Id);
                reminderQueued = true;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        if (patronEmail.Outbox?.Status == "pending")
        {
            outboxIds.Add(patronEmail.Outbox.Id);
        }
        await transaction.CommitAsync(cancellationToken);
        foreach (var outboxId in outboxIds)
        {
            try
            {
                outboxDispatcher.Enqueue(outboxId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Committed reminder {OutboxId} remains pending for retry.", outboxId);
            }
        }
        return new TitleRequestMutationResult("updated", request.Id, outboxIds,
            copyResult?.RequestId, copyResult?.ReminderRequested ?? reminderRequested,
            copyResult?.DispatchOutboxId.HasValue ?? reminderQueued,
            reminderRequested && targetStatus != "outstanding_purchase" ? "skipped_purchase_queue" : null);
    }

    private async Task<ExplicitBibPreflight> PreflightExplicitBibAsync(
        CurrentStaff actor,
        long requestId,
        byte[] expectedVersion,
        TitleRequestActionInput input,
        CancellationToken cancellationToken)
    {
        var bibSupplied = IsSupplied(input.Bibid);
        if (!bibSupplied && input.Action is not ("catalogFound" or "purchase" or "alreadyOwn"))
        {
            return new(input.Action == "additionalCopy" ? "bib_required" : null);
        }
        var proposedBib = bibSupplied ? Clean(ElementString(input.Bibid)) : null;
        if (input.Action == "additionalCopy" && proposedBib is null)
        {
            return new("bib_required");
        }
        if (proposedBib is not null && !IsPositiveInteger(proposedBib))
        {
            return new("invalid_bib");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null || !TitleRequestViewService.CanAccess(actor, request.LibraryOrganizationId))
        {
            return new("not_found");
        }
        if (!request.RowVersion.SequenceEqual(expectedVersion))
        {
            return new("stale_version");
        }

        if (!await context.Organizations.AsNoTracking()
                .AnyAsync(item => item.Id == request.LibraryOrganizationId && item.IsActive, cancellationToken))
        {
            return new("organization_inactive");
        }

        var identifierSupplied = IsSupplied(input.Identifier);
        if (!bibSupplied)
        {
            proposedBib = Clean(request.BibId);
        }
        if (proposedBib is not null && !IsPositiveInteger(proposedBib))
        {
            return new("invalid_bib");
        }
        var proposedIdentifier = identifierSupplied ? Clean(ElementString(input.Identifier)) : Clean(request.Identifier);
        var identifierChanged = input.Action != "additionalCopy" && identifierSupplied &&
                                !string.Equals(proposedIdentifier, Clean(request.Identifier), StringComparison.Ordinal);
        var bibChanged = !string.Equals(proposedBib, Clean(request.BibId), StringComparison.Ordinal);
        var activatesOutstandingBib = input.Action == "edit" && request.Status == "outstanding_purchase" &&
            bibSupplied && proposedBib is not null;
        if (!bibChanged && !(identifierChanged && request.Status == "pending_hold") &&
            !activatesOutstandingBib && input.Action is not
                ("additionalCopy" or "catalogFound" or "purchase" or "alreadyOwn"))
        {
            return new();
        }

        var incompleteOperation = await context.HoldPlacementOperations.AsNoTracking()
            .AnyAsync(item => item.TitleRequestId == request.Id && item.CompletedUtc == null, cancellationToken);
        if (incompleteOperation)
        {
            return new("hold_operation_incomplete");
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
            return new(capability.BlockingReason ?? "identifier_locked_by_stage");
        }
        var targetStatus = ResolveStatus(input.Action, input.Status, request.Status, proposedBib);
        if (targetStatus is null)
        {
            return new("invalid_transition");
        }
        if (targetStatus is "hold_placed" or "closed")
        {
            return new("identifier_locked_by_stage");
        }
        if (proposedBib is null)
        {
            return new();
        }

        try
        {
            var result = await staffPolarisProvider.ValidateBibAsync(int.Parse(proposedBib), cancellationToken);
            if (!result.IsValid)
            {
                return new("bib_not_found");
            }
            if (Clean(result.Identifier) is { } catalogIdentifier &&
                !string.Equals(catalogIdentifier, Clean(request.Identifier), StringComparison.Ordinal) &&
                !capability.CanEditIdentifier)
            {
                return new(capability.BlockingReason ?? "identifier_locked_by_stage");
            }
            return new(ValidatedBib: result);
        }
        catch (PolarisOperationalException)
        {
            return new("bib_validation_unavailable");
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
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockForMutationAsync(context, actor, requestId, [actor.Id], cancellationToken);
        if (locked.Code != "locked") return new TitleRequestMutationResult(locked.Code);
        var request = locked.Request!;
        if (locked.Staff[actor.Id].Role is not ("admin" or "super_admin")) return new TitleRequestMutationResult("delete_forbidden");
        if (!request.RowVersion.SequenceEqual(expectedVersion)) return new TitleRequestMutationResult("stale_version");
        if (request.Status != "closed") return new TitleRequestMutationResult("request_not_closed");
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

    private static async Task LockPatronBibTargetAsync(
        AsapDbContext context,
        TitleRequest request,
        CancellationToken cancellationToken)
    {
        var patronKey = $"{request.LibraryOrganizationId}:{request.Barcode.ToUpperInvariant()}";
        var resource = "asap:title-request-bib:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(patronKey)));
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var name = new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource };
        await context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction';",
            [result, name], cancellationToken);
        if (result.Value is not int code || code < 0)
        {
            throw new InvalidOperationException("Could not acquire the patron BIB-target lock.");
        }
    }

    private bool IsSameCurrentActor(CurrentStaff ticket, StaffUser row) =>
        row.IsActive && allowedTenantIds.Contains(ticket.EntraTenantId) &&
        StaffEmail.MatchesAuthenticationEmail(row, ticket.AuthenticationEmail);

    private bool IsEligibleForLibrary(StaffUser row, int organizationId) =>
        row.IsActive && StaffEmail.IsValidAuthenticationEmail(row) &&
        (row.Role == "super_admin" && row.OrganizationId == 1 ||
         row.Role is "staff" or "admin" && row.OrganizationId == organizationId);

    private static void SetManualClaim(TitleRequest request, StaffUser staff)
    {
        request.ClaimedByStaffUserId = staff.Id;
        request.ClaimedByDisplayName = DisplayName(staff);
        request.ClaimedAtUtc = DateTime.UtcNow;
        request.ClaimType = "manual";
        request.ClaimRuleId = null;
    }

    private async Task<string?> ApplyActionClaimAsync(
        AsapDbContext context,
        TitleRequest request,
        CurrentStaff actor,
        IReadOnlyDictionary<long, StaffUser> lockedStaff,
        bool formatChanged,
        FormatAutoClaimRule? preflightClaimRule,
        CancellationToken cancellationToken)
    {
        if (request.ClaimedByStaffUserId != actor.Id)
        {
            var previousClaimantId = request.ClaimedByStaffUserId;
            SetManualClaim(request, lockedStaff[actor.Id]);
            AddEvent(context, request, actor,
                previousClaimantId.HasValue ? "claim_manual_transferred" : "claim_manual_assigned",
                $"Claim set to {DisplayName(lockedStaff[actor.Id])} after staff action.");
            return null;
        }

        if (!formatChanged || request.ClaimType != "automatic_format_rule")
        {
            return null;
        }

        var candidate = await context.FormatAutoClaimRules.FromSqlInterpolated(
                $"SELECT * FROM [asap].[FormatAutoClaimRule] WITH (UPDLOCK,HOLDLOCK) WHERE [LibraryOrganizationId] = {request.LibraryOrganizationId} AND [MaterialFormatId] = {request.MaterialFormatId} AND [IsActive] = 1")
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate?.Id != preflightClaimRule?.Id ||
            candidate?.StaffUserId != preflightClaimRule?.StaffUserId)
        {
            return "claim_rule_changed";
        }
        if (candidate is null)
        {
            request.ClaimedByStaffUserId = null;
            request.ClaimedByDisplayName = null;
            request.ClaimedAtUtc = null;
            request.ClaimType = null;
            request.ClaimRuleId = null;
            AddEvent(context, request, actor, "claim_auto_cleared",
                "Auto-claim cleared because no automatic claimant is configured for the new format.");
            return null;
        }
        if (candidate.StaffUserId is long candidateStaffId &&
            lockedStaff.TryGetValue(candidateStaffId, out var assignee))
        {
            if (IsEligibleForLibrary(assignee, request.LibraryOrganizationId))
            {
                request.ClaimedByStaffUserId = assignee.Id;
                request.ClaimedByDisplayName = DisplayName(assignee);
                request.ClaimedAtUtc = DateTime.UtcNow;
                request.ClaimType = "automatic_format_rule";
                request.ClaimRuleId = candidate.Id;
                AddEvent(context, request, actor, "claim_auto_reassigned",
                    $"Format rule reassigned this request to {DisplayName(assignee)}.");
                return null;
            }
        }
        AddEvent(context, request, actor, "claim_auto_skipped",
            "Auto-claim skipped because the configured claimant is unavailable or outside this library.");
        return null;
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

    private async Task<(string? Error, EmailOutbox? Outbox)> AddPatronActionEmailAsync(
        AsapDbContext context,
        TitleRequest request,
        string previousStatus,
        TitleRequestActionInput input,
        byte[] expectedVersion,
        PatronSnapshot? currentPatron,
        bool transportConfigured,
        CancellationToken cancellationToken)
    {
        var templateKey = input.Action switch
        {
            "reject" => "rejected",
            "alreadyOwn" => "already_owned",
            "purchase" when previousStatus != "outstanding_purchase" &&
                request.Status == "outstanding_purchase" => "purchase_approved",
            _ => null
        };
        if (templateKey is null)
        {
            return (null, null);
        }

        var templates = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == 1 || item.OrganizationId == request.LibraryOrganizationId)
            .ToListAsync(cancellationToken);
        EmailTemplate? source;
        EmailTemplate? overrideRow = null;
        if (input.Action == "reject" && Clean(input.RejectionTemplateId) is { } selectedId)
        {
            if (!long.TryParse(selectedId, out var templateId))
            {
                return ("invalid_rejection_template", null);
            }
            var selected = templates.SingleOrDefault(item => item.Id == templateId);
            if (selected is null || !selected.TemplateKey.StartsWith("rejection:", StringComparison.OrdinalIgnoreCase))
            {
                return ("invalid_rejection_template", null);
            }
            source = selected.OrganizationId == 1 ? selected :
                templates.SingleOrDefault(item => item.Id == selected.SourceTemplateId && item.OrganizationId == 1);
            if (selected.OrganizationId == request.LibraryOrganizationId && selected.IsCustom)
            {
                source = selected;
            }
            else if (selected != source)
            {
                overrideRow = selected;
            }
        }
        else
        {
            source = templates.SingleOrDefault(item => item.OrganizationId == 1 && item.TemplateKey == templateKey);
        }
        if (source is not null && source.OrganizationId == 1 && overrideRow is null &&
            request.LibraryOrganizationId != 1)
        {
            overrideRow = templates.SingleOrDefault(item => item.OrganizationId == request.LibraryOrganizationId &&
                !item.IsCustom && item.SourceTemplateId == source.Id);
        }
        if (input.Action == "reject" && Clean(input.RejectionTemplateId) is not null &&
            (source is null || source.IsHidden || overrideRow?.IsHidden == true ||
             Clean(overrideRow?.SubjectTemplate) is null && Clean(source.SubjectTemplate) is null ||
             Clean(overrideRow?.BodyTemplate) is null && Clean(source.BodyTemplate) is null))
        {
            return ("invalid_rejection_template", null);
        }

        var fallback = templateKey switch
        {
            "rejected" => new EffectiveEmailTemplate(templateKey,
                "Update on your suggestion: {{title}}",
                "Hello {{name}},\n\nWe are not able to add {{title}} to the collection at this time. Thank you for your suggestion."),
            "already_owned" => new EffectiveEmailTemplate(templateKey,
                "{{title}} is already available",
                "Hello {{name}},\n\nThe library already owns {{title}} or has it on order. Thank you for your suggestion."),
            _ => new EffectiveEmailTemplate(templateKey,
                "Purchase approved: {{title}}",
                "Hello {{name}},\n\nThe library approved {{title}} for purchase. We will update you when it is available.")
        };
        var template = new EffectiveEmailTemplate(templateKey,
            Clean(overrideRow?.SubjectTemplate) ?? Clean(source?.SubjectTemplate) ?? fallback.SubjectTemplate,
            Clean(overrideRow?.BodyTemplate) ?? Clean(source?.BodyTemplate) ?? fallback.BodyTemplate);
        var patronConfiguration = await patronConfigurations.GetAsync(
            context, request.LibraryOrganizationId, cancellationToken);
        var formatLabel = patronConfiguration?.Formats
            .FirstOrDefault(item => item.Id == request.MaterialFormatId)?.Label ??
            await context.MaterialFormats.AsNoTracking()
                .Where(item => item.Id == request.MaterialFormatId)
                .Select(item => item.Label)
                .SingleAsync(cancellationToken);
        var patron = currentPatron ?? new PatronSnapshot(0, request.Barcode, null, request.NameFirst,
            request.NameLast, request.PatronCodeId, request.PatronCodeDescription,
            request.PatronOrganizationId ?? 0, request.LibraryOrganizationId,
            request.LibraryNameSnapshot ?? string.Empty, request.PreferredPickupBranchId);
        var rendered = PatronEmailTemplateRenderer.Render(template, patron, request.Title,
            request.Author, formatLabel, request.Barcode);
        var systemEmail = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var libraryEmail = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId, cancellationToken);
        var fromAddress = Clean(libraryEmail?.FromAddress) ?? Clean(systemEmail?.FromAddress);
        var fromName = Clean(libraryEmail?.FromName) ?? Clean(systemEmail?.FromName);
        var toAddress = Clean(currentPatron?.Email);
        string? suppressionReason = source?.IsHidden == true || overrideRow?.IsHidden == true
            ? "template_hidden"
            : null;
        if (suppressionReason is null && currentPatron is null)
        {
            suppressionReason = "patron_refresh_unavailable";
        }
        if (suppressionReason is null &&
            (toAddress is null || !MailAddress.TryCreate(toAddress, out var parsedAddress) ||
             parsedAddress.Address != toAddress))
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
            BusinessKey = $"staff-patron-action:{input.Action}:{request.Id}:{Convert.ToHexString(expectedVersion)}",
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
        return (null, outbox);
    }

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

    private void Dispatch(EmailOutbox? outbox)
    {
        if (outbox?.Status != "pending")
        {
            return;
        }
        try
        {
            outboxDispatcher.Enqueue(outbox.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Committed email {OutboxId} remains pending for retry.", outbox.Id);
        }
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

    private static async Task ResolveUnreachableIdentifierCheckAsync(
        AsapDbContext context,
        TitleRequest request,
        CancellationToken cancellationToken)
    {
        if (Clean(request.Identifier) is null)
        {
            request.IsbnCheckStatus = "skipped_no_isbn";
            request.IsbnCheckResult = null;
        }
        else
        {
            request.IsbnCheckStatus = "not_found";
            request.IsbnCheckResult = InterruptedIdentifierResult;
        }
        request.IsbnCheckRetryCount = 0;
        request.IsbnCheckLastErrorCode = null;
        request.LastCheckedUtc = null;
        await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
    }

    private static async Task ReconcileExplicitPolarisIdentifierAsync(
        AsapDbContext context,
        TitleRequest request,
        string? validatedIdentifier,
        CancellationToken cancellationToken)
    {
        // A valid BIB alone does not verify the request's identifier. The preflight
        // catalog snapshot is safe to use here only after the locked RowVersion and
        // capability checks have confirmed the same request and selected BIB.
        var found = validatedIdentifier is not null;
        var hasIdentifier = Clean(request.Identifier) is not null;
        var preserveNotFound = !found && hasIdentifier && request.IsbnCheckStatus == "not_found";
        if (found)
        {
            request.IsbnCheckStatus = "found";
            request.IsbnCheckResult = "Polaris bibliographic identifier verified for the BIB selected by staff.";
            request.LastCheckedUtc = DateTime.UtcNow;
        }
        else if (!hasIdentifier)
        {
            request.IsbnCheckStatus = "skipped_no_isbn";
            request.IsbnCheckResult = null;
            request.LastCheckedUtc = null;
        }
        else if (!preserveNotFound)
        {
            // The schema has no terminal "unverified" state. With no catalog
            // identifier, not_found is the least misleading terminal value; the
            // result explains that no identifier/BIB match was established.
            request.IsbnCheckStatus = "not_found";
            request.IsbnCheckResult = "Selected Polaris BIB has no catalog identifier; request identifier was not verified.";
            request.LastCheckedUtc = DateTime.UtcNow;
        }
        request.IsbnCheckRetryCount = 0;
        request.IsbnCheckLastErrorCode = null;

        var tags = await (
            from link in context.TitleRequestWorkflowTags
            join tag in context.WorkflowTags on link.WorkflowTagId equals tag.Id
            where link.TitleRequestId == request.Id && IdentifierDerivedTagCodes.Contains(tag.Code)
            select new { Link = link, tag.Code }).ToListAsync(cancellationToken);
        foreach (var tag in tags)
        {
            if (tag.Code == "polaris_multiple_matches" ||
                tag.Code == "polaris_bib_found" && !found ||
                tag.Code == "polaris_bib_not_found")
            {
                context.TitleRequestWorkflowTags.Remove(tag.Link);
            }
        }
        if (found && tags.All(item => item.Code != "polaris_bib_found"))
        {
            var foundTagId = await context.WorkflowTags
                .Where(item => item.Code == "polaris_bib_found")
                .Select(item => item.Id).SingleAsync(cancellationToken);
            context.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag
            {
                TitleRequestId = request.Id,
                WorkflowTagId = foundTagId
            });
        }
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

    private static string? ResolveStatus(string? actionValue, string? requestedStatus, string current, string? bib)
    {
        var action = Clean(actionValue);
        var requested = Clean(requestedStatus);
        var target = action switch
        {
            "edit" when requested is null || requested == current => current,
            "purchase" when current == "suggestion" =>
                string.IsNullOrWhiteSpace(bib) ? "outstanding_purchase" : "pending_hold",
            "alreadyOwn" when current == "suggestion" && !string.IsNullOrWhiteSpace(bib) => "pending_hold",
            "catalogFound" when current is "suggestion" or "outstanding_purchase" &&
                                !string.IsNullOrWhiteSpace(bib) => "pending_hold",
            "additionalCopy" when current is "suggestion" or "outstanding_purchase" or "pending_hold" &&
                                  !string.IsNullOrWhiteSpace(bib) => "pending_hold",
            "reject" or "silentClose" when current == "suggestion" => "closed",
            "closeDuplicate" when current != "closed" => "closed",
            "close" when current == "hold_placed" => "closed",
            "reopen" when current == "closed" => "suggestion",
            _ => null
        };
        return target is not null && (requested is null || requested == target) ? target : null;
    }

    private static string? ResolveBibTargetStatus(string? action, string current, string? requestedTarget,
        string? proposedBib, bool proposedAutoHold, bool bibSupplied)
    {
        if (requestedTarget is null || action == "additionalCopy")
        {
            return requestedTarget;
        }
        var targetsHold = requestedTarget == "pending_hold" ||
            action == "edit" && current == "outstanding_purchase" && bibSupplied && proposedBib is not null;
        if (!targetsHold)
        {
            return requestedTarget;
        }
        return proposedAutoHold ? "pending_hold" : "closed";
    }

    private static string ResolveCloseReason(string? action) => action switch
    {
        "reject" => "rejected",
        "silentClose" => "Silently Closed",
        "closeDuplicate" => "duplicate_hold",
        _ => "manual"
    };

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
                JsonValueKind.Null => null,
                JsonValueKind.Object when property.Value.TryGetProperty("value", out var element) &&
                                          element.ValueKind == JsonValueKind.String => element.GetString(),
                _ => null
            };
            var value = Clean(raw);
            if (value is null)
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
                var option = definition.Options.FirstOrDefault(item =>
                    item.Key == value || item.Label == value);
                if (option is null)
                {
                    return ("invalid_custom_fields", null);
                }
                merged[property.Name] = JsonSerializer.SerializeToElement(new
                {
                    label = definition.Label,
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
                    label = definition.Label,
                    type = definition.Type,
                    value = value[..Math.Min(value.Length, maxLength)]
                });
            }
        }

        return (null, merged.Count == 0 ? null : JsonSerializer.Serialize(merged));
    }

    private static bool IsSupplied(JsonElement value) => value.ValueKind != JsonValueKind.Undefined;
    private static string? ElementString(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : value.ToString();
    private static bool IsPositiveInteger(string? value) => int.TryParse(value, out var result) && result > 0;
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
