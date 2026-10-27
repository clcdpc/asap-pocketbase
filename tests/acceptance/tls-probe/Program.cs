using System.Security.Cryptography.X509Certificates;

var directory = args.Single();
var privatePath = Path.Combine(directory, "private");
var publicPath = Path.Combine(directory, "public");
await ReviewTls.EnsureAsync(CancellationToken.None, privatePath, publicPath);
var pfxPath = Path.Combine(privatePath, "localhost.pfx");
var first = await File.ReadAllBytesAsync(pfxPath);
await ReviewTls.EnsureAsync(CancellationToken.None, privatePath, publicPath);
var second = await File.ReadAllBytesAsync(pfxPath);
if (!first.SequenceEqual(second))
{
    throw new InvalidOperationException("Review certificate changed during idempotent initialization.");
}
var password = await File.ReadAllTextAsync(Path.Combine(privatePath, "password.txt"));
using var server = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);
using var root = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(publicPath, "root.crt"));
using var chain = new X509Chain();
chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
chain.ChainPolicy.CustomTrustStore.Add(root);
chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
if (!chain.Build(server) || !server.HasPrivateKey || root.HasPrivateKey ||
    Directory.GetFiles(publicPath).Length != 1)
{
    throw new InvalidOperationException("Review certificate chain or key isolation failed.");
}
File.Delete(Path.Combine(privatePath, "password.txt"));
try
{
    await ReviewTls.EnsureAsync(CancellationToken.None, privatePath, publicPath);
    throw new InvalidOperationException("Incomplete TLS state was unexpectedly regenerated.");
}
catch (FileNotFoundException)
{
    await File.WriteAllTextAsync(Path.Combine(privatePath, "password.txt"), password);
}
Console.WriteLine("TLS initialization, idempotence, chain trust and partial-state rejection passed.");
