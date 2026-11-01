namespace Asap.Tests;

// Opt in at method level only. Unclassified tests are required core coverage.
// scripts/ci/selection.py verifies the reviewed inventory against native discovery.
internal static class TestCategories
{
    public const string HistoricalMigration = "HistoricalMigration";
    public const string HistoricalUpgrade = "HistoricalUpgrade";
    public const string Browser = "Browser";
}
