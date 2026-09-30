using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace Asap.Web.Infrastructure.Development;

public sealed class DacpacDeploymentService
{
    public void Deploy(string targetConnectionString, string dacpacPath)
    {
        var target = new SqlConnectionStringBuilder(targetConnectionString);
        ValidateDevelopmentDatabaseName(target.InitialCatalog);

        var databaseName = target.InitialCatalog;
        var databaseConnectionString = target.ConnectionString;
        target.InitialCatalog = "master";

        var options = new DacDeployOptions
        {
            BlockOnPossibleDataLoss = true,
            CreateNewDatabase = false,
            DropObjectsNotInSource = false
        };

        using var package = DacPackage.Load(dacpacPath);
        RunPackagePreflight(package, target.ConnectionString, databaseConnectionString, databaseName);
        var services = new DacServices(target.ConnectionString);
        services.Deploy(package, databaseName, upgradeExisting: true, options);

        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dacpacPath))).ToLowerInvariant();
        using var connection = new SqlConnection(databaseConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [asap].[DeploymentState]
            SET [LastDacpacSha256] = @hash,
                [LastDeployedUtc] = SYSUTCDATETIME()
            WHERE [Id] = 1;
            """;
        command.Parameters.AddWithValue("@hash", hash);
        command.ExecuteNonQuery();
    }

    private static void RunPackagePreflight(
        DacPackage package, string masterConnectionString, string databaseConnectionString, string databaseName)
    {
        using var master = new SqlConnection(masterConnectionString);
        master.Open();
        using var exists = master.CreateCommand();
        exists.CommandText = "SELECT DB_ID(@name);";
        exists.Parameters.AddWithValue("@name", databaseName);
        if (exists.ExecuteScalar() is null or DBNull)
        {
            return;
        }

        // The DACPAC remains the only DDL owner. Run its idempotent guards and
        // narrowly specified retirements before DacFx computes data-loss guards.
        using var script = package.PreDeploymentScript;
        using var reader = new StreamReader(script);
        using var connection = new SqlConnection(databaseConnectionString);
        connection.Open();
        foreach (var batch in Regex.Split(reader.ReadToEnd(), @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.ExecuteNonQuery();
        }
    }

    internal static void ValidateDevelopmentDatabaseName(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) ||
            !databaseName.StartsWith("Asap", StringComparison.OrdinalIgnoreCase) ||
            databaseName.Equals("master", StringComparison.OrdinalIgnoreCase) ||
            databaseName.Equals("model", StringComparison.OrdinalIgnoreCase) ||
            databaseName.Equals("msdb", StringComparison.OrdinalIgnoreCase) ||
            databaseName.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Local F5 deployment requires a dedicated database name beginning with 'Asap'.");
        }
    }
}
