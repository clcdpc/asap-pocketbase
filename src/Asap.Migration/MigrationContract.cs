using Asap.Security;

namespace Asap.Migration;

public static class MigrationContract
{
    public const string PocketBaseBaselineSha = "150b30b776565194260cc327eeeffdfb46475e81";
    public const int ExpectedSchemaVersion = 3;
    public const string ContractVersion = "slice-02";

    public static object Describe() => new
    {
        contractVersion = ContractVersion,
        pocketBaseBaselineSha = PocketBaseBaselineSha,
        expectedSchemaVersion = ExpectedSchemaVersion,
        dataProtectionApplicationName = SecurityContract.DataProtectionApplicationName,
        integrationCredentialPurpose = SecurityContract.IntegrationCredentialPurpose,
        implementedCapabilities = new[]
        {
            "stopped_sqlite_export",
            "hashed_package_validation",
            "fresh_sql_import_and_reconciliation"
        },
        note = "Slice 2 export, validation, fresh-target import, and reconciliation are implemented for the domains owned by this slice."
    };
}
