using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class ReviewTls
{
    // The signing key is discarded. The localhost key and public root live only
    // in this disposable container; Chromium trusts the public root in its NSS store.
    public static async Task EnsureAsync(CancellationToken cancellationToken,
        string directory = "/review/tls", string publicDirectory = "/review/tls-public")
    {
        var pfxPath = Path.Combine(directory, "localhost.pfx");
        var passwordPath = Path.Combine(directory, "password.txt");
        var rootPath = Path.Combine(publicDirectory, "root.crt");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(publicDirectory);
        if (File.Exists(pfxPath) || File.Exists(passwordPath) || File.Exists(rootPath))
        {
            using var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath,
                await File.ReadAllTextAsync(passwordPath, cancellationToken));
            using var root = X509CertificateLoader.LoadCertificateFromFile(rootPath);
            if (!existing.HasPrivateKey || existing.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
                root.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("Review TLS material is expired or incomplete. Start a fresh acceptance run.");
            }
            return;
        }

        using var rootKey = RSA.Create(3072);
        var rootRequest = new CertificateRequest("CN=ASAP disposable review root", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var rootCertificate = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        using var serverKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var issued = request.Create(rootCertificate, DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(364), RandomNumberGenerator.GetBytes(16));
        using var server = issued.CopyWithPrivateKey(serverKey);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllBytesAsync(pfxPath, server.Export(X509ContentType.Pfx, password), cancellationToken);
        await File.WriteAllTextAsync(passwordPath, password, cancellationToken);
        await File.WriteAllTextAsync(rootPath, rootCertificate.ExportCertificatePem(), cancellationToken);
        Console.WriteLine("Created disposable localhost HTTPS material.");
    }
}
