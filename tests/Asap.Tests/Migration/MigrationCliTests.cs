using Asap.Migration;
using System.Text.Json;

namespace Asap.Tests.Migration;

[TestClass]
public sealed class MigrationCliTests
{
    [TestMethod]
    public void HelpIsHonestAboutSliceZeroScope()
    {
        using var output = new StringWriter();

        var exitCode = MigrationCli.Run(["--help"], output, TextWriter.Null);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output.ToString(), "No entity export, import, or reconciliation command");
    }

    [TestMethod]
    public void ContractPinsSourceAndSchemaWithoutClaimingCapabilities()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, MigrationCli.Run(["describe-contract"], output, TextWriter.Null));
        using var contract = JsonDocument.Parse(output.ToString());

        Assert.AreEqual(
            "150b30b776565194260cc327eeeffdfb46475e81",
            contract.RootElement.GetProperty("pocketBaseBaselineSha").GetString());
        Assert.AreEqual(1, contract.RootElement.GetProperty("expectedSchemaVersion").GetInt32());
        Assert.AreEqual(
            "CLC.ASAP",
            contract.RootElement.GetProperty("dataProtectionApplicationName").GetString());
        Assert.AreEqual(
            "CLC.ASAP.IntegrationCredential.v1",
            contract.RootElement.GetProperty("integrationCredentialPurpose").GetString());
        Assert.HasCount(0, contract.RootElement.GetProperty("implementedCapabilities").EnumerateArray().ToList());
    }

    [TestMethod]
    public void UnknownCommandFails()
    {
        using var error = new StringWriter();

        var exitCode = MigrationCli.Run(["export"], TextWriter.Null, error);

        Assert.AreEqual(2, exitCode);
        StringAssert.Contains(error.ToString(), "Unknown argument");
    }
}
