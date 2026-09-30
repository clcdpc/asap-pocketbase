using System.Data;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Infrastructure.Data;

internal static class PatronMutationLock
{
    // Serialize durable write-intent acquisition, never the external calls.
    // This lock precedes organization/staff/request/operation row locks.
    public static async Task<bool> TryAcquireAsync(
        SqlConnection connection, SqlTransaction transaction, string barcode,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = @resource,
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
            SELECT @result;
            """, connection, transaction);
        // sp_getapplock resources are binary/case-sensitive even when the target
        // barcode keys use the database's case-insensitive collation.
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "asap:patron-write:" + barcode.Trim().ToUpperInvariant();
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }
}
