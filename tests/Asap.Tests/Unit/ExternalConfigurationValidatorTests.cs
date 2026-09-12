using Asap.Web.Infrastructure.Configuration;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class ExternalConfigurationValidatorTests
{
    [TestMethod]
    public void CompleteConfigurationIsValid()
    {
        var errors = ExternalConfigurationValidator.Validate(TestConfigurationFactory.Create());

        Assert.HasCount(0, errors);
    }

    [DataRow("*.example.org")]
    [DataRow(".example.org")]
    [DataRow("https://example.org")]
    [DataRow("patron@example.org")]
    [DataRow("example..org")]
    [TestMethod]
    public void MalformedRecipientDomainFailsValidation(string domain)
    {
        var value = TestConfigurationFactory.Create(allowedDomains: [domain]);

        var errors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(errors.ToList(), "recipient_domain_invalid");
    }

    [TestMethod]
    public void ScheduleAndQueueShapesAreExact()
    {
        var value = TestConfigurationFactory.Create();
        value.Hangfire.Schedules!.Remove("WorkflowProcessing");
        value.Hangfire.ProcessingLimits.Queues!["TypoQueue"] = new ProcessingLimit();

        var errors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(errors.ToList(), "hangfire_schedules_shape_invalid");
        CollectionAssert.Contains(errors.ToList(), "processing_queues_shape_invalid");
    }

    [TestMethod]
    public void IndependentNonProductionSwitchMustBePresent()
    {
        var value = TestConfigurationFactory.Create();
        value.Environment.IsNonProduction = null;

        var errors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(errors.ToList(), "environment_is_nonproduction_missing");
    }

    [TestMethod]
    public void ProcessingLimitBoundsAreValidated()
    {
        var value = TestConfigurationFactory.Create();
        value.Hangfire.ProcessingLimits.Default!.PageSize = 501;
        value.Hangfire.ProcessingLimits.Queues!["HoldPlacement"].MaxPerRun = 5001;

        var errors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(errors.ToList(), "processing_default_page_size_invalid");
        CollectionAssert.Contains(errors.ToList(), "processing_queue_HoldPlacement_max_per_run_invalid");
    }
}
