using System.Data;
using System.Text.Json.Serialization;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Staff;

public sealed record AdditionalCopyCapabilities(
    bool CanClaim,
    bool CanUnclaim,
    bool CanAssign,
    bool CanClose,
    bool CanReopen,
    bool CanDelete,
    bool CanClearClaim);

public sealed record AdditionalCopyTimeoutContext(bool Enabled, int? Days);

public sealed record AdditionalCopyDto(
    string Id,
    string Type,
    string? LegacyId,
    string? SourceTitleRequest,
    string? SourceStatus,
    int LibraryOrgId,
    string LibraryOrgName,
    int Bibid,
    string Title,
    string? Author,
    string? Format,
    string? FormatLabel,
    string? Identifier,
    string? Publication,
    string Status,
    string? Notes,
    string? CreatedByStaffUserId,
    string? CreatedByUsername,
    string? ClosedByStaffUserId,
    string? ClosedByUsername,
    DateTime? ClosedAt,
    DateTime Created,
    DateTime Updated,
    string? ClaimedByStaffUserId,
    string? ClaimedByDisplayName,
    DateTime? ClaimedAt,
    string? ClaimType,
    string? ClaimRuleId,
    string Version,
    string? ClaimClearedReason,
    AdditionalCopyCapabilities Capabilities,
    bool? Committed = null,
    string? FinalStatus = null,
    string? NotificationStatus = null,
    string? NotificationReason = null)
{
    public AdditionalCopyTimeoutContext? TimeoutContext { get; init; }
}

public sealed record AdditionalCopyScopeResult(
    IReadOnlyList<AdditionalCopyDto> Items,
    string Scope,
    string Status,
    IReadOnlyList<OrganizationChoice> AvailableLibraries);

public sealed record AdditionalCopyPreview(
    int Bibid,
    int OpenCount,
    bool EmailPurchaseReminderDefault,
    string Version);

public sealed record AdditionalCopyCreateInput(string? Version, bool EmailPurchaseReminder);
public sealed record AssignAdditionalCopyInput(string? Version,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? AssigneeId);

public sealed record AdditionalCopyMutationResult(
    string Code,
    long? RequestId = null,
    string? ClaimClearedReason = null,
    int? OpenCountBefore = null,
    int? OpenCountAfter = null,
    bool ReminderRequested = false,
    long? DispatchOutboxId = null,
    string? FinalStatus = null,
    string? NotificationStatus = null,
    string? NotificationReason = null);

