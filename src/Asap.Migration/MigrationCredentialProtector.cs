using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Asap.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Asap.Migration;

internal sealed class MigrationCredentialProtector
{
    private readonly IDataProtector _protector;

    private MigrationCredentialProtector(IDataProtector protector)
    {
        _protector = protector;
    }

    public static MigrationCredentialProtector? Load(string? externalConfigurationPath) =>
        Load(externalConfigurationPath, createKeyDirectory: true);

    public static MigrationCredentialProtector? LoadForVerification(string? externalConfigurationPath) =>
        Load(externalConfigurationPath, createKeyDirectory: false);

    private static MigrationCredentialProtector? Load(string? externalConfigurationPath, bool createKeyDirectory)
    {
        if (string.IsNullOrWhiteSpace(externalConfigurationPath))
        {
            return null;
        }

        if (!File.Exists(externalConfigurationPath))
        {
            throw new MigrationOperationException("external_configuration_missing", "The target external configuration file does not exist.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(externalConfigurationPath));
            var application = document.RootElement.GetProperty("Application");
            var keyPath = application.GetProperty("DataProtectionKeysPath").GetString();
            var thumbprint = application.GetProperty("DataProtectionKeyEncryptionCertificateThumbprint").GetString();
            if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath) ||
                string.IsNullOrWhiteSpace(thumbprint))
            {
                throw new MigrationOperationException(
                    "credential_protection_configuration_invalid",
                    "Target Data Protection key path and certificate thumbprint are required for credential import.");
            }
            if (createKeyDirectory)
            {
                Directory.CreateDirectory(keyPath);
            }
            else if (!Directory.Exists(keyPath))
            {
                throw new MigrationOperationException(
                    "credential_protection_configuration_invalid",
                    "The configured target Data Protection key directory is unavailable for credential verification.");
            }
            var certificate = FindCertificate(thumbprint);
            if (certificate is null)
            {
                throw new MigrationOperationException(
                    "credential_protection_certificate_missing",
                    "The configured Data Protection key-encryption certificate with private key is unavailable.");
            }
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(keyPath),
                builder =>
                {
                    builder
                        .SetApplicationName(SecurityContract.DataProtectionApplicationName)
                        .ProtectKeysWithCertificate(certificate);
                    if (!createKeyDirectory)
                    {
                        builder.DisableAutomaticKeyGeneration();
                    }
                });
            return new MigrationCredentialProtector(
                provider.CreateProtector(SecurityContract.IntegrationCredentialPurpose));
        }
        catch (MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException or
                CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new MigrationOperationException(
                "credential_protection_configuration_invalid",
                "The target external configuration does not contain a valid Data Protection configuration.");
        }
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);

    private static X509Certificate2? FindCertificate(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                continue;
            }

            var certificate = store.Certificates
                .Find(X509FindType.FindByThumbprint, normalized, validOnly: false)
                .OfType<X509Certificate2>()
                .FirstOrDefault(candidate => candidate.HasPrivateKey);
            if (certificate is not null)
            {
                return certificate;
            }
        }
        return null;
    }
}
