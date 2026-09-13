using Asap.Security;

namespace Asap.Migration;

public static class MigrationContract
{
    public const string PocketBaseBaselineSha = "150b30b776565194260cc327eeeffdfb46475e81";
    public const int ExpectedSchemaVersion = 4;
    public const string ContractVersion = "slice-03";

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
            "fresh_sql_import_and_reconciliation",
            "additional_copy_workflow"
        },
        note = "Slice 3 includes the AdditionalCopy workflow and its stopped-PocketBase migration contract."
    };
}
