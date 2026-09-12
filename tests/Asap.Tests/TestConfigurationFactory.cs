using Asap.Web.Infrastructure.Configuration;

namespace Asap.Tests;

internal static class TestConfigurationFactory
{
    public static ExternalConfiguration Create(
        bool isNonProduction = true,
        List<string>? allowedDomains = null)
    {
        return new ExternalConfiguration
        {
            Environment = new DeploymentEnvironmentOptions
            {
                Name = "Testing",
                IsNonProduction = isNonProduction,
                UiBannerText = "TEST"
            },
            ConnectionStrings = new ConnectionStringOptions
            {
                AsapDatabase = "Server=localhost;Database=AsapTests;Integrated Security=True;TrustServerCertificate=True",
                HangfireDatabase = "Server=localhost;Database=AsapTests;Integrated Security=True;TrustServerCertificate=True"
            },
            Authentication = new AuthenticationOptions
            {
                Entra = new EntraOptions
                {
                    ClientId = "00000000-0000-0000-0000-000000000001",
                    ClientSecret = "test-only-secret",
                    AllowedTenantIds = ["00000000-0000-0000-0000-000000000002"],
                    InitialSuperAdmin = new InitialSuperAdminOptions
                    {
                        TenantId = "00000000-0000-0000-0000-000000000002",
                        ObjectId = "00000000-0000-0000-0000-000000000003",
                        UserPrincipalName = "admin@example.org",
                        DisplayName = "Test Administrator",
                        NotificationEmail = "admin@example.org"
                    }
                }
            },
            Application = new ApplicationOptions
            {
                BusinessTimeZone = "America/New_York",
                DataProtectionKeysPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "asap-test-keys")),
                DataProtectionKeyEncryptionCertificateThumbprint = "TEST-THUMBPRINT",
                LogPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "asap-test-logs"))
            },
            EmailSafety = new EmailSafetyOptions
            {
                AllowedRecipientDomains = allowedDomains
            },
            Hangfire = new HangfireOptions
            {
                Schedules = new Dictionary<string, string>
                {
                    ["WorkflowProcessing"] = "0 * * * *",
                    ["IdentifierProcessing"] = "*/5 * * * *",
                    ["OrganizationRefresh"] = "0 2 * * *",
                    ["WeeklyStaffSummary"] = "0 20 * * 0",
                    ["EmailOutboxSweep"] = "*/5 * * * *",
                    ["PatronSessionCleanup"] = "0 3 * * *",
                    ["EmailPayloadCleanup"] = "30 3 * * *"
                },
                ProcessingLimits = new ProcessingLimitsOptions
                {
                    Default = new ProcessingLimit { PageSize = 50, MaxPerRun = 500 },
                    Timeouts = new ProcessingLimit(),
                    Queues = ExternalConfigurationValidator.RequiredQueueKeys.ToDictionary(
                        key => key,
                        _ => new ProcessingLimit(),
                        StringComparer.Ordinal)
                }
            }
        };
    }
}
