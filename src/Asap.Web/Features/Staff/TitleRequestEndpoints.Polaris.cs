using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record CatalogQueueAction(
    string Action, bool Allowed, string? BlockingReason, bool EnablesAutomaticHold, string? TargetStatus);

public sealed record CatalogQueueActions(
    string? RequestId, string? Version, int? LibraryOrgId, int BibId, bool? CurrentAutohold,
    int? OpenAdditionalCopyCount, bool EmailPurchaseReminderDefault,
    CatalogQueueAction UseBib, CatalogQueueAction AdditionalCopy);

public static partial class TitleRequestEndpoints
{
    private static IResult QueueOutcomeUnconfirmed(long requestId, Guid? operationId) => Results.Json(new
    {
        code = "request_outcome_unconfirmed",
        requestId = requestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        operationId,
        recovery = operationId.HasValue ? "retry_same_operation" : "reload_request",
        message = operationId.HasValue
            ? "The outcome could not be confirmed. Retry the identical action with the same operation ID to recover its result."
            : "The request outcome could not be confirmed. Reload before trying again."
    }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string HoldingsStatus(StaffBibHoldingsSummary? holdings) => holdings switch
    {
        null => "unavailable",
        { ConsortiumCount: 0 } => "no_holdings",
        { IsHoldable: false } => "not_holdable",
        { MyLibraryCount: > 0 } => "owned_by_library",
        _ => "owned_by_consortium"
    };

    private static async Task<CatalogQueueActions> CatalogQueueActionsAsync(
        CurrentStaff actor, TitleRequestDto? request, int bibId, StaffBibHoldingsSummary? holdings,
        IDbContextFactory<AsapDbContext> contextFactory, CancellationToken cancellationToken)
    {
        string? reason = request is null ? "request_context_required" :
            !request.Capabilities.AllowedActions.Contains("catalogFound") ||
            !request.Capabilities.CanChangeBib || !request.Capabilities.CanEditIdentifier
                ? request.Capabilities.BlockingReason ?? "invalid_transition" : null;
        int? openCount = null;
        var sourceHasCopy = false;
        var duplicateOpenRequest = false;
        if (request is not null)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var id = long.Parse(request.Id, System.Globalization.CultureInfo.InvariantCulture);
            var source = await context.TitleRequests.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (source is null || StaffVersion.Encode(source.RowVersion) != request.Version)
            {
                reason = "stale_version";
            }
            else
            {
                var copies = context.AdditionalCopyRequests.AsNoTracking().Where(item =>
                    item.LibraryOrganizationId == request.LibraryOrgId && item.BibId == bibId && item.Status == "open");
                openCount = await copies.CountAsync(cancellationToken);
                sourceHasCopy = await context.AdditionalCopyRequests.AsNoTracking().AnyAsync(item =>
                    item.SourceTitleRequestId == id && item.Status == "open", cancellationToken);
                duplicateOpenRequest = await context.TitleRequests.AsNoTracking().AnyAsync(item =>
                        item.Id != id && item.LibraryOrganizationId == source.LibraryOrganizationId &&
                        ((source.PatronIdSnapshot.HasValue && item.PatronIdSnapshot == source.PatronIdSnapshot) ||
                         (!source.PatronIdSnapshot.HasValue || item.PatronIdSnapshot == null) && item.Barcode == source.Barcode) &&
                        item.Status != RequestStatus.Closed && item.BibId == bibId, cancellationToken);
            }
        }
        var useReason = reason ?? (request?.Autohold == true && duplicateOpenRequest ? "duplicate_open_request" : null) ??
            PolarisQueuePolicy.HoldingsFailure(holdings, false);
        var copyReason = reason ?? (duplicateOpenRequest ? "duplicate_open_request" : null) ??
            (sourceHasCopy ? "additional_copy_exists" : null) ??
            PolarisQueuePolicy.HoldingsFailure(holdings, true);
        return new CatalogQueueActions(request?.Id, request?.Version, request?.LibraryOrgId, bibId, request?.Autohold,
            openCount, actor.AdditionalCopyReminderDefault,
            new CatalogQueueAction("catalogFound", useReason is null, useReason, false,
                request is null ? null : TitleRequestWorkflowPolicy.AfterCatalogMatch(request.Autohold)),
            new CatalogQueueAction("additionalCopy", copyReason is null, copyReason, true, RequestStatus.PendingHold));
    }
}
