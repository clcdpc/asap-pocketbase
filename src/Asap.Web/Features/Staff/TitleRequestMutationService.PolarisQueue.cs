using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed partial class TitleRequestMutationService
{
    private const string QueueReceiptEvent = "polaris_queue_completed";
    private sealed record QueueReceipt(Guid OperationId, string Fingerprint, TitleRequestMutationResult Result);

    private static bool IsValidQueueCommand(TitleRequestActionCommand input) =>
        input.OperationId is { } operationId && operationId != Guid.Empty &&
        input.RequestType == "title_request" &&
        input.Action is "catalogFound" or "additionalCopy" &&
        input.BibidSupplied && input.Bibid is > 0 && input.StaffSelectedBibIdSupplied &&
        input.StaffSelectedBibId == input.Bibid && input.Status is null or RequestStatus.PendingHold &&
        input.Title is null && input.Author is null && !input.Identifier.IsSupplied &&
        input.Publication is null && !input.ExactPublicationDate.IsSupplied && !input.CustomFields.IsSupplied &&
        !input.Autohold.IsSupplied && input.Notes is null && input.Format is null && input.RejectionTemplateId is null;

    private static string QueueFingerprint(TitleRequestActionCommand input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));

    private async Task<TitleRequestMutationResult?> ReplayQueueActionAsync(
        CurrentStaff actor, long requestId, TitleRequestActionCommand input, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Use the same organization/identity/request lock order as the mutation. Authorization
        // is required again even when the original action has already committed.
        var locked = await LockForMutationAsync(context, actor, requestId, [actor.Id], cancellationToken);
        if (locked.Code != "locked")
        {
            return new TitleRequestMutationResult(locked.Code);
        }
        return await ReadQueueReceiptAsync(context, actor, requestId, input, cancellationToken);
    }

    private static async Task<TitleRequestMutationResult?> ReadQueueReceiptAsync(
        AsapDbContext context, CurrentStaff actor, long requestId, TitleRequestActionCommand input,
        CancellationToken cancellationToken)
    {
        var receipts = await context.TitleRequestEvents.AsNoTracking()
            .Where(item => item.TitleRequestId == requestId && item.EventType == QueueReceiptEvent)
            .Select(item => new { item.StaffUserId, item.MetadataJson }).ToListAsync(cancellationToken);
        foreach (var row in receipts)
        {
            var receipt = JsonSerializer.Deserialize<QueueReceipt>(row.MetadataJson!);
            if (receipt is null || receipt.OperationId != input.OperationId)
            {
                continue;
            }
            return row.StaffUserId == actor.Id && receipt.Fingerprint == QueueFingerprint(input)
                ? receipt.Result with { Replayed = true }
                : new TitleRequestMutationResult("operation_conflict");
        }
        return null;
    }

    private async Task ReconcileQueueIdentifierAsync(
        AsapDbContext context, TitleRequest request, BibValidationResult bib, CancellationToken cancellationToken)
    {
        await RemoveIdentifierTagsAsync(context, request.Id, cancellationToken);
        request.Identifier = Clean(bib.Identifier);
        request.IsbnCheckStatus = request.Identifier is null ? IdentifierCheckState.SkippedNoIdentifier : IdentifierCheckState.Found;
        request.IsbnCheckResult = request.Identifier is null ? null : "Identifier verified against the selected Polaris BIB.";
        request.IsbnCheckRetryCount = 0;
        request.IsbnCheckLastErrorCode = null;
        request.LastCheckedUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (request.Identifier is not null)
        {
            var tagId = await context.WorkflowTags.Where(item => item.Code == "polaris_bib_found")
                .Select(item => item.Id).SingleAsync(cancellationToken);
            var tracked = context.ChangeTracker.Entries<TitleRequestWorkflowTag>().SingleOrDefault(entry =>
                entry.Entity.TitleRequestId == request.Id && entry.Entity.WorkflowTagId == tagId);
            if (tracked is not null)
            {
                tracked.State = EntityState.Unchanged;
            }
            else
            {
                context.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag
                {
                    TitleRequestId = request.Id,
                    WorkflowTagId = tagId
                });
            }
        }
    }

    private async Task<AdditionalCopyRequest> AddQueuedCopyAsync(
        AsapDbContext context, TitleRequest source, StaffUser actor, CancellationToken cancellationToken)
    {
        await AdditionalCopyService.LockAndCountOpenAsync(context, source.LibraryOrganizationId, source.BibId!.Value, cancellationToken);
        var format = await context.MaterialFormats.AsNoTracking().Where(item => item.Id == source.MaterialFormatId)
            .Select(item => item.Code).SingleAsync(cancellationToken);
        var task = AdditionalCopyService.NewTask(source, format, actor, timeProvider.GetUtcNow().UtcDateTime);
        task.ClaimedByStaffUserId = source.ClaimedByStaffUserId;
        task.ClaimedByDisplayName = source.ClaimedByDisplayName;
        task.ClaimedAtUtc = source.ClaimedAtUtc;
        task.ClaimType = source.ClaimType;
        task.ClaimRuleId = source.ClaimRuleId;
        context.AdditionalCopyRequests.Add(task);
        await context.SaveChangesAsync(cancellationToken);
        return task;
    }
}
