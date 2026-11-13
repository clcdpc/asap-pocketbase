using System.Globalization;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record CatalogPresentationValues(string? PacBaseUrl, string? FormatIconUrlPattern, int? PacOrganizationId);

// One substitution path serves saved research configuration and synthetic Settings previews.
public static class CatalogPresentation
{
    private static readonly string[] MaterialTokens =
        ["{id}", "{id2}", "{MARCTypeOfMaterialID}", "{MARCTypeOfMaterialID2}", "{SearchCode}"];

    public static CatalogPresentationValues Resolve(SystemSettings system, LibraryCatalogSettings? library) =>
        new(library?.PacBaseUrl ?? system.PacBaseUrl,
            library?.MaterialTypeIconUrlPattern ?? system.MaterialTypeIconUrlPattern, library?.PacOrganizationId);

    public static async Task<CatalogPresentationValues> LoadAsync(
        AsapDbContext context, int libraryOrganizationId, CancellationToken cancellationToken)
    {
        var system = await context.SystemSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var library = await context.LibraryCatalogSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == libraryOrganizationId, cancellationToken);
        return Resolve(system, library);
    }

    public static bool IsPacBaseUrlValid(string? value) => string.IsNullOrWhiteSpace(value) ||
        SafeUrl(value.Trim(), allowLocal: false) is { } url &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        !value.Contains('{') && !value.Contains('}');

    public static bool IsIconPatternValid(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }
        // Existing literal HTTP(S) and single-slash patterns remain supported.
        // Tokens may substitute path/query data, never URL authority or scheme.
        var value = pattern.Trim();
        var baseToken = value.StartsWith("{pacBaseUrl}/", StringComparison.Ordinal);
        if (value.Contains("{pacBaseUrl}", StringComparison.Ordinal) &&
            (!baseToken || value.IndexOf("{pacBaseUrl}", "{pacBaseUrl}".Length, StringComparison.Ordinal) >= 0))
        {
            return false;
        }
        var expanded = value.Replace("{pacBaseUrl}", "https://catalog.example.org/polaris", StringComparison.Ordinal);
        var schemeEnd = expanded.IndexOf("://", StringComparison.Ordinal);
        var authorityEnd = schemeEnd >= 0 ? expanded.IndexOfAny(['/', '?', '#'], schemeEnd + 3) : -1;
        if (schemeEnd >= 0 && expanded[..(authorityEnd < 0 ? expanded.Length : authorityEnd)].Contains('{'))
        {
            return false;
        }
        foreach (var token in MaterialTokens.Append("{pacOrgId}"))
        {
            expanded = expanded.Replace(token, "1", StringComparison.Ordinal);
        }
        return value.Length <= 2048 && SafeUrl(expanded, allowLocal: true) is not null;
    }

    public static string? IconUrl(CatalogPresentationValues values, int? materialTypeId, string? searchCode)
    {
        var pattern = values.FormatIconUrlPattern?.Trim();
        if (string.IsNullOrEmpty(pattern) || !IsIconPatternValid(pattern))
        {
            return null;
        }
        var id = materialTypeId is > 0 ? materialTypeId.Value.ToString(CultureInfo.InvariantCulture) : null;
        var padded = materialTypeId is > 0 ? materialTypeId.Value.ToString("D2", CultureInfo.InvariantCulture) : null;
        var pacBase = IsPacBaseUrlValid(values.PacBaseUrl) ? values.PacBaseUrl?.Trim().TrimEnd('/') : null;
        var substitutions = new Dictionary<string, string?>
        {
            ["{pacBaseUrl}"] = string.IsNullOrEmpty(pacBase) ? null : pacBase,
            ["{pacOrgId}"] = values.PacOrganizationId is > 0
                ? values.PacOrganizationId.Value.ToString(CultureInfo.InvariantCulture) : null,
            ["{id}"] = id, ["{id2}"] = padded,
            ["{MARCTypeOfMaterialID}"] = id, ["{MARCTypeOfMaterialID2}"] = padded,
            ["{SearchCode}"] = string.IsNullOrWhiteSpace(searchCode) ? null : Uri.EscapeDataString(searchCode)
        };
        foreach (var (token, replacement) in substitutions)
        {
            if (!pattern.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }
            if (replacement is null)
            {
                return null;
            }
            pattern = pattern.Replace(token, replacement, StringComparison.Ordinal);
        }
        return SafeUrl(pattern, allowLocal: true);
    }

    private static string? SafeUrl(string value, bool allowLocal)
    {
        if (value.Length > 8192 || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            value.Contains('\\') || value.Contains('{') || value.Contains('}'))
        {
            return null;
        }
        if (allowLocal && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) &&
            Uri.IsWellFormedUriString(value, UriKind.Relative))
        {
            return value;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }
}
