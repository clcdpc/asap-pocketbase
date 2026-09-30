using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Administration;

public sealed record WorkflowSettingsPatch
{
    public SuppliedValue<string?> SuggestionLimitMessage { get; init; }
    public SuppliedValue<string?> CommonAuthorsLabel { get; init; }
    public SuppliedValue<string?> CommonAuthorsHelp { get; init; }
    public SuppliedValue<string?> CommonAuthorsMessage { get; init; }
    public SuppliedValue<string?> PatronCodeEligibilityMessage { get; init; }
    public SuppliedValue<bool?> OutstandingTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> OutstandingTimeoutSendEmail { get; init; }
    public SuppliedValue<bool?> HoldPickupTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> PendingHoldTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> AdditionalCopyTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> AutoPromote { get; init; }
    public SuppliedValue<bool?> CommonAuthorsEnabled { get; init; }
    public SuppliedValue<bool?> AllowPatronAutoholdOptOut { get; init; }
    public SuppliedValue<bool?> AllowAnyRegisteredCardLogin { get; init; }
    public SuppliedValue<bool?> PatronCodeEligibilityEnabled { get; init; }
    public SuppliedValue<int?> SuggestionLimit { get; init; }
    public SuppliedValue<int?> OutstandingTimeoutDays { get; init; }
    public SuppliedValue<int?> HoldPickupTimeoutDays { get; init; }
    public SuppliedValue<int?> PendingHoldTimeoutDays { get; init; }
    public SuppliedValue<int?> AdditionalCopyTimeoutDays { get; init; }
}

public sealed record PatronTextPatch
{
    public SuppliedValue<string?> PageTitle { get; init; }
    public SuppliedValue<string?> BarcodeLabel { get; init; }
    public SuppliedValue<string?> PinLabel { get; init; }
    public SuppliedValue<string?> LoginPrompt { get; init; }
    public SuppliedValue<string?> LoginNote { get; init; }
    public SuppliedValue<string?> SuggestionFormNote { get; init; }
    public SuppliedValue<string?> NoEmailMessage { get; init; }
    public SuppliedValue<string?> SuccessTitle { get; init; }
    public SuppliedValue<string?> SuccessMessage { get; init; }
    public SuppliedValue<string?> AlreadySubmittedMessage { get; init; }
    public SuppliedValue<string?> EbookMessage { get; init; }
    public SuppliedValue<string?> EaudiobookMessage { get; init; }
    public SuppliedValue<string?> SuggestionStatusLabel { get; init; }
    public SuppliedValue<string?> OutstandingPurchaseStatusLabel { get; init; }
    public SuppliedValue<string?> PendingHoldStatusLabel { get; init; }
    public SuppliedValue<string?> HoldPlacedStatusLabel { get; init; }
    public SuppliedValue<string?> ClosedStatusLabel { get; init; }
    public SuppliedValue<string?> RejectedStatusLabel { get; init; }
    public SuppliedValue<string?> HoldCompletedStatusLabel { get; init; }
    public SuppliedValue<string?> HoldNotPickedUpStatusLabel { get; init; }
    public SuppliedValue<string?> ManualStatusLabel { get; init; }
    public SuppliedValue<string?> SilentStatusLabel { get; init; }
}

public sealed record SystemSettingsPatch
{
    public SuppliedValue<string?> StaffUrl { get; init; }
    public SuppliedValue<string?> LeapBibUrlPattern { get; init; }
    public SuppliedValue<string?> LeapPatronUrlPattern { get; init; }
    public SuppliedValue<string?> FormatIconUrlPattern { get; init; }
    public SuppliedValue<string?> SystemNotEnabledMessage { get; init; }
    public SuppliedValue<string?> MisconfiguredMessage { get; init; }
    public bool HasChanges => StaffUrl.IsSupplied || LeapBibUrlPattern.IsSupplied || LeapPatronUrlPattern.IsSupplied ||
        FormatIconUrlPattern.IsSupplied || SystemNotEnabledMessage.IsSupplied || MisconfiguredMessage.IsSupplied;
}

