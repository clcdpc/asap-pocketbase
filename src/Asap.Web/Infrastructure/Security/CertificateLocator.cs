using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Asap.Web.Infrastructure.Security;

public static class CertificateLocator
{
    public static X509Certificate2? FindWithPrivateKey(string thumbprint)
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
