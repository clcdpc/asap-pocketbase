namespace Asap.Web.Infrastructure.Data;

// Persisted/API values stay stable; application rules share these closed sets.
public static class RequestStatus
{
    public const string Suggestion = "suggestion";
    public const string OutstandingPurchase = "outstanding_purchase";
    public const string PendingHold = "pending_hold";
    public const string HoldPlaced = "hold_placed";
    public const string Closed = "closed";
    public static bool IsPrePlacement(string status) => status is Suggestion or OutstandingPurchase or PendingHold;
}

public static class StaffRole
{
    public const string Staff = "staff";
    public const string Admin = "admin";
    public const string SuperAdmin = "super_admin";
}

public static class HoldOperationState
{
    public const string InProgress = "in_progress";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string OperatorRequired = "operator_required";
    public const string Ambiguous = "ambiguous";
    public const string NoHold = "no_hold";
}

public static class HoldOperationPhase
{
    public const string Acquired = "acquired";
    public const string CreateStarted = "create_started";
    public const string ReplyReady = "reply_ready";
    public const string ReplyStarted = "reply_started";
    public const string ResultRecorded = "result_recorded";
}

public static class ClaimType
{
    public const string Manual = "manual";
    public const string AutomaticFormatRule = "automatic_format_rule";
}

public static class IdentifierCheckState
{
    public const string Pending = "pending";
    public const string Found = "found";
    public const string NotFound = "not_found";
    public const string SkippedNoIdentifier = "skipped_no_isbn";
    public const string RetryExhausted = "error_max_retries";
}
