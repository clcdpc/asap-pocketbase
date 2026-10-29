using System.Security.Cryptography.X509Certificates;

const string pfxPath = "/review/config/dataprotection.pfx";
const string passwordPath = "/review/config/dataprotection-password.txt";

if (!File.Exists(pfxPath) || !File.Exists(passwordPath))
{
    Console.Error.WriteLine("Review Data Protection certificate material is missing.");
    return 1;
}

var password = File.ReadAllText(passwordPath).Trim();
using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    pfxPath,
    password,
    X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

if (!certificate.HasPrivateKey)
{
    Console.Error.WriteLine("Review Data Protection certificate does not contain a private key.");
    return 1;
}

using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
store.Open(OpenFlags.ReadWrite);

var existing = store.Certificates
    .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
    .OfType<X509Certificate2>()
    .FirstOrDefault(candidate => candidate.HasPrivateKey);

if (existing is null)
{
    store.Add(certificate);
}
else
{
    existing.Dispose();
}

Console.WriteLine($"Imported review Data Protection certificate {certificate.Thumbprint} into CurrentUser/My.");
return 0;
