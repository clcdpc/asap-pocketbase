using System.Security.Cryptography;
using Asap.Web.Infrastructure.Development;

namespace Asap.Web.Infrastructure.Security;

public sealed class DataProtectionInitializer(
    IntegrationCredentialProtector protector,
    RuntimeInitializationState state,
    ILogger<DataProtectionInitializer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string probe = "asap-data-protection-startup-probe";
            var ciphertext = protector.Protect(probe);
            if (!string.Equals(probe, protector.Unprotect(ciphertext), StringComparison.Ordinal))
            {
                throw new CryptographicException("Data Protection startup round trip failed.");
            }

            logger.LogInformation("Persistent Data Protection key ring initialized.");
        }
        catch (Exception exception)
        {
            state.MarkFailed("data_protection_initialization_failed");
            logger.LogError(exception, "Data Protection initialization failed; readiness remains unhealthy.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
