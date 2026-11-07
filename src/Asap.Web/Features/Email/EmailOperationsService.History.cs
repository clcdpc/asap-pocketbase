using System.Data;
using System.Globalization;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

public sealed record EmailOperationsQuery(
    LibraryScope Scope, string? Search = null, string? Status = null, string? Type = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? BeforeUtc = null,
    string Sort = "newest", int Page = 1, int PageSize = 25, string? Reference = null);

public sealed record EmailOperationFilterOption(string Value, string Label);
public sealed record EmailOperationLibraryOption(int Id, string Name);
public sealed record EmailOperationsPage(
    IReadOnlyList<EmailOperationItem> Items, long Count, int Page, int PageSize, string Scope,
    IReadOnlyList<EmailOperationLibraryOption> Libraries, bool LibrariesTruncated,
    IReadOnlyList<EmailOperationFilterOption> Types);

public sealed partial class EmailOperationsService
{
    private static readonly EmailOperationFilterOption[] HistoryTypes =
    [
        new("business_event", "Business event"),
        new("staff_authorization_sensitive", "Staff notification"),
        new("operational_test", "Operational test")
    ];

    // These are application-owned codes, never provider text. Unknown historical
    // values remain private and cannot affect search results or matching counts.
    private static readonly IReadOnlyDictionary<string, string> HistoryDetails = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["recipient_missing"] = "Recipient not recorded",
        ["recipient_invalid"] = "Recipient address invalid",
        ["recipient_missing_or_invalid"] = "Recipient address missing or invalid",
        ["recipient_domain_not_allowed"] = "Recipient domain not allowed",
        ["recipient_authorization_changed"] = "Recipient authorization changed",
        ["sender_missing"] = "Sender address missing",
        ["staff_url_missing"] = "Staff application address missing",
        ["template_missing"] = "Email template missing",
        ["template_hidden"] = "Email template hidden",
        ["mail_not_configured"] = "Email transport not configured",
        ["mail_readiness_unavailable"] = "Email readiness unavailable; send not started",
        ["pre_send_check_pending"] = "Pre-send check pending",
        ["pre_send_cancelled"] = "Pre-send cancelled; send not started",
        ["provider_timeout"] = "Provider timed out; acceptance unconfirmed",
        ["transport_failure"] = "Transport failed; acceptance unconfirmed",
        ["ambiguous_expired_lease"] = "Expired lease; acceptance unconfirmed",
        ["provider_start_deadline_exceeded"] = "Provider start deadline exceeded; acceptance unconfirmed",
        ["test_configuration_changed"] = "Saved test configuration changed",
        ["test_delivery_mode_changed"] = "Test delivery mode changed",
        ["patron_refresh_unavailable"] = "Current patron information unavailable",
        ["patron_identity_unverified"] = "Patron identity unverified",
        ["native_patron_identity_mismatch"] = "Recorded patron identity changed",
        ["native_patron_identity_unavailable"] = "Recorded patron identity unavailable"
    };

    public async Task<EmailOperationResult> ListAsync(
        CurrentStaff actor, EmailOperationsQuery input, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadHistoryAsync(actor, input, cancellationToken);
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested && exception.Errors.Count > 0 &&
            exception.Errors.Cast<SqlError>().All(error => error.Number == 0 && error.Class == 11 && error.State == 0))
        {
            // SqlClient can report an interrupted blocked read with this error
            // tuple. Preserve unrelated SQL failures even if the caller cancels.
            throw new OperationCanceledException("Email history read was cancelled.", exception, cancellationToken);
        }
    }

    private async Task<EmailOperationResult> ReadHistoryAsync(
        CurrentStaff actor, EmailOperationsQuery input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanOperate(actor) || actor.Role != StaffRole.SuperAdmin &&
            (input.Scope.Kind != LibraryScopeKind.Library || input.Scope.OrganizationId != actor.OrganizationId))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }

        var search = input.Search?.Trim();
        var reference = 0L;
        if (search?.Length > 200 || input.Status is { Length: > 0 } &&
            input.Status is not ("pending" or "sending" or "sent" or "failed" or "suppressed") ||
            input.Type is { Length: > 0 } && !HistoryTypes.Any(item => item.Value == input.Type) ||
            input.Sort is not ("newest" or "oldest") || input.Page < 1 || input.PageSize is < 1 or > 100 ||
            (long)(input.Page - 1) * input.PageSize > int.MaxValue || input.FromUtc >= input.BeforeUtc ||
            input.Reference is not null && (!long.TryParse(input.Reference, NumberStyles.None, CultureInfo.InvariantCulture, out reference) ||
                reference <= 0 || reference.ToString(CultureInfo.InvariantCulture) != input.Reference))
        {
            return new EmailOperationResult("email_query_invalid");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var targetOrganizationId = input.Scope.OrganizationId ?? actor.OrganizationId;
        var lockedIds = new[] { LibraryScope.SystemOrganizationId, actor.OrganizationId, targetOrganizationId }.ToHashSet();
        if (!await StaffEligibilityService.LockOrganizationsAsync(context, lockedIds, cancellationToken))
        {
            return new EmailOperationResult("organization_not_found");
        }
        var target = context.Organizations.Local.Single(item => item.Id == targetOrganizationId);
        if (targetOrganizationId != LibraryScope.SystemOrganizationId && !OrganizationAuthority.IsLibrary(target))
        {
            return new EmailOperationResult("organization_not_found");
        }
        var locked = await staffEligibility.RevalidateLockedAsync(context, actor, targetOrganizationId,
            StaffRoleRequirement.Admin, requireActorParticipation: true, lockedIds, cancellationToken);
        if (locked.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return new EmailOperationResult(locked.Code);
        }
        // A stale global ticket must not select a wider query or option set after
        // an otherwise valid demotion/rebinding to the requested library.
        if (locked.Staff!.Role != actor.Role || locked.Staff.OrganizationId != actor.OrganizationId)
        {
            return new EmailOperationResult("staff_session_invalid");
        }

        var query = context.EmailOutbox.AsNoTracking().Where(item => context.Organizations.Any(organization =>
            organization.Id == item.OrganizationId && (organization.Id == LibraryScope.SystemOrganizationId ||
                organization.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId)));
        if (input.Scope.OrganizationId is { } organizationId)
        {
            query = query.Where(item => item.OrganizationId == organizationId);
        }
        if (input.Reference is not null)
        {
            query = query.Where(item => item.Id == reference);
        }
        if (!string.IsNullOrEmpty(search))
        {
            var searchId = long.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 && id.ToString(CultureInfo.InvariantCulture) == search ? id : 0;
            var matchingTypes = HistoryTypes.Where(item => item.Value.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Label.Contains(search, StringComparison.OrdinalIgnoreCase)).Select(item => item.Value).ToArray();
            var matchingCodes = HistoryDetails.Where(item => item.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Value.Contains(search, StringComparison.OrdinalIgnoreCase)).Select(item => item.Key).ToArray();
            query = query.Where(item => item.Id == searchId || matchingTypes.Contains(item.DeliveryClass) ||
                item.LastErrorCode != null && matchingCodes.Contains(EF.Functions.Collate(item.LastErrorCode, "Latin1_General_100_BIN2")) ||
                item.SuppressionReason != null && matchingCodes.Contains(EF.Functions.Collate(item.SuppressionReason, "Latin1_General_100_BIN2")));
        }
        if (!string.IsNullOrEmpty(input.Status))
        {
            query = query.Where(item => item.Status == input.Status);
        }
        if (!string.IsNullOrEmpty(input.Type))
        {
            query = query.Where(item => item.DeliveryClass == input.Type);
        }
        if (input.FromUtc is { } from)
        {
            query = query.Where(item => item.CreatedUtc >= from.UtcDateTime);
        }
        if (input.BeforeUtc is { } before)
        {
            query = query.Where(item => item.CreatedUtc < before.UtcDateTime);
        }

        var count = await query.LongCountAsync(cancellationToken);
        var lastPage = count == 0 ? 1 : (count - 1) / input.PageSize + 1;
        var page = (int)Math.Min(input.Page, lastPage);
        var ordered = input.Sort == "oldest"
            ? query.OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id)
            : query.OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id);
        var rows = await ordered.Skip((page - 1) * input.PageSize).Take(input.PageSize)
            .Select(item => new
            {
                item.Id, item.OrganizationId, item.Status, item.DeliveryClass, item.RecipientAddressKind,
                item.AttemptCount, item.LastErrorCode, item.SuppressionReason, item.CreatedUtc, item.SentUtc,
                item.RowVersion, item.ProviderMessageId, item.AttemptDeliveryMode,
                item.SendingStartedUtc, item.LeaseId, item.LeaseExpiresUtc, item.NextAttemptUtc
            }).ToListAsync(cancellationToken);
        var libraries = await context.Organizations.AsNoTracking()
            .Where(item => item.Id > LibraryScope.SystemOrganizationId &&
                item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId &&
                (actor.Role == StaffRole.SuperAdmin || item.Id == actor.OrganizationId))
            .OrderBy(item => item.Id != input.Scope.OrganizationId).ThenBy(item => item.DisplayName).ThenBy(item => item.Id)
            .Select(item => new EmailOperationLibraryOption(item.Id, item.DisplayName)).Take(501).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EmailOperationResult("ok", new EmailOperationsPage(rows.Select(item => new EmailOperationItem(
            item.Id.ToString(CultureInfo.InvariantCulture), item.OrganizationId, item.Status, item.DeliveryClass,
            item.RecipientAddressKind, item.AttemptCount, SafeHistoryCode(item.LastErrorCode), SafeHistoryCode(item.SuppressionReason),
            DateTime.SpecifyKind(item.CreatedUtc, DateTimeKind.Utc),
            item.SentUtc.HasValue ? DateTime.SpecifyKind(item.SentUtc.Value, DateTimeKind.Utc) : null,
            StaffVersion.Encode(item.RowVersion),
            IsCertifiedForRetry(item.Status, item.LastErrorCode, item.ProviderMessageId, item.SendingStartedUtc,
                item.LeaseId, item.LeaseExpiresUtc, item.NextAttemptUtc),
            HistoryDeliveryMode(item.AttemptDeliveryMode, item.ProviderMessageId),
            HistoryTypes.Single(type => type.Value == item.DeliveryClass).Label,
            HistoryDetailLabel(item.LastErrorCode, item.SuppressionReason))).ToArray(),
            count, page, input.PageSize, input.Scope.ToTransportValue(), libraries.Take(500).ToArray(), libraries.Count > 500, HistoryTypes));
    }

    private static string? SafeHistoryCode(string? code)
    {
        // SQL string equality pads trailing spaces, even with binary collation.
        var canonical = code?.TrimEnd(' ');
        return canonical is not null && HistoryDetails.ContainsKey(canonical) ? canonical : null;
    }

    private static string? HistoryDetailLabel(string? error, string? suppression)
    {
        var labels = new[] { SafeHistoryCode(error), SafeHistoryCode(suppression) }.Where(code => code is not null)
            .Select(code => HistoryDetails[code!]).Distinct().ToArray();
        return labels.Length > 0 ? string.Join("; ", labels) :
            error is not null || suppression is not null ? "Recorded detail unavailable" : null;
    }

    private static string HistoryDeliveryMode(string? mode, string? providerMessageId) => mode switch
    {
        "postmark" or "file" or "postmark_sandbox" or "non_delivery" => mode,
        null when providerMessageId?.StartsWith("file:", StringComparison.Ordinal) == true => "file",
        _ => "unknown"
    };
}
