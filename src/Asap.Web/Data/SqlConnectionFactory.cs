using System.Data;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Data;

public sealed class SqlConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    private readonly string _connectionString = configuration.GetConnectionString("AsapSql")
        ?? throw new InvalidOperationException("Connection string 'AsapSql' is not configured.");

    public IDbConnection CreateConnection() => new SqlConnection(_connectionString);
}
