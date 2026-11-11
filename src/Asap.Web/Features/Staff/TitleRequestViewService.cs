using System.Globalization;
using System.Data;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record TitleRequestCapabilities(
    bool CanEditIdentifier,
    bool CanChangeBib,
    bool CanRetryIdentifierCheck,
    bool CanChangeWorkflowState,
    string? BlockingReason)
{
    public IReadOnlyList<string> AllowedActions { get; init; } = [];
    public bool CanPlaceHold { get; init; }
}

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

public sealed record PickupOperationSummary(Guid Id, int State, int TargetBranchId, string TargetBranchName);

internal sealed record PickupOperationRead(long RequestId, Guid Id, int State, int TargetBranchId, string TargetBranchName);

public sealed record TitleRequestActivity(
    string Id,
    string EventType,
    string ActorType,
    string? ActorName,
    string? Message,
    DateTime Created)
{
    public string Source { get; init; } = "request_event";
    public RequestEmailActivity? Email { get; init; }
}

public sealed record RequestEmailActivity(
    string OperationReference, string? MessageKind, string MessageType, string Outcome,
    string OutcomeLabel, string? Reason, string? IntendedRecipient, string DeliveryMode,
    bool IsRecordedSummary, string TimestampKind, string? OperationsScope);

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
    IReadOnlyDictionary<string, JsonElement>? CustomFields,
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
    public bool CustomFieldsValid { get; init; } = true;
    public RelatedRequestSummary? RelatedRequests { get; init; }
    public RequestWorkflowContext? WorkflowContext { get; init; }
    public PickupOperationSummary? PickupOperation { get; init; }
    public StaffResearchContext? Research { get; init; }
    public Guid? OperationId { get; init; }
    public string? AdditionalCopyRequestId { get; init; }
    public bool Replayed { get; init; }
    public string? QueueCommittedStatus { get; init; }
}

public sealed record TitleRequestScopeResult(
    IReadOnlyList<TitleRequestDto> Items,
    string Scope,
    IReadOnlyList<OrganizationChoice> Organizations);


