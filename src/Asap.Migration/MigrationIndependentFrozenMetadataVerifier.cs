using System.Text.Json;

namespace Asap.Migration;

internal static class MigrationIndependentFrozenMetadataVerifier
{
    private const string RuntimeFile = "effective-legacy-runtime-config.json";
    private const string OperationalFile = "effective-legacy-operational-config.json";
    private const string DefaultStaffUrl = "http://localhost:8090/staff/";
    private const string DefaultIconPattern =
        "https://catalog.clcohio.org/polaris/themes/shared/formats/formatid{MARCTypeOfMaterialID2}.gif";

    private static readonly string[] IconPlaceholders =
    [
        "{MARCTypeOfMaterialID}", "{MARCTypeOfMaterialID2}", "{id}", "{id2}", "{SearchCode}"
    ];

    private static readonly ScheduleRule[] Schedules =
    [
        new("asap-hold-check", "ASAP_CRON_SCHEDULE", "0 * * * *", "WorkflowProcessing"),
        new("asap-isbn-check", "ASAP_ISBN_CHECK_CRON_SCHEDULE", "*/5 * * * *", "IdentifierProcessing"),
        new("asap-organization-sync", "ASAP_ORG_SYNC_CRON_SCHEDULE", "0 2 * * *", "OrganizationRefresh"),
        new("asap-weekly-staff-action-summary", "ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE", "0 20 * * 0", "WeeklyStaffSummary")
    ];

    private static readonly QueueRule[] Queues =
    [
        new("pending_suggestion_isbn_checks", "IdentifierProcessing", "global"),
        new("outstanding_purchases", "PurchasePromotion", "global"),
        new("pending_holds", "HoldPlacement", "global"),
        new("checked_out", "FulfillmentTracking", "global"),
        new("outstanding_timeout", "OutstandingTimeout", "timeouts"),
        new("pending_hold_timeout", "PendingHoldTimeout", "timeouts"),
        new("hold_pickup_timeout", "HoldPickupTimeout", "timeouts"),
        new("additional_copy_timeout", "AdditionalCopyTimeout", "timeouts")
    ];

    public static void Verify(ValidatedMigrationPackage package)
    {
        var systemRows = MigrationPackageReader.ReadRows(package, "system-settings.json", "system_settings");
        var polarisRows = MigrationPackageReader.ReadRows(package, "polaris-settings.json", "polaris_settings");
        Require(systemRows.Count <= 1 && polarisRows.Count <= 1);

        var runtime = MigrationPackageReader.ReadMetadata(package, RuntimeFile);
        var operational = MigrationPackageReader.ReadMetadata(package, OperationalFile);
        VerifyRuntime(runtime, systemRows.SingleOrDefault(), polarisRows.SingleOrDefault(), systemRows.Count == 1);
        VerifyOperational(operational);
    }

    private static void VerifyRuntime(
        JsonElement root,
        SourceRow? systemRow,
        SourceRow? polarisRow,
        bool hasSystemRow)
    {
        var settings = Object(root, "settings");
        VerifyStaffUrl(Object(settings, "StaffApplicationUrl"), systemRow, hasSystemRow);
        VerifyIconPattern(Object(settings, "MaterialTypeIconUrlPattern"), systemRow);
        VerifySecret(Object(settings, "PolarisApiKey"), polarisRow, "apiKey", "polaris_settings.apiKey");
        VerifySecret(
            Object(settings, "PolarisAdminPassword"),
            polarisRow,
            "adminPassword",
            "polaris_settings.adminPassword");
    }

    private static void VerifyStaffUrl(JsonElement claim, SourceRow? systemRow, bool hasSystemRow)
    {
        var persisted = JsTrim(systemRow?.Text("staffUrl") ?? string.Empty);
        if (persisted.Length > 0)
        {
            var expected = NormalizePersistedStaffUrl(persisted);
            Require(expected.Length > 0);
            RequireResolved(claim, expected, "persisted_database", "system_settings.staffUrl");
            return;
        }

        var value = String(claim, "value");
        var provenance = String(claim, "provenance");
        var source = String(claim, "source");
        if (provenance == "code_default")
        {
            Require(value == DefaultStaffUrl && source == "settings.staffUrl.localhost");
            return;
        }

        Require(provenance == "environment_fallback");
        var allowedSource = source is "ASAP_STAFF_URL" or "ASAP_PUBLIC_URL" ||
            (!hasSystemRow && source == "ASAP_BASE_URL");
        Require(allowedSource && IsAttainableEnvironmentStaffUrl(value));

        // With no system row, PUBLIC is attainable only when captured BASE was truthy but
        // initialized to empty after hash removal. BASE itself is not frozen in the package,
        // so this environment-priority antecedent remains an operator-attested capture fact.
    }

    private static void VerifyIconPattern(JsonElement claim, SourceRow? systemRow)
    {
        var persisted = JsTrim(systemRow?.Text("formatIconUrlPattern") ?? string.Empty);
        if (persisted.Length == 0 ||
            persisted.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            persisted.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            !IconPlaceholders.Any(placeholder => persisted.Contains(placeholder, StringComparison.Ordinal)))
        {
            RequireResolved(
                claim,
                DefaultIconPattern,
                "code_default",
                "normalization.defaultFormatIconUrlPattern");
            return;
        }

        RequireResolved(claim, persisted, "persisted_database", "system_settings.formatIconUrlPattern");
    }