public sealed class AdditionalCopyService(
    IDbContextFactory<AsapDbContext> contextFactory,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailOutboxDispatcher outboxDispatcher,
    TimeProvider timeProvider,
    ILogger<AdditionalCopyService> logger,
    StaffEligibilityService staffEligibility)
{

    public async Task<AdditionalCopyScopeResult?> ListAsync(
        CurrentStaff actor,
        LibraryScope scope,
        string? status,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not ("open" or RequestStatus.Closed))
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => item.Id != 1 && item.IsActive)
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var resolved = ResolveScope(actor, scope, organizations);
        if (!resolved.IsValid)
        {
            return null;
        }

        var requests = await context.AdditionalCopyRequests.AsNoTracking()
            .Where(item => item.Status == normalizedStatus &&
                           (!resolved.OrganizationId.HasValue ||
                            item.LibraryOrganizationId == resolved.OrganizationId.Value))
            .OrderByDescending(item => item.CreatedUtc)
            .ThenByDescending(item => item.Id)
            .ToListAsync(cancellationToken);
        return new AdditionalCopyScopeResult(
            await BuildDtosAsync(context, requests, actor, null, cancellationToken),
            resolved.Scope,
            normalizedStatus,
            organizations.Select(item => new OrganizationChoice(item.Id, item.DisplayName)).ToList());
    }

    public async Task<AdditionalCopyDto?> GetAsync(
        CurrentStaff actor,
        string id,
        string? claimClearedReason,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var requestId = await LegacyRequestLinkResolver.ResolveAsync(
            context,
            context.AdditionalCopyRequests.Select(item => item.Id),
            LegacyRequestLinkResolver.AdditionalCopyEntityType,
            id,
            cancellationToken);
        if (!requestId.HasValue)
        {
            return null;
        }

        var request = await context.AdditionalCopyRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId.Value, cancellationToken);
        if (request is null || !CanAccess(actor, request.LibraryOrganizationId))
        {
            return null;
        }
        return (await BuildDtosAsync(context, [request], actor, claimClearedReason, cancellationToken)).Single();
    }

    public async Task<(string Code, AdditionalCopyPreview? Preview)> PreviewAsync(
        CurrentStaff actor,
        long sourceRequestId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == sourceRequestId, cancellationToken);
        if (source is null || !CanAccess(actor, source.LibraryOrganizationId))
        {
            return ("not_found", null);
        }
        if (source.Status is not (RequestStatus.PendingHold or RequestStatus.HoldPlaced))
        {
            return ("source_stage_invalid", null);
        }
        if (source.BibId is not > 0)
        {
            return ("bib_required", null);
        }
        var count = await context.AdditionalCopyRequests.AsNoTracking().CountAsync(
            item => item.LibraryOrganizationId == source.LibraryOrganizationId &&
                    item.BibId == source.BibId && item.Status == "open",
            cancellationToken);
        return ("loaded", new AdditionalCopyPreview(
            source.BibId.Value,
            count,
            actor.AdditionalCopyReminderDefault,
            StaffVersion.Encode(source.RowVersion)));
    }

    public async Task<AdditionalCopyMutationResult> CreateAsync(
        CurrentStaff actor,
        long sourceRequestId,
        AdditionalCopyCreateInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new AdditionalCopyMutationResult("invalid_version");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await context.TitleRequests.AsNoTracking()
            .Where(item => item.Id == sourceRequestId)
            .Select(item => new RelationshipSnapshot(
                item.LibraryOrganizationId,
                item.ClaimedByStaffUserId,
                item.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null)
        {
            return new AdditionalCopyMutationResult("not_found");
        }
        if (!snapshot.Version.SequenceEqual(expectedVersion))
        {
            return new AdditionalCopyMutationResult("stale_version");
        }
        var currentActor = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (currentActor is null || !IsSameCurrentActor(actor, currentActor) ||
            !IsRelationshipEligible(currentActor, snapshot.LibraryOrganizationId))
        {
            return new AdditionalCopyMutationResult("staff_scope_forbidden");
        }

        EmailTransportReadiness readiness;
        try
        {
            readiness = input.EmailPurchaseReminder
                ? await emailSender.CheckReadinessAsync(snapshot.LibraryOrganizationId, cancellationToken)
                : EmailTransportReadiness.NotConfigured;
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new AdditionalCopyMutationResult("notification_dependency_unavailable");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockRelationshipContextAsync(
            context,
            actor,
            snapshot.LibraryOrganizationId,
            [actor.Id, snapshot.CandidateStaffUserId],
            cancellationToken);
        if (locked.Code != "locked")
        {
            return new AdditionalCopyMutationResult(locked.Code);
        }
        var source = await LockTitleRequestAsync(context, sourceRequestId, cancellationToken);
        if (source is null || source.LibraryOrganizationId != snapshot.LibraryOrganizationId)
        {
            return new AdditionalCopyMutationResult("not_found");
        }
        if (!source.RowVersion.SequenceEqual(snapshot.Version) ||
            source.ClaimedByStaffUserId != snapshot.CandidateStaffUserId ||
            !source.RowVersion.SequenceEqual(expectedVersion))
        {
            return new AdditionalCopyMutationResult("stale_version");
        }
        if (source.Status is not (RequestStatus.PendingHold or RequestStatus.HoldPlaced))
        {
            return new AdditionalCopyMutationResult("source_stage_invalid");
        }
        if (source.BibId is not > 0)
        {
            return new AdditionalCopyMutationResult("bib_required");
        }

        var now = UtcNow();
        var openCount = await LockAndCountOpenAsync(
            context,
            source.LibraryOrganizationId,
            source.BibId.Value,
            cancellationToken);
        var format = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.Id == source.MaterialFormatId)
            .Select(item => item.Code)
            .SingleOrDefaultAsync(cancellationToken);
        var request = new AdditionalCopyRequest
        {
            SourceTitleRequestId = source.Id,
            LibraryOrganizationId = source.LibraryOrganizationId,
            LibraryNameSnapshot = source.LibraryNameSnapshot,
            BibId = source.BibId.Value,
            Title = source.Title,
            Author = source.Author,
            Identifier = source.Identifier,
            Publication = source.Publication,
            MaterialFormatId = source.MaterialFormatId,
            FormatSnapshot = format,
            Status = "open",
            Notes = $"Created from request {source.Id} by {DisplayName(locked.Staff[actor.Id])}.",
            CreatedByStaffUserId = actor.Id,
            CreatedByDisplayName = DisplayName(locked.Staff[actor.Id]),
            CreatedUtc = now,
            UpdatedUtc = now
        };
        if (snapshot.CandidateStaffUserId.HasValue &&
            locked.Staff.TryGetValue(snapshot.CandidateStaffUserId.Value, out var candidate) &&
            IsRelationshipEligible(candidate, source.LibraryOrganizationId))
        {
            request.ClaimedByStaffUserId = candidate.Id;
            request.ClaimedByDisplayName = source.ClaimedByDisplayName ?? DisplayName(candidate);
            request.ClaimedAtUtc = source.ClaimedAtUtc ?? now;
            request.ClaimType = source.ClaimType;
            request.ClaimRuleId = source.ClaimRuleId;
        }
        context.AdditionalCopyRequests.Add(request);

        var openCountAfter = openCount + 1;
        var holdText = source.Status == RequestStatus.HoldPlaced ? "placed" : "queued";
        source.UpdatedUtc = now <= source.UpdatedUtc
            ? source.UpdatedUtc.AddTicks(1)
            : now;
        await context.SaveChangesAsync(cancellationToken);
        context.TitleRequestEvents.Add(new TitleRequestEvent
        {
            TitleRequestId = source.Id,
            EventType = "additional_copy_created",
            Status = source.Status,
            CloseReason = source.CloseReason,
            ActorType = StaffRole.Staff,
            StaffUserId = actor.Id,
            ActorName = DisplayName(locked.Staff[actor.Id]),
            Message = $"Additional-copy task {request.Id} created for BIB {source.BibId}. Patron hold remains {holdText} for the same BIB. Open additional-copy tasks for this library/BIB: {openCountAfter}.",
            CreatedUtc = now
        });
        await context.SaveChangesAsync(cancellationToken);

        EmailOutbox? outbox = null;
        if (input.EmailPurchaseReminder)
        {
            outbox = await AddStaffNotificationAsync(
                context,
                locked.Staff[actor.Id],
                source.LibraryOrganizationId,
                $"additional-copy-reminder:{request.Id}",
                "ASAP additional-copy reminder",
                $"Additional-copy task {request.Id} for {request.Title} (BIB {request.BibId}) was created.",
                readiness.IsConfigured,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var notificationStatus = input.EmailPurchaseReminder
            ? outbox?.Status == "pending" ? "queued" : "suppressed"
            : "not_requested";
        var notificationReason = outbox?.SuppressionReason ??
            (input.EmailPurchaseReminder && outbox is null ? "recipient_missing" : null);
        try
        {
            Dispatch(outbox);
        }
        // The accepted task and outbox are durable; return the dispatch outcome without misreporting a rollback.
        catch (Exception exception)
        {
            logger.LogError(exception, "Additional-copy reminder dispatch failed after task {RequestId} committed", request.Id);
            notificationStatus = "dispatch_failed";
            notificationReason = "queue_unavailable";
        }
        return new AdditionalCopyMutationResult(
            "created",
            request.Id,
            OpenCountBefore: openCount,
            OpenCountAfter: openCountAfter,
            ReminderRequested: input.EmailPurchaseReminder,
            DispatchOutboxId: outbox?.Id,
            FinalStatus: "open",
            NotificationStatus: notificationStatus,
            NotificationReason: notificationReason);
    }

    public Task<AdditionalCopyMutationResult> ClaimAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        bool unclaim,
        CancellationToken cancellationToken) =>
        MutateClaimAsync(actor, requestId, input, unclaim, null, false, cancellationToken);

    public Task<AdditionalCopyMutationResult> ClearClaimAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        CancellationToken cancellationToken) =>
        MutateClaimAsync(actor, requestId, input, true, null, true, cancellationToken);

    public async Task<AdditionalCopyMutationResult> AssignAsync(
        CurrentStaff actor,
        long requestId,
        AssignAdditionalCopyInput input,
        CancellationToken cancellationToken)
    {
        if (!input.AssigneeId.HasValue)
        {
            return new AdditionalCopyMutationResult("invalid_assignment");
        }
        return await MutateClaimAsync(
            actor,
            requestId,
            new VersionInput(input.Version),
            false,
            input.AssigneeId.Value,
            false,
            cancellationToken);
    }

    private async Task<AdditionalCopyMutationResult> MutateClaimAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        bool unclaim,
        long? assigneeId,
        bool clearOther,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new AdditionalCopyMutationResult("invalid_version");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await ReadTaskSnapshotAsync(context, requestId, cancellationToken);
        if (snapshot is null)
        {
            return new AdditionalCopyMutationResult("not_found");
        }
        if (!snapshot.Version.SequenceEqual(expectedVersion))
        {
            return new AdditionalCopyMutationResult("stale_version");
        }
        var currentActor = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (currentActor is null || !IsSameCurrentActor(actor, currentActor) ||
            !IsRelationshipEligible(currentActor, snapshot.LibraryOrganizationId))
        {
            return new AdditionalCopyMutationResult("staff_scope_forbidden");
        }
        EmailTransportReadiness readiness;
        try
        {
            readiness = assigneeId.HasValue
                ? await emailSender.CheckReadinessAsync(snapshot.LibraryOrganizationId, cancellationToken)
                : EmailTransportReadiness.NotConfigured;
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new AdditionalCopyMutationResult("notification_dependency_unavailable");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var targetId = assigneeId ?? actor.Id;
        var locked = await LockRelationshipContextAsync(
            context,
            actor,
            snapshot.LibraryOrganizationId,
            [actor.Id, snapshot.CandidateStaffUserId, targetId],
            cancellationToken);
        if (locked.Code != "locked")
        {
            return new AdditionalCopyMutationResult(locked.Code);
        }
        var request = await LockAdditionalCopyAsync(context, requestId, cancellationToken);
        var validation = ValidateLockedTask(request, snapshot, expectedVersion);
        if (validation is not null)
        {
            return new AdditionalCopyMutationResult(validation);
        }
        if (request!.Status != "open")
        {
            return new AdditionalCopyMutationResult("request_not_open");
        }

        if (unclaim)
        {
            if (clearOther && (locked.Staff[actor.Id].Role is not (StaffRole.Admin or StaffRole.SuperAdmin) ||
                !request.ClaimedByStaffUserId.HasValue || request.ClaimedByStaffUserId == actor.Id))
            {
                return new AdditionalCopyMutationResult("claim_forbidden");
            }
            if (!clearOther && !request.ClaimedByStaffUserId.HasValue)
            {
                return new AdditionalCopyMutationResult("claim_conflict");
            }
            if (!clearOther && request.ClaimedByStaffUserId != actor.Id)
            {
                return new AdditionalCopyMutationResult("claim_forbidden");
            }
            if (clearOther)
            {
                var now = UtcNow();
                request.Notes = AppendNote(request.Notes,
                    $"[{UtcIso(now)}] {SafeNoteValue(DisplayName(locked.Staff[actor.Id]))} (staff ID {actor.Id}) cleared another staff member's claim. Previous claimant: {SafeNoteValue(request.ClaimedByDisplayName)}; claimed at: {(request.ClaimedAtUtc.HasValue ? UtcIso(request.ClaimedAtUtc.Value) : "unknown")}; staff ID: {request.ClaimedByStaffUserId}; claim type: {SafeNoteValue(request.ClaimType)}; rule: {request.ClaimRuleId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
            }
            ClearClaim(request);
        }
        else
        {
            if (!assigneeId.HasValue && request.ClaimedByStaffUserId.HasValue &&
                request.ClaimedByStaffUserId != actor.Id)
            {
                return new AdditionalCopyMutationResult("claim_conflict");
            }
            if (!locked.Staff.TryGetValue(targetId, out var assignee) ||
                !IsRelationshipEligible(assignee, request.LibraryOrganizationId))
            {
                return new AdditionalCopyMutationResult("assignee_ineligible");
            }
            SetManualClaim(request, assignee, UtcNow());
        }
        request.UpdatedUtc = UtcNow();
        await context.SaveChangesAsync(cancellationToken);

        EmailOutbox? outbox = null;
        if (assigneeId.HasValue)
        {
            var assignee = locked.Staff[assigneeId.Value];
            outbox = await AddStaffNotificationAsync(
                context,
                assignee,
                request.LibraryOrganizationId,
                $"additional-copy-assignment:{request.Id}:{Convert.ToHexString(expectedVersion)}:{assignee.Id}",
                "ASAP additional-copy task assigned",
                $"Additional-copy task {request.Id} for {request.Title} has been assigned to you.",
                readiness.IsConfigured,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var notificationStatus = assigneeId.HasValue
            ? outbox?.Status == "pending" ? "queued" : "suppressed"
            : null;
        var notificationReason = outbox?.SuppressionReason ??
            (assigneeId.HasValue && outbox is null ? "recipient_missing" : null);
        try
        {
            Dispatch(outbox);
        }
        // The accepted task and outbox are durable; return the dispatch outcome without misreporting a rollback.
        catch (Exception exception)
        {
            logger.LogError(exception, "Additional-copy assignment dispatch failed after task {RequestId} committed", request.Id);
            notificationStatus = "dispatch_failed";
            notificationReason = "queue_unavailable";
        }
        return new AdditionalCopyMutationResult("updated", request.Id, DispatchOutboxId: outbox?.Id,
            FinalStatus: "open", NotificationStatus: notificationStatus, NotificationReason: notificationReason);
    }

    public async Task<AdditionalCopyMutationResult> SetClosedAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        bool reopen,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new AdditionalCopyMutationResult("invalid_version");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await ReadTaskSnapshotAsync(context, requestId, cancellationToken);
        if (snapshot is null)
        {
            return new AdditionalCopyMutationResult("not_found");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockRelationshipContextAsync(
            context,
            actor,
            snapshot.LibraryOrganizationId,
            [actor.Id, snapshot.CandidateStaffUserId],
            cancellationToken);
        if (locked.Code != "locked")
        {
            return new AdditionalCopyMutationResult(locked.Code);
        }
        var request = await LockAdditionalCopyAsync(context, requestId, cancellationToken);
        var validation = ValidateLockedTask(request, snapshot, expectedVersion);
        if (validation is not null)
        {
            return new AdditionalCopyMutationResult(validation);
        }
        if (reopen && request!.Status == "open" || !reopen && request!.Status == RequestStatus.Closed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AdditionalCopyMutationResult("updated", request.Id, FinalStatus: request.Status);
        }

        var now = UtcNow();
        string? clearedReason = null;
        if (reopen)
        {
            clearedReason = RetainedClaimInvalidReason(request!, locked.Staff);
            if (clearedReason is not null)
            {
                var note = RetainedClaimNote(request!, clearedReason, now);
                ClearClaim(request!);
                request!.Notes = AppendNote(request.Notes, note);
            }
            request!.Status = "open";
            request.ClosedByStaffUserId = null;
            request.ClosedByDisplayName = null;
            request.ClosedUtc = null;
        }
        else
        {
            request!.Status = RequestStatus.Closed;
            request.ClosedByStaffUserId = actor.Id;
            request.ClosedByDisplayName = DisplayName(locked.Staff[actor.Id]);
            request.ClosedUtc = now;
        }
        request.UpdatedUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdditionalCopyMutationResult("updated", request.Id, clearedReason,
            FinalStatus: request.Status);
    }

    public async Task<AdditionalCopyMutationResult> DeleteClosedAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion) ||
            actor.Role is not (StaffRole.Admin or StaffRole.SuperAdmin))
        {
            return new AdditionalCopyMutationResult("delete_forbidden");
        }
        if (!StaffVersion.TryDecode(input.ActorVersion, out var expectedActorVersion))
        {
            return new AdditionalCopyMutationResult("actor_changed_since_preview");
        }
        if (!actor.RowVersion.SequenceEqual(expectedActorVersion))
        {
            return new AdditionalCopyMutationResult("actor_changed_since_preview");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await ReadTaskSnapshotAsync(context, requestId, cancellationToken);
        if (snapshot is null)
        {
            return new AdditionalCopyMutationResult("not_found");
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockRelationshipContextAsync(
            context,
            actor,
            snapshot.LibraryOrganizationId,
            [actor.Id],
            cancellationToken);
        if (locked.Code != "locked")
        {
            return new AdditionalCopyMutationResult(locked.Code);
        }
        var request = await LockAdditionalCopyAsync(context, requestId, cancellationToken);
        var validation = ValidateLockedTask(request, snapshot, expectedVersion, compareCandidate: false);
        if (validation is not null)
        {
            return new AdditionalCopyMutationResult(validation);
        }
        if (request!.Status != RequestStatus.Closed)
        {
            return new AdditionalCopyMutationResult("delete_requires_closed");
        }
        if (locked.Staff[actor.Id].Role is not (StaffRole.Admin or StaffRole.SuperAdmin))
        {
            return new AdditionalCopyMutationResult("delete_forbidden");
        }
        if (!locked.Staff[actor.Id].RowVersion.SequenceEqual(expectedActorVersion))
        {
            return new AdditionalCopyMutationResult("actor_changed_since_preview");
        }
        context.DeletedRequestAudits.Add(new DeletedRequestAudit
        {
            RequestType = "additional_copy",
            OriginalRequestKey = request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            LibraryOrganizationId = request.LibraryOrganizationId,
            Title = request.Title,
            Author = request.Author,
            Identifier = request.Identifier,
            BibId = request.BibId,
            Status = request.Status,
            CreatedUtc = request.CreatedUtc,
            DeletedUtc = UtcNow(),
            DeletedByStaffUserId = actor.Id,
            DeletedByDisplayName = DisplayName(locked.Staff[actor.Id])
        });
        context.AdditionalCopyRequests.Remove(request);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdditionalCopyMutationResult("deleted", requestId);
    }

    private async Task<LockedRelationshipContext> LockRelationshipContextAsync(
        AsapDbContext context,
        CurrentStaff actor,
        int organizationId,
        IEnumerable<long?> staffIds,
        CancellationToken cancellationToken)
    {
        if (!await StaffEligibilityService.LockOrganizationsAsync(context,
                [LibraryScope.SystemOrganizationId, organizationId], cancellationToken))
        {
            return new LockedRelationshipContext("organization_inactive");
        }
        var organization = context.Organizations.Local.Single(item => item.Id == organizationId);
        if (!organization.IsActive)
        {
            return new LockedRelationshipContext("organization_inactive");
        }
        var staff = new Dictionary<long, StaffUser>();
        foreach (var id in staffIds.Where(item => item.HasValue).Select(item => item!.Value).Distinct().Order())
        {
            var row = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
                .SingleOrDefaultAsync(cancellationToken);
            if (row is not null)
            {
                staff[id] = row;
            }
        }
        if (!staff.TryGetValue(actor.Id, out var lockedActor) ||
            !IsSameCurrentActor(actor, lockedActor) ||
            !IsRelationshipEligible(lockedActor, organizationId) ||
            !StaffEligibilityService.HasLockedActiveOrganization(context, lockedActor))
        {
            return new LockedRelationshipContext("staff_scope_forbidden");
        }
        return new LockedRelationshipContext("locked", staff);
    }

    private bool IsSameCurrentActor(CurrentStaff actor, StaffUser row) =>
        staffEligibility.IsCurrentIdentity(actor, row);

    private bool IsRelationshipEligible(StaffUser row, int organizationId) =>
        StaffEligibilityService.IsAssignmentEligible(row, organizationId);

    private string? RetainedClaimInvalidReason(
        AdditionalCopyRequest request,
        IReadOnlyDictionary<long, StaffUser> staff)
    {
        if (!request.ClaimedByStaffUserId.HasValue)
        {
            return request.ClaimedByDisplayName is null && request.ClaimedAtUtc is null
                ? null
                : "claimant_unmapped";
        }
        if (!staff.TryGetValue(request.ClaimedByStaffUserId.Value, out var candidate))
        {
            return "claimant_unmapped";
        }
        if (!candidate.IsActive || !StaffEmail.IsValidAuthenticationEmail(candidate))
        {
            return "claimant_inactive";
        }
        if (!IsRelationshipEligible(candidate, request.LibraryOrganizationId))
        {
            return "claimant_out_of_scope";
        }
        return request.ClaimedByDisplayName is null || !request.ClaimedAtUtc.HasValue
            ? "claim_metadata_incomplete"
            : null;
    }

    private async Task<EmailOutbox?> AddStaffNotificationAsync(
        AsapDbContext context,
        StaffUser recipient,
        int organizationId,
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
            .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var library = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
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
        var now = UtcNow();
        var outbox = new EmailOutbox
        {
            OrganizationId = organizationId,
            BusinessKey = businessKey,
            DeliveryClass = "staff_authorization_sensitive",
            RecipientStaffUserId = recipient.Id,
            RecipientAuthenticationEmail = recipient.NormalizedUserPrincipalName,
            AuthorizationOrganizationId = organizationId,
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
        if (outbox?.Status == "pending")
        {
            outboxDispatcher.Enqueue(outbox.Id);
        }
    }

    private static Task<TitleRequest?> LockTitleRequestAsync(
        AsapDbContext context,
        long id,
        CancellationToken cancellationToken) =>
        context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(cancellationToken);

    private static Task<AdditionalCopyRequest?> LockAdditionalCopyAsync(
        AsapDbContext context,
        long id,
        CancellationToken cancellationToken) =>
        context.AdditionalCopyRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<int> LockAndCountOpenAsync(
        AsapDbContext context,
        int organizationId,
        int bibId,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT COUNT_BIG(*) FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK,INDEX([IX_AdditionalCopyRequest_LibraryBibStatus])) WHERE [LibraryOrganizationId]=@organizationId AND [BibId]=@bibId AND [Status]=N'open';";
        var organizationParameter = command.CreateParameter();
        organizationParameter.ParameterName = "@organizationId";
        organizationParameter.Value = organizationId;
        command.Parameters.Add(organizationParameter);
        var bibParameter = command.CreateParameter();
        bibParameter.ParameterName = "@bibId";
        bibParameter.Value = bibId;
        command.Parameters.Add(bibParameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Task<RelationshipSnapshot?> ReadTaskSnapshotAsync(
        AsapDbContext context,
        long requestId,
        CancellationToken cancellationToken) =>
        context.AdditionalCopyRequests.AsNoTracking()
            .Where(item => item.Id == requestId)
            .Select(item => new RelationshipSnapshot(
                item.LibraryOrganizationId,
                item.ClaimedByStaffUserId,
                item.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    private static string? ValidateLockedTask(
        AdditionalCopyRequest? request,
        RelationshipSnapshot snapshot,
        byte[] expectedVersion,
        bool compareCandidate = true)
    {
        if (request is null || request.LibraryOrganizationId != snapshot.LibraryOrganizationId)
        {
            return "not_found";
        }
        if (!request.RowVersion.SequenceEqual(snapshot.Version) ||
            compareCandidate && request.ClaimedByStaffUserId != snapshot.CandidateStaffUserId ||
            !request.RowVersion.SequenceEqual(expectedVersion))
        {
            return "stale_version";
        }
        return null;
    }

    private static void SetManualClaim(AdditionalCopyRequest request, StaffUser staff, DateTime now)
    {
        request.ClaimedByStaffUserId = staff.Id;
        request.ClaimedByDisplayName = DisplayName(staff);
        request.ClaimedAtUtc = now;
        request.ClaimType = ClaimType.Manual;
        request.ClaimRuleId = null;
    }

    private static void ClearClaim(AdditionalCopyRequest request)
    {
        request.ClaimedByStaffUserId = null;
        request.ClaimedByDisplayName = null;
        request.ClaimedAtUtc = null;
        request.ClaimType = null;
        request.ClaimRuleId = null;
    }

    private static string RetainedClaimNote(AdditionalCopyRequest request, string reason, DateTime now) =>
        $"[{UtcIso(now)}] System cleared retained claim while reopening ({reason}). Previous claimant: {SafeNoteValue(request.ClaimedByDisplayName)}; claimed at: {(request.ClaimedAtUtc.HasValue ? UtcIso(request.ClaimedAtUtc.Value) : "unknown")}; staff ID: {(request.ClaimedByStaffUserId?.ToString() ?? "unmapped")}.";

    internal static string LifecycleClaimNote(AdditionalCopyRequest request, DateTime now) =>
        $"[{UtcIso(now)}] System cleared claim because the assignee's staff access changed. Previous claimant: {SafeNoteValue(request.ClaimedByDisplayName)}; claimed at: {(request.ClaimedAtUtc.HasValue ? UtcIso(request.ClaimedAtUtc.Value) : "unknown")}; reason: staff_scope_contracted.";

    internal static string AppendNote(string? current, string note) =>
        string.IsNullOrWhiteSpace(current) ? note : $"{current.TrimEnd()}\n{note}";

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string UtcIso(DateTime value) => AsUtc(value).ToString("O");
    private static bool CanAccess(CurrentStaff actor, int organizationId) =>
        StaffEligibilityService.CanAccess(actor, organizationId);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string DisplayName(StaffUser value) =>
        Clean(value.DisplayName) ?? Clean(value.UserPrincipalName) ?? "Staff";
    private static string SafeNoteValue(string? value) =>
        Clean(value)?.Replace('\r', ' ').Replace('\n', ' ') ?? "unknown";

    private static ScopeResolution ResolveScope(
        CurrentStaff actor,
        LibraryScope scope,
        IReadOnlyCollection<Organization> organizations)
    {
        if (actor.Role != StaffRole.SuperAdmin)
        {
            return new ScopeResolution(true, actor.OrganizationId, actor.OrganizationId.ToString());
        }
        if (scope.Kind == LibraryScopeKind.All)
        {
            return new ScopeResolution(true, null, "all");
        }
        return scope.Kind == LibraryScopeKind.Library && organizations.Any(item => item.Id == scope.OrganizationId)
            ? new ScopeResolution(true, scope.OrganizationId, scope.ToTransportValue())
            : new ScopeResolution(false, null, string.Empty);
    }

    private static async Task<IReadOnlyList<AdditionalCopyDto>> BuildDtosAsync(
        AsapDbContext context,
        IReadOnlyList<AdditionalCopyRequest> requests,
        CurrentStaff actor,
        string? claimClearedReason,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }
        var sourceIds = requests.Where(item => item.SourceTitleRequestId.HasValue)
            .Select(item => item.SourceTitleRequestId!.Value)
            .Distinct()
            .ToArray();
        var sources = await context.TitleRequests.AsNoTracking()
            .Where(item => sourceIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var formatIds = requests.Where(item => item.MaterialFormatId.HasValue)
            .Select(item => item.MaterialFormatId!.Value)
            .Distinct()
            .ToArray();
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => formatIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var organizationIds = requests.Select(item => item.LibraryOrganizationId).Distinct().ToArray();
        var workflowRows = await context.WorkflowSettings.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || organizationIds.Contains(item.OrganizationId))
            .ToDictionaryAsync(item => item.OrganizationId, cancellationToken);
        workflowRows.TryGetValue(1, out var systemWorkflow);
        return requests.Select(request =>
        {
            sources.TryGetValue(request.SourceTitleRequestId ?? 0, out var source);
            formats.TryGetValue(request.MaterialFormatId ?? 0, out var format);
            var isOpen = request.Status == "open";
            var claimedByActor = request.ClaimedByStaffUserId == actor.Id;
            workflowRows.TryGetValue(request.LibraryOrganizationId, out var libraryWorkflow);
            return new AdditionalCopyDto(
                request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "additional_copy",
                request.LegacyId,
                request.SourceTitleRequestId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                source?.Status,
                request.LibraryOrganizationId,
                request.LibraryNameSnapshot ?? string.Empty,
                request.BibId,
                request.Title,
                request.Author,
                request.FormatSnapshot ?? format?.Code,
                format?.Label,
                request.Identifier,
                request.Publication,
                request.Status,
                request.Notes,
                request.CreatedByStaffUserId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.CreatedByDisplayName,
                request.ClosedByStaffUserId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.ClosedByDisplayName,
                AsUtc(request.ClosedUtc),
                AsUtc(request.CreatedUtc),
                AsUtc(request.UpdatedUtc),
                request.ClaimedByStaffUserId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.ClaimedByDisplayName,
                AsUtc(request.ClaimedAtUtc),
                request.ClaimType,
                request.ClaimRuleId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StaffVersion.Encode(request.RowVersion),
                claimClearedReason,
                new AdditionalCopyCapabilities(
                    isOpen && !request.ClaimedByStaffUserId.HasValue,
                    isOpen && claimedByActor,
                    isOpen,
                    isOpen,
                    !isOpen,
                    !isOpen && actor.Role is StaffRole.Admin or StaffRole.SuperAdmin,
                    isOpen && request.ClaimedByStaffUserId.HasValue && !claimedByActor &&
                    (actor.Role is StaffRole.Admin or StaffRole.SuperAdmin)))
            {
                TimeoutContext = new AdditionalCopyTimeoutContext(
                    libraryWorkflow?.AdditionalCopyTimeoutEnabled ?? systemWorkflow?.AdditionalCopyTimeoutEnabled == true,
                    libraryWorkflow?.AdditionalCopyTimeoutDays ?? systemWorkflow?.AdditionalCopyTimeoutDays)
            };
        }).ToList();
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;

    private sealed record RelationshipSnapshot(
        int LibraryOrganizationId,
        long? CandidateStaffUserId,
        byte[] Version);
    private sealed record ScopeResolution(bool IsValid, int? OrganizationId, string Scope);
    private sealed record LockedRelationshipContext(
        string Code,
        IReadOnlyDictionary<long, StaffUser>? StaffValues = null)
    {
        public IReadOnlyDictionary<long, StaffUser> Staff => StaffValues ?? new Dictionary<long, StaffUser>();
    }
}
