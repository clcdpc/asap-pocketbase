using System.Globalization;

namespace Asap.Migration;

internal static class MigrationEffectiveConfiguration
{
    private static readonly (string LegacyName, string TargetName, string FallbackFamily)[] Queues =
    [
        ("pending_suggestion_isbn_checks", "IdentifierProcessing", "global"),
        ("outstanding_purchases", "PurchasePromotion", "global"),
        ("pending_holds", "HoldPlacement", "global"),
        ("checked_out", "FulfillmentTracking", "global"),
        ("outstanding_timeout", "OutstandingTimeout", "timeouts"),
        ("pending_hold_timeout", "PendingHoldTimeout", "timeouts"),
        ("hold_pickup_timeout", "HoldPickupTimeout", "timeouts"),
        ("additional_copy_timeout", "AdditionalCopyTimeout", "timeouts")
    ];

    public static object Runtime(
        IReadOnlyList<Dictionary<string, object?>> systemRows,
        IReadOnlyList<Dictionary<string, object?>> polarisRows,
        string sourceGitSha,
        DateTimeOffset capturedAtUtc)
    {
        if (systemRows.Count > 1 || polarisRows.Count > 1)
        {
            throw new MigrationOperationException(
                "effective_runtime_config_ambiguous",
                "Effective runtime configuration requires at most one system and Polaris settings row.");
        }

        var system = systemRows.SingleOrDefault();
        var polaris = polarisRows.SingleOrDefault();
        var staffUrl = ResolveStaffUrl(system);
        var iconPattern = ResolveIconPattern(system);
        return new
        {
            contract = "system/global SQL-bound values requiring pinned runtime fallback resolution only; library-scoped values are reconciled through domain exports",
            capturedFromPocketBaseSha = sourceGitSha.ToLowerInvariant(),
            capturedAtUtc = capturedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            settings = new
            {
                StaffApplicationUrl = staffUrl,
                MaterialTypeIconUrlPattern = iconPattern,
                PolarisApiKey = SecretPresence(polaris, "apiKey", "polaris_settings.apiKey"),
                PolarisAdminPassword = SecretPresence(polaris, "adminPassword", "polaris_settings.adminPassword")
            }
        };
    }

    public static object Operational(string sourceGitSha, DateTimeOffset capturedAtUtc)
    {
        var globalPageSize = ResolveInteger("ASAP_JOB_PAGE_SIZE", 50, 1, 500, "code_default");
        var globalMax = ResolveInteger("ASAP_JOB_MAX_PER_RUN", 500, 1, 5000, "code_default");
        var timeoutPageSize = ResolveInteger(
            "ASAP_TIMEOUT_PAGE_SIZE",
            globalPageSize.Value,
            1,
            500,
            "inherited_global");
        var timeoutMax = ResolveInteger(
            "ASAP_TIMEOUT_MAX_PER_RUN",
            globalMax.Value,
            1,
            5000,
            "inherited_global");

        var effectiveQueues = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var configuredOverrides = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var queue in Queues)
        {
            var fallbackPage = queue.FallbackFamily == "timeouts" ? timeoutPageSize.Value : globalPageSize.Value;
            var fallbackMax = queue.FallbackFamily == "timeouts" ? timeoutMax.Value : globalMax.Value;
            var inherited = queue.FallbackFamily == "timeouts" ? "inherited_timeout" : "inherited_global";
            var prefix = "ASAP_" + queue.LegacyName.ToUpperInvariant();
            var page = ResolveInteger($"{prefix}_PAGE_SIZE", fallbackPage, 1, 500, inherited);
            var maximum = ResolveInteger($"{prefix}_MAX_PER_RUN", fallbackMax, 1, 5000, inherited);
            effectiveQueues[queue.LegacyName] = new
            {
                targetQueue = queue.TargetName,
                pageSize = new
                {
                    value = page.Value,
                    provenance = page.Provenance,
                    queueOverrideSource = $"{prefix}_PAGE_SIZE"
                },
                maxPerRun = new
                {
                    value = maximum.Value,
                    provenance = maximum.Provenance,
                    queueOverrideSource = $"{prefix}_MAX_PER_RUN"
                },
                fallbackFamily = queue.FallbackFamily
            };
            if (page.Provenance == "environment_override" || maximum.Provenance == "environment_override")
            {
                configuredOverrides[queue.LegacyName] = new
                {
                    pageSize = page.Provenance == "environment_override" ? page.Value : (int?)null,
                    maxPerRun = maximum.Provenance == "environment_override" ? maximum.Value : (int?)null
                };
            }
        }

        var obsoletePage = ResolveOptionalInteger("ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE", 1, 500);
        var obsoleteMax = ResolveOptionalInteger("ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN", 1, 5000);
        if (obsoletePage.HasValue || obsoleteMax.HasValue)
        {
            configuredOverrides["pending_isbn_checks"] = new
            {
                pageSize = obsoletePage,
                maxPerRun = obsoleteMax
            };
        }

