using System.Security.Cryptography;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;

namespace Asap.Web.Features.Patron;

public static class PolarisConfigurationValidation
{
    // System scope is supported for authentication bootstrap and reference reads.
    // Member operations always supply their own servicing organization.
    public const int SystemOrganizationId = 1;

    public static bool IsStructurallyValid(PolarisSettings? settings) =>
        settings is not null && IsHostValid(settings.Host) &&
        !string.IsNullOrWhiteSpace(settings.AccessId) &&
        !string.IsNullOrWhiteSpace(settings.ProtectedApiKey) &&
        !string.IsNullOrWhiteSpace(settings.StaffDomain) &&
        !string.IsNullOrWhiteSpace(settings.AdminUser) &&
        !string.IsNullOrWhiteSpace(settings.ProtectedAdminPassword) &&
        settings.WorkstationId is > 0 && settings.SystemPolarisUserId is > 0;

    public static bool IsHostValid(string? host) =>
        Uri.TryCreate(host, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    public static bool TryReadCredentials(
        PolarisSettings settings,
        IntegrationCredentialProtector protector,
        out string accessKey,
        out string password)
    {
        accessKey = string.Empty;
        password = string.Empty;
        if (!IsStructurallyValid(settings))
        {
            return false;
        }
        try
        {
            accessKey = protector.Unprotect(settings.ProtectedApiKey!);
            password = protector.Unprotect(settings.ProtectedAdminPassword!);
            return !string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(password);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
