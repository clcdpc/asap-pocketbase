using System.Data;
using Microsoft.Data.SqlClient;
using Asap.Web.Infrastructure.Configuration;

namespace Asap.Web.Infrastructure.Jobs;

public interface IHangfireSchemaCompatibilityChecker
{
    Task<int?> GetActualVersionAsync(CancellationToken cancellationToken);
}

public sealed class HangfireSchemaCompatibilityChecker(ExternalConfiguration configuration)
    : IHangfireSchemaCompatibilityChecker
{
    private readonly string connectionString = configuration.ConnectionStrings.HangfireDatabase!;

    public async Task<int?> GetActualVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'[HangFire].[Schema]', N'U') IS NULL
                SELECT CAST(NULL AS int);
            ELSE
                EXEC sys.sp_executesql N'SELECT MAX([Version]) FROM [HangFire].[Schema];';
            """,
            connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt32(result);
    }
}
