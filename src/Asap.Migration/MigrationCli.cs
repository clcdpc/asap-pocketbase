using System.Reflection;
using System.Text.Json;

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

        error.WriteLine($"Unknown argument: {args[0]}");
        error.WriteLine("Run with --help to see the implemented Slice 0 surface.");
        return 2;
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("ASAP PocketBase to SQL Server migration tool");
        output.WriteLine();
        output.WriteLine("Usage: Asap.Migration [--help|--version|describe-contract]");
        output.WriteLine();
        output.WriteLine("Slice 0 establishes package and contract identity only.");
        output.WriteLine("No entity export, import, or reconciliation command is implemented yet.");
    }
}
