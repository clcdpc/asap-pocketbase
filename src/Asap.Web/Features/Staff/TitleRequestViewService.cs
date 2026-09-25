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
    string BibIdSnapshot,
    string Version,
    DateTime? LastRecoveryUtc,
    string? LastErrorCode,
    bool CanReconcile,
    bool CanResolveSucceeded,
    bool CanResolveNotPerformed);

public sealed record TitleRequestActivity(string EventType, string Message, string? ActorName, DateTime Created);

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
    string? PatronCodeId,
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
    string? Bibid,
    string? Notes,
    IReadOnlyList<TitleRequestActivity> Activity,
    string? ClaimedByStaffUserId,
    string? ClaimedByDisplayName,
    DateTime? ClaimedAt,
    string? ClaimType,
    string? ClaimRuleId,
    string? IsbnCheckStatus,
    string? IsbnCheckResult,
    string? IdentifierPresentation,
    int IsbnCheckRetryCount,
    string? IsbnCheckLastErrorCode,
    DateTime? LastChecked,
    IReadOnlyList<string> WorkflowTags,
    DateTime PhaseEnteredAt,
    DateTime Created,
    DateTime Updated,
    string Version,
    TitleRequestCapabilities Capabilities,
    HoldOperationSummary? HoldOperation);

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
                true,
                "identifier_locked_by_stage");
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
        var items = await BuildDtosAsync(context, requests, staff, cancellationToken);
        return new TitleRequestScopeResult(
            items,
            normalizedScope,
            organizations.Select(item => (object)new { id = item.Id, name = item.DisplayName }).ToList());
    }

    public async Task<TitleRequestDto?> GetAsync(
        CurrentStaff staff,
        string id,
        CancellationToken cancellationToken)
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
        return (await BuildDtosAsync(context, [request], staff, cancellationToken)).Single();
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
        CurrentStaff staff,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }
        var ids = requests.Select(item => item.Id).ToArray();
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => requests.Select(request => request.LibraryOrganizationId).Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
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
                request.Notes,
                requestEvents.Select(item => new TitleRequestActivity(
                    item.EventType, item.Message ?? string.Empty, item.ActorName, AsUtc(item.CreatedUtc))).ToList(),
                request.ClaimedByStaffUserId?.ToString(CultureInfo.InvariantCulture),
                request.ClaimedByDisplayName,
                AsUtc(request.ClaimedAtUtc),
                request.ClaimType,
                request.ClaimRuleId?.ToString(CultureInfo.InvariantCulture),
                request.IsbnCheckStatus,
                request.IsbnCheckResult,
                request.IsbnCheckStatus == "not_found" ? request.IsbnCheckResult switch
                {
                    TitleRequestMutationService.InterruptedIdentifierResult => "not_completed",
                    TitleRequestMutationService.UnverifiedSelectedBibIdentifierResult => "not_verified_on_bib",
                    _ => null
                } : null,
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
                    staff.Role == "super_admin" && canResolveOperation)));
        }

        return result
            .OrderByDescending(item => item.PhaseEnteredAt)
            .ThenByDescending(item => item.Updated)
            .ThenByDescending(item => item.Created)
            .ThenByDescending(item => long.Parse(item.Id))
            .ToList();
    }

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