public sealed record PolarisSettingsPatch
{
    public SuppliedValue<string?> Host { get; init; }
    public SuppliedValue<string?> AccessId { get; init; }
    public SuppliedValue<string?> StaffDomain { get; init; }
    public SuppliedValue<string?> AdminUser { get; init; }
    public SuppliedValue<int?> WorkstationId { get; init; }
    public SuppliedValue<int?> SystemPolarisUserId { get; init; }
    public SuppliedValue<string?> ApiKey { get; init; }
    public SuppliedValue<string?> AdminPassword { get; init; }
    public SuppliedValue<bool?> ClearApiKey { get; init; }
    public SuppliedValue<bool?> ClearAdminPassword { get; init; }
    public bool HasChanges => Host.IsSupplied || AccessId.IsSupplied || StaffDomain.IsSupplied ||
        AdminUser.IsSupplied || WorkstationId.IsSupplied || SystemPolarisUserId.IsSupplied ||
        ApiKey.IsSupplied || AdminPassword.IsSupplied || ClearApiKey.IsSupplied || ClearAdminPassword.IsSupplied;
}

public sealed record EmailSettingsPatch
{
    public SuppliedValue<string?> FromAddress { get; init; }
    public SuppliedValue<string?> FromName { get; init; }
    public SuppliedValue<string?> ServerToken { get; init; }
    public SuppliedValue<bool?> ClearServerToken { get; init; }
    public bool HasTokenChanges => ServerToken.IsSupplied || ClearServerToken.IsSupplied;
}

public sealed class AdministrationInputException(string message) : Exception(message);

public sealed record AdministrationSettingsCommand(
    LibraryScope Scope, string? Version, bool Reset, WorkflowSettingsPatch Workflow, PatronTextPatch Patron,
    JsonElement CollectionEdits, string? BindingError)
{
    public SystemSettingsPatch System { get; init; } = new();
    public PolarisSettingsPatch Polaris { get; init; } = new();
    public EmailSettingsPatch Email { get; init; } = new();
}

// Legacy collection shapes are interpreted by the settings collection mapper. Scalar patches
// are bound once here, with omission distinct from an explicit inherited/null value.
public static class AdministrationSettingsBinding
{
    public static LibraryScope DefaultScope(CurrentStaff actor) => actor.Role == StaffRole.SuperAdmin
        ? LibraryScope.System : LibraryScope.ForLibrary(actor.OrganizationId);

