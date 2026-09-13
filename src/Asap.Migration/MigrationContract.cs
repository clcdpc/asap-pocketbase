using Asap.Security;

namespace Asap.Migration;

public static class MigrationContract
{
    public const string PocketBaseBaselineSha = "150b30b776565194260cc327eeeffdfb46475e81";
    public const int ExpectedSchemaVersion = 4;
    public const string ContractVersion = "slice-04";

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
            "additional_copy_workflow",
            "relational_administration_configuration",
            "scoped_inheritance_and_reset",
            "polaris_reference_sync",
            "settings_semantic_reconciliation"
        },
        note = "Slice 4 includes relational administration/configuration, scoped inheritance/reset, and semantic migration reconciliation."
    };
}
