using Asap.Web.Infrastructure.Development;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class DevelopmentDatabaseNameTests
{
    [DataRow("AsapDevelopment")]
    [DataRow("AsapSlice0Local")]
    [DataRow("Asap")]
    [TestMethod]
    public void DedicatedAsapDatabaseNamesAreAccepted(string databaseName)
    {
        DacpacDeploymentService.ValidateDevelopmentDatabaseName(databaseName);
    }

    [DataRow("master")]
    [DataRow("UnrelatedDatabase")]
    [TestMethod]
    public void BroadOrUnrelatedDatabaseNamesAreRejected(string databaseName)
    {
        Assert.Throws<InvalidOperationException>(
            () => DacpacDeploymentService.ValidateDevelopmentDatabaseName(databaseName));
    }
}
