using System.Text.Json;

namespace Asap.Migration;

internal sealed record OperationalConfigurationReconciliation(
    int MatchedSchedules,
    int MatchedQueues,
    int RetiredObsoleteOverrides);

internal static class MigrationOperationalConfiguration
{
    public static OperationalConfigurationReconciliation Validate(
        ValidatedMigrationPackage package,
        string externalConfigurationPath)
    {
        if (!File.Exists(externalConfigurationPath))
        {
            throw new MigrationOperationException(
                "external_configuration_missing",
                "The target external configuration file does not exist.");
        }

        try
        {
            var source = MigrationPackageReader.ReadMetadata(
                package,
                "effective-legacy-operational-config.json");
            using var targetDocument = JsonDocument.Parse(File.ReadAllText(externalConfigurationPath));
            var targetHangfire = Property(targetDocument.RootElement, "Hangfire");
            var targetSchedules = Property(targetHangfire, "Schedules");
            var matchedSchedules = 0;
            foreach (var sourceSchedule in source.GetProperty("schedules").EnumerateObject())
            {
                var targetKey = sourceSchedule.Value.GetProperty("targetScheduleKey").GetString()!;
                var expected = sourceSchedule.Value.GetProperty("value").GetString();
                var actual = StringProperty(targetSchedules, targetKey);
                RequireEqual(targetKey, expected, actual);
                matchedSchedules++;
            }

            var sourceLimits = source.GetProperty("processingLimits");
            var targetLimits = Property(targetHangfire, "ProcessingLimits");
            var targetDefault = Property(targetLimits, "Default");
            var defaultPageSize = RequiredInt(targetDefault, "PageSize");
            var defaultMaxPerRun = RequiredInt(targetDefault, "MaxPerRun");
            RequireEqual(
                "ProcessingLimits.Default.PageSize",
                sourceLimits.GetProperty("global").GetProperty("pageSize").GetProperty("value").GetInt32(),
                defaultPageSize);
            RequireEqual(
                "ProcessingLimits.Default.MaxPerRun",
                sourceLimits.GetProperty("global").GetProperty("maxPerRun").GetProperty("value").GetInt32(),
                defaultMaxPerRun);

            var targetTimeouts = Property(targetLimits, "Timeouts");
            var timeoutPageSize = OptionalInt(targetTimeouts, "PageSize") ?? defaultPageSize;
            var timeoutMaxPerRun = OptionalInt(targetTimeouts, "MaxPerRun") ?? defaultMaxPerRun;
            RequireEqual(
                "ProcessingLimits.Timeouts.PageSize",
                sourceLimits.GetProperty("timeouts").GetProperty("pageSize").GetProperty("value").GetInt32(),
                timeoutPageSize);
            RequireEqual(
                "ProcessingLimits.Timeouts.MaxPerRun",
                sourceLimits.GetProperty("timeouts").GetProperty("maxPerRun").GetProperty("value").GetInt32(),
                timeoutMaxPerRun);

            var targetQueues = Property(targetLimits, "Queues");
            var matchedQueues = 0;
            foreach (var sourceQueue in sourceLimits.GetProperty("effectiveQueues").EnumerateObject())
            {
                var targetKey = sourceQueue.Value.GetProperty("targetQueue").GetString()!;
                var targetQueue = Property(targetQueues, targetKey);
                var timeoutFamily = string.Equals(
                    sourceQueue.Value.GetProperty("fallbackFamily").GetString(),
                    "timeouts",
                    StringComparison.Ordinal);
                var actualPageSize = OptionalInt(targetQueue, "PageSize") ??
                    (timeoutFamily ? timeoutPageSize : defaultPageSize);
                var actualMaxPerRun = OptionalInt(targetQueue, "MaxPerRun") ??
                    (timeoutFamily ? timeoutMaxPerRun : defaultMaxPerRun);
                RequireEqual(
                    $"ProcessingLimits.Queues.{targetKey}.PageSize",
                    sourceQueue.Value.GetProperty("pageSize").GetProperty("value").GetInt32(),
                    actualPageSize);
                RequireEqual(
                    $"ProcessingLimits.Queues.{targetKey}.MaxPerRun",
                    sourceQueue.Value.GetProperty("maxPerRun").GetProperty("value").GetInt32(),
                    actualMaxPerRun);
                matchedQueues++;
            }

            var obsolete = sourceLimits.GetProperty("obsoletePathOverrides").GetProperty("pending_isbn_checks");
            var retiredOverrides = HasValue(obsolete, "pageSize") || HasValue(obsolete, "maxPerRun") ? 1 : 0;
            return new OperationalConfigurationReconciliation(
                matchedSchedules,
                matchedQueues,
                retiredOverrides);
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new MigrationOperationException(
                "operational_configuration_invalid",
                "The frozen or target operational configuration has an invalid shape.");
        }
    }

    private static void RequireEqual<T>(string key, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new MigrationOperationException(
                "operational_configuration_mismatch",
                $"Target operational setting {key} does not match the frozen source value.");
        }
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        throw new KeyNotFoundException(name);
    }

    private static string? StringProperty(JsonElement value, string name) =>
        Property(value, name).GetString()?.Trim();

    private static int RequiredInt(JsonElement value, string name) =>
        Property(value, name).GetInt32();

    private static int? OptionalInt(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : property.GetInt32();
    }

    private static bool HasValue(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
