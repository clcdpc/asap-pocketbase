using Asap.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Asap.Web.Infrastructure.Security;

public sealed class IntegrationCredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector =
        provider.CreateProtector(SecurityContract.IntegrationCredentialPurpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