public sealed partial class TitleRequestViewService(IDbContextFactory<AsapDbContext> contextFactory, TimeProvider timeProvider,
    StaffEligibilityService staffEligibility)
{
    public async Task<TitleRequestScopeResult?> ListAsync(
        CurrentStaff staff,
        LibraryScope scope,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => item.Id > LibraryScope.SystemOrganizationId &&
                           item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId && item.IsActive)
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .Select(item => new OrganizationChoice(item.Id, item.DisplayName))
            .ToListAsync(cancellationToken);

        var resolvedScope = staff.Role == StaffRole.SuperAdmin ? scope : LibraryScope.ForLibrary(staff.OrganizationId);
        if (resolvedScope.Kind == LibraryScopeKind.System ||
            resolvedScope.Kind == LibraryScopeKind.Library && !organizations.Any(item => item.Id == resolvedScope.OrganizationId))
        {
            return null;
        }
        var organizationId = resolvedScope.OrganizationId;
        var normalizedScope = resolvedScope.ToTransportValue();

        var requestQuery = context.TitleRequests.AsNoTracking()
            .Where(item => organizationId == null || item.LibraryOrganizationId == organizationId);
        var rows = await requestQuery.Select(item => new { Request = item, PhaseEnteredAt = context.TitleRequestEvents
                .Where(entry => entry.TitleRequestId == item.Id && entry.Status == item.Status && PhaseTransitionEvents.Contains(entry.EventType))
                .OrderByDescending(entry => entry.CreatedUtc).ThenByDescending(entry => entry.Id)
                .Select(entry => (DateTime?)entry.CreatedUtc).FirstOrDefault() ?? item.CreatedUtc })
            .ToListAsync(cancellationToken);
        var requests = rows.Select(item => item.Request).ToArray();
        var items = await BuildDtosAsync(context, requests,
            requests.Select(item => new RelatedRequestRead(item.Id, item.LibraryOrganizationId, item.Status,
                item.Identifier, item.BibId, item.Title)).ToArray(), staff, includeActivity: false, cancellationToken,
            rows.ToDictionary(item => item.Request.Id, item => item.PhaseEnteredAt));
        return new TitleRequestScopeResult(
            items,
            normalizedScope,
            organizations);
    }

    public async Task<TitleRequestDto?> GetAsync(
        CurrentStaff staff,
        string id,
        CancellationToken cancellationToken,
        LibraryScope? scope = null,
        bool searchResult = false)
    {
        try
        {
            return await ReadRequestDetailAsync(staff, id, cancellationToken, scope, searchResult);
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested && exception.Errors.Count > 0 &&
            exception.Errors.Cast<SqlError>().All(error => error.Number == 0 && error.Class == 11 && error.State == 0))
        {
            throw new OperationCanceledException("Request activity read was cancelled.", exception, cancellationToken);
        }
    }

    private async Task<TitleRequestDto?> ReadRequestDetailAsync(
        CurrentStaff staff, string id, CancellationToken cancellationToken, LibraryScope? scope, bool searchResult)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var requestScope = await ResolveRequestReadScopeAsync(context, staff, id, cancellationToken);
        if (requestScope is null)
        {
            return null;
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var current = await LockAndRevalidateRequestReaderAsync(context, staff, requestScope, cancellationToken);
        if (current is null)
        {
            return null;
        }
        staff = current;
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestScope.Id && item.LibraryOrganizationId == requestScope.LibraryOrganizationId, cancellationToken);
        if (request is null)
        {
            return null;
        }
        var access = await ResolveRelatedReadScopeAsync(context, staff, request.LibraryOrganizationId, scope, searchResult, cancellationToken);
        if (!access.Allowed)
        {
            return null;
        }
        var relatedCandidates = await context.TitleRequests.AsNoTracking()
            .Where(item => access.OrganizationId == null || item.LibraryOrganizationId == access.OrganizationId)
            .Select(item => new RelatedRequestRead(item.Id, item.LibraryOrganizationId, item.Status, item.Identifier, item.BibId, item.Title))
            .ToListAsync(cancellationToken);
        var result = (await BuildDtosAsync(context, [request], relatedCandidates, staff, includeActivity: true, cancellationToken)).Single();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<RejectionTemplateChoice>?> GetRejectionTemplatesAsync(
        CurrentStaff staff,
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadRejectionTemplatesAsync(staff, id, cancellationToken);
        }
        catch (SqlException exception) when (cancellationToken.IsCancellationRequested && exception.Errors.Count > 0 &&
            exception.Errors.Cast<SqlError>().All(error => error.Number == 0 && error.Class == 11 && error.State == 0))
        {
            throw new OperationCanceledException("Request template read was cancelled.", exception, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<RejectionTemplateChoice>?> ReadRejectionTemplatesAsync(
        CurrentStaff staff, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var requestScope = await ResolveRequestReadScopeAsync(context, staff, id, cancellationToken);
        if (requestScope is null)
        {
            return null;
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var current = await LockAndRevalidateRequestReaderAsync(context, staff, requestScope, cancellationToken);
        if (current is null || !await context.TitleRequests.AsNoTracking().AnyAsync(item =>
                item.Id == requestScope.Id && item.LibraryOrganizationId == requestScope.LibraryOrganizationId, cancellationToken) ||
            !(await ResolveRelatedReadScopeAsync(context, current, requestScope.LibraryOrganizationId,
                null, searchResult: false, cancellationToken)).Allowed)
        {
            return null;
        }
        // Choices consumes identity, inheritance, visibility, ordering, names and nonblank
        // subject/body validity. RowVersion is needed only by template mutations.
        var rows = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == requestScope.LibraryOrganizationId)
            .Select(item => new EmailTemplate { Id = item.Id, OrganizationId = item.OrganizationId,
                TemplateKey = item.TemplateKey, SourceTemplateId = item.SourceTemplateId, DisplayName = item.DisplayName,
                SubjectTemplate = item.SubjectTemplate, BodyTemplate = item.BodyTemplate, IsHidden = item.IsHidden,
                IsCustom = item.IsCustom, SortOrder = item.SortOrder })
            .ToListAsync(cancellationToken);
        var result = RejectionTemplatePolicy.Choices(rows, requestScope.LibraryOrganizationId);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static bool CanAccess(CurrentStaff staff, int organizationId) =>
        StaffEligibilityService.CanAccess(staff, organizationId);

    private async Task<IReadOnlyList<TitleRequestDto>> BuildDtosAsync(
        AsapDbContext context,
        IReadOnlyList<TitleRequest> requests,
        IReadOnlyList<RelatedRequestRead> relatedCandidates,
        CurrentStaff staff,
        bool includeActivity,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<long, DateTime>? phaseEntries = null)
    {
        if (requests.Count == 0)
        {
            return [];
        }
        var ids = requests.Select(item => item.Id).ToArray();
        var visibleOrganizationIds = relatedCandidates.Select(item => item.LibraryOrganizationId).Distinct().ToArray();
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => visibleOrganizationIds.Contains(item.Id))
            .Select(item => new OrganizationChoice(item.Id, item.DisplayName))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var workflowRows = await context.WorkflowSettings.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId ||
                requests.Select(request => request.LibraryOrganizationId).Contains(item.OrganizationId))
            .Select(item => new { item.OrganizationId, item.AutoPromote, item.OutstandingTimeoutEnabled,
                item.OutstandingTimeoutDays, item.PendingHoldTimeoutEnabled, item.PendingHoldTimeoutDays,
                item.HoldPickupTimeoutEnabled, item.HoldPickupTimeoutDays })
            .ToDictionaryAsync(item => item.OrganizationId, cancellationToken);
        workflowRows.TryGetValue(1, out var systemWorkflow);
        var research = await context.SystemSettings.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .Select(item => new StaffResearchContext(item.LeapBibUrlPattern, item.LeapPatronUrlPattern, null))
            .SingleAsync(cancellationToken);
        var relatedByKey = new Dictionary<string, List<RelatedRequestRead>>(StringComparer.Ordinal);
        foreach (var candidate in relatedCandidates)
        {
            foreach (var key in SimilarityKeys(candidate.Identifier, candidate.BibId, candidate.Title))
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
            .Select(item => new { item.Id, item.Code, item.Label })
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var events = includeActivity ? await context.TitleRequestEvents.AsNoTracking()
            .Where(item => ids.Contains(item.TitleRequestId))
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Select(item => new RequestEventRead(item.Id, item.TitleRequestId, item.EventType, item.Status,
                item.ActorType, item.ActorName, item.Message, item.CreatedUtc))
            .ToListAsync(cancellationToken) : [];
        var eventGroups = events.GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => item.ToList());
        var sourceLibraries = requests.ToDictionary(item => item.Id, item => item.LibraryOrganizationId);
        var linkedCopies = await context.AdditionalCopyRequests.AsNoTracking()
            .Where(item => item.SourceTitleRequestId.HasValue && ids.Contains(item.SourceTitleRequestId.Value))
            .OrderByDescending(item => item.Id)
            .Select(item => new { SourceId = item.SourceTitleRequestId!.Value, item.LibraryOrganizationId, item.Id })
            .ToListAsync(cancellationToken);
        var additionalCopyIds = linkedCopies
            .Where(item => sourceLibraries.TryGetValue(item.SourceId, out var organizationId) &&
                organizationId == item.LibraryOrganizationId)
            .GroupBy(item => item.SourceId)
            .ToDictionary(item => item.Key, item => item.First().Id);
        var emailActivity = includeActivity
            ? await LoadEmailActivityAsync(context, requests.Single(), staff, cancellationToken)
            : [];
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
            .Where(item => ids.Contains(item.TitleRequestId) &&
                (item.CompletedUtc == null && !context.HoldPlacementOperations.Any(newer => newer.TitleRequestId == item.TitleRequestId &&
                    newer.CompletedUtc == null && newer.AttemptNumber > item.AttemptNumber) ||
                 item.State == HoldOperationState.Succeeded && !context.HoldPlacementOperations.Any(newer => newer.TitleRequestId == item.TitleRequestId &&
                    newer.State == HoldOperationState.Succeeded && newer.AttemptNumber > item.AttemptNumber)))
            .OrderByDescending(item => item.AttemptNumber)
            .Select(item => new HoldOperationRead(item.Id, item.TitleRequestId, item.State, item.Phase, item.AttemptNumber,
                item.ExecutionEpoch, item.PatronBarcodeSnapshot, item.BibIdSnapshot, item.RowVersion,
                item.LastRecoveryUtc, item.LastErrorCode, item.OwnerToken.HasValue, item.LeaseExpiresUtc, item.CompletedUtc, item.PolarisHoldId))
            .ToListAsync(cancellationToken);
        var incomplete = operations.Where(item => item.CompletedUtc == null)
            .GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => item.First());
        var latestSuccessful = operations.Where(item => item.State == HoldOperationState.Succeeded)
            .GroupBy(item => item.TitleRequestId)
            .ToDictionary(item => item.Key, item => item.First());
        var pendingPickups = await context.Database.SqlQuery<PickupOperationRead>($"""
            SELECT [TitleRequestId] AS [RequestId], [Id], [State],
                   [ToPickupBranchId] AS [TargetBranchId], [ToPickupBranchName] AS [TargetBranchName]
            FROM [asap].[PickupPreferenceOperation] WHERE [CompletedUtc] IS NULL AND [TitleRequestId] IS NOT NULL
            """).Where(item => ids.Contains(item.RequestId)).ToDictionaryAsync(item => item.RequestId, cancellationToken);

        var result = new List<TitleRequestDto>(requests.Count);
        foreach (var request in requests)
        {
            var requestEvents = eventGroups.GetValueOrDefault(request.Id) ?? [];
            var incompleteOperation = incomplete.GetValueOrDefault(request.Id);
            var successfulOperation = latestSuccessful.GetValueOrDefault(request.Id);
            var operation = incompleteOperation ??
                (successfulOperation is { PolarisHoldId: null } ? successfulOperation : null);
            var protectedHistory = successfulOperation is not null;
            var pendingPickup = pendingPickups.GetValueOrDefault(request.Id);
            var capabilities = TitleRequestWorkflowPolicy.Evaluate(request, incompleteOperation is not null,
                protectedHistory, pendingPickup is not null);
            var canTakeOverOperation = incompleteOperation is not null &&
                                       (!incompleteOperation.HasOwner ||
                                        incompleteOperation.LeaseExpiresUtc <= timeProvider.GetUtcNow().UtcDateTime);
            var canResolveOperation = canTakeOverOperation && operation!.State == HoldOperationState.OperatorRequired;
            var phaseEnteredAt = phaseEntries?.GetValueOrDefault(request.Id) ?? requestEvents
                .Where(item => item.Status == request.Status && PhaseTransitionEvents.Contains(item.EventType))
                .Select(item => (DateTime?)item.CreatedUtc)
                .LastOrDefault() ?? request.CreatedUtc;
            formats.TryGetValue(request.MaterialFormatId, out var format);
            workflowRows.TryGetValue(request.LibraryOrganizationId, out var libraryWorkflow);
            var related = SimilarityKeys(request.Identifier, request.BibId, request.Title)
                .SelectMany(key => relatedByKey.GetValueOrDefault(key) ?? [])
                .Where(candidate => candidate.Id != request.Id)
                .DistinctBy(candidate => candidate.Id)
                .ToList();
            var relatedCounts = related
                .GroupBy(item => new { item.Status, item.LibraryOrganizationId })
                .Select(group => new RelatedRequestCount(
                    group.Key.Status,
                    group.Key.LibraryOrganizationId,
                    organizations.GetValueOrDefault(group.Key.LibraryOrganizationId)?.Name ?? string.Empty,
                    group.Count()))
                .OrderBy(item => item.LibraryOrgName)
                .ThenBy(item => item.Status)
                .ToArray();
            IReadOnlyDictionary<string, JsonElement>? customFields;
            var customFieldsValid = true;
            try
            {
                customFields = ParseCustomFields(request.CustomFieldsJson);
            }
            catch (JsonException)
            {
                customFields = null;
                customFieldsValid = false;
            }
            result.Add(new TitleRequestDto(
                request.Id.ToString(CultureInfo.InvariantCulture),
                "title_request",
                request.LibraryOrganizationId,
                organizations.GetValueOrDefault(request.LibraryOrganizationId)?.Name ?? request.LibraryNameSnapshot ?? string.Empty,
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
                customFields,
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
                    staff.Role == StaffRole.SuperAdmin && canTakeOverOperation,
                    staff.Role == StaffRole.SuperAdmin && canResolveOperation && operation.Phase != HoldOperationPhase.Acquired,
                    staff.Role == StaffRole.SuperAdmin && canResolveOperation),
                includeActivity ? requestEvents.Select(item => new TitleRequestActivity(
                    item.Id.ToString(CultureInfo.InvariantCulture),
                    item.EventType,
                    item.ActorType,
                    item.ActorName,
                    item.Message,
                    AsUtc(item.CreatedUtc))).Concat(emailActivity)
                    // Preserve chronological API order; display uses the inverse source/ID ties.
                    .OrderBy(item => item.Created).ThenByDescending(item => item.Source, StringComparer.Ordinal)
                    .ThenBy(item => long.Parse(item.Id, CultureInfo.InvariantCulture)).ToArray() : [])
            {
                CustomFieldsValid = customFieldsValid,
                Research = research with { PatronId = request.PatronIdSnapshot is > 0 ? request.PatronIdSnapshot : null },
                AdditionalCopyRequestId = additionalCopyIds.TryGetValue(request.Id, out var additionalCopyId)
                    ? additionalCopyId.ToString(CultureInfo.InvariantCulture) : null,
                PickupOperation = pendingPickup is null ? null : new PickupOperationSummary(pendingPickup.Id,
                    pendingPickup.State, pendingPickup.TargetBranchId, pendingPickup.TargetBranchName),
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

    private static readonly string[] PhaseTransitionEvents = ["status_changed", "promoted", "hold_placed", "fulfilled", "timeout_closed"];

    private sealed record RelatedRequestRead(long Id, int LibraryOrganizationId, string Status, string? Identifier, int? BibId, string Title);
    private sealed record RequestEventRead(long Id, long TitleRequestId, string EventType, string? Status,
        string ActorType, string? ActorName, string? Message, DateTime CreatedUtc);
    private sealed record HoldOperationRead(long Id, long TitleRequestId, string State, string Phase, int AttemptNumber,
        long ExecutionEpoch, string PatronBarcodeSnapshot, int BibIdSnapshot, byte[] RowVersion, DateTime? LastRecoveryUtc,
        string? LastErrorCode, bool HasOwner, DateTime? LeaseExpiresUtc, DateTime? CompletedUtc, int? PolarisHoldId);

    private static IEnumerable<string> SimilarityKeys(string? identifierValue, int? bibId, string titleValue)
    {
        var identifier = NormalizeSimilarityValue(identifierValue);
        if (identifier.Length > 0)
        {
            yield return $"identifier:{identifier}";
        }
        if (bibId.HasValue)
        {
            yield return $"bib:{bibId.Value}";
        }
        var title = NormalizeSimilarityValue(titleValue);
        if (title.Length > 0)
        {
            yield return $"title:{title}";
        }
    }

    private static string NormalizeSimilarityValue(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyDictionary<string, JsonElement> ParseCustomFields(string? value)
        => TitleRequestCustomFieldsSnapshot.Parse(value);

    private static string MaskBarcode(string value) =>
        value.Length <= 4 ? new string('*', value.Length) : $"{new string('*', value.Length - 4)}{value[^4..]}";

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;
}
