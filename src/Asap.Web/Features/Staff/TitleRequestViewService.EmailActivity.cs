using System.Globalization;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed partial class TitleRequestViewService
{
    private static readonly IReadOnlyDictionary<string, string> EmailOutcomes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["queued"] = "Queued",
        ["claimed"] = "Pre-send checks started",
        ["send_intent"] = "Transport send intent recorded; acceptance unconfirmed",
        ["provider_accepted"] = "Provider accepted; delivery unconfirmed",
        ["captured"] = "Captured to file; no live email sent",
        ["non_delivery"] = "Non-delivery transport accepted; no live email sent",
        ["failed_no_send"] = "Failed before sending",
        ["suppressed"] = "Suppressed; no email sent",
        ["retry_queued"] = "Explicit retry queued",
        ["pre_send_released"] = "Pre-send claim released; send not started",
        ["pre_send_deferred"] = "Send deferred before transport started",
        ["ambiguous"] = "Transport outcome unknown; acceptance unconfirmed",
        ["lease_expired_ambiguous"] = "Expired delivery lease; acceptance unconfirmed",
        ["recorded_pending"] = "Recorded summary: pending",
        ["recorded_sending"] = "Recorded summary: sending; acceptance unconfirmed",
        ["recorded_sent"] = "Recorded summary: sent status; delivery unconfirmed",
        ["recorded_failed"] = "Recorded summary: failed",
        ["recorded_suppressed"] = "Recorded summary: suppressed",
        ["delivered"] = "Provider recorded delivery"
    };

    // GetAsync has already loaded and revalidated the request, current identity,
    // role and library before this query can inspect any email evidence.
    private static async Task<IReadOnlyList<TitleRequestActivity>> LoadEmailActivityAsync(
        AsapDbContext context, TitleRequest request, CurrentStaff staff, CancellationToken cancellationToken)
    {
        var copyIds = await context.AdditionalCopyRequests.AsNoTracking().Where(item =>
                item.SourceTitleRequestId == request.Id && item.LibraryOrganizationId == request.LibraryOrganizationId)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var requestId = request.Id.ToString(CultureInfo.InvariantCulture);
        string[] exactKeys = [$"patron-submission:{requestId}", $"timeout:OutstandingTimeout:{requestId}",
            .. copyIds.Select(id => $"additional-copy-reminder:{id.ToString(CultureInfo.InvariantCulture)}")];
        string[] prefixes = [$"title-assignment:{requestId}:", $"purchase-reminder:{requestId}:",
            $"staff-patron-action:purchase_approved:{requestId}:", $"staff-patron-action:already_owned:{requestId}:",
            $"rejection:{requestId}:", $"title-hold-placed:{requestId}:",
            .. copyIds.Select(id => $"additional-copy-assignment:{id.ToString(CultureInfo.InvariantCulture)}:")];
        var candidates = await context.EmailOutbox.AsNoTracking()
            .Where(item => item.OrganizationId == request.LibraryOrganizationId && item.DeliveryClass != "operational_test" &&
                (item.RecipientAddressKind == null || !item.RecipientAddressKind.StartsWith("weekly_summary")) &&
                (item.TitleRequestId == request.Id || item.TitleRequestId == null && item.MessageKind == null &&
                    item.BusinessKey != null && (exactKeys.Contains(item.BusinessKey) || prefixes.Any(prefix => item.BusinessKey.StartsWith(prefix)))))
            .Select(item => new
            {
                item.Id, item.TitleRequestId, item.MessageKind, item.BusinessKey, item.DeliveryClass,
                item.RecipientStaffUserId, item.ToAddress, item.Status, item.SuppressionReason, item.LastErrorCode,
                item.CreatedUtc, item.LastAttemptUtc, item.SentUtc, item.SuppressedUtc, item.AttemptDeliveryMode,
                item.ProviderMessageId,
                IsFile = item.ProviderMessageId != null && item.ProviderMessageId.StartsWith("file:")
            }).ToArrayAsync(cancellationToken);
        var associated = candidates.Select(item => new
            {
                Row = item,
                Kind = item.TitleRequestId == request.Id ? item.MessageKind : RequestEmailEvidence.ParseLegacyKey(item.BusinessKey) is { } identity &&
                    (identity.IsCopy ? copyIds.Contains(identity.Id) : identity.Id == request.Id) ? identity.MessageKind : null
            })
            .Where(item => item.Kind is not null && RequestEmailEvidence.MessageKinds.ContainsKey(item.Kind)).ToArray();
        var outboxIds = associated.Select(item => item.Row.Id).ToArray();
        if (outboxIds.Length == 0)
        {
            return [];
        }
        var events = await context.EmailDeliveryEvents.AsNoTracking()
            .Where(item => item.EmailOutboxId.HasValue && outboxIds.Contains(item.EmailOutboxId.Value))
            .Select(item => new
            {
                item.Id, item.EmailOutboxId, item.EventType, item.ReceivedUtc, item.ReasonCode, item.DeliveryMode,
                item.TimestampKind, item.LocalEventKey, item.ProviderMessageId, IsProviderEvent = item.ProviderEventId != null && item.ProviderEventId != ""
            }).ToArrayAsync(cancellationToken);
        var staffIds = associated.Where(item => item.Row.RecipientStaffUserId.HasValue)
            .Select(item => item.Row.RecipientStaffUserId!.Value).Distinct().ToArray();
        var recipients = await context.StaffUsers.AsNoTracking().Where(item => staffIds.Contains(item.Id))
            .Select(item => new { item.Id, item.OrganizationId, item.Role }).ToDictionaryAsync(item => item.Id, cancellationToken);
        var activity = new List<TitleRequestActivity>();
        foreach (var item in associated)
        {
            var row = item.Row;
            var staffMessage = item.Kind is "assignment" or "purchase_reminder" or "additional_copy_reminder" or "additional_copy_assignment" ||
                row.DeliveryClass == "staff_authorization_sensitive";
            var canSeeRecipient = !staffMessage || row.RecipientStaffUserId is { } recipientId &&
                (recipientId == staff.Id || staff.Role == StaffRole.SuperAdmin || staff.Role == StaffRole.Admin &&
                    recipients.TryGetValue(recipientId, out var recipient) && recipient.Role != StaffRole.SuperAdmin && recipient.OrganizationId == staff.OrganizationId);
            // An intended staff address is private under the same directory/own
            // recipient authority as its source. Authentication email is never a fallback.
            var intendedRecipient = canSeeRecipient ? row.ToAddress : null;
            var recorded = events.Where(entry => entry.EmailOutboxId == row.Id && EmailOutcomes.ContainsKey(entry.EventType) &&
                (entry.EventType == "delivered" ? entry.IsProviderEvent && entry.ProviderMessageId is not null &&
                    entry.ProviderMessageId == row.ProviderMessageId && !row.IsFile : entry.LocalEventKey is not null)).ToArray();
            foreach (var entry in recorded)
            {
                Add(entry.Id, "email_event", entry.EventType, entry.ReceivedUtc, entry.ReasonCode,
                    entry.DeliveryMode, entry.TimestampKind ?? "recorded", entry.EventType.StartsWith("recorded_", StringComparison.Ordinal));
            }
            if (!recorded.Any(entry => entry.LocalEventKey is not null))
            {
                var timestamp = row.Status == "sent" ? row.SentUtc : row.Status == "suppressed" ? row.SuppressedUtc : row.LastAttemptUtc;
                var timestampKind = row.Status == "sent" && timestamp.HasValue ? "sent" : row.Status == "suppressed" && timestamp.HasValue
                    ? "suppressed" : timestamp.HasValue ? "last_attempt" : "created";
                Add(row.Id, "email_summary", $"recorded_{row.Status}", timestamp ?? row.CreatedUtc,
                    row.Status == "suppressed" ? row.SuppressionReason : row.LastErrorCode,
                    row.AttemptDeliveryMode ?? (row.IsFile ? "file" : null), timestampKind, true);
            }

            void Add(long id, string source, string outcome, DateTime timestamp, string? reason, string? mode, string timeKind, bool summary)
            {
                var safeMode = mode is "file" or "postmark" or "postmark_sandbox" or "non_delivery" ? mode : "unknown";
                var label = EmailOutcomes.GetValueOrDefault(outcome) ?? "Recorded outcome unavailable";
                if (outcome == "recorded_sent" && safeMode == "file")
                {
                    label = "Recorded summary: captured to file; no live email sent";
                }
                if (summary)
                {
                    label = (source == "email_summary" ? "Current recorded summary" : "Earlier recorded summary") + label["Recorded summary".Length..];
                }
                activity.Add(new TitleRequestActivity(id.ToString(CultureInfo.InvariantCulture), "email", "system", null, label, AsUtc(timestamp))
                {
                    Source = source,
                    Email = new RequestEmailActivity(row.Id.ToString(CultureInfo.InvariantCulture), item.Kind,
                        RequestEmailEvidence.MessageKinds[item.Kind!], outcome, label,
                        EmailOperationsService.HistoryDetailLabel(reason, null), intendedRecipient, safeMode, summary, timeKind,
                        EmailOperationsService.CanOperate(staff) ? LibraryScope.ForLibrary(request.LibraryOrganizationId).ToTransportValue() : null)
                });
            }
        }
        return activity;
    }
}
