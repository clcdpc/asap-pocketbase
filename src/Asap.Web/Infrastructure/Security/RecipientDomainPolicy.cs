using System.Globalization;
using System.Net.Mail;
using Asap.Web.Infrastructure.Configuration;

namespace Asap.Web.Infrastructure.Security;

public sealed class RecipientDomainPolicy(ExternalConfiguration configuration)
{
    private readonly bool _isNonProduction = configuration.Environment.IsNonProduction!.Value;
    private readonly HashSet<string> _allowedDomains = new(
        (configuration.EmailSafety.AllowedRecipientDomains ?? [])
            .Select(domain => new IdnMapping().GetAscii(domain).ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(string recipient)
    {
        if (!_isNonProduction)
        {
            return true;
        }

        try
        {
            var address = new MailAddress(recipient);
            var domain = new IdnMapping().GetAscii(address.Host).ToLowerInvariant();
            return _allowedDomains.Contains(domain);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