    private static void VerifySecret(
        JsonElement claim,
        SourceRow? polarisRow,
        string field,
        string expectedSource)
    {
        var expectedPresence = polarisRow?.Text(field) is { Length: > 0 };
        var hasValue = Boolean(claim, "hasValue");
        var expectedProvenance = polarisRow is null ? "absent_database_record" : "persisted_database";
        Require(
            hasValue == expectedPresence &&
            String(claim, "provenance") == expectedProvenance &&
            String(claim, "source") == expectedSource);
    }

    private static void VerifyOperational(JsonElement root)
    {
        var schedules = Object(root, "schedules");
        Require(schedules.EnumerateObject().Count() == Schedules.Length);
        foreach (var rule in Schedules)
        {
            var schedule = Object(schedules, rule.Name);
            var value = String(schedule, "value");
            var provenance = String(schedule, "provenance");
            Require(String(schedule, "source") == rule.Source);
            Require(String(schedule, "targetScheduleKey") == rule.Target);
            if (provenance == "code_default")
            {
                Require(value == rule.DefaultValue);
            }
            else
            {
                Require(provenance == "environment_override" && value.Length > 0);
            }
        }

        var limits = Object(root, "processingLimits");
        var global = Object(limits, "global");
        var globalPageSize = ReadLimit(global, "pageSize", "source", "ASAP_JOB_PAGE_SIZE", 1, 500);
        var globalMaxPerRun = ReadLimit(global, "maxPerRun", "source", "ASAP_JOB_MAX_PER_RUN", 1, 5000);
        ValidateDefaultOrOverride(globalPageSize, 50, "code_default", 1, 500);
        ValidateDefaultOrOverride(globalMaxPerRun, 500, "code_default", 1, 5000);

        var timeouts = Object(limits, "timeouts");
        var timeoutPageSize = ReadLimit(timeouts, "pageSize", "source", "ASAP_TIMEOUT_PAGE_SIZE", 1, 500);
        var timeoutMaxPerRun = ReadLimit(timeouts, "maxPerRun", "source", "ASAP_TIMEOUT_MAX_PER_RUN", 1, 5000);
        ValidateInheritedOrOverride(timeoutPageSize, "inherited_global", globalPageSize.Value, 1, 500);
        ValidateInheritedOrOverride(timeoutMaxPerRun, "inherited_global", globalMaxPerRun.Value, 1, 5000);

        var queues = Object(limits, "effectiveQueues");
        Require(queues.EnumerateObject().Count() == Queues.Length);
        var expectedConfigured = new Dictionary<string, (int? PageSize, int? MaxPerRun)>(StringComparer.Ordinal);
        foreach (var rule in Queues)
        {
            var queue = Object(queues, rule.Name);
            Require(String(queue, "targetQueue") == rule.Target);
            Require(String(queue, "fallbackFamily") == rule.FallbackFamily);
            var prefix = "ASAP_" + rule.Name.ToUpperInvariant();
            var pageSize = ReadLimit(
                queue,
                "pageSize",
                "queueOverrideSource",
                prefix + "_PAGE_SIZE",
                1,
                500);
            var maxPerRun = ReadLimit(
                queue,
                "maxPerRun",
                "queueOverrideSource",
                prefix + "_MAX_PER_RUN",
                1,
                5000);
            var fallbackPageSize = rule.FallbackFamily == "timeouts" ? timeoutPageSize.Value : globalPageSize.Value;
            var fallbackMaxPerRun = rule.FallbackFamily == "timeouts" ? timeoutMaxPerRun.Value : globalMaxPerRun.Value;
            var inheritedProvenance = rule.FallbackFamily == "timeouts" ? "inherited_timeout" : "inherited_global";
            ValidateInheritedOrOverride(pageSize, inheritedProvenance, fallbackPageSize, 1, 500);
            ValidateInheritedOrOverride(maxPerRun, inheritedProvenance, fallbackMaxPerRun, 1, 5000);

            var pageOverride = pageSize.Provenance == "environment_override";
            var maxOverride = maxPerRun.Provenance == "environment_override";
            if (pageOverride || maxOverride)
            {
                expectedConfigured.Add(
                    rule.Name,
                    (pageOverride ? pageSize.Value : null, maxOverride ? maxPerRun.Value : null));
            }
        }

        var obsolete = Object(Object(limits, "obsoletePathOverrides"), "pending_isbn_checks");
        Require(String(obsolete, "resolution") == "retired_with_hourly_identifier_path");
        Require(String(obsolete, "pageSizeEnvKey") == "ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE");
        Require(String(obsolete, "maxPerRunEnvKey") == "ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN");
        var retiredPageSize = ReadRetiredValue(obsolete, "pageSize", 1, 500);
        var retiredMaxPerRun = ReadRetiredValue(obsolete, "maxPerRun", 1, 5000);
        if (retiredPageSize.HasValue || retiredMaxPerRun.HasValue)
        {
            expectedConfigured.Add("pending_isbn_checks", (retiredPageSize, retiredMaxPerRun));
        }

        VerifyConfiguredOverrides(Object(limits, "configuredQueueOverrides"), expectedConfigured);
    }

