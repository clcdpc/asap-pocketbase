using Asap.Web.Infrastructure.Jobs;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class HangfireStorageConfigurationTests
{
    [TestMethod]
    public void RuntimeOptionsNeverPrepareOrAutoUpgradeStorageSchema()
    {
        var options = HangfireStorageConfiguration.CreateRuntimeOptions();

        Assert.IsFalse(options.PrepareSchemaIfNecessary);
        Assert.IsFalse(options.TryAutoDetectSchemaDependentOptions);
    }
}
