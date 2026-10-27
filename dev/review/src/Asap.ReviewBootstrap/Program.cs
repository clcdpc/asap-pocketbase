using System.Security.Cryptography.X509Certificates;

if (args.SequenceEqual(["--health"]) || (args.Length == 2 && args[0] == "--challenge"))
{
    using var root = X509CertificateLoader.LoadCertificateFromFile("/review/tls-public/root.crt");
    using var handler = new HttpClientHandler();
    handler.AllowAutoRedirect = false;
    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
    {
        if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return false;
        }
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    try
    {
        if (args[0] == "--health")
        {
            using var response = await client.GetAsync("https://localhost:8443/health/ready", cancellation.Token);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token));
            return response.IsSuccessStatusCode && body.RootElement.GetProperty("status").GetString() == "healthy" ? 0 : 1;
        }
        if (!int.TryParse(args[1], out var port) || port is < 1 or > 65535)
        {
            return 1;
        }
        using var challenge = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8443/api/asap/staff/sign-in?returnUrl=%2Fstaff%2F");
        challenge.Headers.Host = $"localhost:{port}";
        using var redirect = await client.SendAsync(challenge, cancellation.Token);
        var callback = Uri.EscapeDataString($"https://localhost:{port}/signin-oidc");
        return (int)redirect.StatusCode is >= 300 and < 400 &&
            redirect.Headers.Location?.ToString().Contains($"redirect_uri={callback}", StringComparison.OrdinalIgnoreCase) == true ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        return 1;
    }
}

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