    private static LimitValue ReadLimit(
        JsonElement parent,
        string name,
        string sourceProperty,
        string expectedSource,
        int minimum,
        int maximum)
    {
        var limit = Object(parent, name);
        var value = Integer(limit, "value");
        var provenance = String(limit, "provenance");
        Require(value >= minimum && value <= maximum && String(limit, sourceProperty) == expectedSource);
        return new(value, provenance);
    }

    private static void ValidateDefaultOrOverride(
        LimitValue limit,
        int defaultValue,
        string defaultProvenance,
        int minimum,
        int maximum)
    {
        if (limit.Provenance == "environment_override")
        {
            Require(limit.Value >= minimum && limit.Value <= maximum);
            return;
        }

        Require(limit.Provenance == defaultProvenance && limit.Value == defaultValue);
    }

    private static void ValidateInheritedOrOverride(
        LimitValue limit,
        string inheritedProvenance,
        int inheritedValue,
        int minimum,
        int maximum)
    {
        if (limit.Provenance == "environment_override")
        {
            Require(limit.Value >= minimum && limit.Value <= maximum);
            return;
        }

        Require(limit.Provenance == inheritedProvenance && limit.Value == inheritedValue);
    }

    private static int? ReadRetiredValue(JsonElement parent, string name, int minimum, int maximum)
    {
        Require(parent.TryGetProperty(name, out var value));
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireExactlyProperties(value, "value");
        var parsed = Integer(value, "value");
        Require(parsed >= minimum && parsed <= maximum);
        return parsed;
    }

    private static void VerifyConfiguredOverrides(
        JsonElement configured,
        IReadOnlyDictionary<string, (int? PageSize, int? MaxPerRun)> expected)
    {
        Require(configured.ValueKind == JsonValueKind.Object);
        Require(configured.EnumerateObject().Count() == expected.Count);
        foreach (var expectedEntry in expected)
        {
            Require(configured.TryGetProperty(expectedEntry.Key, out var actual));
            RequireExactlyProperties(actual, "pageSize", "maxPerRun");
            Require(NullableInteger(actual, "pageSize") == expectedEntry.Value.PageSize);
            Require(NullableInteger(actual, "maxPerRun") == expectedEntry.Value.MaxPerRun);
        }
    }

    private static int? NullableInteger(JsonElement parent, string name)
    {
        Require(parent.TryGetProperty(name, out var value));
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        return value.GetInt32();
    }

    private static void RequireResolved(JsonElement claim, string value, string provenance, string source)
    {
        Require(
            String(claim, "value") == value &&
            String(claim, "provenance") == provenance &&
            String(claim, "source") == source);
    }

    private static bool IsAttainableEnvironmentStaffUrl(string value) =>
        value.Length > 0 &&
        value.IndexOf('#') < 0 &&
        value.EndsWith("/staff/", StringComparison.Ordinal);

    private static string NormalizePersistedStaffUrl(string value)
    {
        value = StripHash(value);
        if (value.Length == 0)
        {
            return string.Empty;
        }
        if (!value.EndsWith('/'))
        {
            value += "/";
        }
        if (!value.EndsWith("/staff/", StringComparison.Ordinal))
        {
            value += "staff/";
        }
        return value;
    }

    private static string StripHash(string value)
    {
        var index = value.IndexOf('#');
        return JsTrim(index >= 0 ? value[..index] : value);
    }

    private static string JsTrim(string value)
    {
        var start = 0;
        while (start < value.Length && IsEcmaScriptWhitespace(value[start]))
        {
            start++;
        }
        var end = value.Length;
        while (end > start && IsEcmaScriptWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsEcmaScriptWhitespace(char value) => value is
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or
        '\u1680' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or
        '\u2006' or '\u2007' or '\u2008' or '\u2009' or '\u200A' or '\u2028' or '\u2029' or
        '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static JsonElement Object(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        Require(value.ValueKind == JsonValueKind.Object);
        return value;
    }

    private static string String(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        Require(value.ValueKind == JsonValueKind.String);
        return value.GetString()!;
    }

    private static bool Boolean(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return value.GetBoolean();
    }

    private static int Integer(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        return value.GetInt32();
    }

    private static JsonElement Property(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            throw Conflict();
        }
        return value;
    }

    private static void RequireExactlyProperties(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(actual.Length == names.Length && names.All(name => actual.Contains(name, StringComparer.Ordinal)));
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw Conflict();
        }
    }

    private static MigrationOperationException Conflict() => new(
        "package_metadata_conflict",
        "Frozen migration metadata contradicts source rows or pinned configuration rules.");

    private sealed record ScheduleRule(string Name, string Source, string DefaultValue, string Target);

    private sealed record QueueRule(string Name, string Target, string FallbackFamily);

    private sealed record LimitValue(int Value, string Provenance);
}
