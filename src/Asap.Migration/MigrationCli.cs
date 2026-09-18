using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Migration;

public static class MigrationCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args.SequenceEqual(["--help"]) || args.SequenceEqual(["-h"]))
        {
            WriteHelp(output);
            return 0;
        }

        if (args.SequenceEqual(["--version"]))
        {
            output.WriteLine(
                typeof(MigrationCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion.Split('+')[0] ?? "0.1.0");
            return 0;
        }

        if (args.SequenceEqual(["describe-contract"]))
        {
            output.WriteLine(JsonSerializer.Serialize(MigrationContract.Describe(), new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            return 0;
        }

        if (args[0] == "export")
        {
            return RunExport(args[1..], output, error);
        }

        if (args[0] == "validate")
        {
            return RunValidate(args[1..], output, error);
        }

        if (args[0] == "import")
        {
            return RunImport(args[1..], output, error);
        }

        if (args[0] == "reconcile")
        {
            return RunReconcile(args[1..], output, error);
        }

        error.WriteLine($"Unknown argument: {args[0]}");
        error.WriteLine("Run with --help to see the implemented migration surface.");
        return 2;
    }

    private static int RunExport(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var parsed = ParseOptions(
                args,
                ["--source", "--storage", "--output", "--source-git-sha", "--exported-at-utc"],
                ["--confirm-source-stopped"]);
            var exportedAtUtc = parsed.Values.TryGetValue("--exported-at-utc", out var timestamp)
                ? DateTimeOffset.Parse(
                    timestamp,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal)
                : DateTimeOffset.UtcNow;
            MigrationPackageExporter.Export(new MigrationExportOptions(
                Require(parsed.Values, "--source"),
                Require(parsed.Values, "--storage"),
                Require(parsed.Values, "--output"),
                Require(parsed.Values, "--source-git-sha"),
                exportedAtUtc,
                parsed.Flags.Contains("--confirm-source-stopped")));
            output.WriteLine("Export package created and hashed successfully.");
            return 0;
        }
        catch (Exception exception) when (exception is MigrationOperationException or ArgumentException or FormatException)
        {
            error.WriteLine(exception is MigrationOperationException operation ? operation.Code : "invalid_arguments");
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RunValidate(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var parsed = ParseOptions(args, ["--package", "--external-config"], []);
            var package = MigrationPackageValidator.Validate(Require(parsed.Values, "--package"));
            if (Optional(parsed.Values, "--external-config") is { } externalConfigurationPath)
            {
                MigrationOperationalConfiguration.Validate(package, externalConfigurationPath);
            }
            output.WriteLine(
                $"Package valid: {package.Manifest.EntityCounts.Values.Sum()} records across {package.Manifest.Files.Count} files.");
            return 0;
        }
        catch (Exception exception) when (exception is MigrationOperationException or ArgumentException)
        {
            error.WriteLine(exception is MigrationOperationException operation ? operation.Code : "invalid_arguments");
            error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int RunImport(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var parsed = ParseOptions(
                args,
                ["--package", "--connection-string-env", "--allowed-tenant-ids", "--report",
                 "--external-config", "--postmark-token-env"],
                []);
            var environmentName = Require(parsed.Values, "--connection-string-env");
            var connectionString = Environment.GetEnvironmentVariable(environmentName);
            var allowedTenantIds = Require(parsed.Values, "--allowed-tenant-ids")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Guid.Parse)
                .ToHashSet();
            var result = MigrationImporter.Import(new MigrationImportOptions(
                Require(parsed.Values, "--package"),
                connectionString ?? string.Empty,
                allowedTenantIds,
                Require(parsed.Values, "--report"),
                Require(parsed.Values, "--external-config"),
                Optional(parsed.Values, "--postmark-token-env")));
            output.WriteLine($"Import and reconciliation succeeded for {result.ImportedCounts.Values.Sum()} records.");
            return 0;
        }
        catch (Exception exception) when (
            exception is MigrationOperationException or ArgumentException or FormatException or SqlException or JsonException)
        {
            if (exception is MigrationOperationException operation)
            {
                error.WriteLine(operation.Code);
                error.WriteLine(operation.Message);
            }
            else if (exception is ArgumentException or FormatException)
            {
                error.WriteLine("invalid_arguments");
                error.WriteLine(exception.Message);
            }
            else
            {
                error.WriteLine("import_failed");
                error.WriteLine("The import failed while reading or writing structured migration data; inspect the restricted migration report and operator diagnostics.");
            }
            return 1;
        }
    }

    private static int RunReconcile(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var parsed = ParseOptions(
                args,
                ["--package", "--connection-string-env", "--report", "--external-config"],
                []);
            var environmentName = Require(parsed.Values, "--connection-string-env");
            MigrationReconciler.Reconcile(new MigrationReconcileOptions(
                Require(parsed.Values, "--package"),
                Environment.GetEnvironmentVariable(environmentName) ?? string.Empty,
                Require(parsed.Values, "--report"),
                Require(parsed.Values, "--external-config")));
            output.WriteLine("Reconciliation succeeded; target SQL matches the restricted import report.");
            return 0;
        }
        catch (Exception exception) when (
            exception is MigrationOperationException or ArgumentException or SqlException or JsonException)
        {
            if (exception is MigrationOperationException operation)
            {
                error.WriteLine(operation.Code);
                error.WriteLine(operation.Message);
            }
            else if (exception is ArgumentException)
            {
                error.WriteLine("invalid_arguments");
                error.WriteLine(exception.Message);
            }
            else
            {
                error.WriteLine("reconciliation_failed");
                error.WriteLine("Target reconciliation failed; inspect the restricted migration report and operator diagnostics.");
            }
            return 1;
        }
    }

    private static ParsedOptions ParseOptions(
        IReadOnlyList<string> args,
        IReadOnlyCollection<string> valueOptions,
        IReadOnlyCollection<string> flagOptions)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (flagOptions.Contains(option))
            {
                if (!flags.Add(option)) throw new ArgumentException($"Duplicate option: {option}");
                continue;
            }

            if (!valueOptions.Contains(option)) throw new ArgumentException($"Unknown option: {option}");
            if (++index >= args.Count) throw new ArgumentException($"Missing value for {option}");
            if (!values.TryAdd(option, args[index])) throw new ArgumentException($"Duplicate option: {option}");
        }
        return new ParsedOptions(values, flags);
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string option) =>
        values.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option: {option}");

    private static string? Optional(IReadOnlyDictionary<string, string> values, string option) =>
        values.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed record ParsedOptions(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> Flags);

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("ASAP PocketBase to SQL Server migration tool");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  Asap.Migration [--help|--version|describe-contract]");
        output.WriteLine("  Asap.Migration export --source <data.db> --storage <storage-dir> --output <package-dir>");
        output.WriteLine("      --source-git-sha <40-char-sha> --confirm-source-stopped [--exported-at-utc <timestamp>]");
        output.WriteLine("  Asap.Migration validate --package <package-dir> [--external-config <path>]");
        output.WriteLine("  Asap.Migration import --package <package-dir> --connection-string-env <name>");
        output.WriteLine("      --allowed-tenant-ids <comma-separated-guids> --report <path>");
        output.WriteLine("      --external-config <path> [--postmark-token-env <name>]");
        output.WriteLine("  Asap.Migration reconcile --package <package-dir> --connection-string-env <name> --report <path> --external-config <path>");
        output.WriteLine();
        output.WriteLine("Export reads a stopped PocketBase SQLite database and writes normalized hashed JSON.");
        output.WriteLine("Import validates a fresh target and writes a deterministic reconciliation report.");
    }
}
