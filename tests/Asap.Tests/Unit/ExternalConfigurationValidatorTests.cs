using System.Diagnostics;
using System.Text;
using Asap.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

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

    [TestMethod]
    public void GeneratedTestHostTemplateIsValid()
    {
        var repositoryRoot = FindRepositoryRoot();
        var bootstrapPath = Path.Combine(
            repositoryRoot,
            "scripts",
            "deployment",
            "Initialize-AsapTestHost.ps1");
        var hostRoot = Path.Combine(
            Path.GetTempPath(),
            $"asap-bootstrap-validator-{Guid.NewGuid():N}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-File",
                bootstrapPath,
                "-Initialize",
                "-RootPath",
                hostRoot
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start PowerShell.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.AreEqual(0, process.ExitCode, standardOutput + Environment.NewLine + standardError);

            var templatePath = Path.Combine(hostRoot, "Config", "application.json");
            var template = File.ReadAllText(templatePath);
            Assert.IsFalse(template.Contains("ClientSecret", StringComparison.Ordinal));
            Assert.IsFalse(template.Contains("REPLACE-CLIENT-SECRET", StringComparison.Ordinal));
            Assert.IsFalse(template.Contains("\"TenantId\"", StringComparison.Ordinal));
            Assert.IsFalse(template.Contains("\"ObjectId\"", StringComparison.Ordinal));
            Assert.IsFalse(template.Contains("REPLACE-OBJECT-ID", StringComparison.Ordinal));
            Assert.IsFalse(template.Contains("REPLACE-ADMIN-UPN", StringComparison.Ordinal));
            foreach (var (placeholder, replacement) in new Dictionary<string, string>
            {
                ["REPLACE-SQL-SERVER"] = "localhost",
                ["REPLACE-ASAP-DATABASE"] = "AsapTest",
                ["REPLACE-HANGFIRE-DATABASE"] = "AsapHangfireTest",
                ["REPLACE-CLIENT-ID"] = "11111111-1111-1111-1111-111111111111",
                ["REPLACE-TENANT-ID"] = "22222222-2222-2222-2222-222222222222",
                ["REPLACE-ADMIN-EMAIL"] = "admin@example.org",
                ["REPLACE-DATA-PROTECTION-CERTIFICATE-THUMBPRINT"] = "AABBCC",
                ["REPLACE-ALLOWED-DOMAIN"] = "example.org"
            })
            {
                template = template.Replace(placeholder, replacement, StringComparison.Ordinal);
            }

            Assert.IsFalse(template.Contains("REPLACE-", StringComparison.Ordinal));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(template));
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var configurationValue = new ExternalConfiguration();
            // Missing sections must remain null instead of being hidden by model defaults.
            foreach (var property in typeof(ExternalConfiguration).GetProperties()
                         .Where(property => property.CanWrite && !property.PropertyType.IsValueType))
            {
                property.SetValue(configurationValue, null);
            }
            configuration.Bind(configurationValue);

            var errors = ExternalConfigurationValidator.Validate(configurationValue);

            Assert.HasCount(0, errors, string.Join(", ", errors));
            Assert.AreEqual(20, configurationValue.PatronLoginRateLimit.PermitLimit);
            Assert.AreEqual(300, configurationValue.PatronLoginRateLimit.WindowSeconds);
        }
        finally
        {
            if (Directory.Exists(hostRoot))
            {
                Directory.Delete(hostRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SqlAuthenticationRequiresTrustedTestingHostAllowance()
    {
        var value = TestConfigurationFactory.Create(isNonProduction: true);
        value.ConnectionStrings.AsapDatabase =
            "Server=localhost;Database=AsapTests;User ID=sa;Password=test-only-secret;TrustServerCertificate=True";
        value.ConnectionStrings.HangfireDatabase = value.ConnectionStrings.AsapDatabase;

        var defaultErrors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(defaultErrors.ToList(), "asap_database_integrated_security_required");
        CollectionAssert.Contains(defaultErrors.ToList(), "hangfire_database_integrated_security_required");

        var testingHostErrors = ExternalConfigurationValidator.Validate(
            value,
            allowSqlAuthenticationForTesting: true);

        Assert.HasCount(0, testingHostErrors);
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

    [TestMethod]
    public void PatronLoginRateLimitDefaultsAreValid()
    {
        var value = TestConfigurationFactory.Create();

        var errors = ExternalConfigurationValidator.Validate(value);

        Assert.HasCount(0, errors);
        Assert.AreEqual(20, value.PatronLoginRateLimit.PermitLimit);
        Assert.AreEqual(300, value.PatronLoginRateLimit.WindowSeconds);
    }

    [DataRow("live")]
    [DataRow("capture")]
    [TestMethod]
    public void EmailTransportModeMustBeExplicitlySupported(string mode)
    {
        var value = TestConfigurationFactory.Create();
        value.EmailTransport.Mode = mode;

        var errors = ExternalConfigurationValidator.Validate(value);

        Assert.IsFalse(errors.Contains("email_transport_mode_invalid"));
    }

    [TestMethod]
    public void UnknownEmailTransportModeFailsValidation()
    {
        var value = TestConfigurationFactory.Create();
        value.EmailTransport.Mode = "file-but-not-capture";

        var errors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(errors.ToList(), "email_transport_mode_invalid");
    }

    [DataRow(0, 300, "patron_login_rate_limit_permit_invalid")]
    [DataRow(20, 0, "patron_login_rate_limit_window_invalid")]
    [TestMethod]
    public void PatronLoginRateLimitBoundsAreValidated(
        int permitLimit,
        int windowSeconds,
        string expectedError)
    {
        var value = TestConfigurationFactory.Create();
        value.PatronLoginRateLimit.PermitLimit = permitLimit;
        value.PatronLoginRateLimit.WindowSeconds = windowSeconds;

        var errors = ExternalConfigurationValidator.Validate(value);

        CollectionAssert.Contains(errors.ToList(), expectedError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Asap.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
