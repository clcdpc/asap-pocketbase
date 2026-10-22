namespace Asap.Migration;

public static class MigrationProgram
{
    public static int Main(string[] args) => MigrationCli.Run(args, Console.Out, Console.Error);
}