    public static AdministrationSettingsCommand Bind(CurrentStaff actor, JsonElement payload)
    {
        string? error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "settings_payload_invalid";
        }
        var scopeValue = ReadScope(payload, "orgId", ref error);
        var scopeText = scopeValue.IsSupplied ? scopeValue.Value : ReadScope(payload, "organizationId", ref error).Value;
        if (!LibraryScope.TryParse(scopeText, DefaultScope(actor), out var scope))
        {
            error ??= "organization_invalid";
        }
        var workflow = Section(payload, ref error, "workflow");
        var patron = Section(payload, ref error, "ui_text", "patron");
        var workflowPatch = new WorkflowSettingsPatch
        {
            SuggestionLimitMessage = Text(workflow, "suggestionLimitMessage", ref error),
            CommonAuthorsLabel = Text(workflow, "commonAuthorsLabel", ref error),
            CommonAuthorsHelp = Text(workflow, "commonAuthorsHelp", ref error),
            CommonAuthorsMessage = Text(workflow, "commonAuthorsMessage", ref error),
            PatronCodeEligibilityMessage = Text(workflow, "patronCodeEligibilityMessage", ref error),
            OutstandingTimeoutEnabled = Boolean(workflow, "outstandingTimeoutEnabled", ref error),
            OutstandingTimeoutSendEmail = Boolean(workflow, "outstandingTimeoutSendEmail", ref error),
            HoldPickupTimeoutEnabled = Boolean(workflow, "holdPickupTimeoutEnabled", ref error),
            PendingHoldTimeoutEnabled = Boolean(workflow, "pendingHoldTimeoutEnabled", ref error),
            AdditionalCopyTimeoutEnabled = Boolean(workflow, "additionalCopyTimeoutEnabled", ref error),
            AutoPromote = Boolean(workflow, "autoPromote", ref error),
            CommonAuthorsEnabled = Boolean(workflow, "commonAuthorsEnabled", ref error),
            AllowPatronAutoholdOptOut = Boolean(workflow, "allowPatronAutoholdOptOut", ref error),
            AllowAnyRegisteredCardLogin = Boolean(workflow, "allowAnyRegisteredCardLogin", ref error),
            PatronCodeEligibilityEnabled = Boolean(workflow, "patronCodeEligibilityEnabled", ref error),
            SuggestionLimit = Integer(workflow, "suggestionLimit", ref error),
            OutstandingTimeoutDays = Integer(workflow, "outstandingTimeoutDays", ref error),
            HoldPickupTimeoutDays = Integer(workflow, "holdPickupTimeoutDays", ref error),
            PendingHoldTimeoutDays = Integer(workflow, "pendingHoldTimeoutDays", ref error),
            AdditionalCopyTimeoutDays = Integer(workflow, "additionalCopyTimeoutDays", ref error),
        };
        var patronPatch = new PatronTextPatch
        {
            PageTitle = Text(patron, "pageTitle", ref error),
            BarcodeLabel = Text(patron, "barcodeLabel", ref error),
            PinLabel = Text(patron, "pinLabel", ref error),
            LoginPrompt = Text(patron, "loginPrompt", ref error),
            LoginNote = Text(patron, "loginNote", ref error),
            SuggestionFormNote = Text(patron, "suggestionFormNote", ref error),
            NoEmailMessage = Text(patron, "noEmailMessage", ref error),
            SuccessTitle = Text(patron, "successTitle", ref error),
            SuccessMessage = Text(patron, "successMessage", ref error),
            AlreadySubmittedMessage = Text(patron, "alreadySubmittedMessage", ref error),
            EbookMessage = Text(patron, "ebookMessage", ref error),
            EaudiobookMessage = Text(patron, "eaudiobookMessage", ref error),
            SuggestionStatusLabel = Text(patron, "suggestionStatusLabel", ref error),
            OutstandingPurchaseStatusLabel = Text(patron, "outstandingPurchaseStatusLabel", ref error),
            PendingHoldStatusLabel = Text(patron, "pendingHoldStatusLabel", ref error),
            HoldPlacedStatusLabel = Text(patron, "holdPlacedStatusLabel", ref error),
            ClosedStatusLabel = Text(patron, "closedStatusLabel", ref error),
            RejectedStatusLabel = Text(patron, "rejectedStatusLabel", ref error),
            HoldCompletedStatusLabel = Text(patron, "holdCompletedStatusLabel", ref error),
            HoldNotPickedUpStatusLabel = Text(patron, "holdNotPickedUpStatusLabel", ref error),
            ManualStatusLabel = Text(patron, "manualStatusLabel", ref error),
            SilentStatusLabel = Text(patron, "silentStatusLabel", ref error),
        };
        var system = Section(payload, ref error, "systemSettings", "system");
        var polaris = Section(payload, ref error, "polaris");
        var email = Section(payload, ref error, "emails", "email");
        var smtp = Section(payload, ref error, "smtp");
        var systemPatch = new SystemSettingsPatch
        {
            StaffUrl = Last(Text(system, "staffUrl", ref error), Text(payload, "staffUrl", ref error)),
            LeapBibUrlPattern = Last(Text(system, "leapBibUrlPattern", ref error), Text(payload, "leapBibUrlPattern", ref error)),
            LeapPatronUrlPattern = Last(Text(system, "leapPatronUrlPattern", ref error), Text(payload, "leapPatronUrlPattern", ref error)),
            FormatIconUrlPattern = Last(Text(system, "formatIconUrlPattern", ref error), Text(payload, "formatIconUrlPattern", ref error)),
            SystemNotEnabledMessage = Last(Last(Text(system, "systemNotEnabledMessage", ref error),
                Text(payload, "systemNotEnabledMessage", ref error)), Text(patron, "systemNotEnabledMessage", ref error)),
            MisconfiguredMessage = Last(Last(Text(system, "misconfiguredMessage", ref error),
                Text(payload, "misconfiguredMessage", ref error)), Text(patron, "misconfiguredMessage", ref error))
        };
        var polarisPatch = new PolarisSettingsPatch
        {
            Host = Text(polaris, "host", ref error, "polaris_host_invalid"),
            AccessId = Text(polaris, "accessId", ref error),
            StaffDomain = Text(polaris, "staffDomain", ref error),
            AdminUser = Text(polaris, "adminUser", ref error),
            WorkstationId = Integer(polaris, "workstationId", ref error, int.MaxValue, "polaris_identity_invalid"),
            SystemPolarisUserId = Last(Integer(polaris, "systemPolarisUserId", ref error, int.MaxValue, "polaris_identity_invalid"),
                Integer(polaris, "userId", ref error, int.MaxValue, "polaris_identity_invalid")),
            ApiKey = Text(polaris, "apiKey", ref error),
            AdminPassword = Text(polaris, "adminPassword", ref error),
            ClearApiKey = Boolean(polaris, "clearApiKey", ref error),
            ClearAdminPassword = Boolean(polaris, "clearAdminPassword", ref error)
        };
        var emailPatch = new EmailSettingsPatch
        {
            FromAddress = Last(Text(email, "fromAddress", ref error), Text(smtp, "fromAddress", ref error)),
            FromName = Last(Text(email, "fromName", ref error), Text(smtp, "fromName", ref error)),
            ServerToken = Last(Last(Text(email, "postmarkToken", ref error), Text(email, "serverToken", ref error)),
                Last(Text(payload, "postmarkToken", ref error), Text(payload, "serverToken", ref error))),
            ClearServerToken = AnyTrue(AnyTrue(Boolean(email, "clearPostmarkToken", ref error), Boolean(email, "clearServerToken", ref error)),
                AnyTrue(Boolean(payload, "clearPostmarkToken", ref error), Boolean(payload, "clearServerToken", ref error)))
        };
        if (new[] { "organizationIdForRequests", "requestingOrgId", "pickupOrganizationId", "pickupOrgId" }
            .Any(name => polaris.ValueKind == JsonValueKind.Object && polaris.TryGetProperty(name, out _)))
        {
            error = "polaris_context_retired";
        }
        if (scope.Kind != LibraryScopeKind.System && systemPatch.HasChanges)
        {
            error = "settings_system_only";
        }
        if (scope.Kind != LibraryScopeKind.System && polarisPatch.HasChanges)
        {
            error = "polaris_settings_system_only";
        }
        if (scope.Kind != LibraryScopeKind.System && (emailPatch.HasTokenChanges ||
            new[] { "postmarkToken", "serverToken", "clearPostmarkToken", "clearServerToken" }
                .Any(name => smtp.ValueKind == JsonValueKind.Object && smtp.TryGetProperty(name, out _))))
        {
            error = "postmark_token_system_only";
        }
        if (!string.IsNullOrWhiteSpace(polarisPatch.Host.Value) &&
            !Asap.Web.Features.Patron.PolarisConfigurationValidation.IsHostValid(polarisPatch.Host.Value.Trim()))
        {
            error ??= "polaris_host_invalid";
        }
        if (!string.IsNullOrWhiteSpace(emailPatch.ServerToken.Value) && emailPatch.ClearServerToken.Value == true)
        {
            error ??= "postmark_token_intent_conflict";
        }
        return new(scope, Text(payload, "version", ref error).Value,
            string.Equals(Text(payload, "action", ref error).Value, "reset", StringComparison.OrdinalIgnoreCase),
            workflowPatch, patronPatch, payload.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { }) : payload.Clone(), error)
        {
            System = systemPatch, Polaris = polarisPatch, Email = emailPatch
        };
    }

    private static SuppliedValue<bool?> AnyTrue(SuppliedValue<bool?> first, SuppliedValue<bool?> last) =>
        new(first.IsSupplied || last.IsSupplied, first.Value == true || last.Value == true);

    private static SuppliedValue<T> Last<T>(SuppliedValue<T> first, SuppliedValue<T> last) =>
        last.IsSupplied ? last : first;

    private static SuppliedValue<string?> ReadScope(JsonElement payload, string name, ref string? error)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number))
        {
            error ??= "organization_invalid";
            return default;
        }
        return new(true, value.ValueKind == JsonValueKind.Number ? value.GetRawText() :
            value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private static JsonElement Section(JsonElement root, ref string? error, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var section))
            {
                if (section.ValueKind != JsonValueKind.Object)
                {
                    error ??= "settings_payload_invalid";
                    return default;
                }
                return section;
            }
        }
        return root;
    }

    private static SuppliedValue<string?> Text(JsonElement root, string name, ref string? error, string errorCode = "settings_payload_invalid")
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            error ??= errorCode;
            return default;
        }
        return new(true, value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private static SuppliedValue<bool?> Boolean(JsonElement root, string name, ref string? error)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
        {
            error ??= "settings_payload_invalid";
            return default;
        }
        return new(true, value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean());
    }

    private static SuppliedValue<int?> Integer(JsonElement root, string name, ref string? error, int? upperLimit = null, string errorCode = "settings_payload_invalid")
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new(true, null);
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ||
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            var maximum = upperLimit ?? (name == "suggestionLimit" ? 1000 : 3650);
            if (number <= 0 || number > maximum)
            {
                error ??= errorCode;
                return default;
            }
            return new(true, number);
        }
        error ??= errorCode;
        return default;
    }
}
