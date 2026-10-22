using System.Text.RegularExpressions;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Patron;

public sealed class PatronContentSecurityPolicyMiddleware(
    RequestDelegate next,
    IDbContextFactory<AsapDbContext> contextFactory)
{
    private static readonly Regex RequestOriginPattern = new(
        @"^(https?):\/\/([^\/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SafeAuthorityPattern = new(
        @"^[a-z0-9.:[\]-]+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex TrailingPortPattern = new(
        @":[0-9]+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex WildcardOriginPattern = new(
        @"^https:\/\/\*\.([^\/:]+)(?::([0-9]+))?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex CandidateOriginPattern = new(
        @"^https:\/\/([^\/:]+)(?::([0-9]+))?$",
        RegexOptions.CultureInvariant);

    public async Task InvokeAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Path.StartsWithSegments("/patron"))
        {
            await using var context = await contextFactory.CreateDbContextAsync(
                httpContext.RequestAborted);
            var origins = await context.PatronEmbedAllowedOrigins.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .OrderBy(item => item.NormalizedOrigin)
                .Select(item => item.NormalizedOrigin)
                .ToListAsync(httpContext.RequestAborted);
            var requestOrigin = RequestOrigin(httpContext.Request);
            if (requestOrigin.Length > 0 &&
                !origins.Contains(requestOrigin, StringComparer.Ordinal) &&
                origins.Any(origin => WildcardMatchesOrigin(origin, requestOrigin)))
            {
                origins.Add(requestOrigin);
            }

            var frameAncestors = string.Join(' ', new[] { "'self'" }.Concat(origins));
            httpContext.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; " +
                "script-src 'self'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "font-src 'self'; " +
                "img-src 'self' data:; " +
                "connect-src 'self'; " +
                $"frame-ancestors {frameAncestors}; " +
                "base-uri 'self'; form-action 'self'";
        }

        await next(httpContext);
    }

    private static string RequestOrigin(HttpRequest request)
    {
        var referer = request.Headers["Referer"].ToString();
        var ancestorUrl = referer.Length > 0
            ? referer
            : request.Headers["Origin"].ToString();
        var match = RequestOriginPattern.Match(ancestorUrl.Trim());
        if (!match.Success)
        {
            return string.Empty;
        }

        var scheme = match.Groups[1].Value.ToLowerInvariant();
        var authority = match.Groups[2].Value.ToLowerInvariant();
        if (authority.Any(character =>
                char.IsWhiteSpace(character) || character is '"' or '\'' or '`' or ';' or '\\') ||
            authority.Contains('@', StringComparison.Ordinal) ||
            !SafeAuthorityPattern.IsMatch(authority))
        {
            return string.Empty;
        }

        var hostname = TrailingPortPattern.Replace(authority, string.Empty);
        var isLocal = hostname is "localhost" or "127.0.0.1" or "[::1]";
        if (scheme != "https" && !(scheme == "http" && isLocal))
        {
            return string.Empty;
        }

        return $"{scheme}://{authority}";
    }

    private static bool WildcardMatchesOrigin(string wildcardOrigin, string candidateOrigin)
    {
        var wildcardMatch = WildcardOriginPattern.Match(wildcardOrigin.ToLowerInvariant());
        var candidateMatch = CandidateOriginPattern.Match(candidateOrigin.ToLowerInvariant());
        if (!wildcardMatch.Success || !candidateMatch.Success)
        {
            return false;
        }

        var wildcardHost = wildcardMatch.Groups[1].Value;
        var wildcardPort = wildcardMatch.Groups[2].Value;
        var candidateHost = candidateMatch.Groups[1].Value;
        var candidatePort = candidateMatch.Groups[2].Value;
        return candidateHost.Length > wildcardHost.Length &&
               candidateHost.EndsWith($".{wildcardHost}", StringComparison.Ordinal) &&
               wildcardPort == candidatePort;
    }
}
