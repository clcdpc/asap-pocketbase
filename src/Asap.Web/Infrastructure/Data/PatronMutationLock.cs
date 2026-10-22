using System.Data;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Infrastructure.Data;

internal static class PatronMutationLock
{
    // Serialize durable write-intent acquisition, never the external calls.
    // This lock precedes organization/staff/request/operation row locks.
    public static async Task<bool> TryAcquireAsync(
        SqlConnection connection, SqlTransaction transaction, string barcode,
        CancellationToken cancellationToken) =>
        await TryAcquireAsync(connection, transaction, null, [barcode], cancellationToken);

    public static async Task<bool> TryAcquireAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int? nativePatronId,
        IEnumerable<string> verifiedBarcodeAliases,
        CancellationToken cancellationToken)
    {
        if (nativePatronId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nativePatronId), "A native patron ID must be positive when supplied.");
        }

        var resources = new SortedSet<string>(StringComparer.Ordinal);
        if (nativePatronId.HasValue)
        {
            resources.Add($"asap:patron-write:id:{nativePatronId.Value}");
        }

        foreach (var alias in verifiedBarcodeAliases)
        {
            var barcode = alias?.Trim();
            if (string.IsNullOrEmpty(barcode))
            {
                continue;
            }
            if (barcode.Length > 50)
            {
                throw new ArgumentException("A verified barcode alias must fit its persisted identity fields.", nameof(verifiedBarcodeAliases));
            }

            // Preserve the existing barcode resource so pre-identity journals
            // and cooperating callers remain fenced during the schema transition.
            resources.Add("asap:patron-write:" + barcode.ToUpperInvariant());
        }

        if (resources.Count == 0)
        {
            throw new ArgumentException("At least one verified patron barcode alias is required.", nameof(verifiedBarcodeAliases));
        }

        foreach (var resource in resources)
        {
            await using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = @resource,
                    @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
                SELECT @result;
                """, connection, transaction);
            command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
