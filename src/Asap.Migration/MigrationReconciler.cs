using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

public sealed record MigrationReconcileOptions(
    string PackagePath,
    string ConnectionString,
    string ImportReportPath,
    string? ExternalConfigurationPath = null);

public static class MigrationReconciler
{
    public static void Reconcile(MigrationReconcileOptions options)
    {
        var package = MigrationPackageValidator.Validate(options.PackagePath);
        if (options.ExternalConfigurationPath is not null)
        {
            MigrationOperationalConfiguration.Validate(package, options.ExternalConfigurationPath);
        }
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new MigrationOperationException(
                "target_connection_missing",
                "The target SQL connection environment value is missing.");
        }
        if (!File.Exists(options.ImportReportPath))
        {
            throw new MigrationOperationException(
                "reconciliation_report_missing",
                "The restricted import reconciliation report does not exist.");
        }

        try
        {
            using var report = JsonDocument.Parse(File.ReadAllText(options.ImportReportPath));
            var root = report.RootElement;
            if (root.GetProperty("reportVersion").GetInt32() != 4 ||
                !root.GetProperty("reconciliationPassed").GetBoolean() ||
                !string.Equals(
                    root.GetProperty("sourceGitSha").GetString(),
                    package.Manifest.PocketBaseSourceGitSha,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    root.GetProperty("sourceSchemaVersion").GetString(),
                    package.Manifest.PocketBaseSourceSchemaVersion,
                    StringComparison.Ordinal) ||
                root.GetProperty("exportedAtUtc").GetDateTimeOffset() != package.Manifest.ExportedAtUtc ||
                !string.Equals(
                    root.GetProperty("packageIdentitySha256").GetString(),
                    MigrationPackageValidator.ComputePackageIdentitySha256(package),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException(
                    "reconciliation_report_mismatch",
                    "The reconciliation report does not identify this immutable export package.");
            }

            var expectedQueueProgress = root.GetProperty("targetCounts")
                .GetProperty("queue_progress")
                .GetInt32();
            var actualQueueProgress = ReadQueueProgressCount(options.ConnectionString);
            if (actualQueueProgress != expectedQueueProgress)
            {
                throw new MigrationOperationException(
                    "reconciliation_failed",
                    $"QueueProgress runtime count changed: report={expectedQueueProgress}, target={actualQueueProgress}.");
            }

            var expectedFingerprint = root.GetProperty("targetFingerprintSha256").GetString();
            var actualFingerprint = ComputeTargetFingerprint(options.ConnectionString);
            if (string.IsNullOrWhiteSpace(expectedFingerprint) ||
                !string.Equals(expectedFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationOperationException(
                    "reconciliation_failed",
                    "Target SQL state changed after the successful import reconciliation.");
            }
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new MigrationOperationException(
                "reconciliation_report_invalid",
                "The restricted import reconciliation report is invalid.");
        }
    }

    internal static string ComputeTargetFingerprint(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var tables = ReadTables(connection);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            Append(hash, $"table:{table.Name}");
            foreach (var column in table.Columns) Append(hash, $"column:{column}");

            using var command = connection.CreateCommand();
            var selected = string.Join(", ", table.Columns.Select(QuoteIdentifier));
            var ordered = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
            command.CommandText =
                $"SELECT {selected} FROM [asap].{QuoteIdentifier(table.Name)} ORDER BY {ordered};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Append(hash, "row");
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    Append(hash, CanonicalValue(table.Columns[index], reader.GetValue(index)));
                }
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static int ReadQueueProgressCount(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand("SELECT COUNT(*) FROM [asap].[QueueProgress];", connection);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IReadOnlyList<TargetTable> ReadTables(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                t.[name], c.[name], c.[column_id],
                CASE WHEN ic.[column_id] IS NULL THEN 0 ELSE ic.[key_ordinal] END AS [key_ordinal]
            FROM sys.tables t
            JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
            JOIN sys.columns c ON c.[object_id] = t.[object_id]
            JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
            LEFT JOIN sys.indexes i ON i.[object_id] = t.[object_id] AND i.[is_primary_key] = 1
            LEFT JOIN sys.index_columns ic
              ON ic.[object_id] = i.[object_id]
             AND ic.[index_id] = i.[index_id]
             AND ic.[column_id] = c.[column_id]
            WHERE s.[name] = N'asap'
              AND c.[is_computed] = 0
              AND ty.[name] NOT IN (N'timestamp', N'rowversion')
            ORDER BY t.[name], c.[column_id];
            """;
        var builders = new SortedDictionary<string, TableBuilder>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            if (!builders.TryGetValue(table, out var builder))
            {
                builder = new TableBuilder();
                builders.Add(table, builder);
            }
            var column = reader.GetString(1);
            builder.Columns.Add(column);
            var keyOrdinal = reader.GetInt32(3);
            if (keyOrdinal > 0) builder.Keys.Add((keyOrdinal, column));
        }
        return builders.Select(item => new TargetTable(
                item.Key,
                item.Value.Columns,
                item.Value.Keys.Count == 0
                    ? item.Value.Columns
                    : item.Value.Keys.OrderBy(key => key.Ordinal).Select(key => key.Name).ToArray()))
            .ToArray();
    }

    private static string CanonicalValue(string column, object value)
    {
        if (column.StartsWith("Protected", StringComparison.Ordinal))
        {
            return value is DBNull ? "protected:null" : "protected:present";
        }

        return value switch
        {
            DBNull => "null",
            byte[] bytes => $"bytes:{Convert.ToHexString(bytes)}",
            DateTime timestamp => $"datetime:{timestamp:O}",
            DateTimeOffset timestamp => $"datetimeoffset:{timestamp:O}",
            Guid identifier => $"guid:{identifier:D}",
            bool boolean => boolean ? "bool:1" : "bool:0",
            string text => $"string:{text}",
            IFormattable formattable => $"value:{formattable.ToString(null, CultureInfo.InvariantCulture)}",
            _ => $"value:{value}"
        };
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string QuoteIdentifier(string value) =>
        $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private sealed class TableBuilder
    {
        public List<string> Columns { get; } = [];
        public List<(int Ordinal, string Name)> Keys { get; } = [];
    }

    private sealed record TargetTable(
        string Name,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> KeyColumns);
}