        return new
        {
            contract = "effective legacy external cron and queue-processing configuration used for cutover parity",
            capturedFromPocketBaseSha = sourceGitSha.ToLowerInvariant(),
            capturedAtUtc = capturedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            schedules = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["asap-hold-check"] = Schedule("ASAP_CRON_SCHEDULE", "0 * * * *", "WorkflowProcessing"),
                ["asap-isbn-check"] = Schedule("ASAP_ISBN_CHECK_CRON_SCHEDULE", "*/5 * * * *", "IdentifierProcessing"),
                ["asap-organization-sync"] = Schedule("ASAP_ORG_SYNC_CRON_SCHEDULE", "0 2 * * *", "OrganizationRefresh"),
                ["asap-weekly-staff-action-summary"] = Schedule(
                    "ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE",
                    "0 20 * * 0",
                    "WeeklyStaffSummary")
            },
            processingLimits = new
            {
                global = new
                {
                    pageSize = Limit(globalPageSize, "ASAP_JOB_PAGE_SIZE"),
                    maxPerRun = Limit(globalMax, "ASAP_JOB_MAX_PER_RUN")
                },
                timeouts = new
                {
                    pageSize = Limit(timeoutPageSize, "ASAP_TIMEOUT_PAGE_SIZE"),
                    maxPerRun = Limit(timeoutMax, "ASAP_TIMEOUT_MAX_PER_RUN")
                },
                effectiveQueues,
                obsoletePathOverrides = new
                {
                    pending_isbn_checks = new
                    {
                        pageSize = obsoletePage.HasValue ? new { value = obsoletePage.Value } : null,
                        maxPerRun = obsoleteMax.HasValue ? new { value = obsoleteMax.Value } : null,
                        resolution = "retired_with_hourly_identifier_path",
                        pageSizeEnvKey = "ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE",
                        maxPerRunEnvKey = "ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN"
                    }
                },
                configuredQueueOverrides = configuredOverrides
            }
        };
    }

    private static object ResolveStaffUrl(IReadOnlyDictionary<string, object?>? row)
    {
        var persisted = Value(row, "staffUrl");
        if (persisted is not null)
        {
            return Resolved(NormalizePersistedStaffUrl(persisted), "persisted_database", "system_settings.staffUrl");
        }
        var staff = EnvironmentValue("ASAP_STAFF_URL");
        if (staff is not null)
        {
            return Resolved(StaffUrlFromEnvironment(staff), "environment_fallback", "ASAP_STAFF_URL");
        }
        var publicUrl = EnvironmentValue("ASAP_PUBLIC_URL");
        return publicUrl is not null
            ? Resolved(StaffUrlFromEnvironment(publicUrl), "environment_fallback", "ASAP_PUBLIC_URL")
            : Resolved("http://localhost:8090/staff/", "code_default", "settings.staffUrl.localhost");
    }

    private static object ResolveIconPattern(IReadOnlyDictionary<string, object?>? row)
    {
        const string fallback =
            "https://catalog.clcohio.org/polaris/themes/shared/formats/formatid{MARCTypeOfMaterialID2}.gif";
        var persisted = Value(row, "formatIconUrlPattern");
        if (persisted is null ||
            persisted.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            persisted.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            !new[] { "{MARCTypeOfMaterialID}", "{MARCTypeOfMaterialID2}", "{id}", "{id2}", "{SearchCode}" }
                .Any(persisted.Contains))
        {
            return Resolved(fallback, "code_default", "normalization.defaultFormatIconUrlPattern");
        }
        return Resolved(persisted, "persisted_database", "system_settings.formatIconUrlPattern");
    }

    private static object SecretPresence(
        IReadOnlyDictionary<string, object?>? row,
        string field,
        string source) => new
        {
            hasValue = Value(row, field) is not null,
            provenance = row is null ? "absent_database_record" : "persisted_database",
            source
        };

    private static object Resolved(string value, string provenance, string source) =>
        new { value, provenance, source };

    private static object Schedule(string environmentName, string fallback, string targetScheduleKey)
    {
        var configured = EnvironmentValue(environmentName);
        return new
        {
            value = configured ?? fallback,
            provenance = configured is null ? "code_default" : "environment_override",
            source = environmentName,
            targetScheduleKey
        };
    }

    private static object Limit(ResolvedInteger resolved, string source) =>
        new { value = resolved.Value, provenance = resolved.Provenance, source };

    private static ResolvedInteger ResolveInteger(
        string name,
        int fallback,
        int minimum,
        int maximum,
        string fallbackProvenance)
    {
        var raw = EnvironmentValue(name);
        if (raw is null) return new(fallback, fallbackProvenance);
        var parsed = ParseLegacyInteger(raw, fallback);
        return new(Math.Clamp(parsed, minimum, maximum), "environment_override");
    }

    private static int? ResolveOptionalInteger(string name, int minimum, int maximum)
    {
        var raw = EnvironmentValue(name);
        if (raw is null) return null;
        var parsed = ParseLegacyInteger(raw, 0);
        return Math.Clamp(parsed, minimum, maximum);
    }

    private static int ParseLegacyInteger(string value, int fallback)
    {
        var digits = new string(value
            .SkipWhile(character => char.IsWhiteSpace(character))
            .TakeWhile((character, index) =>
                char.IsDigit(character) || index == 0 && character is '+' or '-')
            .ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static string NormalizePersistedStaffUrl(string value)
    {
        value = StripHash(value);
        if (!value.EndsWith('/')) value += "/";
        if (!value.EndsWith("/staff/", StringComparison.Ordinal)) value += "staff/";
        return value;
    }

    private static string StaffUrlFromEnvironment(string value)
    {
        value = StripHash(value);
        if (!value.EndsWith('/')) value += "/";
        return value + "staff/";
    }

    private static string StripHash(string value)
    {
        var index = value.IndexOf('#', StringComparison.Ordinal);
        return (index >= 0 ? value[..index] : value).Trim();
    }

    private static string? EnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? Value(IReadOnlyDictionary<string, object?>? row, string name)
    {
        if (row is null || !row.TryGetValue(name, out var raw) || raw is null) return null;
        var value = raw is byte[] bytes
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : Convert.ToString(raw, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record ResolvedInteger(int Value, string Provenance);
}
