using System.Text.Encodings.Web;

namespace Asap.Web.Features.Administration;

public sealed record PublicPatronLinks(string Url, string Iframe, string AutoResize);

public static class PublicPatronLinkBuilder
{
    public static PublicPatronLinks? Build(string? configuredStaffUrl, int organizationId, bool isActive)
    {
        if (organizationId <= 1 || !isActive ||
            !Uri.TryCreate(configuredStaffUrl, UriKind.Absolute, out var configured) ||
            !string.IsNullOrEmpty(configured.UserInfo) ||
            configured.Host.Length == 0 ||
            configured.AbsolutePath is not ("/staff" or "/staff/") ||
            configured.Query.Length > 0 || configured.Fragment.Length > 0 ||
            (configured.Scheme != Uri.UriSchemeHttps &&
             !(configured.Scheme == Uri.UriSchemeHttp &&
               configured.Host is "localhost" or "127.0.0.1" or "::1")))
        {
            return null;
        }

        // Staff and patron routes are served by the same ASP.NET host. The persisted
        // staff URL is the configured external address; request Host is untrusted.
        var origin = configured.GetLeftPart(UriPartial.Authority);
        var url = $"{origin}/patron/?libraryOrgId={organizationId}";
        var embeddedUrl = $"{url}&embed=1";
        var safeUrl = HtmlEncoder.Default.Encode(embeddedUrl);
        var safeOrigin = HtmlEncoder.Default.Encode(origin);
        var iframe = $"<iframe src=\"{safeUrl}\" title=\"Suggest a purchase\" style=\"width:100%;min-height:520px;border:0\" loading=\"lazy\"></iframe>";
        var autoResize = $"<div data-asap-suggestions data-src=\"{safeOrigin}\" data-library-org-id=\"{organizationId}\"></div>\n<script src=\"{safeOrigin}/patron/embed.js\" defer></script>";
        return new PublicPatronLinks(url, iframe, autoResize);
    }
}
