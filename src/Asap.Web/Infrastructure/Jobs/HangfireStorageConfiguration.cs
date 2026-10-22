using Hangfire.SqlServer;

namespace Asap.Web.Infrastructure.Jobs;

public static class HangfireStorageConfiguration
{
    public const int ExpectedSchemaVersion = 9;

    public static SqlServerStorageOptions CreateRuntimeOptions() => new()
    {
        PrepareSchemaIfNecessary = false,
        TryAutoDetectSchemaDependentOptions = false
    };
}
