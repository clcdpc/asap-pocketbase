using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Staff;

public static class TitleRequestWorkflowPolicy
{
    public static System.Linq.Expressions.Expression<Func<TitleRequest, bool>> HoldPlacementStage { get; } =
        request => request.Status == RequestStatus.PendingHold && request.AutoHold;
    private static readonly Func<TitleRequest, bool> IsHoldPlacementStage = HoldPlacementStage.Compile();

    public static bool CanPlaceHold(TitleRequest request) => IsHoldPlacementStage(request) &&
        request.BibId is > 0 && request.BibIdStaffVerified && !request.LegacyHoldProtected;

    private static TitleRequestCapabilities EvaluateCapabilities(
        TitleRequest request,
        bool hasIncompleteOperation,
        bool hasPlacedProtection,
        bool hasIncompletePickup = false)
    {
        if (hasIncompletePickup)
        {
            return new TitleRequestCapabilities(false, false, false, false, "pickup_reconciliation_required");
        }
        if (hasIncompleteOperation)
        {
            return new TitleRequestCapabilities(
                false,
                false,
                false,
                false,
                "hold_operation_incomplete");
        }

        if (request.Status is RequestStatus.HoldPlaced or RequestStatus.Closed || hasPlacedProtection)
        {
            return new TitleRequestCapabilities(
                false,
                false,
                false,
                request.Status != RequestStatus.Closed || !hasPlacedProtection,
                request.Status == RequestStatus.Closed && hasPlacedProtection
                    ? "hold_history_retained"
                    : "identifier_locked_by_stage");
        }

        var canEdit = RequestStatus.IsPrePlacement(request.Status);
        return new TitleRequestCapabilities(
            canEdit,
            canEdit,
            request.Status == RequestStatus.Suggestion &&
            !string.IsNullOrWhiteSpace(request.Identifier) &&
            request.IsbnCheckStatus == IdentifierCheckState.RetryExhausted,
            true,
            canEdit ? null : "identifier_locked_by_stage");
    }

    public static TitleRequestCapabilities Evaluate(TitleRequest request, bool hasIncompleteOperation,
        bool hasPlacedProtection, bool hasIncompletePickup = false)
    {
        var protection = hasPlacedProtection || request.LegacyHoldProtected;
        var result = EvaluateCapabilities(request, hasIncompleteOperation, protection, hasIncompletePickup);
        var actions = result.CanChangeWorkflowState
            ? new[] { "edit", "purchase", "alreadyOwn", "catalogFound", "reject", "silentClose", "closeDuplicate", "close", "reopen" }
                .Where(action => ResolveStatus(action, null, request.Status, request.BibId) is not null &&
                    !(action == "reopen" && protection)).ToArray()
            : [];
        return result with { AllowedActions = actions,
            CanPlaceHold = !hasIncompleteOperation && !hasIncompletePickup && !protection && CanPlaceHold(request) };
    }

    public static string AfterCatalogMatch(bool autoHold) => autoHold ? RequestStatus.PendingHold : RequestStatus.Closed;

    public static string? ResolveStatus(string? actionValue, string? requestedStatus, string current, int? bib)
    {
        var action = Clean(actionValue);
        var requested = Clean(requestedStatus);
        var target = action switch
        {
            "edit" when requested is null || requested == current => current,
            "purchase" when current == RequestStatus.Suggestion =>
                bib is null ? RequestStatus.OutstandingPurchase : RequestStatus.PendingHold,
            "alreadyOwn" when current == RequestStatus.Suggestion => RequestStatus.PendingHold,
            "catalogFound" when current is RequestStatus.Suggestion or RequestStatus.OutstandingPurchase => RequestStatus.PendingHold,
            "reject" or "silentClose" when current == RequestStatus.Suggestion => RequestStatus.Closed,
            "closeDuplicate" when current != RequestStatus.Closed => RequestStatus.Closed,
            "close" when current == RequestStatus.HoldPlaced => RequestStatus.Closed,
            "reopen" when current == RequestStatus.Closed => RequestStatus.Suggestion,
            _ => null
        };
        return target is not null && (requested is null || requested == target) ? target : null;
    }

    public static string? ResolveBibTargetStatus(string? action, string current, string? requestedTarget,
        int? bib, bool autoHold, bool bibSupplied)
    {
        if (requestedTarget is null)
        {
            return null;
        }
        var targetsHold = requestedTarget == RequestStatus.PendingHold ||
            action == "edit" && current == RequestStatus.OutstandingPurchase && bibSupplied && bib is not null;
        return targetsHold ? AfterCatalogMatch(autoHold) : requestedTarget;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
