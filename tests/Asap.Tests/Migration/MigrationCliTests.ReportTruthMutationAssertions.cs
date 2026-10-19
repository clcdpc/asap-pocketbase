using System.Text;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    private static async Task AssertRejectedReportTruthMutationAsync(
        string reportPath,
        bool preparedRecovery,
        string propertyName,
        string originalReport,
        string tamperedReport,
        Func<StringWriter, int> runCommand,
        Func<string, Task> assertExternalStateUnchangedAsync)
    {
        var testedReportPath = preparedRecovery ? reportPath + ".pending" : reportPath;
        await File.WriteAllTextAsync(testedReportPath, tamperedReport, new UTF8Encoding(false));
        using (var error = new StringWriter())
        {
            var operationName = preparedRecovery ? "Prepared recovery" : "Reconcile";
            Assert.AreEqual(
                1,
                runCommand(error),
                $"{operationName} accepted a false {propertyName} claim: {error}");
            StringAssert.Contains(error.ToString(), "reconciliation_report_mismatch");
        }

        Assert.AreEqual(tamperedReport, await File.ReadAllTextAsync(testedReportPath),
            $"Rejected report validation must preserve the tampered {propertyName} bytes.");
        if (preparedRecovery)
        {
            Assert.IsFalse(File.Exists(reportPath), "Rejected recovery must not promote a false transformation report.");
            Assert.IsFalse(Directory.Exists(reportPath), "Rejected recovery must leave the final report path absent.");
        }
        else
        {
            Assert.IsFalse(File.Exists(reportPath + ".pending"), "Rejected reconciliation must not create a pending report.");
        }
        await assertExternalStateUnchangedAsync(propertyName);

        await File.WriteAllTextAsync(testedReportPath, originalReport, new UTF8Encoding(false));
        Assert.AreEqual(originalReport, await File.ReadAllTextAsync(testedReportPath),
            $"Restoring {propertyName} must restore the exact original report bytes.");
    }
}
