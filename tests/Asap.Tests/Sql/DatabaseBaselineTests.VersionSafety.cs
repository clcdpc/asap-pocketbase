using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
    [TestMethod]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(10)]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(14)]
    public async Task UnsupportedVersionFailsBeforeChangingCurrentData(int version)
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE [asap].[SchemaVersion] SET [Version] = @version WHERE [Id] = 1;";
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync();
        var data = await Scalar(connection, "SELECT COUNT(*) FROM [asap].[EmailTemplate];");
        try
        {
            Assert.Throws<SqlException>(() => new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath));
            Assert.AreEqual(version, Convert.ToInt32(await Scalar(connection, "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
            Assert.AreEqual(data, await Scalar(connection, "SELECT COUNT(*) FROM [asap].[EmailTemplate];"));
        }
        finally
        {
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 13 WHERE [Id] = 1;");
        }
    }
}
