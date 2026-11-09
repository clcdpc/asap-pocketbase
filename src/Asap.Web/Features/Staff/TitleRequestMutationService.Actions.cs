using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed partial class TitleRequestMutationService
{
    private sealed record ActionPreparation(int LibraryOrganizationId, bool ActorAdditionalCopyReminderDefault, bool ReminderRequested,
        ActionClaimRuleSnapshot? ClaimRule, PatronSnapshot? Patron, bool TransportConfigured);

    private async Task<(TitleRequestMutationResult? Failure, ActionPreparation? Preparation)> PrepareActionAsync(
        AsapDbContext context, CurrentStaff actor, long requestId, TitleRequestActionCommand input,
        byte[] expectedVersion, bool directQueue, CancellationToken cancellationToken)
    {
        var notificationRequest = await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId)
            .Select(item => new { item.LibraryOrganizationId, item.Barcode, item.Status, item.Identifier, item.BibId,
                item.MaterialFormatId, item.ClaimedByStaffUserId, item.ClaimType, item.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (notificationRequest is null ||
            !TitleRequestViewService.CanAccess(actor, notificationRequest.LibraryOrganizationId))
        {
            return (new TitleRequestMutationResult("not_found"), null);
        }
        var actionActor = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (actionActor is null || !IsSameCurrentActor(actor, actionActor) ||
            !IsEligibleForLibrary(actionActor, notificationRequest.LibraryOrganizationId))
        {
            return (new TitleRequestMutationResult("staff_scope_forbidden"), null);
        }
        if (!notificationRequest.RowVersion.SequenceEqual(expectedVersion))
        {
            return ((directQueue ? await ReplayQueueActionAsync(actor, requestId, input, cancellationToken) : null)
                ?? new TitleRequestMutationResult("stale_version"), null);
        }
        var reminderRequested = input.EmailPurchaseReminder ??
            (input.Action == "additionalCopy" && actionActor.AdditionalCopyReminderDefault);
        ActionClaimRuleSnapshot? claimRuleSnapshot = null;
        if (notificationRequest.ClaimedByStaffUserId == actor.Id &&
            notificationRequest.ClaimType == ClaimType.AutomaticFormatRule &&
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
        var previewIdentifierChanged = input.Identifier.IsSupplied &&
            !string.Equals(Clean(input.Identifier.Value), previewIdentifier, StringComparison.Ordinal);
        var previewBibSupplied = input.BibidSupplied;
        var previewProposedBib = previewBibSupplied ? input.Bibid : previewBib;
        var previewBibChanged = previewBibSupplied &&
            previewProposedBib != previewBib;
        var previewSelectedBibId = input.StaffSelectedBibIdSupplied
            ? input.StaffSelectedBibId : null;
        var previewStaffSelectedBib = previewSelectedBibId is not null &&
            previewSelectedBibId == previewProposedBib;
        var notificationTargetStatus = TitleRequestWorkflowPolicy.ResolveStatus(input.Action, input.Status, notificationRequest.Status,
            ProjectBibAfterMutation(previewIdentifierChanged, previewBibChanged,
                previewStaffSelectedBib, previewProposedBib));
        var notificationRequested = input.Action is "reject" or "alreadyOwn" ||
            input.Action == "purchase" && notificationTargetStatus == RequestStatus.OutstandingPurchase ||
            input.Action == "additionalCopy" && reminderRequested;
        PatronSnapshot? currentPatron = null;
        if (input.Action is "reject" or "alreadyOwn" ||
            input.Action == "purchase" && notificationTargetStatus == RequestStatus.OutstandingPurchase)
        {
            try
            {
                currentPatron = await patronProvider.RefreshAsync(notificationRequest.Barcode, notificationRequest.LibraryOrganizationId, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PolarisOperationalException exception)
            {
                logger.LogWarning(exception,
                    "Patron refresh failed before action email for request {RequestId}.", requestId);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        var transportConfigured = false;
        if (notificationRequested)
        {
            try
            {
                transportConfigured = (await emailSender.CheckReadinessAsync(
                    notificationRequest.LibraryOrganizationId, cancellationToken)).IsConfigured;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return (new TitleRequestMutationResult("notification_dependency_unavailable"), null);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        return (null, new ActionPreparation(notificationRequest.LibraryOrganizationId, actionActor.AdditionalCopyReminderDefault,
            reminderRequested, claimRuleSnapshot, currentPatron, transportConfigured));
    }

    // Use the caller's tracked request and transaction. SaveChanges in the notification/task
    // helpers only obtains identities; any later returned failure still rolls everything back.
    private async Task<(TitleRequestMutationResult Result, long? PatronNotificationOutboxId)> ApplyLockedActionAsync(
        AsapDbContext context, CurrentStaff actor, LockedMutation locked, TitleRequestActionCommand input,
        byte[] expectedVersion, BibValidationResult? validatedBib, ActionPreparation preparation, bool directQueue,
        CancellationToken cancellationToken)
    {
        var request = locked.Request!;
        var proposedExactDate = input.ExactPublicationDate.Value;
        var claimRuleSnapshot = preparation.ClaimRule;
        var reminderRequested = preparation.ReminderRequested;
        var currentPatron = preparation.Patron;
        var transportConfigured = preparation.TransportConfigured;
        if (await PickupPreferenceMutationService.HasIncompleteAsync(context, request.Id, cancellationToken))
        {
            return (new TitleRequestMutationResult("pickup_reconciliation_required"), null);
        }
        var incompleteOperation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
            cancellationToken);
        var successfulOperation = await context.HoldPlacementOperations.AnyAsync(
            item => item.TitleRequestId == request.Id && item.State == HoldOperationState.Succeeded,
            cancellationToken);
        var placedProtection = successfulOperation;
        var capabilities = TitleRequestWorkflowPolicy.Evaluate(request, incompleteOperation, placedProtection);
        if (directQueue && (!capabilities.CanChangeWorkflowState || !capabilities.CanChangeBib || !capabilities.CanEditIdentifier))
        {
            return (new TitleRequestMutationResult(capabilities.BlockingReason ?? "invalid_transition"), null);
        }
        if (input.Action == "reopen" && placedProtection)
        {
            return (new TitleRequestMutationResult("hold_history_retained"), null);
        }

        var identifierSupplied = input.Identifier.IsSupplied;
        var proposedIdentifier = identifierSupplied ? Clean(input.Identifier.Value) : Clean(request.Identifier);
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
        var autoHoldSupplied = input.Autohold.IsSupplied;
        var proposedAutoHold = input.Action == "additionalCopy" || (autoHoldSupplied ? input.Autohold.Value : request.AutoHold);
        var requestedTarget = TitleRequestWorkflowPolicy.ResolveStatus(input.Action, input.Status, request.Status, bibAfterMutation);
        var targetStatus = TitleRequestWorkflowPolicy.ResolveBibTargetStatus(input.Action, request.Status, requestedTarget,
            bibAfterMutation, proposedAutoHold, bibSupplied);
        if (targetStatus is null)
        {
            return (new TitleRequestMutationResult("invalid_transition"), null);
        }
        var statusChanged = targetStatus != request.Status;
        var autoHoldOptOut = targetStatus == RequestStatus.Closed &&
            (requestedTarget == RequestStatus.PendingHold ||
             input.Action == "edit" && request.Status == RequestStatus.OutstandingPurchase &&
             bibSupplied && bibAfterMutation is not null);

        if (incompleteOperation && (identifierChanged || bibChanged || statusChanged || proposedAutoHold != request.AutoHold))
        {
            return (new TitleRequestMutationResult("hold_operation_incomplete"), null);
        }
        if (identifierChanged && !capabilities.CanEditIdentifier || bibChanged && !capabilities.CanChangeBib)
        {
            return (new TitleRequestMutationResult(capabilities.BlockingReason ?? "identifier_locked_by_stage"), null);
        }
        if ((targetStatus == RequestStatus.HoldPlaced || targetStatus == RequestStatus.Closed && !autoHoldOptOut) &&
            (identifierChanged || bibChanged))
        {
            return (new TitleRequestMutationResult("identifier_locked_by_stage"), null);
        }
        if (requestedTarget == RequestStatus.PendingHold && statusChanged && bibAfterMutation is null)
        {
            return (new TitleRequestMutationResult("bib_required"), null);
        }
        if (requestedTarget == RequestStatus.PendingHold && statusChanged &&
            !request.BibIdStaffVerified && !bibChanged && !staffSelectedBib)
        {
            return (new TitleRequestMutationResult("bib_unverified"), null);
        }
        if (bibChanged && proposedBib is not null && proposedBib <= 0)
        {
            return (new TitleRequestMutationResult("invalid_bib"), null);
        }
        if (targetStatus == RequestStatus.PendingHold && bibAfterMutation is not null &&
            (request.Status != RequestStatus.PendingHold || !request.AutoHold || bibChanged))
        {
            // LockForMutationAsync holds the library Organization row until commit. Patron
            // creation and automatic promotion take the same lock before writing requests.
            var duplicate = await context.TitleRequests.AsNoTracking().Where(item =>
                item.LibraryOrganizationId == request.LibraryOrganizationId &&
                ((request.PatronIdSnapshot.HasValue && item.PatronIdSnapshot == request.PatronIdSnapshot) ||
                 (!request.PatronIdSnapshot.HasValue || item.PatronIdSnapshot == null) && item.Barcode == request.Barcode) && item.BibId == bibAfterMutation &&
                item.Id != request.Id && item.Status != RequestStatus.Closed)
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Title, item.Status, item.BibId })
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicate is not null)
            {
                return (new TitleRequestMutationResult("duplicate_open_request", Duplicate:
                    new TitleRequestDuplicateConflict(duplicate.Id, duplicate.Title, duplicate.Status,
                        duplicate.BibId!.Value, "bibid")), null);
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
                    return (new TitleRequestMutationResult("invalid_rejection_template"), null);
                }
                rejectionTemplate = RejectionTemplatePolicy.Resolve(
                    templates, request.LibraryOrganizationId, parsedId);
                if (rejectionTemplate is null)
                {
                    return (new TitleRequestMutationResult("invalid_rejection_template"), null);
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
            return (new TitleRequestMutationResult("invalid_rejection_template"), null);
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
        if (input.ExactPublicationDate.IsSupplied)
        {
            request.ExactPublicationDate = proposedExactDate;
        }
        if (autoHoldSupplied || input.Action == "additionalCopy")
        {
            request.AutoHold = proposedAutoHold;
        }
        if (directQueue && validatedBib is { } verifiedBib)
        {
            request.Title = Clean(verifiedBib.Title) ?? request.Title;
            await ReconcileQueueIdentifierAsync(context, request, verifiedBib, cancellationToken);
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
                    return (new TitleRequestMutationResult("invalid_format"), null);
                }
                request.MaterialFormatId = formatId.Value;
            }
        }
        var customFieldsChanged = false;
        if (input.CustomFields.IsSupplied || previousFormatId != request.MaterialFormatId)
        {
            IReadOnlyDictionary<string, string?> submittedFields = input.CustomFields.Value ?? new Dictionary<string, string?>();
            if (!input.CustomFields.IsSupplied)
            {
                try
                {
                    submittedFields = JsonSerializer.Deserialize<Dictionary<string, CustomFieldSnapshot>>(
                        string.IsNullOrWhiteSpace(previousCustomFields) ? "{}" : previousCustomFields)?
                        .ToDictionary(item => item.Key, item => item.Value.Value, StringComparer.Ordinal)
                        ?? new Dictionary<string, string?>();
                }
                catch (JsonException)
                {
                    return (new TitleRequestMutationResult("invalid_custom_fields"), null);
                }
            }
            var customFields = await MergeCustomFieldsAsync(context, request, submittedFields, cancellationToken);
            if (customFields.Error is not null)
            {
                return (new TitleRequestMutationResult(customFields.Error), null);
            }
            customFieldsChanged = !string.Equals(previousCustomFields, customFields.Json, StringComparison.Ordinal);
            if (customFieldsChanged)
            {
                request.CustomFieldsJson = customFields.Json;
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        request.Status = targetStatus;
        request.CloseReason = targetStatus == RequestStatus.Closed
            ? autoHoldOptOut ? "purchased_no_hold"
                : statusChanged ? ResolveCloseReason(input.Action) : request.CloseReason
            : null;
        request.UpdatedUtc = now;
        if (targetStatus != RequestStatus.Suggestion && request.IsbnCheckStatus == IdentifierCheckState.Pending)
        {
            await ResolveUnreachableIdentifierCheckAsync(context, request, cancellationToken);
        }
        else if (input.Action == "reopen" && targetStatus == RequestStatus.Suggestion &&
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
            return (new TitleRequestMutationResult(claimError), null);
        }

        var outboxIds = new List<long>();
        string? notificationStatus = null;
        string? notificationReason = null;
        string? patronNotificationStatus = null;
        string? patronNotificationReason = null;
        long? patronNotificationOutboxId = null;
        AdditionalCopyRequest? additionalCopy = null;
        if (input.Action == "additionalCopy")
        {
            if (await context.AdditionalCopyRequests.AnyAsync(item =>
                    item.SourceTitleRequestId == request.Id && item.Status == "open", cancellationToken))
            {
                return (new TitleRequestMutationResult("additional_copy_exists"), null);
            }
            additionalCopy = await AddQueuedCopyAsync(context, request, locked.Staff[actor.Id], cancellationToken);
            AddEvent(context, request, actor, "additional_copy_created",
                $"Additional-copy task {additionalCopy.Id} created for BIB {request.BibId}; automatic hold placement enabled and request queued.",
                new { input.OperationId, additionalCopyRequestId = additionalCopy.Id.ToString(CultureInfo.InvariantCulture),
                    bibId = request.BibId, previousAutoHold = previousAutohold, autoHold = true });
            notificationStatus = "not_requested";
            if (reminderRequested)
            {
                var outbox = await AddStaffNotificationAsync(context, locked.Staff[actor.Id], request,
                    $"additional-copy-reminder:{additionalCopy.Id}", "additional_copy_reminder", "ASAP additional-copy reminder",
                    $"Additional-copy task {additionalCopy.Id} for {request.Title} (BIB {request.BibId}) was created.",
                    transportConfigured, cancellationToken);
                notificationStatus = outbox?.Status == "pending" ? "queued" : "suppressed";
                notificationReason = outbox?.SuppressionReason ?? "recipient_missing";
                if (outbox?.Status == "pending")
                {
                    notificationReason = null;
                    outboxIds.Add(outbox.Id);
                }
            }
        }
        if (reminderRequested && input.Action == "purchase" && targetStatus == RequestStatus.OutstandingPurchase)
        {
            var outbox = await AddStaffNotificationAsync(
                context,
                locked.Staff[actor.Id],
                request,
                $"purchase-reminder:{request.Id}:{Convert.ToHexString(expectedVersion)}",
                "purchase_reminder",
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
            notificationStatus = reminderRequested ? "not_applicable" : "not_requested";
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
        if (input.Action == "purchase" && targetStatus == RequestStatus.OutstandingPurchase ||
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

        var committedResult = new TitleRequestMutationResult("updated", request.Id, outboxIds,
            targetStatus, notificationStatus, notificationReason, patronNotificationStatus, patronNotificationReason,
            OperationId: input.OperationId, AdditionalCopyRequestId: additionalCopy?.Id.ToString(CultureInfo.InvariantCulture));
        if (directQueue)
        {
            AddEvent(context, request, actor, QueueReceiptEvent,
                $"Selected Polaris BIB {request.BibId}; workflow action {input.Action} committed.",
                new QueueReceipt(input.OperationId!.Value, QueueFingerprint(input), committedResult));
        }
        return (committedResult, patronNotificationOutboxId);
    }

    private TitleRequestMutationResult DispatchActionNotifications(
        TitleRequestMutationResult committedResult, long? patronNotificationOutboxId, CancellationToken cancellationToken)
    {
        var notificationStatus = committedResult.NotificationStatus;
        var notificationReason = committedResult.NotificationReason;
        var patronNotificationStatus = committedResult.PatronNotificationStatus;
        var patronNotificationReason = committedResult.PatronNotificationReason;
        foreach (var outboxId in committedResult.DispatchOutboxIds!)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                outboxDispatcher.Enqueue(outboxId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            // Committed outbox rows remain recoverable even if the immediate dispatch path has a defect.
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification dispatch failed after request {RequestId} committed", committedResult.RequestId);
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
        return committedResult with { NotificationStatus = notificationStatus, NotificationReason = notificationReason,
            PatronNotificationStatus = patronNotificationStatus, PatronNotificationReason = patronNotificationReason };
    }
}
