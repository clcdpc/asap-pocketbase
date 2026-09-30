using System.Globalization;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record TitleRequestCapabilities(
    bool CanEditIdentifier,
    bool CanChangeBib,
    bool CanRetryIdentifierCheck,
    bool CanChangeWorkflowState,
    string? BlockingReason);

public sealed record HoldOperationSummary(
    string Id,
    string State,
    string Phase,
    int AttemptNumber,
    long ExecutionEpoch,
    string PatronBarcodeSnapshotMasked,
    int BibIdSnapshot,
    string Version,
    DateTime? LastRecoveryUtc,
    string? LastErrorCode,
    bool CanReconcile,
    bool CanResolveSucceeded,
    bool CanResolveNotPerformed);

public sealed record TitleRequestActivity(
    string Id,
    string EventType,
    string ActorType,
    string? ActorName,
    string? Message,
    DateTime Created);

public sealed record RelatedRequestCount(string Status, int LibraryOrgId, string LibraryOrgName, int Count);

public sealed record RelatedRequestSummary(int Count, IReadOnlyList<RelatedRequestCount> StatusAndLibraryCounts);

public sealed record RequestWorkflowContext(
    bool AutoPromote,
    bool OutstandingTimeoutEnabled,
    int? OutstandingTimeoutDays,
    bool PendingHoldTimeoutEnabled,
    int? PendingHoldTimeoutDays,
    bool HoldPickupTimeoutEnabled,
    int? HoldPickupTimeoutDays);

public sealed record TitleRequestDto(
    string Id,
    string Type,
    string? LegacyId,
    int LibraryOrgId,
    string LibraryOrgName,
    int? PatronOrgId,
    string Barcode,
    string? Email,
    string? NameFirst,
    string? NameLast,
    int? PatronCodeId,
    string? PatronCodeDescription,
    int? PreferredPickupBranchId,
    string? PreferredPickupBranchName,
    string Title,
    string? Author,
    string? Identifier,
    string? Publication,
    DateOnly? ExactPublicationDate,
    object CustomFields,
    bool Autohold,
    string Format,
    string FormatLabel,
    string Status,
    string? CloseReason,
    int? Bibid,
    bool BibidStaffVerified,
    string? Notes,
    string? ClaimedByStaffUserId,
    string? ClaimedByDisplayName,
    DateTime? ClaimedAt,
    string? ClaimType,
    string? ClaimRuleId,
    string? IsbnCheckStatus,
    string? IsbnCheckResult,
    int IsbnCheckRetryCount,
    string? IsbnCheckLastErrorCode,
    DateTime? LastChecked,
    IReadOnlyList<string> WorkflowTags,
    DateTime PhaseEnteredAt,
    DateTime Created,
    DateTime Updated,
    string Version,
    TitleRequestCapabilities Capabilities,
    HoldOperationSummary? HoldOperation,
    IReadOnlyList<TitleRequestActivity> Activity,
    bool? Committed = null,
    string? FinalStatus = null,
    string? NotificationStatus = null,
    string? NotificationReason = null,
    string? PatronNotificationStatus = null,
    string? PatronNotificationReason = null)
{
    public RelatedRequestSummary? RelatedRequests { get; init; }
    public RequestWorkflowContext? WorkflowContext { get; init; }
}

public sealed record TitleRequestScopeResult(
    IReadOnlyList<TitleRequestDto> Items,
    string Scope,
    IReadOnlyList<object> Organizations);

public static class TitleRequestCapabilityPolicy
{
    private static readonly HashSet<string> PrePlacementStatuses =
        ["suggestion", "outstanding_purchase", "pending_hold"];

    public static TitleRequestCapabilities Evaluate(
        TitleRequest request,
        bool hasIncompleteOperation,
        bool hasPlacedProtection)
    {
        if (hasIncompleteOperation)
        {
            return new TitleRequestCapabilities(
                false,
                false,
                false,
                false,
                "hold_operation_incomplete");
        }

        if (request.Status is "hold_placed" or "closed" || hasPlacedProtection)
        {
            return new TitleRequestCapabilities(
                false,
                false,
                false,
                request.Status != "closed" || !hasPlacedProtection,
                request.Status == "closed" && hasPlacedProtection
                    ? "hold_history_retained"
                    : "identifier_locked_by_stage");
        }

        var canEdit = PrePlacementStatuses.Contains(request.Status);
        return new TitleRequestCapabilities(
            canEdit,
            canEdit,
            request.Status == "suggestion" &&
            !string.IsNullOrWhiteSpace(request.Identifier) &&
            request.IsbnCheckStatus == "error_max_retries",
            true,
            canEdit ? null : "identifier_locked_by_stage");
    }
}

