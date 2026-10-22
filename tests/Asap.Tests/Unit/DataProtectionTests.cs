using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Asap.Security;
using Asap.Web.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class DataProtectionTests
{
    [TestMethod]
    public void PersistentCertificateProtectedRingRoundTripsAcrossProviders()
    {
        var directory = Directory.CreateTempSubdirectory("asap-dp-");
        try
        {
            using var certificate = CreateCertificate();
            var first = CreateProvider(directory, certificate);
            var ciphertext = new IntegrationCredentialProtector(first).Protect("integration-secret");

            var second = CreateProvider(directory, certificate);
            var plaintext = new IntegrationCredentialProtector(second).Unprotect(ciphertext);

            Assert.AreEqual("integration-secret", plaintext);
            Assert.AreNotEqual("integration-secret", ciphertext);
            Assert.IsFalse(
                Directory.EnumerateFiles(directory.FullName).Any(path =>
                    File.ReadAllText(path).Contains("integration-secret", StringComparison.Ordinal)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void DifferentRingCannotDecryptCredential()
    {
        var firstDirectory = Directory.CreateTempSubdirectory("asap-dp-a-");
        var secondDirectory = Directory.CreateTempSubdirectory("asap-dp-b-");
        try
        {
            using var firstCertificate = CreateCertificate();
            using var secondCertificate = CreateCertificate();
            var ciphertext = new IntegrationCredentialProtector(
                CreateProvider(firstDirectory, firstCertificate)).Protect("integration-secret");

            Assert.Throws<CryptographicException>(() =>
                new IntegrationCredentialProtector(
                    CreateProvider(secondDirectory, secondCertificate)).Unprotect(ciphertext));
        }
        finally
        {
            firstDirectory.Delete(recursive: true);
            secondDirectory.Delete(recursive: true);
        }
    }

    private static IDataProtectionProvider CreateProvider(
        DirectoryInfo directory,
        X509Certificate2 certificate) =>
        DataProtectionProvider.Create(directory, builder => builder
            .SetApplicationName(SecurityContract.DataProtectionApplicationName)
            .ProtectKeysWithCertificate(certificate));

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ASAP Test Data Protection",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }
}