public sealed class TitleRequestViewService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public async Task<TitleRequestScopeResult?> ListAsync(
        CurrentStaff staff,
        string? scope,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => item.Id != 1 && item.IsActive)
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        int? organizationId;
        string normalizedScope;
        if (staff.Role == "super_admin")
        {
            if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            {
                organizationId = null;
                normalizedScope = "all";
            }
            else if (int.TryParse(scope, out var selectedId) && organizations.Any(item => item.Id == selectedId))
            {
                organizationId = selectedId;
                normalizedScope = selectedId.ToString();
            }
            else
            {
                return null;
            }
        }
        else
        {
            organizationId = staff.OrganizationId;
            normalizedScope = staff.OrganizationId.ToString();
        }

        var requestQuery = context.TitleRequests.AsNoTracking()
            .Where(item => organizationId == null || item.LibraryOrganizationId == organizationId);
        var requests = await requestQuery.ToListAsync(cancellationToken);
        var items = await BuildDtosAsync(context, requests, requests, staff, includeActivity: false, cancellationToken);
        return new TitleRequestScopeResult(
            items,
            normalizedScope,
            organizations.Select(item => (object)new { id = item.Id, name = item.DisplayName }).ToList());
    }

    public async Task<TitleRequestDto?> GetAsync(
        CurrentStaff staff,
        string id,
        CancellationToken cancellationToken,
        string? scope = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var requestId = await LegacyRequestLinkResolver.ResolveAsync(
            context,
            context.TitleRequests.Select(item => item.Id),
            LegacyRequestLinkResolver.TitleRequestEntityType,
            id,
            cancellationToken);
        if (!requestId.HasValue)
        {
            return null;
        }

        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId.Value, cancellationToken);
        if (request is null || !CanAccess(staff, request.LibraryOrganizationId))
        {
            return null;
        }
        int? relatedOrganizationId = staff.Role == "super_admin" ? null : staff.OrganizationId;
        if (staff.Role == "super_admin" && !string.IsNullOrWhiteSpace(scope) &&
            !string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(scope, out var selectedOrganizationId) ||
                selectedOrganizationId != request.LibraryOrganizationId ||
                !await context.Organizations.AsNoTracking().AnyAsync(item =>
                    item.Id == selectedOrganizationId && item.IsActive, cancellationToken))
            {
                return null;
            }
            relatedOrganizationId = selectedOrganizationId;
        }
        var relatedCandidates = await context.TitleRequests.AsNoTracking()
            .Where(item => relatedOrganizationId == null || item.LibraryOrganizationId == relatedOrganizationId)
            .ToListAsync(cancellationToken);
        return (await BuildDtosAsync(context, [request], relatedCandidates, staff, includeActivity: true, cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<RejectionTemplateChoice>?> GetRejectionTemplatesAsync(
        CurrentStaff staff,
        string id,
        CancellationToken cancellationToken)
    {
        var request = await GetAsync(staff, id, cancellationToken);
        if (request is null)
        {
            return null;
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == 1 || item.OrganizationId == request.LibraryOrgId)
            .ToListAsync(cancellationToken);
        return RejectionTemplatePolicy.Choices(rows, request.LibraryOrgId);
    }

    internal static bool CanAccess(CurrentStaff staff, int organizationId) =>
        staff.Role == "super_admin" || staff.OrganizationId == organizationId;

    internal static bool HasLegacyPlacedProtection(IEnumerable<TitleRequestEvent> events)
    {
        foreach (var item in events)
        {
            if (string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(item.MetadataJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("legacyBibProtection", out var marker) &&
                    marker.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Database constraints keep new metadata valid; malformed imported evidence is not authority.
            }
        }
        return false;
    }

    private static async Task<IReadOnlyList<TitleRequestDto>> BuildDtosAsync(
        AsapDbContext context,
        IReadOnlyList<TitleRequest> requests,
        IReadOnlyList<TitleRequest> relatedCandidates,
        CurrentStaff staff,
        bool includeActivity,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }
        var ids = requests.Select(item => item.Id).ToArray();
        var visibleOrganizationIds = relatedCandidates.Select(item => item.LibraryOrganizationId).Distinct().ToArray();
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => visibleOrganizationIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var workflowRows = await context.WorkflowSettings.AsNoTracking()
            .Where(item => item.OrganizationId == 1 || visibleOrganizationIds.Contains(item.OrganizationId))
            .ToDictionaryAsync(item => item.OrganizationId, cancellationToken);
        workflowRows.TryGetValue(1, out var systemWorkflow);
        var relatedByKey = new Dictionary<string, List<TitleRequest>>(StringComparer.Ordinal);
        foreach (var candidate in relatedCandidates)
        {
            foreach (var key in SimilarityKeys(candidate))
            {
                if (!relatedByKey.TryGetValue(key, out var matching))
                {
                    matching = [];
                    relatedByKey[key] = matching;
                }
                matching.Add(candidate);
            }
        }
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => requests.Select(request => request.MaterialFormatId).Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var events = await context.TitleRequestEvents.AsNoTracking()
            .Where(item => ids.Contains(item.TitleRequestId))
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var eventGroups = events.GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => item.ToList());
        var tagRows = await (
                from link in context.TitleRequestWorkflowTags.AsNoTracking()
                join tag in context.WorkflowTags.AsNoTracking() on link.WorkflowTagId equals tag.Id
                where ids.Contains(link.TitleRequestId)
                orderby tag.SortOrder, tag.Id
                select new { link.TitleRequestId, tag.Label })
            .ToListAsync(cancellationToken);
        var tags = tagRows.GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Select(value => value.Label).ToList());
        var operations = await context.HoldPlacementOperations.AsNoTracking()
            .Where(item => ids.Contains(item.TitleRequestId))
            .OrderByDescending(item => item.AttemptNumber)
            .ToListAsync(cancellationToken);
        var incomplete = operations.Where(item => item.CompletedUtc == null)
            .GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => item.First());
        var latestSuccessful = operations.Where(item => item.State == "succeeded")
            .GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => item.First());

        var result = new List<TitleRequestDto>(requests.Count);
        foreach (var request in requests)
        {
            var requestEvents = eventGroups.GetValueOrDefault(request.Id) ?? [];
            var incompleteOperation = incomplete.GetValueOrDefault(request.Id);
            var successfulOperation = latestSuccessful.GetValueOrDefault(request.Id);
            var operation = incompleteOperation ??
                (successfulOperation is { PolarisHoldId: null } ? successfulOperation : null);
            var protectedHistory = successfulOperation is not null || HasLegacyPlacedProtection(requestEvents);
            var capabilities = TitleRequestCapabilityPolicy.Evaluate(request, incompleteOperation is not null, protectedHistory);
            var canTakeOverOperation = incompleteOperation is not null &&
                                       (!incompleteOperation.OwnerToken.HasValue ||
                                        incompleteOperation.LeaseExpiresUtc <= DateTime.UtcNow);
            var canResolveOperation = canTakeOverOperation && operation!.State == "operator_required";
            var phaseEnteredAt = requestEvents
                .Where(item => item.EventType == "status_changed" && item.Status == request.Status)
                .Select(item => (DateTime?)item.CreatedUtc)
                .LastOrDefault() ?? request.CreatedUtc;
            formats.TryGetValue(request.MaterialFormatId, out var format);
            workflowRows.TryGetValue(request.LibraryOrganizationId, out var libraryWorkflow);
            var related = SimilarityKeys(request)
                .SelectMany(key => relatedByKey.GetValueOrDefault(key) ?? [])
                .Where(candidate => candidate.Id != request.Id)
                .DistinctBy(candidate => candidate.Id)
                .ToList();
            var relatedCounts = related
                .GroupBy(item => new { item.Status, item.LibraryOrganizationId })
                .Select(group => new RelatedRequestCount(
                    group.Key.Status,
                    group.Key.LibraryOrganizationId,
                    organizations.GetValueOrDefault(group.Key.LibraryOrganizationId)?.DisplayName ?? string.Empty,
                    group.Count()))
                .OrderBy(item => item.LibraryOrgName)
                .ThenBy(item => item.Status)
                .ToArray();
            result.Add(new TitleRequestDto(
                request.Id.ToString(CultureInfo.InvariantCulture),
                "title_request",
                request.LegacyId,
                request.LibraryOrganizationId,
                organizations.GetValueOrDefault(request.LibraryOrganizationId)?.DisplayName ?? request.LibraryNameSnapshot ?? string.Empty,
                request.PatronOrganizationId,
                request.Barcode,
                request.Email,
                request.NameFirst,
                request.NameLast,
                request.PatronCodeId,
                request.PatronCodeDescription,
                request.PreferredPickupBranchId,
                request.PreferredPickupBranchName,
                request.Title,
                request.Author,
                request.Identifier,
                request.Publication,
                request.ExactPublicationDate,
                ParseCustomFields(request.CustomFieldsJson),
                request.AutoHold,
                format?.Code ?? request.MaterialFormatId.ToString(CultureInfo.InvariantCulture),
                format?.Label ?? string.Empty,
                request.Status,
                request.CloseReason,
                request.BibId,
                request.BibIdStaffVerified,
                request.Notes,
                request.ClaimedByStaffUserId?.ToString(CultureInfo.InvariantCulture),
                request.ClaimedByDisplayName,
                AsUtc(request.ClaimedAtUtc),
                request.ClaimType,
                request.ClaimRuleId?.ToString(CultureInfo.InvariantCulture),
                request.IsbnCheckStatus,
                request.IsbnCheckResult,
                request.IsbnCheckRetryCount,
                request.IsbnCheckLastErrorCode,
                AsUtc(request.LastCheckedUtc),
                tags.GetValueOrDefault(request.Id) ?? [],
                AsUtc(phaseEnteredAt),
                AsUtc(request.CreatedUtc),
                AsUtc(request.UpdatedUtc),
                StaffVersion.Encode(request.RowVersion),
                capabilities,
                operation is null ? null : new HoldOperationSummary(
                    operation.Id.ToString(CultureInfo.InvariantCulture),
                    operation.State,
                    operation.Phase,
                    operation.AttemptNumber,
                    operation.ExecutionEpoch,
                    MaskBarcode(operation.PatronBarcodeSnapshot),
                    operation.BibIdSnapshot,
                    StaffVersion.Encode(operation.RowVersion),
                    AsUtc(operation.LastRecoveryUtc),
                    operation.LastErrorCode,
                    staff.Role == "super_admin" && canTakeOverOperation,
                    staff.Role == "super_admin" && canResolveOperation && operation.Phase != "acquired",
                    staff.Role == "super_admin" && canResolveOperation),
                includeActivity ? requestEvents.Select(item => new TitleRequestActivity(
                    item.Id.ToString(CultureInfo.InvariantCulture),
                    item.EventType,
                    item.ActorType,
                    item.ActorName,
                    item.Message,
                    AsUtc(item.CreatedUtc))).ToArray() : [])
            {
                RelatedRequests = new RelatedRequestSummary(related.Count, relatedCounts),
                WorkflowContext = new RequestWorkflowContext(
                    libraryWorkflow?.AutoPromote ?? systemWorkflow?.AutoPromote == true,
                    libraryWorkflow?.OutstandingTimeoutEnabled ?? systemWorkflow?.OutstandingTimeoutEnabled == true,
                    libraryWorkflow?.OutstandingTimeoutDays ?? systemWorkflow?.OutstandingTimeoutDays,
                    libraryWorkflow?.PendingHoldTimeoutEnabled ?? systemWorkflow?.PendingHoldTimeoutEnabled == true,
                    libraryWorkflow?.PendingHoldTimeoutDays ?? systemWorkflow?.PendingHoldTimeoutDays,
                    libraryWorkflow?.HoldPickupTimeoutEnabled ?? systemWorkflow?.HoldPickupTimeoutEnabled == true,
                    libraryWorkflow?.HoldPickupTimeoutDays ?? systemWorkflow?.HoldPickupTimeoutDays)
            });
        }

        return result
            .OrderByDescending(item => item.PhaseEnteredAt)
            .ThenByDescending(item => item.Updated)
            .ThenByDescending(item => item.Created)
            .ThenByDescending(item => long.Parse(item.Id))
            .ToList();
    }

    private static IEnumerable<string> SimilarityKeys(TitleRequest request)
    {
        var identifier = NormalizeSimilarityValue(request.Identifier);
        if (identifier.Length > 0)
        {
            yield return $"identifier:{identifier}";
        }
        if (request.BibId.HasValue)
        {
            yield return $"bib:{request.BibId.Value}";
        }
        var title = NormalizeSimilarityValue(request.Title);
        if (title.Length > 0)
        {
            yield return $"title:{title}";
        }
    }

    private static string NormalizeSimilarityValue(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static object ParseCustomFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, object?>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(value) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static string MaskBarcode(string value) =>
        value.Length <= 4 ? new string('*', value.Length) : $"{new string('*', value.Length - 4)}{value[^4..]}";

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;
}
